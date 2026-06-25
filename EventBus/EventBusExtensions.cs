using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace VassasCo.Utility
{
    /// <summary>
    /// EventBus 扩展方法 — 特性自动注册、聚合令牌、便捷订阅。
    /// </summary>
    public static class EventBusExtensions
    {
        /// <summary>
        /// 扫描实例上所有标注了 [EventSubscribe] 的方法，自动订阅到事件总线。
        /// 方法可以是 public / private、实例 / 静态，只要有一个事件参数且返回 void 或 Task 即可。
        /// 返回一个聚合令牌，Dispose 时一次性取消该实例注册的所有订阅。
        /// <para>
        /// Register 本质是批量版 Subscribe：遍历实例上的方法，找到打了 [EventSubscribe] 的，
        /// 为每个方法构造委托并调用底层 SubscribeInternal，最后把所有 SubscriptionToken
        /// 包进 CompositeDisposable。调用 token.Dispose() 即一次性全部解绑。
        /// </para>
        /// <code>
        /// var service = new OrderService();
        /// var token = EventBus.Default.Register(service);
        /// // ... 业务运行 ...
        /// token.Dispose(); // 一键解绑 OrderService 上的所有订阅
        /// </code>
        /// </summary>
        public static IDisposable Register(this EventBus bus, object instance)
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
                var attrs = (EventSubscribeAttribute[])method.GetCustomAttributes(
                    typeof(EventSubscribeAttribute), inherit: false);
                if (attrs.Length == 0) continue;

                foreach (var attr in attrs)
                {
                    SubscriptionToken? token = RegisterMethod(bus, instance, method, attr);
                    if (token != null)
                        tokens.Add(token);
                }
            }

            return new CompositeDisposable(tokens);
        }

        private static SubscriptionToken? RegisterMethod(
            EventBus bus, object instance, MethodInfo method, EventSubscribeAttribute attr)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                return null;

            var eventType = parameters[0].ParameterType;

            bool isAsync;
            if (method.ReturnType == typeof(Task))
                isAsync = true;
            else if (method.ReturnType == typeof(void))
                isAsync = false;
            else
                return null;

            // 构建委托：同步 → Action<TEvent>，异步 → Func<TEvent, Task>
            Delegate handler;
            if (isAsync)
            {
                var delegateType = typeof(Func<,>).MakeGenericType(eventType, typeof(Task));
                object? target = method.IsStatic ? null : instance;
                handler = Delegate.CreateDelegate(delegateType, target, method);
            }
            else
            {
                var delegateType = typeof(Action<>).MakeGenericType(eventType);
                object? target = method.IsStatic ? null : instance;
                handler = Delegate.CreateDelegate(delegateType, target, method);
            }

            // 构建过滤器（保持为 Func<object, bool>，SubscribeInternal 直接接收此类型）
            Func<object, bool>? filter = BuildFilterFromAttribute(instance, instance.GetType(), attr, eventType);

            // 直接调用非泛型 SubscribeInternal，避免值类型事件的 Filter 委托转换崩溃
            return CallSubscribeInternal(bus, eventType, handler, attr.Priority, attr.Channels, filter, isAsync);
        }

        private static SubscriptionToken CallSubscribeInternal(
            EventBus bus, Type eventType, Delegate handler,
            int priority, string[]? channels, Func<object, bool>? filter, bool isAsync)
        {
            var method = typeof(EventBus).GetMethod("SubscribeInternal",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (method == null)
                throw new InvalidOperationException("未找到 SubscribeInternal 方法");

            return (SubscriptionToken)method.Invoke(bus, new object?[]
            {
                eventType, handler, priority, channels, filter, isAsync
            })!;
        }

        private static Func<object, bool>? BuildFilterFromAttribute(
            object instance, Type instanceType, EventSubscribeAttribute attr, Type eventType)
        {
            // 优先用 FilterMethod — 指向实例上的自定义判断方法
            if (!string.IsNullOrEmpty(attr.FilterMethod))
            {
                var filterMethod = instanceType.GetMethod(attr.FilterMethod,
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic);

                if (filterMethod == null) return null;

                var delegateType = typeof(Func<,>).MakeGenericType(eventType, typeof(bool));
                object? target = filterMethod.IsStatic ? null : instance;
                var typedFilter = Delegate.CreateDelegate(delegateType, target, filterMethod);

                // DynamicInvoke 包装 → Func<object, bool>，无论 TEvent 是值类型还是引用类型都能正常工作
                return new Func<object, bool>(e => (bool)typedFilter.DynamicInvoke(e)!);
            }

            // 其次用 FilterProperty + 范围判断
            if (!string.IsNullOrEmpty(attr.FilterProperty) &&
                (attr.FilterMin > double.MinValue || attr.FilterMax < double.MaxValue))
            {
                var prop = eventType.GetProperty(attr.FilterProperty,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null) return null;

                return e =>
                {
                    var value = prop.GetValue(e);
                    if (value == null) return false;
                    try
                    {
                        double numeric = Convert.ToDouble(value);
                        return numeric >= attr.FilterMin && numeric <= attr.FilterMax;
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
            foreach (var token in _tokens)
                token.Dispose();
            _tokens.Clear();
        }
    }
}
