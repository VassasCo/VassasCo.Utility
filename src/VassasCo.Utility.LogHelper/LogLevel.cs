// SPDX-License-Identifier: MIT

namespace VassasCo.Utility
{
    /// <summary>
    /// 日志级别（数值越大越严重）。用于「最低级别过滤」与「内容标注」，不决定文件（文件由分类决定）。
    /// 与 <see cref="LogCategories"/>（分类）是两个独立维度。
    /// </summary>
    public enum LogLevel
    {
        /// <summary>最细粒度，仅调试期</summary>
        Trace = 0,

        /// <summary>调试信息</summary>
        Debug = 1,

        /// <summary>常规运行信息</summary>
        Info = 2,

        /// <summary>警告，不影响主流程但需关注</summary>
        Warning = 3,

        /// <summary>错误，功能异常</summary>
        Error = 4,

        /// <summary>致命错误，可能导致进程不可用</summary>
        Fatal = 5,
    }

    /// <summary>
    /// 常用日志分类（模块/业务域）常量。分类是自由字符串，可自行扩展；
    /// 它与 <see cref="LogLevel"/> 正交——同一条日志同时拥有「级别」与「分类」。
    /// </summary>
    public static class LogCategories
    {
        /// <summary>默认/通用分类</summary>
        public const string General = "General";

        /// <summary>安全/鉴权</summary>
        public const string Security = "Security";

        /// <summary>性能指标</summary>
        public const string Performance = "Performance";

        /// <summary>业务逻辑</summary>
        public const string Business = "Business";

        /// <summary>审计</summary>
        public const string Audit = "Audit";

        /// <summary>运维/操作</summary>
        public const string Operation = "Operation";

        /// <summary>定时任务</summary>
        public const string TimerTask = "TimerTask";

        /// <summary>系统/框架</summary>
        public const string System = "System";

        /// <summary>数据库访问</summary>
        public const string Database = "Database";

        /// <summary>接口调用</summary>
        public const string Api = "Api";

        /// <summary>网络通信</summary>
        public const string Network = "Network";
    }
}
