// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace VassasCo.Utility
{
    /// <summary>
    /// 订阅者执行出错时的事件参数，包含原始事件对象和异常信息。
    /// 订阅此事件可统一捕获所有订阅者的未处理异常（如记录日志）。
    /// </summary>
    public class EventHandlerErrorEventArgs : EventArgs
    {
        /// <summary>触发异常的事件对象</summary>
        public object Event { get; }

        /// <summary>订阅者抛出的异常</summary>
        public Exception Exception { get; }

        internal EventHandlerErrorEventArgs(object @event, Exception exception)
        {
            Event = @event;
            Exception = exception;
        }
    }

    /// <summary>
    /// 订阅令牌，调用 Dispose 即可取消订阅。
    /// 由 Subscribe / SubscribeWeak / Register 返回，持有此令牌即可随时取消对应的订阅。
    /// </summary>
    public sealed class SubscriptionToken : IDisposable
    {
        private readonly EventBus _bus;
        private readonly long _id;
        private int _disposed;

        internal SubscriptionToken(EventBus bus, long id)
        {
            _bus = bus;
            _id = id;
        }

        /// <summary>取消订阅。重复调用安全。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _bus.Unsubscribe(_id);
        }
    }

    /// <summary>
    /// 事件总线的订阅快照（按事件类型聚合），用于排查订阅泄漏。
    /// </summary>
    public sealed class SubscriptionSnapshot
    {
        /// <summary>事件类型。</summary>
        public Type EventType { get; }

        /// <summary>该事件类型的订阅总数（含已失效的弱引用订阅）。</summary>
        public int Total { get; }

        /// <summary>其中弱引用订阅的数量。</summary>
        public int Weak { get; }

        /// <summary>其中弱引用目标已被 GC 回收、等待清理的「死订阅」数量。</summary>
        public int Dead { get; }

        internal SubscriptionSnapshot(Type eventType, int total, int weak, int dead)
        {
            EventType = eventType;
            Total = total;
            Weak = weak;
            Dead = dead;
        }

        /// <inheritdoc/>
        public override string ToString()
            => $"{EventType.Name}: 共 {Total} 个订阅（弱引用 {Weak}，已失效 {Dead}）";
    }

    /// <summary>
    /// 轻量级进程内事件总线。发布者和订阅者通过事件类型解耦，互不感知。
    /// 支持优先级排序、条件过滤、粘性事件、多通道隔离、异步处理、特性自动注册、
    /// 异常隔离、线程封送、弱引用订阅与订阅快照诊断。
    /// </summary>
    /// <remarks>
    /// 本类型线程安全：可在任意线程并发 <c>Subscribe</c> / <c>Unsubscribe</c> / <c>Publish</c>。
    /// 全局默认实例通过 <see cref="Default"/> 获取（刻意不引入 DI 容器，保持零依赖）。
    /// </remarks>
    public class EventBus : IDisposable
    {
        #region 内部类型

        private sealed class Subscription
        {
            public long Id;
            public int Priority;

            /// <summary>订阅方关注的通道列表。null 或空 = 监听所有通道。</summary>
            public string[]? Channels;

            public Func<object, bool>? Filter;

            /// <summary>
            /// 订阅时捕获的同步上下文。非 null 表示发布时需要把处理器封送回该上下文执行。
            /// </summary>
            public SynchronizationContext? Context;

            /// <summary>是否是弱引用订阅。</summary>
            public bool IsWeak;

            /// <summary>弱引用订阅的目标（handler.Target）的弱引用；强引用订阅为 null。</summary>
            public WeakReference? TargetRef;

            /// <summary>
            /// 统一调用入口（订阅时在泛型上下文中一次性构造好，发布路径零反射）。
            /// 同步处理器返回 <c>null</c>；异步处理器返回其 <see cref="Task"/>。
            /// </summary>
            public Func<object, Task?> Invoker = null!;

            /// <summary>处理器描述（仅用于诊断输出，不持有任何引用）。</summary>
            public string Description = string.Empty;

            /// <summary>弱引用订阅的目标是否仍然存活。强引用订阅恒为 true。</summary>
            public bool IsAlive => !IsWeak || (TargetRef != null && TargetRef.IsAlive);

            public Task? Invoke(object @event) => Invoker(@event);
        }

        /// <summary>按实例退订的登记项：某个实例名下的所有订阅 ID。</summary>
        private sealed class OwnerIds
        {
            public readonly List<long> Ids = new List<long>();
        }

        #endregion

        #region 静态单例

        private static readonly Lazy<EventBus> _default = new Lazy<EventBus>(
            () => new EventBus(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局默认实例，开箱即用（不依赖 DI 容器）。</summary>
        public static EventBus Default => _default.Value;

        #endregion

        private readonly EventBusOptions _options;

        private readonly ConcurrentDictionary<Type, List<Subscription>> _subscriptions
            = new ConcurrentDictionary<Type, List<Subscription>>();

        private readonly ConcurrentDictionary<Type, (object Event, string? Channel)> _stickyEvents
            = new ConcurrentDictionary<Type, (object, string?)>();

        /// <summary>
        /// 订阅 ID → 事件类型索引，让 <c>Unsubscribe(id)</c> 不必遍历所有事件类型的所有订阅。
        /// 缺少该索引时退订复杂度为 O(总订阅数)，在窗口频繁开关、高速订阅-退订场景下退化明显。
        /// </summary>
        private readonly ConcurrentDictionary<long, Type> _subIndex
            = new ConcurrentDictionary<long, Type>();

        /// <summary>
        /// 登记实例 → 订阅 ID 列表，用于 <see cref="Unregister(object)"/> 一键退订。
        /// 键为弱引用（<see cref="ConditionalWeakTable{TKey,TValue}"/>），不会阻止实例被回收。
        /// </summary>
        private readonly ConditionalWeakTable<object, OwnerIds> _ownerIndex
            = new ConditionalWeakTable<object, OwnerIds>();

        /// <summary>粘性事件写入顺序（FIFO 淘汰用）。</summary>
        private readonly LinkedList<Type> _stickyOrder = new LinkedList<Type>();

        private readonly object _lock = new object();
        private long _idCounter;
        private volatile bool _disposed;

        /// <summary>当某个订阅者抛出未捕获异常时触发。订阅此事件可集中记录错误日志。</summary>
        public event EventHandler<EventHandlerErrorEventArgs>? HandlerError;

        /// <summary>使用默认配置创建事件总线。</summary>
        public EventBus() : this(null) { }

        /// <summary>使用指定配置创建事件总线。</summary>
        /// <param name="options">行为配置；为 null 时使用默认配置。</param>
        public EventBus(EventBusOptions? options)
        {
            _options = options ?? new EventBusOptions();
        }

        /// <summary>本总线的配置（只读用途，构造后不再变更）。</summary>
        public EventBusOptions Options => _options;

        private long NextId() => Interlocked.Increment(ref _idCounter);

        #region 订阅 (单通道 / 多通道)

        /// <summary>
        /// 订阅一个同步事件（单通道）。
        /// </summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        /// <param name="handler">事件处理方法</param>
        /// <param name="priority">优先级，越小越先执行，默认 100</param>
        /// <param name="channel">订阅通道，为 null 时接收所有通道的事件</param>
        /// <param name="filter">条件过滤器，返回 true 时才执行订阅方法</param>
        /// <returns>订阅令牌，Dispose 即可取消订阅</returns>
        public SubscriptionToken Subscribe<TEvent>(
            Action<TEvent> handler,
            int priority = 100,
            string? channel = null,
            Func<TEvent, bool>? filter = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            var channels = channel != null ? new[] { channel } : null;
            return SubscribeCore<TEvent>(handler, priority, channels,
                filter != null ? e => filter((TEvent)e) : null, isAsync: false, isWeak: false, owner: null);
        }

        /// <summary>
        /// 订阅一个同步事件（多通道）。发布到任意一个匹配通道时触发。
        /// </summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        /// <param name="handler">事件处理方法</param>
        /// <param name="channels">订阅的通道列表，为 null 或空时接收所有通道的事件</param>
        /// <param name="priority">优先级，越小越先执行，默认 100</param>
        /// <param name="filter">条件过滤器，返回 true 时才执行订阅方法</param>
        public SubscriptionToken Subscribe<TEvent>(
            Action<TEvent> handler,
            string[]? channels,
            int priority = 100,
            Func<TEvent, bool>? filter = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            return SubscribeCore<TEvent>(handler, priority, channels?.Length > 0 ? channels : null,
                filter != null ? e => filter((TEvent)e) : null, isAsync: false, isWeak: false, owner: null);
        }

        /// <summary>
        /// 订阅一个异步事件（单通道）。
        /// </summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        /// <param name="asyncHandler">异步事件处理方法</param>
        /// <param name="priority">优先级，越小越先执行，默认 100</param>
        /// <param name="channel">订阅通道，为 null 时接收所有通道的事件</param>
        /// <param name="filter">条件过滤器，返回 true 时才执行订阅方法</param>
        /// <returns>订阅令牌，Dispose 即可取消订阅</returns>
        public SubscriptionToken Subscribe<TEvent>(
            Func<TEvent, Task> asyncHandler,
            int priority = 100,
            string? channel = null,
            Func<TEvent, bool>? filter = null)
        {
            if (asyncHandler == null) throw new ArgumentNullException(nameof(asyncHandler));

            var channels = channel != null ? new[] { channel } : null;
            return SubscribeCore<TEvent>(asyncHandler, priority, channels,
                filter != null ? e => filter((TEvent)e) : null, isAsync: true, isWeak: false, owner: null);
        }

        /// <summary>
        /// 订阅一个异步事件（多通道）。发布到任意一个匹配通道时触发。
        /// </summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        /// <param name="asyncHandler">异步事件处理方法</param>
        /// <param name="channels">订阅的通道列表，为 null 或空时接收所有通道的事件</param>
        /// <param name="priority">优先级，越小越先执行，默认 100</param>
        /// <param name="filter">条件过滤器，返回 true 时才执行订阅方法</param>
        public SubscriptionToken Subscribe<TEvent>(
            Func<TEvent, Task> asyncHandler,
            string[]? channels,
            int priority = 100,
            Func<TEvent, bool>? filter = null)
        {
            if (asyncHandler == null) throw new ArgumentNullException(nameof(asyncHandler));

            return SubscribeCore<TEvent>(asyncHandler, priority, channels?.Length > 0 ? channels : null,
                filter != null ? e => filter((TEvent)e) : null, isAsync: true, isWeak: false, owner: null);
        }

        #endregion

        #region 弱引用订阅 (防内存泄漏)

        /// <summary>
        /// 弱引用订阅一个同步事件（单通道）。
        /// <para>
        /// 与 <see cref="Subscribe{TEvent}(Action{TEvent}, int, string, Func{TEvent, bool})"/> 的区别：
        /// 事件总线<b>只弱持有</b>处理器所属的实例（<c>handler.Target</c>）。
        /// 当该实例被 GC 回收后，这条订阅会在下一次发布或调用 <see cref="PurgeDeadSubscriptions"/> 时自动清理。
        /// </para>
        /// <para>
        /// 典型用途：窗体 / 用户控件订阅<b>静态事件源转发出来的消息</b>（例如全局日志事件）。
        /// 强引用订阅会让总线一直持有已关闭的窗体，造成「关窗不释放」的经典泄漏。
        /// </para>
        /// </summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        /// <param name="handler">
        /// 事件处理方法。请传<b>方法组</b>（如 <c>this.OnLog</c>）而非捕获了 <c>this</c> 的 Lambda：
        /// Lambda 的 Target 是编译器生成的闭包对象，只有委托本身引用它，弱引用下会被立刻回收。
        /// 若确实传了闭包，本库会输出一条诊断提示。
        /// </param>
        /// <param name="priority">优先级，越小越先执行，默认 100</param>
        /// <param name="channel">订阅通道，为 null 时接收所有通道的事件</param>
        /// <param name="filter">条件过滤器，返回 true 时才执行订阅方法</param>
        /// <returns>订阅令牌，Dispose 即可取消订阅</returns>
        public SubscriptionToken SubscribeWeak<TEvent>(
            Action<TEvent> handler,
            int priority = 100,
            string? channel = null,
            Func<TEvent, bool>? filter = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            var channels = channel != null ? new[] { channel } : null;
            return SubscribeCore<TEvent>(handler, priority, channels,
                filter != null ? e => filter((TEvent)e) : null, isAsync: false, isWeak: true, owner: null);
        }

        /// <summary>
        /// 弱引用订阅一个同步事件（多通道）。语义同 <see cref="SubscribeWeak{TEvent}(Action{TEvent}, int, string, Func{TEvent, bool})"/>。
        /// </summary>
        /// <param name="handler">事件处理方法（建议传方法组，原因见对应重载说明）。</param>
        /// <param name="channels">订阅的通道列表，为 null 或空时接收所有通道的事件</param>
        /// <param name="priority">优先级，越小越先执行，默认 100</param>
        /// <param name="filter">条件过滤器，返回 true 时才执行订阅方法</param>
        public SubscriptionToken SubscribeWeak<TEvent>(
            Action<TEvent> handler,
            string[]? channels,
            int priority = 100,
            Func<TEvent, bool>? filter = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            return SubscribeCore<TEvent>(handler, priority, channels?.Length > 0 ? channels : null,
                filter != null ? e => filter((TEvent)e) : null, isAsync: false, isWeak: true, owner: null);
        }

        /// <summary>
        /// 弱引用订阅一个异步事件（单通道）。语义同 <see cref="SubscribeWeak{TEvent}(Action{TEvent}, int, string, Func{TEvent, bool})"/>。
        /// </summary>
        /// <param name="asyncHandler">异步事件处理方法（建议传方法组，原因见对应重载说明）。</param>
        /// <param name="priority">优先级，越小越先执行，默认 100</param>
        /// <param name="channel">订阅通道，为 null 时接收所有通道的事件</param>
        /// <param name="filter">条件过滤器，返回 true 时才执行订阅方法</param>
        public SubscriptionToken SubscribeWeak<TEvent>(
            Func<TEvent, Task> asyncHandler,
            int priority = 100,
            string? channel = null,
            Func<TEvent, bool>? filter = null)
        {
            if (asyncHandler == null) throw new ArgumentNullException(nameof(asyncHandler));

            var channels = channel != null ? new[] { channel } : null;
            return SubscribeCore<TEvent>(asyncHandler, priority, channels,
                filter != null ? e => filter((TEvent)e) : null, isAsync: true, isWeak: true, owner: null);
        }

        /// <summary>
        /// 弱引用订阅一个异步事件（多通道）。语义同 <see cref="SubscribeWeak{TEvent}(Action{TEvent}, int, string, Func{TEvent, bool})"/>。
        /// </summary>
        /// <param name="asyncHandler">异步事件处理方法（建议传方法组，原因见对应重载说明）。</param>
        /// <param name="channels">订阅的通道列表，为 null 或空时接收所有通道的事件</param>
        /// <param name="priority">优先级，越小越先执行，默认 100</param>
        /// <param name="filter">条件过滤器，返回 true 时才执行订阅方法</param>
        public SubscriptionToken SubscribeWeak<TEvent>(
            Func<TEvent, Task> asyncHandler,
            string[]? channels,
            int priority = 100,
            Func<TEvent, bool>? filter = null)
        {
            if (asyncHandler == null) throw new ArgumentNullException(nameof(asyncHandler));

            return SubscribeCore<TEvent>(asyncHandler, priority, channels?.Length > 0 ? channels : null,
                filter != null ? e => filter((TEvent)e) : null, isAsync: true, isWeak: true, owner: null);
        }

        #endregion

        #region 订阅核心

        /// <summary>
        /// 缓存 <see cref="SubscribeCore{TEvent}"/> 的 MethodInfo，
        /// 供 <see cref="EventBusExtensions"/> 在「只有 Type、没有编译期泛型参数」的
        /// 反射注册路径上复用，避免每次订阅都重新查找方法。
        /// </summary>
        internal static readonly MethodInfo SubscribeCoreMethod =
            typeof(EventBus).GetMethod(nameof(SubscribeCore),
                BindingFlags.Instance | BindingFlags.NonPublic)!;

        /// <summary>
        /// 所有订阅路径的统一入口。泛型版本已知 <typeparamref name="TEvent"/>，
        /// 因此可以在此处一次性构造好免反射的调用包装。
        /// </summary>
        internal SubscriptionToken SubscribeCore<TEvent>(
            Delegate handler,
            int priority,
            string[]? channels,
            Func<object, bool>? filter,
            bool isAsync,
            bool isWeak,
            object? owner)
        {
            ThrowIfDisposed();
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            var sub = new Subscription
            {
                Id = NextId(),
                Priority = priority,
                Channels = channels?.Length > 0 ? channels : null,
                Filter = filter,
                Context = _options.MarshalToContext ? SynchronizationContext.Current : null,
                Description = DescribeHandler(handler)
            };

            // 弱引用订阅的前提是处理器绑定到某个实例（handler.Target != null）。
            // 静态方法委托没有 Target，此时「弱引用」无意义且会导致永不触发，退化为强引用。
            bool effectiveWeak = isWeak && handler.Target != null;
            if (isWeak && !effectiveWeak)
            {
                ReportDiagnostic(
                    $"[EventBus] 弱引用订阅退化为强引用：处理器 {sub.Description} 是静态方法，没有可回收的目标实例。");
            }

            sub.IsWeak = effectiveWeak;

            if (effectiveWeak)
            {
                var method = handler.Method;
                var delegateType = handler.GetType();

                // 只捕获 MethodInfo / 委托类型 / WeakReference，
                // 绝不捕获 delegate 本身 —— 否则它会反向强引用 target，弱引用就失效了。
                if (isAsync)
                {
                    sub.Invoker = e =>
                    {
                        var t = sub.TargetRef?.Target;
                        if (t == null) return null;
                        var h = (Func<TEvent, Task>)method.CreateDelegate(delegateType, t);
                        return h((TEvent)e);
                    };
                }
                else
                {
                    sub.Invoker = e =>
                    {
                        var t = sub.TargetRef?.Target;
                        if (t == null) return null;
                        var h = (Action<TEvent>)method.CreateDelegate(delegateType, t);
                        h((TEvent)e);
                        return null;
                    };
                }

                sub.TargetRef = new WeakReference(handler.Target);

                WarnIfClosure(handler.Target!.GetType(), sub.Description);
            }
            else if (isAsync)
            {
                // 预编译调用包装，发布路径不走反射调用。
                var h = (Func<TEvent, Task>)handler;
                sub.Invoker = e => h((TEvent)e);
            }
            else
            {
                var h = (Action<TEvent>)handler;
                sub.Invoker = e => { h((TEvent)e); return null; };
            }

            var eventType = typeof(TEvent);
            lock (_lock)
            {
                var list = _subscriptions.GetOrAdd(eventType, _ => new List<Subscription>());
                list.Add(sub);

                // 建立 id → 类型的索引，让 Unsubscribe 不必全局扫描。
                _subIndex[sub.Id] = eventType;

                if (owner != null)
                {
                    var ids = _ownerIndex.GetOrCreateValue(owner);
                    ids.Ids.Add(sub.Id);
                }
            }

            // 粘性事件：新订阅者立刻收到该类型最后一次发布的粘性事件
            if (_stickyEvents.TryGetValue(eventType, out var sticky))
            {
                if (AnyChannelMatches(sub.Channels, sticky.Channel)
                    && PassesFilter(filter, sticky.Event))
                {
                    InvokeOne(sub, sticky.Event);
                }
            }

            return new SubscriptionToken(this, sub.Id);
        }

        private static string DescribeHandler(Delegate handler)
        {
            var m = handler.Method;
            return $"{m.DeclaringType?.FullName}.{m.Name}";
        }

        /// <summary>
        /// 弱引用订阅 + 闭包是一个隐蔽陷阱：Lambda 的 Target 是编译器生成的
        /// 闭包对象，通常只有委托自己引用它；一旦总线不强持委托，闭包会在下次 GC 时被回收，
        /// 表现就是「订阅神秘失效」。这里给出明确提示。
        /// </summary>
        private void WarnIfClosure(Type targetType, string description)
        {
            if (targetType.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                ReportDiagnostic(
                    $"[EventBus] 弱引用订阅的目标是编译器生成的闭包类型（{targetType.Name}），" +
                    $"它通常只被委托本身引用，可能在下次 GC 后立即失效。{description} " +
                    "建议改用方法组订阅（如 SubscribeWeak(this.OnXxx)）或改用强引用 Subscribe。");
            }
        }

        #endregion

        #region 取消订阅

        /// <summary>根据令牌 ID 取消订阅。通常不直接调用，由 SubscriptionToken.Dispose() 触发。</summary>
        /// <param name="id">订阅 ID（由 <see cref="SubscriptionToken"/> 内部持有）。</param>
        public void Unsubscribe(long id)
        {
            lock (_lock)
            {
                RemoveByIdLocked(id);
            }
        }

        /// <summary>取消指定事件类型的所有订阅。</summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        public void UnsubscribeAll<TEvent>()
        {
            var eventType = typeof(TEvent);
            lock (_lock)
            {
                if (!_subscriptions.TryRemove(eventType, out var list))
                    return;

                for (int i = 0; i < list.Count; i++)
                    _subIndex.TryRemove(list[i].Id, out _);
            }
        }

        /// <summary>
        /// 注销某个实例名下的所有订阅（由 <c>Register</c> / <c>RegisterWeak</c> 登记）。
        /// 适合不便长期保管 <see cref="SubscriptionToken"/> 的场景。
        /// </summary>
        /// <param name="instance">此前调用 Register / RegisterWeak 传入的实例。</param>
        /// <returns>被移除的订阅条数。</returns>
        public int Unregister(object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            lock (_lock)
            {
                OwnerIds? ids;
                if (!_ownerIndex.TryGetValue(instance, out ids))
                    return 0;

                int removed = 0;
                for (int i = 0; i < ids.Ids.Count; i++)
                {
                    if (RemoveByIdLocked(ids.Ids[i]))
                        removed++;
                }

                ids.Ids.Clear();
                _ownerIndex.Remove(instance);
                return removed;
            }
        }

        /// <summary>清空所有订阅和粘性事件。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _subscriptions.Clear();
                _subIndex.Clear();
                _stickyOrder.Clear();
                _stickyEvents.Clear();
            }
        }

        /// <summary>在已进入 <see cref="_lock"/> 的前提下按 ID 移除一条订阅。</summary>
        private bool RemoveByIdLocked(long id)
        {
            Type? eventType;
            if (!_subIndex.TryRemove(id, out eventType) || eventType == null)
                return false;

            List<Subscription>? list;
            if (_subscriptions.TryGetValue(eventType, out list))
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Id == id)
                    {
                        list.RemoveAt(i);
                        break;
                    }
                }

                if (list.Count == 0)
                    _subscriptions.TryRemove(eventType, out _);
            }

            return true;
        }

        /// <summary>移除一条已失效的弱引用订阅（无需调用方持有 token）。</summary>
        private void RemoveDead(Subscription sub)
        {
            lock (_lock)
            {
                RemoveByIdLocked(sub.Id);
            }
        }

        #endregion

        #region 发布

        /// <summary>
        /// 同步发布事件到指定通道。遍历所有匹配的订阅者并依次调用其订阅方法。
        /// <para>
        /// <b>异步订阅者语义</b>：本方法<b>不等待</b>返回 <see cref="Task"/> 的处理器
        /// （默认 <see cref="AsyncHandlerStrategy.FireAndForget"/>），其异常经
        /// <see cref="HandlerError"/> 上报。需要等待请用 <see cref="PublishAsync{TEvent}"/>。
        /// </para>
        /// </summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        /// <param name="event">事件对象，不可为 null</param>
        /// <param name="channel">发布通道；为 null 表示不携带通道信息</param>
        public void Publish<TEvent>(TEvent @event, string? channel = null)
        {
            if (@event == null) throw new ArgumentNullException(nameof(@event));
            ThrowIfDisposed();

            var subs = GetOrderedSubscriptions(typeof(TEvent), channel, @event);
            for (int i = 0; i < subs.Count; i++)
                InvokeOne(subs[i], @event);
        }

        /// <summary>
        /// 异步发布事件到指定通道。并行等待所有异步订阅者完成，同步订阅者同步执行。
        /// 订阅者之间异常隔离，单个失败不影响其他。
        /// </summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        /// <param name="event">事件对象，不可为 null</param>
        /// <param name="channel">发布通道；为 null 表示不携带通道信息</param>
        public async Task PublishAsync<TEvent>(TEvent @event, string? channel = null)
        {
            if (@event == null) throw new ArgumentNullException(nameof(@event));
            ThrowIfDisposed();

            var subs = GetOrderedSubscriptions(typeof(TEvent), channel, @event);
            var tasks = new List<Task>(subs.Count);

            for (int i = 0; i < subs.Count; i++)
            {
                var sub = subs[i];
                if (!sub.IsAlive) { RemoveDead(sub); continue; }

                var ctx = sub.Context;
                try
                {
                    if (ctx != null && !ReferenceEquals(ctx, SynchronizationContext.Current))
                    {
                        // 需要封送：Post 到订阅时捕获的上下文，
                        // 并用 TaskCompletionSource 把完成信号桥接回 await。
                        var tcs = new TaskCompletionSource<object?>();
                        ctx.Post(_ =>
                        {
                            try
                            {
                                var t = sub.Invoke(@event!);
                                if (t == null) tcs.TrySetResult(null);
                        else Bridge(t, tcs);
                            }
                            catch (Exception ex) { tcs.TrySetException(ex); }
                        }, null);
                        tasks.Add(tcs.Task);
                    }
                    else
                    {
                        var t = sub.Invoke(@event!);
                        if (t != null) tasks.Add(t);
                    }
                }
                catch (Exception ex)
                {
                    OnHandlerError(@event!, ex);
                }
            }

            if (tasks.Count == 0) return;

            // 说明：await Task.WhenAll(...) 只会重新抛出第一条异常，
            // 因此这里先取到 WhenAll 的 Task，再从它的 Exception 里逐条上报，保证不漏。
            var whenAll = Task.WhenAll(tasks);
            try
            {
                await whenAll.ConfigureAwait(false);
            }
            catch
            {
                var error = whenAll.Exception;
                if (error != null)
                {
                    foreach (var inner in error.InnerExceptions)
                        OnHandlerError(@event!, inner);
                }
            }
        }

        /// <summary>
        /// 发布粘性事件。后续新订阅者将立即收到该事件（以事件类型为键，仅保留最后一次）。
        /// 当前已存在的订阅者同样会收到此事件。
        /// </summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        /// <param name="event">事件对象，不可为 null</param>
        /// <param name="channel">发布通道；为 null 表示不携带通道信息</param>
        public void PublishSticky<TEvent>(TEvent @event, string? channel = null)
        {
            if (@event == null) throw new ArgumentNullException(nameof(@event));
            ThrowIfDisposed();

            var eventType = typeof(TEvent);
            int max = _options.MaxStickyEvents < 1 ? 1 : _options.MaxStickyEvents;

            lock (_lock)
            {
                // 维护写入顺序，超出上限时淘汰最早的一条
                if (_stickyOrder.Contains(eventType))
                    _stickyOrder.Remove(eventType);
                _stickyOrder.AddLast(eventType);

                while (_stickyOrder.Count > max)
                {
                    var oldest = _stickyOrder.First;
                    if (oldest == null) break;
                    _stickyOrder.RemoveFirst();
                    _stickyEvents.TryRemove(oldest.Value, out _);
                }
            }

            _stickyEvents[eventType] = (@event!, channel);
            Publish(@event, channel);
        }

        /// <summary>清除指定类型的粘性事件缓存。</summary>
        /// <typeparam name="TEvent">事件类型</typeparam>
        public void ClearSticky<TEvent>()
        {
            var eventType = typeof(TEvent);
            lock (_lock)
            {
                _stickyOrder.Remove(eventType);
            }
            _stickyEvents.TryRemove(eventType, out _);
        }

        /// <summary>清除全部粘性事件缓存。</summary>
        /// <returns>被清除的条数。</returns>
        public int ClearAllSticky()
        {
            lock (_lock)
            {
                int count = _stickyOrder.Count;
                _stickyOrder.Clear();
                _stickyEvents.Clear();
                return count;
            }
        }

        #endregion

        #region 诊断

        /// <summary>
        /// 获取当前订阅快照（按事件类型聚合），用于排查订阅泄漏。
        /// <c>Dead &gt; 0</c> 表示存在弱引用目标已回收但尚未清理的订阅，
        /// 它们会在下一次发布或调用 <see cref="PurgeDeadSubscriptions"/> 时被移除。
        /// </summary>
        public IReadOnlyList<SubscriptionSnapshot> GetSnapshot()
        {
            var result = new List<SubscriptionSnapshot>();
            lock (_lock)
            {
                foreach (var kv in _subscriptions)
                {
                    int total = kv.Value.Count;
                    int weak = 0, dead = 0;

                    for (int i = 0; i < kv.Value.Count; i++)
                    {
                        var s = kv.Value[i];
                        if (s.IsWeak)
                        {
                            weak++;
                            if (!s.IsAlive) dead++;
                        }
                    }

                    result.Add(new SubscriptionSnapshot(kv.Key, total, weak, dead));
                }
            }
            return result;
        }

        /// <summary>
        /// 立即清理所有已失效的弱引用订阅。
        /// </summary>
        /// <returns>被清理的订阅条数。</returns>
        public int PurgeDeadSubscriptions()
        {
            int removed = 0;
            lock (_lock)
            {
                foreach (var kv in _subscriptions)
                {
                    var list = kv.Value;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i].IsWeak && !list[i].IsAlive)
                        {
                            _subIndex.TryRemove(list[i].Id, out _);
                            list.RemoveAt(i);
                            removed++;
                        }
                    }
                }

                // 回收空桶，避免长期运行后残留大量空列表
                var empties = _subscriptions.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
                for (int i = 0; i < empties.Count; i++)
                    _subscriptions.TryRemove(empties[i], out _);
            }
            return removed;
        }

        /// <summary>输出一条诊断信息（未配置 <see cref="EventBusOptions.Diagnostics"/> 时静默忽略）。</summary>
        internal void ReportDiagnostic(string message)
        {
            var sink = _options.Diagnostics;
            if (sink == null) return;
            try { sink(message); }
            catch { /* 诊断回调自身的异常绝不能影响主流程 */ }
        }

        #endregion

        #region 内部方法

        private List<Subscription> GetOrderedSubscriptions(Type eventType, string? publishChannel, object @event)
        {
            List<Subscription> source;
            lock (_lock)
            {
                List<Subscription>? list;
                if (!_subscriptions.TryGetValue(eventType, out list))
                    return new List<Subscription>(0);
                source = new List<Subscription>(list);
            }

            return source
                .Where(s => AnyChannelMatches(s.Channels, publishChannel)
                         && PassesFilter(s.Filter, @event))
                .OrderBy(s => s.Priority)
                .ToList();
        }

        /// <summary>
        /// 执行一条订阅：存活检查 → 上下文封送 → 执行 → 异常隔离。
        /// </summary>
        private void InvokeOne(Subscription sub, object @event)
        {
            if (!sub.IsAlive)
            {
                RemoveDead(sub);
                return;
            }

            var ctx = sub.Context;
            if (ctx != null && !ReferenceEquals(ctx, SynchronizationContext.Current))
            {
                // 订阅发生在某个同步上下文（典型：WinForms/WPF 的 UI 线程），
                // 而发布来自另一个线程 —— 把执行 Post 回订阅时的上下文。
                // 用 Post（异步投递）而非 Send（阻塞等待），避免同步 Publish 在 UI 线程上自锁。
                try
                {
                    ctx.Post(state => Execute(sub, state!), @event);
                }
                catch (Exception ex)
                {
                    OnHandlerError(@event, ex);
                }
            }
            else
            {
                Execute(sub, @event);
            }
        }

        private void Execute(Subscription sub, object @event)
        {
            try
            {
                var task = sub.Invoke(@event);
                if (task != null)
                    HandleAsyncResult(task, @event);
            }
            catch (Exception ex)
            {
                OnHandlerError(@event, ex);
            }
        }

        /// <summary>
        /// 同步 <c>Publish</c> 遇到异步订阅者时的处理策略。
        /// <para>
        /// 阻塞等待（<c>task.GetAwaiter().GetResult()</c>）在带 <see cref="SynchronizationContext"/>
        /// 的线程上是危险的：若处理器内部 <c>await</c> 之后需要回到该上下文，而上下文正被阻塞，
        /// <c>await</c> 将永不完成 —— 死锁。因此默认不等待。
        /// 因此默认改为 fire-and-forget，异常经 <see cref="HandlerError"/> 上报。
        /// 需要等待语义请用 <see cref="PublishAsync{TEvent}"/>，或把
        /// <see cref="EventBusOptions.AsyncHandlerStrategy"/> 设为
        /// <see cref="AsyncHandlerStrategy.Block"/>（仅限无同步上下文的线程）。
        /// </para>
        /// </summary>
        private void HandleAsyncResult(Task task, object @event)
        {
            if (_options.AsyncHandlerStrategy == AsyncHandlerStrategy.Block)
            {
                try { task.GetAwaiter().GetResult(); }
                catch (Exception ex) { OnHandlerError(@event, ex); }
                return;
            }

            _ = task.ContinueWith(t =>
            {
                var error = t.Exception;
                if (error == null) return;
                foreach (var inner in error.InnerExceptions)
                    OnHandlerError(@event, inner);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>把子任务的结果桥接到 <see cref="TaskCompletionSource{TResult}"/>。</summary>
        private static void Bridge(Task task, TaskCompletionSource<object?> tcs)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                else if (t.IsCanceled) tcs.TrySetCanceled();
                else tcs.TrySetResult(null);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>
        /// 判断订阅方的通道列表与发布方的通道是否匹配。
        /// 订阅方通道为空（null 或长度 0）= 监听所有通道，始终返回 true。
        /// 发布方通道为空 = 不携带通道信息，不触发通道过滤订阅（返回 false）。
        /// </summary>
        private static bool AnyChannelMatches(string[]? subscribedChannels, string? publishedChannel)
        {
            if (subscribedChannels == null || subscribedChannels.Length == 0)
                return true;

            if (string.IsNullOrEmpty(publishedChannel))
                return false;

            for (int i = 0; i < subscribedChannels.Length; i++)
            {
                if (string.Equals(subscribedChannels[i], publishedChannel, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool PassesFilter(Func<object, bool>? filter, object @event)
            => filter == null || filter(@event);

        /// <summary>
        /// 通知外部「有处理器抛出了异常」。
        /// <para>
        /// 本方法必须吞掉一切异常：错误处理器自身再抛异常时，会穿透 <c>Publish</c> 的
        /// <c>catch</c> 块向外冒，中断后续订阅者的执行，破坏本库承诺的异常隔离。
        /// </para>
        /// </summary>
        private void OnHandlerError(object @event, Exception ex)
        {
            var handler = HandlerError;
            if (handler == null) return;

            try
            {
                handler(this, new EventHandlerErrorEventArgs(@event, ex));
            }
            catch (Exception handlerEx)
            {
                ReportDiagnostic(
                    $"[EventBus] HandlerError 处理器自身抛出异常，已忽略: " +
                    $"{handlerEx.GetType().Name}: {handlerEx.Message}");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EventBus));
        }

        #endregion

        #region IDisposable

        /// <summary>清空所有订阅和粘性事件，释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }

        #endregion
    }
}
