using System;
using System.Diagnostics;

namespace VassasCo.Utility
{
    /// <summary>日志管理器 — 全局管理当前日志实例</summary>
    public static class LogManager
    {
        private static LogHelper? _current;
        private static readonly object _lockObject = new();

        /// <summary>获取/设置当前日志实例。未初始化时 get 抛出 InvalidOperationException。</summary>
        public static LogHelper Current
        {
            get
            {
                lock (_lockObject)
                {
                    return _current ?? throw new InvalidOperationException("日志系统未初始化，请先设置LogManager.Current");
                }
            }
            set
            {
                lock (_lockObject)
                {
                    _current?.StopLogService();
                    _current = value;
                }
            }
        }

        /// <summary>检查日志系统是否已初始化</summary>
        public static bool IsInitialized
        {
            get { lock (_lockObject) { return _current != null; } }
        }

        /// <summary>安全停止并清理当前日志实例</summary>
        public static void Shutdown()
        {
            lock (_lockObject)
            {
                _current?.StopLogService();
                _current?.Dispose();
                _current = null;
            }
        }

        /// <summary>安全添加日志（未初始化则忽略）</summary>
        public static void SafeAddLog(string logType, string message, string model)
        {
            if (!IsInitialized) return;
            try { Current.AddLog(logType, message, model); }
            catch (Exception ex) { Debug.WriteLine($"记录日志失败: {ex.Message}"); }
        }
    }
}
