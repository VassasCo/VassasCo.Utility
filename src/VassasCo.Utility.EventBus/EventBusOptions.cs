// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace VassasCo.Utility
{
    /// <summary>
    /// 同步 <see cref="EventBus.Publish{TEvent}"/> 遇到异步订阅者（处理器返回 <see cref="System.Threading.Tasks.Task"/>）时的执行策略。
    /// </summary>
    public enum AsyncHandlerStrategy
    {
        /// <summary>
        /// 触发后不等待（默认）。异步订阅者在后台执行，异常通过 <see cref="EventBus.HandlerError"/> 上报。
        /// <para>
        /// <b>为什么默认不是阻塞等待</b>：同步 <c>Publish</c> 若用 <c>task.GetAwaiter().GetResult()</c>
        /// 阻塞等待，而当前线程带有 <see cref="SynchronizationContext"/>（典型是 WinForms/WPF 的 UI 线程），
        /// 且异步处理器内部 <c>await</c> 之后需要回到该上下文 —— 则上下文被阻塞、<c>await</c>
        /// 永远无法完成，<b>必然死锁</b>。
        /// </para>
        /// <para>需要「发布后等待所有异步订阅者完成」的语义时，请用 <see cref="EventBus.PublishAsync{TEvent}"/>。</para>
        /// </summary>
        FireAndForget = 0,

        /// <summary>
        /// 阻塞等待异步订阅者完成。
        /// 仅应在确认无 <see cref="SynchronizationContext"/> 的线程（线程池 / 控制台 / 后台服务）上使用，
        /// 在 UI 线程上存在死锁风险。
        /// </summary>
        Block = 1
    }

    /// <summary>
    /// <see cref="EventBus"/> 行为配置。不传时使用默认值（见各属性说明）。
    /// </summary>
    public sealed class EventBusOptions
    {
        /// <summary>
        /// 是否在订阅时捕获 <see cref="SynchronizationContext.Current"/>，
        /// 并在发布时把处理器封送回该上下文执行。默认 <c>true</c>。
        /// <para>
        /// 开启后：UI 线程上订阅、后台线程（设备轮询 / PLC 扫描）上发布，
        /// 处理器会自动回到 UI 线程执行，调用方无需手写 <c>InvokeRequired</c> / <c>Dispatcher.Invoke</c>。
        /// </para>
        /// <para>
        /// 若发布与订阅在同一上下文，则直接同步执行，不会引入额外调度开销。
        /// 在线程池/控制台等无上下文的线程上订阅时，捕获结果为 <c>null</c>，等同于不封送。
        /// </para>
        /// </summary>
        public bool MarshalToContext { get; set; } = true;

        /// <summary>
        /// 同步 <c>Publish</c> 遇到异步订阅者时的执行策略，
        /// 默认 <see cref="AsyncHandlerStrategy.FireAndForget"/>（详见枚举说明）。
        /// </summary>
        public AsyncHandlerStrategy AsyncHandlerStrategy { get; set; } = AsyncHandlerStrategy.FireAndForget;

        /// <summary>
        /// 粘性事件缓存的最大条数（按事件类型计）。超过时淘汰最早写入的一条。默认 64，最小 1。
        /// 不设上限时，长时间运行的进程会让缓存单调增长，故保留此约束。
        /// </summary>
        public int MaxStickyEvents { get; set; } = 64;

        /// <summary>
        /// 诊断回调。订阅方法签名不合规、弱引用目标已被回收等<b>不抛异常但值得关注</b>的情况经此上报。
        /// 为 <c>null</c> 时静默忽略。
        /// </summary>
        public Action<string>? Diagnostics { get; set; }
    }
}
