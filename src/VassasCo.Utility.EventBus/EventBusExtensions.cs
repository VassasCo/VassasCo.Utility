// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace VassasCo.Utility
{
    /// <summary>
    /// EventBus 扩展方法 — 特性自动注册、按实例注销、聚合令牌。
    /// </summary>
    public static class EventBusExtensions
    {
        /// <summary>
        /// 扫描实例上所有标注了 [EventSubscribe] 的方法，自动订阅到事件总线（强引用）。
        /// 方法可以是 public / private、实例 / 静态，只要有一个事件参数且返回 void 或 Task 即可。
        /// 返回一个聚合令牌，Dispose 时一次性取消该实例注册的所有订阅。
        /// <para>
        /// Register 本质是批量版 Subscribe：遍历实例上的方法，找到打了 [EventSubscribe] 的，
        /// 为每个方法构造委托并调用底层 SubscribeCore，最后把所有 SubscriptionToken
        /// 包进 CompositeDisposable。调用 token.Dispose() 即一次性全部解绑。
        /// </para>
        /// <para>
        /// <b>注意</b>：强引用订阅会让事件总线持有该实例。若实例是反复创建销毁的窗体/控件，
        /// 且事件源生命周期与进程等长，请改用 <see cref="RegisterWeak(EventBus, object)"/>。
        /// </para>
        /// <code>
        /// var service = new OrderService();
        /// var token = EventBus.Default.Register(service);
        /// // ... 业务运行 ...
        /// token.Dispose(); // 一键解绑 OrderService 上的所有订阅
        /// </code>
        /// </summary>
        /// <param name="bus">事件总线实例。</param>
        /// <param name="instance">待扫描的实例。</param>
        /// <returns>聚合令牌；Dispose 时取消本实例注册的所有订阅。</returns>
        public static IDisposable Register(this EventBus bus, object instance)
            => RegisterCore(bus, instance, weak: false);

        /// <summary>
        /// 扫描实例上所有标注了 [EventSubscribe] 的方法，以<b>弱引用</b>方式自动订阅。
        /// 实例被 GC 回收后，这些订阅会在下一次发布或 <see cref="EventBus.PurgeDeadSubscriptions"/> 时自动清理。
        /// <para>
        /// 适合「短命订阅者」订阅「长命事件源」的场景，例如反复打开关闭的窗体订阅全局日志事件。
        /// 返回令牌仍可用于提前主动解绑。
        /// </para>
        /// <code>
        /// // 在窗体构造函数中：
        /// _token = EventBus.Default.RegisterWeak(this);
        /// // 窗体 Dispose 中：
        /// _token.Dispose();
        /// </code>
        /// </summary>
        /// <param name="bus">事件总线实例。</param>
        /// <param name="instance">待扫描的实例。</param>
        /// <returns>聚合令牌；Dispose 时取消本实例注册的所有订阅。</returns>
        public static IDisposable RegisterWeak(this EventBus bus, object instance)
            => RegisterCore(bus, instance, weak: true);

        /// <summary>
        /// 注销此前由 <see cref="Register"/> 或 <see cref="RegisterWeak"/> 登记的全部订阅。
        /// 与聚合令牌 Dispose 等价，适用于不便长期保管令牌的场景。
        /// </summary>
        /// <param name="bus">事件总线实例。</param>
        /// <param name="instance">此前调用 Register / RegisterWeak 传入的实例。</param>
        /// <returns>被移除的订阅条数。</returns>
        public static int UnregisterInstance(this EventBus bus, object instance)
            => bus.Unregister(instance);

        private static IDisposable RegisterCore(EventBus bus, object instance, bool weak)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            var tokens = new List<SubscriptionToken>();
            var instanceType = instance.GetType();

            var methods = instanceType.GetMethods(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                if (method.IsSpecialName) continue;   // 跳过属性访问器等编译器生成方法

                var attrs = (EventSubscribeAttribute[])method.GetCustomAttributes(
                    typeof(EventSubscribeAttribute), inherit: false);
                if (attrs.Length == 0) continue;

                foreach (var attr in attrs)
                {
                    var token = RegisterMethod(bus, instance, method, attr, weak);
                    if (token != null)
                        tokens.Add(token);
                }
            }

            return new CompositeDisposable(tokens);
        }

        private static SubscriptionToken? RegisterMethod(
            EventBus bus, object instance, MethodInfo method,
            EventSubscribeAttribute attr, bool weak)
        {
            // RegisterWeak() 与 [EventSubscribe(Weak = true)] 任一为真即走弱引用订阅，
            // 避免特性上标了 Weak 却被 Register() 的实参覆盖。
            bool useWeak = weak || attr.Weak;

            // 签名不合规时静默跳过会让调用方无从排查，故经诊断通道上报。
            if (method.ContainsGenericParameters)
            {
                bus.ReportDiagnostic(
                    $"[EventBus] 跳过订阅：{instance.GetType().FullName}.{method.Name} 是泛型方法，无法自动构造订阅委托。");
                return null;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 1)
            {
                bus.ReportDiagnostic(
                    $"[EventBus] 跳过订阅：{instance.GetType().FullName}.{method.Name} 标注了 [EventSubscribe]，" +
                    $"但参数个数为 {parameters.Length}（要求恰好 1 个事件参数）。");
                return null;
            }

            var eventType = parameters[0].ParameterType;

            bool isAsync;
            if (method.ReturnType == typeof(Task))
                isAsync = true;
            else if (method.ReturnType == typeof(void))
                isAsync = false;
            else
            {
                bus.ReportDiagnostic(
                    $"[EventBus] 跳过订阅：{instance.GetType().FullName}.{method.Name} 标注了 [EventSubscribe]，" +
                    $"但返回值类型为 {method.ReturnType.Name}（要求 void 或 Task）。");
                return null;
            }

            // 构造委托：同步 → Action<TEvent>，异步 → Func<TEvent, Task>
            Delegate handler;
            try
            {
                var delegateType = isAsync
                    ? typeof(Func<,>).MakeGenericType(eventType, typeof(Task))
                    : typeof(Action<>).MakeGenericType(eventType);
                object? target = method.IsStatic ? null : instance;
                handler = Delegate.CreateDelegate(delegateType, target, method);
            }
            catch (Exception ex)
            {
                bus.ReportDiagnostic(
                    $"[EventBus] 跳过订阅：为 {instance.GetType().FullName}.{method.Name} 构造委托失败 —— " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }

            // 构造过滤器（保持为 Func<object, bool>）
            var filter = BuildFilter(bus, instance, attr, eventType);

            return CallSubscribeCore(bus, eventType, handler, attr.Priority,
                attr.Channels, filter, isAsync, useWeak, instance);
        }

        /// <summary>
        /// 通过缓存的 MethodInfo 调用泛型入口 <see cref="EventBus.SubscribeCore{TEvent}"/>。
        /// 反射查找只在类型初始化时做一次；每个订阅仅是一次 MakeGenericMethod + Invoke。
        /// </summary>
        private static SubscriptionToken CallSubscribeCore(
            EventBus bus, Type eventType, Delegate handler, int priority,
            string[]? channels, Func<object, bool>? filter, bool isAsync, bool weak, object? owner)
        {
            return (SubscriptionToken)EventBus.SubscribeCoreMethod
                .MakeGenericMethod(eventType)
                .Invoke(bus, new object?[] { handler, priority, channels, filter, isAsync, weak, owner })!;
        }

        private static readonly MethodInfo BuildFilterMethod =
            typeof(EventBusExtensions).GetMethod(nameof(BuildFilterCore),
                BindingFlags.Static | BindingFlags.NonPublic)!;

        /// <summary>
        /// 在泛型上下文中构造一次强类型过滤包装，发布路径上零反射调用。
        /// </summary>
        private static Func<object, bool>? BuildFilter(
            EventBus bus, object instance, EventSubscribeAttribute attr, Type eventType)
        {
            return (Func<object, bool>?)BuildFilterMethod
                .MakeGenericMethod(eventType)
                .Invoke(null, new object?[] { bus, instance, attr });
        }

        private static Func<object, bool>? BuildFilterCore<TEvent>(
            EventBus bus, object instance, EventSubscribeAttribute attr)
        {
            var instanceType = instance.GetType();

            // 优先用 FilterMethod —— 指向实例上的自定义判断方法
            if (!string.IsNullOrEmpty(attr.FilterMethod))
            {
                var filterMethod = instanceType.GetMethod(attr.FilterMethod!,
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic);

                if (filterMethod == null)
                {
                    bus.ReportDiagnostic(
                        $"[EventBus] 未找到 [EventSubscribe(FilterMethod = \"{attr.FilterMethod}\")] " +
                        $"指向的过滤方法：{instanceType.FullName}.{attr.FilterMethod} 不存在。" +
                        "该订阅将不带过滤器运行（等价于接收全部事件）。");
                    return null;
                }

                var ps = filterMethod.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(TEvent) || filterMethod.ReturnType != typeof(bool))
                {
                    bus.ReportDiagnostic(
                        $"[EventBus] 过滤方法 {instanceType.FullName}.{filterMethod.Name} 签名不合规，" +
                        $"要求 bool {filterMethod.Name}({typeof(TEvent).Name} e)。" +
                        "该订阅将不带过滤器运行（等价于接收全部事件）。");
                    return null;
                }

                object? target = filterMethod.IsStatic ? null : instance;
                var typed = (Func<TEvent, bool>)Delegate.CreateDelegate(
                    typeof(Func<TEvent, bool>), target, filterMethod);

                return e => typed((TEvent)e);
            }

            // 其次用 FilterProperty + 范围判断
            if (!string.IsNullOrEmpty(attr.FilterProperty) &&
                (attr.FilterMin > double.MinValue || attr.FilterMax < double.MaxValue))
            {
                var prop = typeof(TEvent).GetProperty(attr.FilterProperty!,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (prop == null)
                {
                    bus.ReportDiagnostic(
                        $"[EventBus] 未找到过滤属性：{typeof(TEvent).FullName}.{attr.FilterProperty} 不存在。" +
                        "该订阅将不带过滤器运行（等价于接收全部事件）。");
                    return null;
                }

                double min = attr.FilterMin, max = attr.FilterMax;

                return e =>
                {
                    var value = prop.GetValue(e);
                    if (value == null) return false;
                    try
                    {
                        double numeric = Convert.ToDouble(value);
                        return numeric >= min && numeric <= max;
                    }
                    catch
                    {
                        return false;
                    }
                };
            }

            return null;
        }
    }

    /// <summary>
    /// 聚合订阅令牌，Dispose 时一次性取消内部所有订阅。
    /// </summary>
    internal sealed class CompositeDisposable : IDisposable
    {
        private readonly List<SubscriptionToken> _tokens;
        private volatile bool _disposed;

        public CompositeDisposable(List<SubscriptionToken> tokens)
        {
            _tokens = tokens ?? new List<SubscriptionToken>();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = 0; i < _tokens.Count; i++)
            {
                try { _tokens[i].Dispose(); }
                catch { /* 单个退订失败不得影响其余订阅的清理 */ }
            }

            _tokens.Clear();
        }
    }
}
