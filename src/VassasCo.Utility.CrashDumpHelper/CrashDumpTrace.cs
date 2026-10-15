// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VassasCo.Utility
{
    /// <summary>
    /// <see cref="CrashDumpHelper"/> 自用的极简跟踪日志，用于免除对外部日志库的编译期依赖。
    /// <para>
    /// 能力边界：只做「分级 + 格式化 + 内存缓冲 + 阈值自动落盘 + 显式落盘」。
    /// 不提供滚动分片、多输出目标、结构化字段、异步后台线程等能力 ——
    /// 本类的职责是让崩溃库自给自足，不是成为一个通用日志框架。
    /// </para>
    /// <para>
    /// 不使用后台线程定时 flush：崩溃库自身引入线程会带来生命周期管理成本与进程退出竞态，
    /// 反而可能在最需要日志的时刻丢日志。这里采用「缓冲 + 阈值自动 flush + 崩溃前显式 flush」，
    /// 在写入开销与可靠性之间取平衡。
    /// </para>
    /// <para>
    /// 所有写失败一律静默且保留缓冲：跟踪日志属于尽力而为的诊断辅助，
    /// 它若反过来向崩溃流程抛异常，会导致真正要排查的故障被掩盖。
    /// </para>
    /// </summary>
    internal static class CrashDumpTrace
    {
        /// <summary>
        /// 跟踪日志文件前缀。必须是 <c>Trace_</c> 而非 <c>Crash</c> 开头：
        /// 否则会被崩溃日志的 <c>Crash_*.log</c> 通配规则一并命中，污染清理范围与文件计数。
        /// </summary>
        internal const string FilePrefix = "Trace_";

        /// <summary>缓冲条数达到该值即自动落盘，给内存占用封顶。</summary>
        private const int AutoFlushThreshold = 32;

        /// <summary>
        /// 缓冲硬上限，达到后丢弃最旧的记录。
        /// 磁盘长期不可写时，缓冲不能无上限增长进而拖垮宿主进程。
        /// </summary>
        private const int MaxPending = 256;

        /// <summary>
        /// 落盘 IO 在锁内完成。本类调用点均为低频的诊断路径，
        /// 串行化的代价远小于「并发 Append 抛 IOException导致整批丢失」的排查成本。
        /// </summary>
        private static readonly object _sync = new object();

        private static readonly Queue<string> _pending = new Queue<string>();

        private static string? _directory;
        private static bool _enabled;

        /// <summary>
        /// 应用配置。目录由调用方解析为绝对路径后传入，避免多处各自解析相对路径导致日志散落。
        /// </summary>
        /// <param name="directory">已解析为绝对路径的输出目录。</param>
        /// <param name="enabled">是否启用跟踪日志；关闭时立即丢弃未落盘的缓冲。</param>
        internal static void Configure(string directory, bool enabled)
        {
            lock (_sync)
            {
                _directory = directory;
                _enabled = enabled;
                if (!enabled)
                    _pending.Clear();
            }
        }

        /// <summary>清空配置与缓冲，用于注销监听后的状态复位。幂等。</summary>
        internal static void Reset()
        {
            lock (_sync)
            {
                _directory = null;
                _enabled = false;
                _pending.Clear();
            }
        }

        /// <summary>
        /// 记录一条跟踪日志。未启用时为空操作，不会构造字符串，因此热路径上调用开销可忽略。
        /// </summary>
        internal static void Write(CrashDumpLogLevel level, string message)
        {
            bool reachThreshold;
            lock (_sync)
            {
                if (!_enabled || _directory == null)
                    return;

                if (_pending.Count >= MaxPending)
                    _pending.Dequeue();

                _pending.Enqueue(Format(level, message));
                reachThreshold = _pending.Count >= AutoFlushThreshold;
            }

            if (reachThreshold)
                Flush();
        }

        /// <summary>
        /// 把缓冲中的记录一次性写入当日跟踪日志文件。
        /// 整批合并为单次写入，避免逐行开关文件句柄。
        /// </summary>
        /// <returns>本次落盘的条数；未启用、无缓冲或写入失败时返回 0。</returns>
        internal static int Flush()
        {
            lock (_sync)
            {
                if (!_enabled || _directory == null || _pending.Count == 0)
                    return 0;

                try
                {
                    Directory.CreateDirectory(_directory);

                    var path = Path.Combine(_directory,
                        FilePrefix + DateTime.Now.ToString("yyyyMMdd") + ".log");

                    var sb = new StringBuilder();
                    foreach (var line in _pending)
                        sb.AppendLine(line);

                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));

                    var written = _pending.Count;
                    _pending.Clear();
                    return written;
                }
                catch
                {
                    // 保留缓冲，等下一次 flush 重试；本类不向调用方抛出异常，详见类型说明。
                    return 0;
                }
            }
        }

        private static string Format(CrashDumpLogLevel level, string message)
            => $"[{DateTime.Now:HH:mm:ss.fff}] [{LevelText(level)}] {message}";

        private static string LevelText(CrashDumpLogLevel level)
        {
            switch (level)
            {
                case CrashDumpLogLevel.Debug: return "DEBUG";
                case CrashDumpLogLevel.Info: return "INFO";
                case CrashDumpLogLevel.Warn: return "WARN";
                case CrashDumpLogLevel.Error: return "ERROR";
                case CrashDumpLogLevel.Fatal: return "FATAL";
                default: return level.ToString().ToUpperInvariant();
            }
        }
    }
}
