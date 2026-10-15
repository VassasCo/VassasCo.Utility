// SPDX-License-Identifier: MIT

namespace VassasCo.Utility
{
    /// <summary>
    /// 内部跟踪日志级别，取值顺序即严重程度递增顺序。
    /// </summary>
    internal enum CrashDumpLogLevel
    {
        /// <summary>细粒度流程线索，仅排查本库自身行为时使用。</summary>
        Debug = 0,

        /// <summary>正常运行中的关键节点，例如初始化、注销、落盘完成。</summary>
        Info = 1,

        /// <summary>本库已降级处理但尚未影响主流程的情况，例如 dump 生成失败。</summary>
        Warn = 2,

        /// <summary>本库的某个环节失败，例如写日志失败。</summary>
        Error = 3,

        /// <summary>崩溃已被捕获并处理完毕，是跟踪日志中最高等级的收尾记录。</summary>
        Fatal = 4
    }
}
