using System;
using System.Threading;
using System.Threading.Tasks;

namespace VassasCo.Utility
{
    /// <summary>调度模式</summary>
    public enum ScheduleMode
    {
        /// <summary>CRON 表达式</summary>
        Cron,
        /// <summary>固定速率：每隔 Interval 触发，不管上次是否完成</summary>
        FixedRate,
        /// <summary>固定延迟：上次完成后等待 Interval 再触发</summary>
        FixedDelay,
    }

    /// <summary>任务策略</summary>
    public enum JobPolicy
    {
        /// <summary>单例：同一任务同时只允许一个实例运行</summary>
        Singleton,
        /// <summary>多例：允许并发运行多个实例</summary>
        MultiInstance,
    }

    /// <summary>任务执行统计</summary>
    public class JobStats
    {
        /// <summary>任务名称</summary>
        public string JobName { get; }
        /// <summary>总触发次数</summary>
        public long TotalTriggerCount;
        /// <summary>总执行次数</summary>
        public long TotalRunCount;
        /// <summary>成功次数</summary>
        public long SuccessCount;
        /// <summary>失败次数</summary>
        public long FailureCount;
        /// <summary>因重入被跳过次数</summary>
        public long SkippedCount;
        /// <summary>最后一次执行开始时间（UTC）</summary>
        public DateTime? LastStartTimeUtc;
        /// <summary>最后一次执行耗时</summary>
        public TimeSpan? LastDuration;
        /// <summary>总执行耗时</summary>
        public TimeSpan TotalDuration;

        internal JobStats(string jobName) { JobName = jobName; }

        /// <summary>平均耗时</summary>
        public TimeSpan AverageDuration => TotalRunCount > 0
            ? TimeSpan.FromTicks(TotalDuration.Ticks / TotalRunCount)
            : TimeSpan.Zero;

        /// <summary>成功率（0~1）</summary>
        public double SuccessRate => TotalRunCount > 0 ? (double)SuccessCount / TotalRunCount : 0;

        /// <inheritdoc />
        public override string ToString()
            => $"[{JobName}] 触发:{TotalTriggerCount} 执行:{TotalRunCount} " +
               $"成功:{SuccessCount} 失败:{FailureCount} 跳过:{SkippedCount} " +
               $"成功率:{SuccessRate:P1} 平均耗时:{AverageDuration.TotalMilliseconds:F1}ms";
    }

    /// <summary>任务执行上下文</summary>
    public class JobContext
    {
        /// <summary>任务名称</summary>
        public string JobName { get; init; } = string.Empty;
        /// <summary>本次执行序号（从 1 开始）</summary>
        public long RunIndex { get; init; }
        /// <summary>调度触发时间</summary>
        public DateTime ScheduledTimeUtc { get; init; }
        /// <summary>当前重试次数（0=首次执行）</summary>
        public int RetryAttempt { get; internal set; }
        /// <summary>取消令牌</summary>
        public CancellationToken CancellationToken { get; init; }
    }

    /// <summary>任务执行结果</summary>
    public class JobResult
    {
        /// <summary>是否成功</summary>
        public bool IsSuccess { get; init; }
        /// <summary>异常信息（失败时）</summary>
        public Exception? Exception { get; init; }
        /// <summary>是否应该重试</summary>
        public bool ShouldRetry { get; init; }
        /// <summary>总耗时</summary>
        public TimeSpan Duration { get; init; }

        public static JobResult Success(TimeSpan duration) => new() { IsSuccess = true, Duration = duration };

        public static JobResult Failure(Exception ex, bool shouldRetry = true) => new()
        { IsSuccess = false, Exception = ex, ShouldRetry = shouldRetry };

        public static JobResult Fatal(Exception ex) => new()
        { IsSuccess = false, Exception = ex, ShouldRetry = false };
    }

    /// <summary>任务事件参数</summary>
    public class JobEventArgs : EventArgs
    {
        public string JobName { get; }
        public JobContext Context { get; }
        public JobResult? Result { get; }

        internal JobEventArgs(string jobName, JobContext context, JobResult? result = null)
        {
            JobName = jobName;
            Context = context;
            Result = result;
        }
    }

    /// <summary>同步任务委托</summary>
    public delegate JobResult SyncJobDelegate(JobContext context);

    /// <summary>异步任务委托</summary>
    public delegate Task<JobResult> AsyncJobDelegate(JobContext context);
}
