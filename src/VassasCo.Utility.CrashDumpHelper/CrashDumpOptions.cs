// SPDX-License-Identifier: MIT

using System;

namespace VassasCo.Utility
{
    /// <summary>
    /// Windows MiniDump 的转储类型（各标志位与 dbghelp.dll 的 MINIDUMP_TYPE 一致）。
    /// </summary>
    [Flags]
    public enum MiniDumpType
    {
        /// <summary>仅基本信息（堆栈、模块列表、线程上下文）。</summary>
        Normal = 0x00000000,

        /// <summary>包含所有已加载模块的数据段。</summary>
        WithDataSegs = 0x00000001,

        /// <summary>包含进程的完整内存。<b>文件体积极大</b>（约等于进程工作集大小）。</summary>
        WithFullMemory = 0x00000002,

        /// <summary>包含句柄信息。</summary>
        WithHandleData = 0x00000004,

        /// <summary>过滤掉不需要的内存。</summary>
        FilterMemory = 0x00000008,

        /// <summary>扫描内存以包含引用的模块。</summary>
        ScanMemory = 0x00000010,

        /// <summary>包含已卸载模块信息。</summary>
        WithUnloadedModules = 0x00000020,

        /// <summary>包含间接引用的内存。</summary>
        WithIndirectlyReferencedMemory = 0x00000040,

        /// <summary>过滤模块路径信息。</summary>
        FilterModulePaths = 0x00000080,

        /// <summary>包含进程与线程信息。</summary>
        WithProcessThreadData = 0x00000100,

        /// <summary>包含私有读写内存页。</summary>
        WithPrivateReadWriteMemory = 0x00000200,

        /// <summary>不包含可选数据。</summary>
        WithoutOptionalData = 0x00000400,

        /// <summary>包含完整内存信息。</summary>
        WithFullMemoryInfo = 0x00000800,

        /// <summary>包含线程信息。</summary>
        WithThreadInfo = 0x00001000,

        /// <summary>包含代码段。</summary>
        WithCodeSegs = 0x00002000,

        /// <summary>不包含辅助状态。</summary>
        WithoutAuxiliaryState = 0x00004000,

        /// <summary>包含完整辅助状态。</summary>
        WithFullAuxiliaryState = 0x00008000,

        /// <summary>包含私有写时复制内存页。</summary>
        WithPrivateWriteCopyMemory = 0x00010000,

        /// <summary>忽略不可访问的内存，避免转储失败。</summary>
        IgnoreInaccessibleMemory = 0x00020000,

        /// <summary>包含令牌信息。</summary>
        WithTokenInformation = 0x00040000,

        /// <summary>
        /// 默认组合：覆盖线程、模块、代码段信息，同时忽略不可访问内存以保证转储成功率，体积可控。
        /// </summary>
        Default =
            WithDataSegs
          | WithIndirectlyReferencedMemory
          | WithThreadInfo
          | WithUnloadedModules
          | WithProcessThreadData
          | WithCodeSegs
          | IgnoreInaccessibleMemory,

        /// <summary>完整内存转储 —— 信息最全，但文件极大，慎用。</summary>
        Full =
            Default
          | WithFullMemory
          | WithHandleData
          | WithFullMemoryInfo
    }

    /// <summary>
    /// <see cref="CrashDumpHelper"/> 行为配置。
        /// </summary>
    public sealed class CrashDumpOptions
    {
        /// <summary>
        /// 崩溃日志与 dump 文件的输出目录。
        /// 为 <c>null</c> 时使用 <c>{程序目录}/CrashLogs</c>。
        /// </summary>
        public string? LogDirectory { get; set; }

        /// <summary>是否生成 Windows MiniDump 文件。默认 <c>true</c>（仅 Windows 平台生效）。</summary>
        public bool GenerateDump { get; set; } = true;

        /// <summary>MiniDump 转储类型，默认 <see cref="MiniDumpType.Default"/>。</summary>
        public MiniDumpType DumpType { get; set; } = MiniDumpType.Default;

        /// <summary>
        /// 崩溃文件保留天数。按文件最后修改时间清理 `Crash_*.log` 与 `CrashDump_*.dmp`。
        /// 默认 30 天；设为 0 或负数表示<b>不清理</b>。
        /// </summary>
        public int RetentionDays { get; set; } = 30;

        /// <summary>写崩溃日志前是否先 flush LogHelper 的缓冲队列。默认 <c>true</c>。</summary>
        public bool FlushLogger { get; set; } = true;

        /// <summary>
        /// 是否在 <see cref="CrashDumpHelper.Initialize(CrashDumpOptions)"/> 时自动接管
        /// <c>AppDomain.UnhandledException</c>。默认 <c>true</c>。
        /// <para>
        /// 该事件属于 BCL 核心成员，接管不会引入任何程序集依赖。
        /// 关闭后必须由调用方自行订阅并调用
        /// <see cref="CrashDumpHelper.FlushAndWriteCrashLog(Exception, string)"/>，
        /// 否则环形缓冲会随进程一起消失，等于什么都没记下。
        /// </para>
        /// </summary>
        public bool HookUnhandledException { get; set; } = true;

        /// <summary>
        /// 是否在初始化时自动订阅 <c>TaskScheduler.UnobservedTaskException</c>。默认 <c>true</c>。
        /// <para>
        /// .NET Framework 4.5 起未观察的 Task 异常默认不再终止进程，只会静默丢弃；
        /// 订阅该事件是捕获这类异常的唯一途径。
        /// </para>
        /// </summary>
        public bool HookUnobservedTaskException { get; set; } = true;

        /// <summary>
        /// 异常过滤器：返回 <c>true</c> 表示该异常不纳入环形缓冲。
        /// 用于剔除把异常当控制流所产生的噪声，避免冲掉崩溃前的关键线索。
        /// <para>
        /// 回调自身抛出的异常按「不忽略」处理 —— 宁可多记，不可漏记。
        /// </para>
        /// </summary>
        public Func<Exception, bool>? ShouldIgnore { get; set; }

        /// <summary>
        /// 环形缓冲写满时是否自动落盘并清空。默认 <c>true</c>。
        /// <para>
        /// 缓冲是覆盖式的，写满后继续写入会丢掉最旧的记录；
        /// 及时落盘可以把这一批记录固化到日志文件中。
        /// 仅写日志，不生成 MiniDump —— 缓冲满是预警，不是崩溃。
        /// </para>
        /// </summary>
        public bool AutoFlushWhenFull { get; set; } = true;

        /// <summary>
        /// 诊断回调：日志目录创建失败、dump 生成失败等
        /// <b>不向外抛但值得关注</b>的情况经此上报。为 <c>null</c> 时静默忽略。
        /// </summary>
        public Action<string>? Diagnostics { get; set; }
    }
}
