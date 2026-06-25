using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// 由 Subscribe / Register 返回，持有此令牌即可随时取消对应的订阅。
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
    /// 轻量级进程内事件总线。发布者和订阅者通过事件类型解耦，互不感知。
    /// 支持优先级排序、条件过滤、粘性事件、多通道隔离、异步处理、特性自动注册。
    /// </summary>
    public class EventBus : IDisposable
    {
        #region 内部类型

        private sealed class Subscription
        {
            public long Id;
            public int Priority;
            public Delegate Handler = null!;
            /// <summary>订阅方关注的通道列表。null 或空 = 监听所有通道。</summary>
            public string[]? Channels;
            public Func<object, bool>? Filter;
            public bool IsAsync;
        }

        #endregion

        #region 静态单例

        private static readonly Lazy<EventBus> _default = new Lazy<EventBus>(
            () => new EventBus(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局默认实例，开箱即用。</summary>
        public static EventBus Default => _default.Value;

        #endregion

        private readonly ConcurrentDictionary<Type, List<Subscription>> _subscriptions
            = new ConcurrentDictionary<Type, List<Subscription>>();

        private readonly ConcurrentDictionary<Type, (object Event, string? Channel)> _stickyEvents
            = new ConcurrentDictionary<Type, (object, string?)>();

        private readonly object _lock = new object();
        private long _idCounter;
        private volatile bool _disposed;

        /// <summary>当某个订阅者抛出未捕获异常时触发。订阅此事件可集中记录错误日志。</summary>
        public event EventHandler<EventHandlerErrorEventArgs>? HandlerError;

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
            return AddSubscription(typeof(TEvent), handler, priority, channels,
                filter != null ? e => filter((TEvent)e) : null, isAsync: false);
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

            return AddSubscription(typeof(TEvent), handler, priority,
                channels?.Length > 0 ? channels : null,
                filter != null ? e => filter((TEvent)e) : null, isAsync: false);
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
            return AddSubscription(typeof(TEvent), asyncHandler, priority, channels,
                filter != null ? e => filter((TEvent)e) : null, isAsync: true);
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

            return AddSubscription(typeof(TEvent), asyncHandler, priority,
                channels?.Length > 0 ? channels : null,
                filter != null ? e => filter((TEvent)e) : null, isAsync: true);
        }

        /// <summary>
        /// [内部] 非泛型订阅入口，供 EventBusExtensions 反射调用。
        /// 绕过泛型类型转换，避免值类型事件的 Filter 委托转换崩溃。
        /// </summary>
        internal SubscriptionToken SubscribeInternal(
            Type eventType, Delegate handler, int priority,
            string[]? channels, Func<object, bool>? filter, bool isAsync)
        {
            ThrowIfDisposed();

            var sub = new Subscription
            {
                Id = NextId(),
                Priority = priority,
                Handler = handler,
                Channels = channels?.Length > 0 ? channels : null,
                Filter = filter,
                IsAsync = isAsync
            };

            lock (_lock)
            {
                var list = _subscriptions.GetOrAdd(eventType, _ => new List<Subscription>());
                list.Add(sub);
            }

            // 粘性事件：新订阅者立刻收到该类型最后一次发布的粘性事件
            if (_stickyEvents.TryGetValue(eventType, out var sticky))
            {
                if (AnyChannelMatches(sub.Channels, sticky.Channel)
                    && PassesFilter(filter, sticky.Event))
                {
                    InvokeHandler(sub, sticky.Event);
                }
            }

            return new SubscriptionToken(this, sub.Id);
        }

        private SubscriptionToken AddSubscription(
            Type eventType, Delegate handler, int priority,
            string[]? channels, Func<object, bool>? filter, bool isAsync)
        {
            ThrowIfDisposed();

            var sub = new Subscription
            {
                Id = NextId(),
                Priority = priority,
                Handler = handler,
                Channels = channels,
                Filter = filter,
                IsAsync = isAsync
            };

            lock (_lock)
            {
                var list = _subscriptions.GetOrAdd(eventType, _ => new List<Subscription>());
                list.Add(sub);
            }

            if (_stickyEvents.TryGetValue(eventType, out var sticky))
            {
                if (AnyChannelMatches(sub.Channels, sticky.Channel)
                    && PassesFilter(filter, sticky.Event))
                {
                    InvokeHandler(sub, sticky.Event);
                }
            }

            return new SubscriptionToken(this, sub.Id);
        }

        #endregion

        #region 取消订阅

        /// <summary>根据令牌 ID 取消订阅。通常不直接调用，由 SubscriptionToken.Dispose() 触发。</summary>
        public void Unsubscribe(long id)
        {
            lock (_lock)
            {
                foreach (var kv in _subscriptions)
                {
                    var list = kv.Value;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i].Id == id)
                        {
                            list.RemoveAt(i);
                            if (list.Count == 0)
                                _subscriptions.TryRemove(kv.Key, out _);
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>取消指定事件类型的所有订阅。</summary>
        public void UnsubscribeAll<TEvent>()
        {
            lock (_lock)
            {
                _subscriptions.TryRemove(typeof(TEvent), out _);
            }
        }

        /// <summary>清空所有订阅和粘性事件。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _subscriptions.Clear();
            }
            _stickyEvents.Clear();
        }

        #endregion

        #region 发布

        /// <summary>同步发布事件到指定通道。遍历所有匹配的订阅者并依次调用其订阅方法。</summary>
        public void Publish<TEvent>(TEvent @event, string? channel = null)
        {
            if (@event == null) throw new ArgumentNullException(nameof(@event));
            ThrowIfDisposed();

            var handlers = GetOrderedSubscriptions(typeof(TEvent), channel, @event);
            foreach (var sub in handlers)
            {
                try
                {
                    if (sub.IsAsync)
                        ((Func<TEvent, Task>)sub.Handler)(@event).GetAwaiter().GetResult();
                    else
                        ((Action<TEvent>)sub.Handler)(@event);
                }
                catch (Exception ex)
                {
                    OnHandlerError(@event!, ex);
                }
            }
        }

        /// <summary>
        /// 异步发布事件到指定通道。并行等待所有异步订阅者完成，同步订阅者同步执行。
        /// 订阅者之间异常隔离，单个失败不影响其他。
        /// </summary>
        public async Task PublishAsync<TEvent>(TEvent @event, string? channel = null)
        {
            if (@event == null) throw new ArgumentNullException(nameof(@event));
            ThrowIfDisposed();

            var handlers = GetOrderedSubscriptions(typeof(TEvent), channel, @event);
            var tasks = new List<Task>(handlers.Count);

            foreach (var sub in handlers)
            {
                try
                {
                    if (sub.IsAsync)
                        tasks.Add(((Func<TEvent, Task>)sub.Handler)(@event));
                    else
                        ((Action<TEvent>)sub.Handler)(@event);
                }
                catch (Exception ex)
                {
                    OnHandlerError(@event!, ex);
                }
            }

            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (ex is AggregateException agg)
                    {
                        foreach (var inner in agg.InnerExceptions)
                            OnHandlerError(@event!, inner);
                    }
                    else
                    {
                        OnHandlerError(@event!, ex);
                    }
                }
            }
        }

        /// <summary>
        /// 发布粘性事件。后续新订阅者将立即收到该事件（以事件类型为键，仅保留最后一次）。
        /// 当前已存在的订阅者同样会收到此事件。
        /// </summary>
        public void PublishSticky<TEvent>(TEvent @event, string? channel = null)
        {
            if (@event == null) throw new ArgumentNullException(nameof(@event));
            _stickyEvents[typeof(TEvent)] = (@event, channel);
            Publish(@event, channel);
        }

        /// <summary>清除指定类型的粘性事件缓存。</summary>
        public void ClearSticky<TEvent>()
        {
            _stickyEvents.TryRemove(typeof(TEvent), out _);
        }

        #endregion

        #region 内部方法

        private List<Subscription> GetOrderedSubscriptions(Type eventType, string? publishChannel, object @event)
        {
            List<Subscription> source;
            lock (_lock)
            {
                if (!_subscriptions.TryGetValue(eventType, out var list))
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
        {
            return filter == null || filter(@event);
        }

        private void InvokeHandler(Subscription sub, object @event)
        {
            try
            {
                if (sub.IsAsync)
                {
                    var task = (Task)sub.Handler.DynamicInvoke(@event)!;
                    task?.GetAwaiter().GetResult();
                }
                else
                {
                    sub.Handler.DynamicInvoke(@event);
                }
            }
            catch (Exception ex)
            {
                OnHandlerError(@event, ex is TargetInvocationException tie ? tie.InnerException! : ex);
            }
        }

        private void OnHandlerError(object @event, Exception ex)
        {
            HandlerError?.Invoke(this, new EventHandlerErrorEventArgs(@event, ex));
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
