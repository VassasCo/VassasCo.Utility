// SPDX-License-Identifier: MIT

using System;

namespace VassasCo.Utility
{
    /// <summary>
    /// 标记一个方法为事件订阅方法。方法必须恰好接收一个参数（事件类型），
    /// 返回值可为 void（同步处理）或 Task（异步处理）。
    /// 通过 <see cref="EventBusExtensions.Register(EventBus, object)"/> 扫描实例上
    /// 所有标注了此特性的方法并自动订阅。
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
        /// private bool ShouldHandle(OrderCreatedEvent e) =&gt; e.Amount &gt; 1000;
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

        /// <summary>
        /// 是否使用弱引用订阅。默认 <c>false</c>（强引用，订阅者需自行退订）。
        /// <para>
        /// 设为 <c>true</c> 后，事件总线只弱持有处理器所属实例，
        /// 实例被 GC 回收后订阅自动失效并清理。适合<b>生命周期短于进程的订阅者</b>
        /// （反复打开关闭的窗体 / 用户控件）订阅静态事件源转发出来的消息。
        /// </para>
        /// <para>
        /// 注意：必须把特性标注在<b>实例方法</b>上。静态方法没有可回收的目标实例，
        /// 弱引用无意义，本库会自动退化为强引用订阅并输出诊断提示。
        /// </para>
        /// </summary>
        public bool Weak { get; set; }
    }
}
