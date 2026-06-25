using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace VassasCo.Utility
{
    /// <summary>
    /// 高性能异步日志系统。
    /// 核心设计：建造者模式、异步队列、自动清理。
    /// 目录结构：LogPath/yyyy-MM/MM-dd/LogType.log
    /// </summary>
    public class LogHelper : IDisposable
    {
        private readonly string _logBasePath;
        private readonly int _retentionDays;
        private readonly HashSet<string> _registeredLogTypes;
        private readonly Dictionary<string, StreamWriter?> _logWriters;
        private readonly Dictionary<string, object> _fileLocks;
        private readonly Queue<LogMessage> _logQueue;
        private readonly ManualResetEvent _logEvent;
        private readonly Thread _logThread;
        private readonly Timer? _cleanupTimer;
        private bool _isRunning;
        private bool _disposed;

        /// <summary>建造者 — 链式配置并创建 LogHelper 实例</summary>
        public class LogHelperBuilder
        {
            private string _logPath = string.Empty;
            private int _retentionMonths = 6;
            private bool _enableDailyCleanup = true;
            private TimeSpan _cleanupTime = new(2, 0, 0);
            private readonly HashSet<string> _logTypes = new();

            /// <summary>设置日志根路径（必填）</summary>
            public LogHelperBuilder SetLogPath(string path) { _logPath = path; return this; }

            /// <summary>从 JSON 配置文件加载日志类型列表</summary>
            public LogHelperBuilder UseConfigFile(string configFilePath)
            {
                try
                {
                    if (File.Exists(configFilePath))
                    {
                        var json = File.ReadAllText(configFilePath, Encoding.UTF8);
                        var config = JsonSerializer.Deserialize<LogHelperConfig>(json);
                        if (config?.LogTypes != null)
                            foreach (var t in config.LogTypes) _logTypes.Add(t);
                    }
                }
                catch { }
                return this;
            }

            /// <summary>添加单个日志类型</summary>
            public LogHelperBuilder AddLogType(string logType) { _logTypes.Add(logType); return this; }

            /// <summary>批量添加日志类型</summary>
            public LogHelperBuilder AddLogTypes(params string[] logTypes)
            {
                foreach (var t in logTypes) _logTypes.Add(t);
                return this;
            }

            /// <summary>设置日志保留月数，默认 6</summary>
            public LogHelperBuilder SetRetentionMonths(int months) { _retentionMonths = Math.Max(1, months); return this; }

            /// <summary>设置每日清理时间，默认 2:00</summary>
            public LogHelperBuilder SetCleanupTime(int hour, int minute = 0) { _cleanupTime = new TimeSpan(hour, minute, 0); return this; }

            /// <summary>启用/禁用每日自动清理，默认启用</summary>
            public LogHelperBuilder EnableDailyCleanup(bool enable) { _enableDailyCleanup = enable; return this; }

            /// <summary>构建并返回 LogHelper 实例</summary>
            public LogHelper Build()
            {
                if (string.IsNullOrWhiteSpace(_logPath))
                    throw new InvalidOperationException("日志路径未设置，请先调用 SetLogPath()");
                return new LogHelper(_logPath, _logTypes, _retentionMonths, _enableDailyCleanup, _cleanupTime);
            }

            /// <summary>构建并返回，等价于 Build()</summary>
            public LogHelper Start() => Build();

            internal class LogHelperConfig
            {
                public List<string>? LogTypes { get; set; }
            }
        }

        /// <summary>开始建造者链</summary>
        public static LogHelperBuilder Build() => new();

        /// <summary>快捷创建（无建造者）</summary>
        public static LogHelper Create(string logPath, params string[] logTypes)
        {
            return Build().SetLogPath(logPath).AddLogTypes(logTypes).Start();
        }

        internal LogHelper(string logPath, HashSet<string> logTypes, int retentionMonths, bool enableDailyCleanup, TimeSpan cleanupTime)
        {
            _logBasePath = logPath;
            _retentionDays = retentionMonths * 30;

            if (logTypes.Count == 0)
            {
                logTypes.Add(LogTypes.Debug);
                logTypes.Add(LogTypes.Info);
                logTypes.Add(LogTypes.Error);
                logTypes.Add(LogTypes.Warning);
                logTypes.Add(LogTypes.Performance);
                logTypes.Add(LogTypes.Security);
                logTypes.Add(LogTypes.Business);
                logTypes.Add(LogTypes.Audit);
                logTypes.Add(LogTypes.Operation);
                logTypes.Add(LogTypes.TimerTask);
                logTypes.Add(LogTypes.System);
                logTypes.Add(LogTypes.Database);
                logTypes.Add(LogTypes.Api);
                logTypes.Add(LogTypes.Network);
            }

            _registeredLogTypes = logTypes;
            _fileLocks = new Dictionary<string, object>();
            foreach (var t in _registeredLogTypes) _fileLocks[t] = new object();

            _logWriters = new Dictionary<string, StreamWriter?>();
            _logQueue = new Queue<LogMessage>();
            _logEvent = new ManualResetEvent(false);
            _isRunning = true;

            _logThread = new Thread(ProcessLogQueue) { IsBackground = true, Name = "LogHelper-Worker" };
            _logThread.Start();

            if (enableDailyCleanup)
            {
                var now = DateTime.Now;
                var nextRun = now.Date.Add(cleanupTime);
                if (nextRun <= now) nextRun = nextRun.AddDays(1);
                var firstInterval = (nextRun - now);

                _cleanupTimer = new Timer(_ => { CleanOldLogs(); }, null, firstInterval, TimeSpan.FromDays(1));
            }
        }

        /// <summary>停止日志服务：等待工作线程结束（最长 5s），关闭所有写入器</summary>
        public void StopLogService()
        {
            if (!_isRunning) return;

            this.AddLog(LogTypes.System, "日志服务正在停止", "LogHelper");
            _isRunning = false;
            _logEvent.Set();

            if (_logThread.IsAlive) _logThread.Join(5000);

            foreach (var writer in _logWriters.Values) writer?.Dispose();
            _logWriters.Clear();

            _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _cleanupTimer?.Dispose();

            Debug.WriteLine("日志服务已停止");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopLogService();
        }

        /// <summary>添加日志记录（线程安全，入队立即返回）</summary>
        /// <param name="logType">日志类型（如 LogTypes.Error）</param>
        /// <param name="message">日志内容</param>
        /// <param name="model">来源模块名</param>
        public void AddLog(string logType, string message, string model)
        {
            if (!_isRunning) return;

            if (!_registeredLogTypes.Contains(logType))
            {
                lock (_fileLocks)
                {
                    if (!_registeredLogTypes.Contains(logType))
                    {
                        _registeredLogTypes.Add(logType);
                        _fileLocks[logType] = new object();
                    }
                }
            }

            lock (_logQueue)
            {
                _logQueue.Enqueue(new LogMessage
                {
                    LogType = logType, Message = message, Timestamp = DateTime.Now, Model = model
                });
                _logEvent.Set();
            }
        }

        /// <summary>批量添加日志记录</summary>
        public void AddLogs(string logType, IEnumerable<string> messages, string model)
        {
            foreach (var message in messages) AddLog(logType, message, model);
        }

        private void ProcessLogQueue()
        {
            while (_isRunning)
            {
                _logEvent.WaitOne();
                while (true)
                {
                    LogMessage? logMessage;
                    lock (_logQueue)
                    {
                        if (_logQueue.Count == 0) { _logEvent.Reset(); break; }
                        logMessage = _logQueue.Dequeue();
                    }
                    WriteLogToFile(logMessage);
                }
            }
        }

        private void WriteLogToFile(LogMessage? logMessage)
        {
            if (logMessage == null) return;
            try
            {
                var filePath = GetLogFilePath(logMessage.LogType!, logMessage.Timestamp);
                var logContent = $"[{logMessage.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{logMessage.Model}] {logMessage.Message}";

                lock (_fileLocks[logMessage.LogType!])
                {
                    var directory = Path.GetDirectoryName(filePath) ?? "";
                    if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                    if (!_logWriters.TryGetValue(logMessage.LogType!, out var writer) || writer == null)
                    {
                        writer = new StreamWriter(filePath, true, Encoding.UTF8) { AutoFlush = true };
                        _logWriters[logMessage.LogType!] = writer;
                    }
                    writer.WriteLine(logContent);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"日志写入失败: {ex}");
            }
        }

        private string GetLogFilePath(string logType, DateTime timestamp)
        {
            var monthFolder = timestamp.ToString("yyyy-MM");
            var dayFolder = timestamp.ToString("MM-dd");
            return Path.Combine(_logBasePath, monthFolder, dayFolder, $"{logType}.log");
        }

        private void CleanOldLogs()
        {
            try
            {
                if (!Directory.Exists(_logBasePath)) return;

                var cutoffDate = DateTime.Now.AddDays(-_retentionDays);
                foreach (var monthDir in Directory.GetDirectories(_logBasePath))
                {
                    if (IsDirectoryExpired(monthDir, cutoffDate))
                    {
                        try { Directory.Delete(monthDir, true); }
                        catch (Exception ex) { Debug.WriteLine($"删除过期日志目录失败 {monthDir}: {ex}"); }
                    }
                    else CleanExpiredDayFolders(monthDir, cutoffDate);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"清理过期日志失败: {ex}"); }
        }

        private static bool IsDirectoryExpired(string directoryPath, DateTime cutoffDate)
        {
            var dirName = Path.GetFileName(directoryPath);
            if (!DateTime.TryParseExact(dirName, "yyyy-MM", null, System.Globalization.DateTimeStyles.None, out var dirDate))
                return false;
            var lastDayOfMonth = new DateTime(dirDate.Year, dirDate.Month, 1).AddMonths(1).AddDays(-1);
            return lastDayOfMonth < cutoffDate;
        }

        private static void CleanExpiredDayFolders(string monthDir, DateTime cutoffDate)
        {
            foreach (var dayDir in Directory.GetDirectories(monthDir))
            {
                var dirName = Path.GetFileName(dayDir);
                var monthDirName = Path.GetFileName(monthDir);
                if (!DateTime.TryParseExact(monthDirName, "yyyy-MM", null,
                        System.Globalization.DateTimeStyles.None, out var monthDate)) continue;
                if (!DateTime.TryParseExact(dirName, "MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out _)) continue;
                var fullDate = new DateTime(monthDate.Year, monthDate.Month,
                    int.Parse(dirName.Split('-')[1]));
                if (fullDate >= cutoffDate) continue;
                try { Directory.Delete(dayDir, true); }
                catch (Exception ex) { Debug.WriteLine($"删除过期日志目录失败 {dayDir}: {ex}"); }
            }
        }

        private sealed class LogMessage
        {
            public string? LogType { get; set; }
            public string? Message { get; set; }
            public DateTime Timestamp { get; set; }
            public string? Model { get; set; }
        }
    }
}
