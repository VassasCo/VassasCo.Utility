using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VassasCo.Utility
{
    /// <summary>任务配置建造者</summary>
    public class JobBuilder
    {
        internal string Name = string.Empty;
        internal ScheduleMode Mode = ScheduleMode.FixedDelay;
        internal Delegate? JobDelegate;
        internal CronExpression? CronExpression;
        internal TimeSpan Interval = TimeSpan.FromSeconds(1);
        internal JobPolicy Policy = JobPolicy.Singleton;
        internal TimeSpan Timeout = TimeSpan.FromMinutes(5);
        internal int MaxRetries;
        internal TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);
        internal bool ExponentialBackoff = true;
        internal Func<DateTime, bool>? CalendarFilter;
        internal readonly List<(Type ExceptionType, bool ShouldRetry)> RetryableExceptions = new();
        internal int? MaxConcurrency;

        /// <summary>设置任务名称（必填）</summary>
        public JobBuilder SetName(string name) { Name = name; return this; }

        /// <summary>设置 CRON 调度模式</summary>
        public JobBuilder SetCron(string cronExpression)
        {
            Mode = ScheduleMode.Cron;
            CronExpression = new CronExpression(cronExpression);
            return this;
        }

        /// <summary>设置固定速率模式</summary>
        public JobBuilder SetFixedRate(TimeSpan interval)
        {
            Mode = ScheduleMode.FixedRate;
            Interval = interval;
            return this;
        }

        /// <summary>设置固定延迟模式（默认）</summary>
        public JobBuilder SetFixedDelay(TimeSpan interval)
        {
            Mode = ScheduleMode.FixedDelay;
            Interval = interval;
            return this;
        }

        /// <summary>设置同步任务体</summary>
        public JobBuilder SetJob(SyncJobDelegate job) { JobDelegate = job; return this; }

        /// <summary>设置异步任务体</summary>
        public JobBuilder SetJob(AsyncJobDelegate job) { JobDelegate = job; return this; }

        /// <summary>设置单例/多例策略（默认单例）</summary>
        public JobBuilder SetPolicy(JobPolicy policy) { Policy = policy; return this; }

        /// <summary>设置执行超时（默认 5 分钟）</summary>
        public JobBuilder SetTimeout(TimeSpan timeout) { Timeout = timeout; return this; }

        /// <summary>启用重试，指定最大重试次数和基础延迟</summary>
        public JobBuilder SetRetry(int maxRetries, TimeSpan? baseDelay = null, bool exponentialBackoff = true)
        {
            MaxRetries = maxRetries;
            RetryBaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
            ExponentialBackoff = exponentialBackoff;
            return this;
        }

        /// <summary>添加可重试的异常类型</summary>
        public JobBuilder AddRetryableException<T>(bool shouldRetry = true) where T : Exception
        {
            RetryableExceptions.Add((typeof(T), shouldRetry));
            return this;
        }

        /// <summary>设置日历过滤器（返回 false 跳过）</summary>
        public JobBuilder SetCalendarFilter(Func<DateTime, bool> filter) { CalendarFilter = filter; return this; }

        /// <summary>设置此任务的最大并发数（null=继承全局）</summary>
        public JobBuilder SetMaxConcurrency(int? maxConcurrency) { MaxConcurrency = maxConcurrency; return this; }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                throw new InvalidOperationException("任务名称不能为空");
            if (JobDelegate == null)
                throw new InvalidOperationException($"任务 [{Name}] 未设置执行体");
        }
    }

    /// <summary>调度器建造者</summary>
    public class ScheduleBuilder
    {
        internal readonly List<JobBuilder> Jobs = new();
        internal int MaxConcurrency = Environment.ProcessorCount * 2;
        internal TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);
        internal string LogPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        /// <summary>添加一个任务</summary>
        public ScheduleBuilder AddJob(Action<JobBuilder> configure)
        {
            var builder = new JobBuilder();
            configure(builder);
            builder.Validate();
            Jobs.Add(builder);
            return this;
        }

        /// <summary>设置全局最大并发任务数（默认 CPU 核数 * 2）</summary>
        public ScheduleBuilder SetMaxConcurrency(int max) { MaxConcurrency = max; return this; }

        /// <summary>设置优雅关闭超时（默认 30 秒）</summary>
        public ScheduleBuilder SetShutdownTimeout(TimeSpan timeout) { ShutdownTimeout = timeout; return this; }

        /// <summary>设置日志存放路径（默认 {BaseDirectory}/logs/）</summary>
        public ScheduleBuilder SetLogPath(string path) { LogPath = path; return this; }

        /// <summary>创建并启动调度器</summary>
        public ScheduleHelper Build()
        {
            if (Jobs.Count == 0)
                throw new InvalidOperationException("至少需要添加一个任务");
            return new ScheduleHelper(this);
        }
    }

    /// <summary>
    /// 高性能定时任务调度器。
    /// 支持 CRON/FixedRate/FixedDelay 三种模式，重试+指数退避、超时取消、异常隔离、日历过滤。
    /// 内置独立日志系统，不依赖 LogHelper。
    /// </summary>
    public class ScheduleHelper : IDisposable
    {
        private readonly List<ScheduledJob> _jobs;
        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly int _maxConcurrency;
        private readonly TimeSpan _shutdownTimeout;
        private readonly ScheduleLogWriter _logWriter;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;
        private int _isShuttingDown;

        /// <summary>任务开始时触发</summary>
        public event EventHandler<JobEventArgs>? OnStarting;
        /// <summary>任务完成时触发</summary>
        public event EventHandler<JobEventArgs>? OnCompleted;
        /// <summary>任务出错时触发</summary>
        public event EventHandler<JobEventArgs>? OnError;
        /// <summary>任务因重入被跳过时触发</summary>
        public event EventHandler<JobEventArgs>? OnSkipped;

        internal ScheduleHelper(ScheduleBuilder builder)
        {
            _maxConcurrency = builder.MaxConcurrency;
            _shutdownTimeout = builder.ShutdownTimeout;
            _logWriter = new ScheduleLogWriter(builder.LogPath);
            _concurrencySemaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
            _jobs = builder.Jobs.Select(jb => new ScheduledJob(jb, _concurrencySemaphore, this)).ToList();
        }

        /// <summary>创建建造者</summary>
        public static ScheduleBuilder Build() => new ScheduleBuilder();

        /// <summary>获取所有任务的统计信息</summary>
        public IReadOnlyList<JobStats> GetAllStats() => _jobs.Select(j => j.Stats).ToList().AsReadOnly();

        /// <summary>获取指定任务的统计信息</summary>
        public JobStats? GetStats(string jobName) => _jobs.FirstOrDefault(j => j.Name == jobName)?.Stats;

        /// <summary>动态修改指定任务的 CRON 表达式</summary>
        public void UpdateCron(string jobName, string cronExpression)
        {
            var job = _jobs.FirstOrDefault(j => j.Name == jobName)
                ?? throw new ArgumentException($"任务 [{jobName}] 不存在");
            job.UpdateCron(new CronExpression(cronExpression));
        }

        /// <summary>动态修改指定任务的执行间隔（仅 FixedRate/FixedDelay 模式）</summary>
        public void UpdateInterval(string jobName, TimeSpan interval)
        {
            var job = _jobs.FirstOrDefault(j => j.Name == jobName)
                ?? throw new ArgumentException($"任务 [{jobName}] 不存在");
            job.UpdateInterval(interval);
        }

        /// <summary>优雅关闭：等待所有运行中的任务完成，然后停止调度器</summary>
        public async Task ShutdownAsync()
        {
            if (Interlocked.Exchange(ref _isShuttingDown, 1) == 1) return;

            LogInfo("调度器正在优雅关闭...");
            _cts.Cancel();

            foreach (var job in _jobs) job.Stop();

            using var timeoutCts = new CancellationTokenSource(_shutdownTimeout);
            try
            {
                LogInfo($"等待最多 {_maxConcurrency} 个运行中任务完成（超时:{_shutdownTimeout.TotalSeconds}s）...");
                await DrainRunningTasksAsync(timeoutCts.Token);
                LogInfo("所有运行中任务已完成");
            }
            catch (OperationCanceledException)
            {
                LogWarn($"关闭超时（>{_shutdownTimeout.TotalSeconds}s），强制结束");
            }

            LogInfo("调度器已关闭");
        }

        private async Task DrainRunningTasksAsync(CancellationToken token)
        {
            var acquired = 0;
            try
            {
                for (var i = 0; i < _maxConcurrency; i++)
                {
                    await _concurrencySemaphore.WaitAsync(token);
                    acquired++;
                }
            }
            finally
            {
                for (var i = 0; i < acquired; i++)
                    _concurrencySemaphore.Release();
            }
        }

        internal void RaiseOnStarting(string name, JobContext ctx)
            => OnStarting?.Invoke(this, new JobEventArgs(name, ctx));

        internal void RaiseOnCompleted(string name, JobContext ctx, JobResult result)
            => OnCompleted?.Invoke(this, new JobEventArgs(name, ctx, result));

        internal void RaiseOnError(string name, JobContext ctx, JobResult? result)
            => OnError?.Invoke(this, new JobEventArgs(name, ctx, result));

        internal void RaiseOnSkipped(string name, JobContext ctx)
            => OnSkipped?.Invoke(this, new JobEventArgs(name, ctx));

        internal void LogInfo(string msg) => _logWriter.Write(msg, "INFO");
        internal void LogError(string msg) => _logWriter.Write(msg, "ERROR");
        internal void LogWarn(string msg) => _logWriter.Write(msg, "WARN");

        internal CancellationToken GlobalToken => _cts.Token;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ShutdownAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            _logWriter.Dispose();
            _cts.Dispose();
            _concurrencySemaphore.Dispose();
        }

        /// <summary>内部任务包装器：管理单个任务的定时器、执行、重试和统计</summary>
        private sealed class ScheduledJob
        {
            public readonly string Name;
            public readonly JobStats Stats;
            private readonly JobBuilder _config;
            private readonly ScheduleHelper _scheduler;
            private readonly SemaphoreSlim _globalSemaphore;
            private readonly SemaphoreSlim? _localSemaphore;

            private Timer? _timer;
            private long _runIndex;
            private int _isRunning;
            private DateTime _nextScheduledTime;
            private bool _stopped;

            public ScheduledJob(JobBuilder config, SemaphoreSlim globalSemaphore, ScheduleHelper scheduler)
            {
                _config = config;
                _scheduler = scheduler;
                _globalSemaphore = globalSemaphore;
                Name = config.Name;
                Stats = new JobStats(Name);

                if (config.MaxConcurrency.HasValue)
                    _localSemaphore = new SemaphoreSlim(config.MaxConcurrency.Value, config.MaxConcurrency.Value);

                ScheduleNext();
            }

            public void UpdateCron(CronExpression cron)
            {
                _config.CronExpression = cron;
                if (_config.Mode == ScheduleMode.Cron) ScheduleNext();
            }

            public void UpdateInterval(TimeSpan interval)
            {
                _config.Interval = interval;
                if (_config.Mode is ScheduleMode.FixedRate or ScheduleMode.FixedDelay) ScheduleNext();
            }

            public void Stop()
            {
                _stopped = true;
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }

            private void ScheduleNext()
            {
                TimeSpan delay;
                if (_config.Mode == ScheduleMode.Cron)
                {
                    _nextScheduledTime = _config.CronExpression!.GetNext(DateTime.UtcNow);
                    delay = _nextScheduledTime - DateTime.UtcNow;
                }
                else
                {
                    _nextScheduledTime = DateTime.UtcNow + _config.Interval;
                    delay = _config.Interval;
                }

                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

                _timer?.Dispose();
                _timer = new Timer(OnTimerTick, null, delay, Timeout.InfiniteTimeSpan);
            }

            private void OnTimerTick(object? state)
            {
                if (_stopped || _scheduler.GlobalToken.IsCancellationRequested) return;

                var now = DateTime.UtcNow;

                if (_config.CalendarFilter != null && !_config.CalendarFilter(now))
                {
                    _scheduler.LogInfo($"任务 [{Name}] 因日历过滤跳过（{now:yyyy-MM-dd HH:mm:ss}）");
                    ScheduleNext();
                    return;
                }

                if (_config.Policy == JobPolicy.Singleton && Interlocked.CompareExchange(ref _isRunning, 1, 0) == 1)
                {
                    Interlocked.Increment(ref Stats.SkippedCount);
                    _scheduler.RaiseOnSkipped(Name, new JobContext
                    {
                        JobName = Name,
                        RunIndex = Stats.TotalTriggerCount,
                        ScheduledTimeUtc = now,
                        CancellationToken = _scheduler.GlobalToken,
                    });
                    _scheduler.LogWarn($"任务 [{Name}] 因重入被跳过");

                    if (_config.Mode == ScheduleMode.FixedRate) ScheduleNext();
                    return;
                }

                Interlocked.Increment(ref Stats.TotalTriggerCount);
                var runIndex = Interlocked.Increment(ref _runIndex);

                var ctx = new JobContext
                {
                    JobName = Name,
                    RunIndex = runIndex,
                    ScheduledTimeUtc = now,
                    CancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                        _scheduler.GlobalToken).Token,
                };

                _ = ExecuteAsync(ctx);
            }

            private async Task ExecuteAsync(JobContext ctx)
            {
                var startTime = DateTime.UtcNow;
                _scheduler.LogInfo($"任务 [{Name}] 开始执行 (第 {ctx.RunIndex} 次)");
                _scheduler.RaiseOnStarting(Name, ctx);
                Stats.LastStartTimeUtc = startTime;

                try
                {
                    await _globalSemaphore.WaitAsync(ctx.CancellationToken);
                    try
                    {
                        if (_localSemaphore != null)
                            await _localSemaphore.WaitAsync(ctx.CancellationToken);

                        try
                        {
                            var result = await ExecuteWithRetryAsync(ctx, startTime);
                            Stats.LastDuration = DateTime.UtcNow - startTime;
                            Stats.TotalDuration += Stats.LastDuration.Value;
                            Interlocked.Increment(ref Stats.TotalRunCount);

                            if (result.IsSuccess)
                            {
                                Interlocked.Increment(ref Stats.SuccessCount);
                                _scheduler.RaiseOnCompleted(Name, ctx, result);
                                _scheduler.LogInfo($"任务 [{Name}] 完成，耗时 {Stats.LastDuration.Value.TotalMilliseconds:F1}ms");
                            }
                            else
                            {
                                Interlocked.Increment(ref Stats.FailureCount);
                                _scheduler.RaiseOnError(Name, ctx, result);
                                _scheduler.LogError($"任务 [{Name}] 失败（已重试 {ctx.RetryAttempt} 次）: {result.Exception?.Message}");
                            }
                        }
                        finally
                        {
                            _localSemaphore?.Release();
                        }
                    }
                    finally
                    {
                        _globalSemaphore.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref Stats.TotalRunCount);
                    Interlocked.Increment(ref Stats.FailureCount);
                    _scheduler.RaiseOnError(Name, ctx, null);
                    _scheduler.LogWarn($"任务 [{Name}] 被取消");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref Stats.TotalRunCount);
                    Interlocked.Increment(ref Stats.FailureCount);
                    _scheduler.RaiseOnError(Name, ctx, JobResult.Failure(ex, false));
                    _scheduler.LogError($"任务 [{Name}] 未处理异常: {ex}");
                }
                finally
                {
                    if (_config.Policy == JobPolicy.Singleton)
                        Interlocked.Exchange(ref _isRunning, 0);
                }

                if (!_stopped && !_scheduler.GlobalToken.IsCancellationRequested)
                {
                    if (_config.Mode == ScheduleMode.FixedDelay)
                        ScheduleNext();
                    else if (_config.Mode != ScheduleMode.FixedRate)
                        ScheduleNext();
                }
            }

            private async Task<JobResult> ExecuteWithRetryAsync(JobContext ctx, DateTime startTime)
            {
                Exception? lastException = null;

                for (var attempt = 0; attempt <= _config.MaxRetries; attempt++)
                {
                    ctx.RetryAttempt = attempt;

                    if (attempt > 0)
                    {
                        var delay = _config.ExponentialBackoff
                            ? TimeSpan.FromMilliseconds(
                                _config.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1))
                            : _config.RetryBaseDelay;

                        _scheduler.LogWarn($"任务 [{Name}] 第 {attempt}/{_config.MaxRetries} 次重试，等待 {delay.TotalSeconds:F1}s");
                        await Task.Delay(delay, ctx.CancellationToken);
                    }

                    try
                    {
                        using var timeoutCts = new CancellationTokenSource(_config.Timeout);
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                            ctx.CancellationToken, timeoutCts.Token);

                        var task = _config.JobDelegate switch
                        {
                            SyncJobDelegate sync => Task.Run(() => sync(ctx), linkedCts.Token),
                            AsyncJobDelegate asyncDel => asyncDel(ctx),
                            _ => throw new InvalidOperationException("未设置任务委托")
                        };

                        var completedTask = await Task.WhenAny(task, Task.Delay(_config.Timeout, linkedCts.Token));

                        if (completedTask != task)
                        {
                            linkedCts.Cancel();
                            throw new TimeoutException(
                                $"任务 [{Name}] 执行超时（>{_config.Timeout.TotalSeconds}s）");
                        }

                        var result = await task;
                        if (result.IsSuccess) return result;

                        if (!result.ShouldRetry || attempt >= _config.MaxRetries) return result;

                        lastException = result.Exception;
                        if (lastException != null && !IsRetryable(lastException)) return result;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (TimeoutException)
                    {
                        lastException = new TimeoutException($"任务 [{Name}] 超时");
                        if (attempt >= _config.MaxRetries) break;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        if (!IsRetryable(ex) || attempt >= _config.MaxRetries) break;
                    }
                }

                return JobResult.Failure(
                    lastException ?? new InvalidOperationException(
                        $"任务 [{Name}] 重试 {_config.MaxRetries} 次后仍失败"), false);
            }

            private bool IsRetryable(Exception ex)
            {
                if (_config.RetryableExceptions.Count == 0) return true;
                foreach (var (type, shouldRetry) in _config.RetryableExceptions)
                {
                    if (type.IsInstanceOfType(ex)) return shouldRetry;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// 轻量级独立日志写入器，不依赖 LogHelper。
    /// 格式：[yyyy-MM-dd HH:mm:ss.fff] [Schedule] [LEVEL] message
    /// 目录结构：LogPath/yyyy-MM/MM-dd/Schedule.log
    /// </summary>
    internal sealed class ScheduleLogWriter : IDisposable
    {
        private readonly string _logBasePath;
        private readonly object _lock = new object();
        private string? _currentFilePath;
        private StreamWriter? _writer;
        private bool _disposed;

        public ScheduleLogWriter(string logBasePath)
        {
            _logBasePath = logBasePath;
        }

        public void Write(string message, string level)
        {
            if (_disposed) return;

            var now = DateTime.Now;
            var logLine = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [Schedule] [{level}] {message}";
            var filePath = GetFilePath(now);

            lock (_lock)
            {
                try
                {
                    if (_writer == null || _currentFilePath != filePath)
                    {
                        _writer?.Dispose();
                        var directory = Path.GetDirectoryName(filePath);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                            Directory.CreateDirectory(directory);
                        _writer = new StreamWriter(filePath, true, Encoding.UTF8) { AutoFlush = true };
                        _currentFilePath = filePath;
                    }

                    _writer.WriteLine(logLine);
                }
                catch { }
            }
        }

        private string GetFilePath(DateTime timestamp)
        {
            return Path.Combine(_logBasePath, timestamp.ToString("yyyy-MM"), timestamp.ToString("MM-dd"),
                "Schedule.log");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
