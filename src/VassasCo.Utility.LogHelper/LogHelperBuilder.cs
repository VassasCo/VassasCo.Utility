// SPDX-License-Identifier: MIT

using System;

namespace VassasCo.Utility
{
    /// <summary>
    /// <see cref="LogHelper"/> 的链式构建器，用于以 Fluent 风格配置并启动日志器。
    /// </summary>
    public sealed class LogHelperBuilder
    {
        private readonly LogOptions _options = new LogOptions();

        /// <summary>设置日志根目录（默认 "Logs"，相对当前工作目录）</summary>
        public LogHelperBuilder SetLogPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("日志路径不能为空", nameof(path));
            _options.LogPath = path;
            return this;
        }

        /// <summary>设置最低记录级别，低于该级别的日志被丢弃（默认 <see cref="LogLevel.Info"/>）</summary>
        public LogHelperBuilder SetMinLevel(LogLevel level)
        {
            _options.MinLevel = level;
            return this;
        }

        /// <summary>设置日志保留天数（默认 30 天，最小 1）</summary>
        public LogHelperBuilder SetRetentionDays(int days)
        {
            _options.RetentionDays = Math.Max(1, days);
            return this;
        }

        /// <summary>设置有界队列容量（默认 100000，最小 100）；满时丢弃新日志并触发错误回调</summary>
        public LogHelperBuilder SetQueueCapacity(int capacity)
        {
            _options.QueueCapacity = Math.Max(100, capacity);
            return this;
        }

        /// <summary>设置批量刷盘间隔（默认 2 秒）</summary>
        public LogHelperBuilder SetAutoFlushInterval(TimeSpan interval)
        {
            _options.AutoFlushInterval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : interval;
            return this;
        }

        /// <summary>设置过期日志清理扫描间隔（默认 1 小时）</summary>
        public LogHelperBuilder SetCleanupInterval(TimeSpan interval)
        {
            _options.CleanupInterval = interval <= TimeSpan.Zero ? TimeSpan.FromHours(1) : interval;
            return this;
        }

        /// <summary>是否启用按天自动清理过期日志（默认 true）</summary>
        public LogHelperBuilder EnableDailyCleanup(bool enable)
        {
            _options.EnableDailyCleanup = enable;
            return this;
        }

        /// <summary>设置写入/清理等后台错误回调（默认 null，即静默）；回调异常会被吞掉不影响主流程</summary>
        public LogHelperBuilder OnError(Action<Exception> handler)
        {
            _options.OnError = handler;
            return this;
        }

        /// <summary>构建并启动日志器</summary>
        public LogHelper Start() => new LogHelper(_options);
    }
}
