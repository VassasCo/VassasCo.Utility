using System;
using System.Reflection;

namespace VassasCo.Utility
{
    /// <summary>
    /// 标记一个方法为事件订阅方法。方法必须恰好接收一个参数（事件类型），
    /// 返回值可为 void（同步处理）或 Task（异步处理）。
    /// 通过 EventBus.Register() 扫描实例上所有标注了此特性的方法并自动订阅。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class EventSubscribeAttribute : Attribute
    {
        /// <summary>优先级，数字越小越早执行，默认 100。</summary>
        public int Priority { get; set; } = 100;

        /// <summary>
        /// 订阅通道列表。发布时携带的通道命中列表任意一项即触发此订阅方法。
        /// 为 null 或空时表示不限制通道，任何通道（含无通道的发布）都会触发。
        /// <code>
        /// [EventSubscribe(Channels = new[] { "Payment", "Shipping" })]
        /// </code>
        /// </summary>
        public string[]? Channels { get; set; }

        /// <summary>
        /// 自定义过滤方法名。指向当前实例的一个方法，该方法签名须与订阅方法参数类型一致且返回 bool。
        /// 返回 true 时订阅方法才会执行。可与 Channels 同时使用，互不干扰。
        /// 当此字段有值时，FilterProperty 系列将被忽略。
        /// <code>
        /// [EventSubscribe(FilterMethod = nameof(ShouldHandle), Channels = new[] { "Payment" })]
        /// private void OnOrderCreated(OrderCreatedEvent e) { }
        /// private bool ShouldHandle(OrderCreatedEvent e) => e.Amount > 1000;
        /// </code>
        /// </summary>
        public string? FilterMethod { get; set; }

        /// <summary>
        /// 按属性名做简单值过滤。当 FilterMethod 为 null 时生效。
        /// 搭配 FilterMin / FilterMax 使用，对事件对象上指定属性的值进行范围判断。
        /// </summary>
        public string? FilterProperty { get; set; }

        /// <summary>过滤属性的最小值（含）。与 FilterProperty 配合使用。</summary>
        public double FilterMin { get; set; } = double.MinValue;

        /// <summary>过滤属性的最大值（含）。与 FilterProperty 配合使用。</summary>
        public double FilterMax { get; set; } = double.MaxValue;
    }
}
