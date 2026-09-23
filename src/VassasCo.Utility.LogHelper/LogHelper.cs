// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace VassasCo.Utility
{
    /// <summary>
    /// 轻量级异步文件日志器。后台单线程消费有界队列，按「分类」分文件、按「天」分目录，
    /// 跨天自动切换文件，定时批量刷盘与清理过期日志，支持可靠关闭（排空队列不丢日志）。
    /// 线程安全：任意线程可并发调用 <see cref="AddLog"/>。
    /// </summary>
    public sealed class LogHelper : IDisposable
    {
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        private readonly LogOptions _options;
        private readonly BlockingCollection<LogEntry> _queue;
        private readonly Thread _worker;
        private readonly Dictionary<string, StreamWriter> _writers = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);
        private readonly object _writerLock = new object();
        private readonly Timer _flushTimer;
        private readonly Timer? _cleanupTimer;
        private string _currentDateKey = string.Empty;
        private int _disposed;

        internal LogHelper(LogOptions options)
        {
            _options = options;
            _queue = new BlockingCollection<LogEntry>(options.QueueCapacity);
            _worker = new Thread(ProcessQueue) { IsBackground = true, Name = "VassasCo.LogHelper" };
            _worker.Start();

            _flushTimer = new Timer(OnFlush, null, options.AutoFlushInterval, options.AutoFlushInterval);
            if (options.EnableDailyCleanup)
                _cleanupTimer = new Timer(OnCleanup, null, options.CleanupInterval, options.CleanupInterval);
        }

        /// <summary>创建链式构建器</summary>
        public static LogHelperBuilder Build() => new LogHelperBuilder();

        /// <summary>写入一条日志（线程安全；低于最低级别的日志被直接丢弃）</summary>
        /// <param name="category">日志分类（决定写入哪个文件；null/空白回退为 <see cref="LogCategories.General"/>）</param>
        /// <param name="level">日志级别（仅用于最低级别过滤与内容标注，不决定文件）</param>
        /// <param name="message">日志内容</param>
        /// <param name="module">可选模块标识，写入内容中的 [模块] 字段</param>
        public void AddLog(string category, LogLevel level, string message, string? module = null)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (level < _options.MinLevel)
                return;

            var entry = new LogEntry(NormalizeCategory(category), level, message ?? string.Empty, module, DateTimeOffset.Now);
            if (!_queue.TryAdd(entry))
                ReportError(new InvalidOperationException($"日志队列已满（容量 {_options.QueueCapacity}），已丢弃一条 {level} 日志"));
        }

        /// <summary>立即将所有缓冲日志刷盘（线程安全）</summary>
        public void Flush()
        {
            lock (_writerLock)
            {
                foreach (StreamWriter writer in _writers.Values)
                {
                    try { writer.Flush(); }
                    catch (Exception ex) { ReportError(ex); }
                }
            }
        }

        /// <summary>停止接收新日志、排空队列、释放所有资源（幂等）</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _queue.CompleteAdding();
            if (!_worker.Join(TimeSpan.FromSeconds(30)))
                ReportError(new InvalidOperationException("日志后台线程未在 30 秒内退出，可能存在未落盘日志"));

            _flushTimer.Dispose();
            _cleanupTimer?.Dispose();
            _queue.Dispose();

            lock (_writerLock)
            {
                CloseAllWriters();
            }
        }

        private void ProcessQueue()
        {
            foreach (LogEntry entry in _queue.GetConsumingEnumerable())
            {
                try { WriteEntry(entry); }
                catch (Exception ex) { ReportError(ex); }
            }
        }

        private void WriteEntry(LogEntry entry)
        {
            string dateKey = entry.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            lock (_writerLock)
            {
                // 跨天：关闭并清空前一天的 writer，避免日志继续写入旧文件
                if (!string.Equals(dateKey, _currentDateKey, StringComparison.Ordinal))
                {
                    CloseAllWriters();
                    _currentDateKey = dateKey;
                }

                string writerKey = entry.Category + "|" + dateKey;
                if (!_writers.TryGetValue(writerKey, out StreamWriter? writer))
                {
                    string filePath = GetFilePath(entry.Category, entry.Timestamp);
                    string? dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    writer = new StreamWriter(filePath, true, new UTF8Encoding(false));
                    _writers[writerKey] = writer;
                }

                writer.WriteLine(FormatEntry(entry));
            }
        }

        private static string FormatEntry(LogEntry entry)
        {
            string modulePart = entry.Module == null ? string.Empty : $" [{entry.Module}]";
            return $"[{entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] [{entry.Level}]{modulePart} {entry.Message}";
        }

        private string GetFilePath(string category, DateTimeOffset timestamp)
            => Path.Combine(
                _options.LogPath,
                timestamp.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                timestamp.ToString("MM-dd", CultureInfo.InvariantCulture),
                category + ".log");

        private void CloseAllWriters()
        {
            foreach (StreamWriter writer in _writers.Values)
            {
                try { writer.Flush(); writer.Dispose(); }
                catch (Exception ex) { ReportError(ex); }
            }
            _writers.Clear();
        }

        private void OnFlush(object? state) => Flush();

        private void OnCleanup(object? state)
        {
            try { CleanOldLogs(); }
            catch (Exception ex) { ReportError(ex); }
        }

        private void CleanOldLogs()
        {
            if (!Directory.Exists(_options.LogPath))
                return;

            DateTime cutoff = DateTime.Today.AddDays(-_options.RetentionDays);

            foreach (string monthDir in Directory.GetDirectories(_options.LogPath))
            {
                string monthName = Path.GetFileName(monthDir);
                if (!DateTime.TryParseExact(monthName, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime monthDate))
                    continue;

                // 整个月都已过期则直接删除月目录
                DateTime monthEnd = monthDate.AddMonths(1).AddDays(-1);
                if (monthEnd < cutoff)
                {
                    TryDeleteDirectory(monthDir);
                    continue;
                }

                // 月内按「日」目录逐个清理
                foreach (string dayDir in Directory.GetDirectories(monthDir))
                {
                    string dayName = Path.GetFileName(dayDir);
                    if (!DateTime.TryParseExact(dayName, "MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dayDate))
                        continue;

                    var full = new DateTime(monthDate.Year, dayDate.Month, dayDate.Day);
                    if (full < cutoff)
                        TryDeleteDirectory(dayDir);
                }
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { Directory.Delete(path, true); }
            catch { /* 目录被占用或不存在，忽略 */ }
        }

        // 分类将被用作文件名，必须清洗非法文件名字符、路径分隔符与路径穿越，防止路径注入。
        private static string NormalizeCategory(string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return LogCategories.General;

            string result = category!.Trim();

            for (int i = 0; i < InvalidFileNameChars.Length; i++)
                result = result.Replace(InvalidFileNameChars[i], '_');

            result = result.Replace("\r", string.Empty).Replace("\n", string.Empty);

            if (result == "." || result == "..")
                return LogCategories.General;

            if (result.Length > 50)
                result = result.Substring(0, 50);

            return result.Length == 0 ? LogCategories.General : result;
        }

        private void ReportError(Exception ex)
        {
            if (_options.OnError == null)
                return;
            try { _options.OnError(ex); }
            catch { /* 回调自身异常不能影响日志主流程 */ }
        }

        internal readonly struct LogEntry
        {
            public readonly string Category;
            public readonly LogLevel Level;
            public readonly string Message;
            public readonly string? Module;
            public readonly DateTimeOffset Timestamp;

            public LogEntry(string category, LogLevel level, string message, string? module, DateTimeOffset timestamp)
            {
                Category = category;
                Level = level;
                Message = message;
                Module = module;
                Timestamp = timestamp;
            }
        }
    }

    /// <summary>内部配置项，由 <see cref="LogHelperBuilder"/> 组装</summary>
    internal sealed class LogOptions
    {
        public string LogPath = "Logs";
        public LogLevel MinLevel = LogLevel.Info;
        public int RetentionDays = 30;
        public int QueueCapacity = 100_000;
        public TimeSpan AutoFlushInterval = TimeSpan.FromSeconds(2);
        public TimeSpan CleanupInterval = TimeSpan.FromHours(1);
        public bool EnableDailyCleanup = true;
        public Action<Exception>? OnError;
    }
}
