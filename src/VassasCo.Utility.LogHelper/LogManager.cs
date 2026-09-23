// SPDX-License-Identifier: MIT

using System;

namespace VassasCo.Utility
{
    /// <summary>
    /// 全局日志器管理器，维护一个进程级的 <see cref="LogHelper"/> 单例。
    /// 扩展方法（<see cref="LogHelperExtensions"/>）通过它写入日志。
    /// </summary>
    public static class LogManager
    {
        private static LogHelper? _current;
        private static readonly object Lock = new object();

        /// <summary>
        /// 获取或设置全局日志器。读取未初始化时抛 <see cref="InvalidOperationException"/>；
        /// 设置时先取出旧实例、释放锁后再 <see cref="LogHelper.Dispose"/> 旧实例（避免锁内阻塞 Join）。
        /// </summary>
        public static LogHelper Current
        {
            get
            {
                lock (Lock)
                {
                    return _current ?? throw new InvalidOperationException("日志系统未初始化，请先通过 LogManager.Current = LogHelper.Build()...Start() 设置");
                }
            }
            set
            {
                LogHelper? old;
                lock (Lock)
                {
                    old = _current;
                    _current = value;
                }
                old?.Dispose();
            }
        }

        /// <summary>日志系统是否已初始化</summary>
        public static bool IsInitialized
        {
            get { lock (Lock) { return _current != null; } }
        }

        /// <summary>停止并释放全局日志器（幂等）</summary>
        public static void Shutdown()
        {
            LogHelper? old;
            lock (Lock)
            {
                old = _current;
                _current = null;
            }
            old?.Dispose();
        }

        /// <summary>安全写入：未初始化则静默忽略，不会抛异常（供扩展方法内部使用）</summary>
        internal static void SafeAddLog(string category, LogLevel level, string message, string? module = null)
        {
            LogHelper? current;
            lock (Lock)
            {
                current = _current;
            }

            if (current == null)
                return;

            try { current.AddLog(category, level, message, module); }
            catch (Exception) { /* 队列已关闭等边界情况，忽略 */ }
        }
    }
}
