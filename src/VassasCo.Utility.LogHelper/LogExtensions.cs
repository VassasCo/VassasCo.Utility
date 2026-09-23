// SPDX-License-Identifier: MIT

using System;
using System.Text;

namespace VassasCo.Utility
{
    /// <summary>
    /// 面向 <see cref="string"/> 与 <see cref="Exception"/> 的日志扩展方法。
    /// 方法名即「分类」（决定写入哪个文件），级别作为写入参数传入，模块为可选的内容字段。
    /// 全部经 <see cref="LogManager"/> 写入，未初始化时静默忽略。
    /// </summary>
    public static class LogHelperExtensions
    {
        /// <summary>写入 General 分类日志</summary>
        public static void Log(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.General, level, message, module);

        /// <summary>写入 Security 分类日志</summary>
        public static void LogSecurity(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.Security, level, message, module);

        /// <summary>写入 Performance 分类日志</summary>
        public static void LogPerformance(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.Performance, level, message, module);

        /// <summary>写入 Business 分类日志</summary>
        public static void LogBusiness(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.Business, level, message, module);

        /// <summary>写入 Audit 分类日志</summary>
        public static void LogAudit(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.Audit, level, message, module);

        /// <summary>写入 Operation 分类日志</summary>
        public static void LogOperation(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.Operation, level, message, module);

        /// <summary>写入 TimerTask 分类日志</summary>
        public static void LogTimerTask(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.TimerTask, level, message, module);

        /// <summary>写入 System 分类日志</summary>
        public static void LogSystem(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.System, level, message, module);

        /// <summary>写入 Database 分类日志</summary>
        public static void LogDatabase(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.Database, level, message, module);

        /// <summary>写入 Api 分类日志</summary>
        public static void LogApi(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.Api, level, message, module);

        /// <summary>写入 Network 分类日志</summary>
        public static void LogNetwork(this string message, LogLevel level = LogLevel.Info, string? module = null)
            => LogManager.SafeAddLog(LogCategories.Network, level, message, module);

        /// <summary>
        /// 记录异常为 Error 级别，递归展开 <see cref="Exception.InnerException"/> 完整异常链。
        /// </summary>
        public static void LogError(this Exception exception, string category = LogCategories.General, string? module = null)
        {
            var sb = new StringBuilder();
            Exception? current = exception;
            int depth = 0;

            while (current != null)
            {
                string prefix = depth == 0 ? "异常" : "内部异常";
                sb.AppendLine($"{prefix}: {current.GetType().FullName}: {current.Message}");
                if (!string.IsNullOrEmpty(current.StackTrace))
                    sb.AppendLine(current.StackTrace);
                current = current.InnerException;
                depth++;
            }

            LogManager.SafeAddLog(category, LogLevel.Error, sb.ToString().TrimEnd(), module);
        }
    }
}
