using System;

namespace VassasCo.Utility
{
    /// <summary>日志扩展方法 — string/Exception 链式日志记录</summary>
    public static class LogHelperExtensions
    {
        public static void LogPerOperation(this string message, string model)
            => LogManager.SafeAddLog(LogTypes.Operation, message, model);

        public static void LogError(this string message, string model)
            => LogManager.SafeAddLog(LogTypes.Error, message, model);

        public static void LogError(this Exception exception, string model)
        {
            var errorMessage = $"异常类型: {exception.GetType().Name}\n" +
                               $"异常消息: {exception.Message}\n" +
                               $"堆栈跟踪: {exception.StackTrace}";
            LogManager.SafeAddLog(LogTypes.Error, errorMessage, model);
        }

        public static void LogDebug(this string message, string model)
            => LogManager.SafeAddLog(LogTypes.Debug, message, model);

        public static void LogInfo(this string message, string model)
            => LogManager.SafeAddLog(LogTypes.Info, message, model);
    }
}
