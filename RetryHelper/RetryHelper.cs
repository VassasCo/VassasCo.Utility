using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace VassasCo.Utility
{
    #region Enums

    /// <summary>抖动类型</summary>
    public enum JitterType
    {
        /// <summary>无抖动</summary>
        None,
        /// <summary>全抖动：[0, 计算间隔] 随机</summary>
        Full,
        /// <summary>等量抖动：[计算间隔/2, 计算间隔] 随机</summary>
        Equal,
        /// <summary>去相关抖动：取 [计算间隔, 上次间隔×3] 中较小者的随机值</summary>
        Decorrelated
    }

    /// <summary>断路器状态</summary>
    public enum CircuitState
    {
        /// <summary>关闭 — 正常通行</summary>
        Closed,
        /// <summary>打开 — 快速失败</summary>
        Open,
        /// <summary>半开 — 允许试探请求</summary>
        HalfOpen
    }

    /// <summary>退避策略</summary>
    public enum BackoffType
    {
        /// <summary>固定间隔</summary>
        Fixed,
        /// <summary>线性增长：baseInterval × attempt</summary>
        Linear,
        /// <summary>指数退避：baseInterval × 2^(attempt-1)</summary>
        Exponential
    }

    #endregion

    #region Context & Events

    /// <summary>单次尝试上下文</summary>
    public class RetryContext
    {
        /// <summary>当前第几次（1-based）</summary>
        public int Attempt { get; internal set; }
        /// <summary>最大重试次数</summary>
        public int MaxRetries { get; internal set; }
        /// <summary>本次耗时</summary>
        public TimeSpan Elapsed { get; internal set; }
        /// <summary>累计耗时</summary>
        public TimeSpan TotalElapsed { get; internal set; }
        /// <summary>本次等待（首次为 Zero）</summary>
        public TimeSpan WaitDuration { get; internal set; }
        /// <summary>本次异常（成功时为 null）</summary>
        public Exception? Exception { get; internal set; }
        /// <summary>是否成功</summary>
        public bool IsSuccess => Exception == null;
        /// <summary>是否最后一次</summary>
        public bool IsLastAttempt => Attempt >= MaxRetries;
    }

    /// <summary>重试最终报告</summary>
    public class RetryReport
    {
        /// <summary>最终是否成功</summary>
        public bool IsSuccess { get; internal set; }
        /// <summary>总尝试次数</summary>
        public int TotalAttempts { get; internal set; }
        /// <summary>总耗时</summary>
        public TimeSpan TotalElapsed { get; internal set; }
        /// <summary>累计等待</summary>
        public TimeSpan TotalWaitTime { get; internal set; }
        /// <summary>最终异常</summary>
        public Exception? FinalException { get; internal set; }
        /// <summary>每次尝试记录</summary>
        public List<RetryContext> Attempts { get; internal set; } = new();
    }

    /// <summary>断路器状态变更事件</summary>
    public class CircuitStateChangedEventArgs : EventArgs
    {
        /// <summary>操作标识键</summary>
        public string OperationKey { get; }
        /// <summary>旧状态</summary>
        public CircuitState OldState { get; }
        /// <summary>新状态</summary>
        public CircuitState NewState { get; }

        internal CircuitStateChangedEventArgs(string operationKey, CircuitState oldState, CircuitState newState)
        {
            OperationKey = operationKey;
            OldState = oldState;
            NewState = newState;
        }
    }

    #endregion

    #region CircuitBreaker

    /// <summary>
    /// 断路器 — 连续失败 N 次后熔断，暂停一段时间后半开试探。线程安全。
    /// </summary>
    public class CircuitBreaker
    {
        private readonly int _failureThreshold;
        private readonly TimeSpan _breakDuration;
        private readonly object _lock = new();
        private int _failureCount;
        private DateTime _lastFailureTime = DateTime.MinValue;
        private CircuitState _state = CircuitState.Closed;

        /// <summary>当前断路器状态</summary>
        public CircuitState State
        {
            get { lock (_lock) return _state; }
        }

        /// <summary>当前失败计数</summary>
        public int FailureCount
        {
            get { lock (_lock) return _failureCount; }
        }

        /// <summary>断路器状态变更事件</summary>
        public event EventHandler<CircuitStateChangedEventArgs>? StateChanged;

        /// <param name="failureThreshold">连续失败多少次后熔断（≥1）</param>
        /// <param name="breakDuration">熔断持续时间</param>
        public CircuitBreaker(int failureThreshold, TimeSpan breakDuration)
        {
            if (failureThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(failureThreshold), "失败阈值必须 >= 1");
            if (breakDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(breakDuration), "熔断时长必须 > 0");

            _failureThreshold = failureThreshold;
            _breakDuration = breakDuration;
        }

        /// <summary>允许执行时通过；熔断中抛出 CircuitBrokenException</summary>
        public void CheckBeforeExecution(string operationKey)
        {
            lock (_lock)
            {
                switch (_state)
                {
                    case CircuitState.Closed:
                        return;

                    case CircuitState.Open:
                        if (DateTime.UtcNow - _lastFailureTime >= _breakDuration)
                        {
                            TransitionTo(CircuitState.HalfOpen, operationKey);
                            return;
                        }
                        throw new CircuitBrokenException(
                            $"断路器已熔断 (operationKey={operationKey})，" +
                            $"将在 {_breakDuration.TotalSeconds - (DateTime.UtcNow - _lastFailureTime).TotalSeconds:F0} 秒后恢复");

                    case CircuitState.HalfOpen:
                        return;
                }
            }
        }

        /// <summary>记录一次成功</summary>
        public void RecordSuccess(string operationKey)
        {
            lock (_lock)
            {
                _failureCount = 0;
                if (_state == CircuitState.HalfOpen)
                    TransitionTo(CircuitState.Closed, operationKey);
            }
        }

        /// <summary>记录一次失败</summary>
        public void RecordFailure(string operationKey)
        {
            lock (_lock)
            {
                _failureCount++;
                _lastFailureTime = DateTime.UtcNow;

                if (_state == CircuitState.HalfOpen)
                    TransitionTo(CircuitState.Open, operationKey);
                else if (_state == CircuitState.Closed && _failureCount >= _failureThreshold)
                    TransitionTo(CircuitState.Open, operationKey);
            }
        }

        /// <summary>手动重置为关闭状态</summary>
        public void Reset(string operationKey)
        {
            lock (_lock)
            {
                _failureCount = 0;
                TransitionTo(CircuitState.Closed, operationKey);
            }
        }

        private void TransitionTo(CircuitState newState, string operationKey)
        {
            if (_state == newState) return;
            var oldState = _state;
            _state = newState;

            var args = new CircuitStateChangedEventArgs(operationKey, oldState, newState);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { StateChanged?.Invoke(this, args); }
                catch { /* ignore event handler exceptions */ }
            });
        }
    }

    /// <summary>断路器熔断异常</summary>
    public class CircuitBrokenException : Exception
    {
        /// <summary>创建断路器熔断异常</summary>
        public CircuitBrokenException(string message) : base(message) { }
    }

    #endregion

    #region RetryPolicy

    /// <summary>
    /// 重试策略 — 通过 Builder 构建，不可变，线程安全，可跨线程复用。
    /// 支持退避策略（固定/线性/指数）+ 抖动 + 断路器 + 单次超时 + 总超时 + 降级。
    /// </summary>
    public class RetryPolicy
    {
        internal readonly int MaxRetries;
        internal readonly BackoffType BackoffType;
        internal readonly TimeSpan BaseInterval;
        internal readonly TimeSpan MaxInterval;
        internal readonly JitterType JitterType;
        internal readonly CircuitBreaker? CircuitBreaker;
        internal readonly string? OperationKey;
        internal readonly TimeSpan? PerAttemptTimeout;
        internal readonly TimeSpan? TotalTimeout;
        internal readonly Action<RetryContext>? OnRetryCallback;
        internal readonly List<Func<Exception, bool>> RetryPredicates;
        internal readonly Func<double> RandomProvider;

        internal RetryPolicy(RetryPolicyBuilder builder)
        {
            MaxRetries = builder._maxRetries;
            BackoffType = builder._backoffType;
            BaseInterval = builder._baseInterval;
            MaxInterval = builder._maxInterval;
            JitterType = builder._jitter;
            CircuitBreaker = builder._circuitBreaker;
            OperationKey = builder._operationKey ?? "default";
            PerAttemptTimeout = builder._perAttemptTimeout;
            TotalTimeout = builder._totalTimeout;
            OnRetryCallback = builder._onRetry;
            RetryPredicates = new List<Func<Exception, bool>>(builder._retryPredicates);
            RandomProvider = CreateRandomProvider();
        }

        /// <summary>开始构建</summary>
        public static RetryPolicyBuilder Build() => new();

        /// <summary>快捷获取默认策略（3 次，指数退避 1s→30s，全抖动）</summary>
        public static RetryPolicy Default => new Lazy<RetryPolicy>(() => Build()
            .WithMaxRetries(3)
            .WithExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
            .WithJitter(JitterType.Full)
            .Build()).Value;

        private int _lastWaitDurationMs;
        private static long ToMsSafe(TimeSpan ts) => ts.Ticks < 0 ? 0L : (ts.TotalMilliseconds > int.MaxValue ? int.MaxValue : (long)ts.TotalMilliseconds);

        #region Sync Execution

        /// <summary>执行无返回值操作</summary>
        public void Execute(Action action)
        {
            Execute(() => { action(); return true; });
        }

        /// <summary>执行无返回值操作（带 CancellationToken）</summary>
        public void Execute(Action<CancellationToken> action, CancellationToken cancellationToken = default)
        {
            Execute(ct => { action(ct); return true; }, cancellationToken);
        }

        /// <summary>执行有返回值操作</summary>
        public T Execute<T>(Func<T> action)
        {
            return Execute(_ => action(), CancellationToken.None);
        }

        /// <summary>执行有返回值操作（带 CancellationToken）</summary>
        public T Execute<T>(Func<CancellationToken, T> action, CancellationToken cancellationToken = default)
        {
            var report = new RetryReport();
            var totalStopwatch = Stopwatch.StartNew();
            using var linkedCts = CreateLinkedTokenSource(cancellationToken);

            CancellationTokenRegistration? totalTimeoutReg = null;
            if (TotalTimeout.HasValue)
            {
                var timeoutCts = new CancellationTokenSource(TotalTimeout.Value);
                totalTimeoutReg = timeoutCts.Token.Register(() => linkedCts.Cancel());
                linkedCts.Token.Register(() => timeoutCts.Dispose());
            }

            try
            {
                for (int attempt = 1; attempt <= MaxRetries; attempt++)
                {
                    var ctx = new RetryContext { Attempt = attempt, MaxRetries = MaxRetries };

                    CircuitBreaker?.CheckBeforeExecution(OperationKey!);
                    linkedCts.Token.ThrowIfCancellationRequested();

                    var attemptSw = Stopwatch.StartNew();
                    try
                    {
                        T result;
                        if (PerAttemptTimeout.HasValue)
                        {
                            result = ExecuteWithTimeout(action, linkedCts.Token, PerAttemptTimeout.Value, ctx);
                        }
                        else
                        {
                            result = action(linkedCts.Token);
                        }

                        attemptSw.Stop();
                        ctx.Elapsed = attemptSw.Elapsed;
                        ctx.TotalElapsed = totalStopwatch.Elapsed;
                        ctx.WaitDuration = attempt == 1 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_lastWaitDurationMs);

                        CircuitBreaker?.RecordSuccess(OperationKey!);

                        report.IsSuccess = true;
                        report.TotalAttempts = attempt;
                        report.TotalElapsed = totalStopwatch.Elapsed;
                        report.Attempts.Add(ctx);
                        return result;
                    }
                    catch (Exception ex) when (
                        !(ex is CircuitBrokenException) &&
                        !(ex is OperationCanceledException && linkedCts.IsCancellationRequested) &&
                        ShouldRetry(ex) && attempt < MaxRetries)
                    {
                        attemptSw.Stop();
                        ctx.Elapsed = attemptSw.Elapsed;
                        ctx.TotalElapsed = totalStopwatch.Elapsed;
                        ctx.Exception = ex;
                        ctx.WaitDuration = attempt == 1 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_lastWaitDurationMs);
                        report.Attempts.Add(ctx);

                        CircuitBreaker?.RecordFailure(OperationKey!);

                        try { OnRetryCallback?.Invoke(ctx); } catch { }

                        var wait = CalculateBackoff(attempt);
                        Interlocked.Exchange(ref _lastWaitDurationMs, (int)wait.TotalMilliseconds);

                        if (wait > TimeSpan.Zero)
                            linkedCts.Token.WaitHandle.WaitOne(wait);
                    }
                }

                return FinalAttempt(action, report, linkedCts.Token, totalStopwatch);
            }
            catch (CircuitBrokenException ex)
            {
                report.IsSuccess = false;
                report.TotalElapsed = totalStopwatch.Elapsed;
                report.FinalException = ex;
                throw;
            }
            catch (OperationCanceledException) when (TotalTimeout.HasValue && linkedCts.IsCancellationRequested)
            {
                report.IsSuccess = false;
                report.TotalElapsed = totalStopwatch.Elapsed;
                report.FinalException = new TimeoutException($"重试总超时 ({TotalTimeout!.Value.TotalSeconds:F0}s)");
                throw report.FinalException;
            }
            finally
            {
                totalStopwatch.Stop();
                totalTimeoutReg?.Dispose();
            }
        }

        private T ExecuteWithTimeout<T>(Func<CancellationToken, T> action,
            CancellationToken ct, TimeSpan timeout, RetryContext ctx)
        {
            Exception? capturedEx = null;
            T? result = default;

            var thread = new Thread(() =>
            {
                try { result = action(ct); }
                catch (Exception ex) { capturedEx = ex; }
            })
            { IsBackground = true, Name = "Retry-TimedOp" };

            thread.Start();

            if (!thread.Join(timeout))
            {
                throw new TimeoutException($"单次执行超时 ({timeout.TotalSeconds:F0}s)");
            }

            if (capturedEx != null)
                ExceptionDispatchInfo.Capture(capturedEx).Throw();

            return result!;
        }

        private T FinalAttempt<T>(Func<CancellationToken, T> action,
            RetryReport report, CancellationToken ct, Stopwatch totalStopwatch)
        {
            var ctx = new RetryContext { Attempt = MaxRetries, MaxRetries = MaxRetries };

            ct.ThrowIfCancellationRequested();
            CircuitBreaker?.CheckBeforeExecution(OperationKey!);

            var sw = Stopwatch.StartNew();
            try
            {
                T result;
                if (PerAttemptTimeout.HasValue)
                    result = ExecuteWithTimeout(action, ct, PerAttemptTimeout.Value, ctx);
                else
                    result = action(ct);

                sw.Stop();
                ctx.Elapsed = sw.Elapsed;
                ctx.TotalElapsed = totalStopwatch.Elapsed;
                ctx.WaitDuration = MaxRetries > 1 ? TimeSpan.FromMilliseconds(_lastWaitDurationMs) : TimeSpan.Zero;

                CircuitBreaker?.RecordSuccess(OperationKey!);

                report.IsSuccess = true;
                report.TotalAttempts = MaxRetries;
                report.TotalElapsed = totalStopwatch.Elapsed;
                report.Attempts.Add(ctx);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                ctx.Elapsed = sw.Elapsed;
                ctx.TotalElapsed = totalStopwatch.Elapsed;
                ctx.Exception = ex;
                ctx.WaitDuration = MaxRetries > 1 ? TimeSpan.FromMilliseconds(_lastWaitDurationMs) : TimeSpan.Zero;
                report.Attempts.Add(ctx);

                CircuitBreaker?.RecordFailure(OperationKey!);

                report.IsSuccess = false;
                report.TotalAttempts = MaxRetries;
                report.TotalElapsed = totalStopwatch.Elapsed;
                report.FinalException = ex;

                throw new RetryExhaustedException(
                    $"重试 {MaxRetries} 次后仍然失败", ex, report);
            }
        }

        #endregion

        #region Async Execution

        /// <summary>异步执行无返回值操作</summary>
        public Task ExecuteAsync(Func<Task> action)
        {
            return ExecuteAsync(async ct => { await action().ConfigureAwait(false); return true; });
        }

        /// <summary>异步执行有返回值操作</summary>
        public Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        {
            return ExecuteAsync(_ => action(), CancellationToken.None);
        }

        /// <summary>异步执行有返回值操作（带 CancellationToken）</summary>
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken = default)
        {
            var report = new RetryReport();
            var totalStopwatch = Stopwatch.StartNew();
            using var linkedCts = CreateLinkedTokenSource(cancellationToken);

            CancellationTokenRegistration? totalTimeoutReg = null;
            if (TotalTimeout.HasValue)
            {
                var timeoutCts = new CancellationTokenSource(TotalTimeout.Value);
                totalTimeoutReg = timeoutCts.Token.Register(() => linkedCts.Cancel());
                linkedCts.Token.Register(() => timeoutCts.Dispose());
            }

            CancellationToken effectiveCt = linkedCts.Token;

            try
            {
                for (int attempt = 1; attempt <= MaxRetries; attempt++)
                {
                    var ctx = new RetryContext { Attempt = attempt, MaxRetries = MaxRetries };

                    CircuitBreaker?.CheckBeforeExecution(OperationKey!);
                    effectiveCt.ThrowIfCancellationRequested();

                    var attemptSw = Stopwatch.StartNew();
                    try
                    {
                        Task<T> task;
                        if (PerAttemptTimeout.HasValue)
                        {
                            using var perAttemptCts = new CancellationTokenSource(PerAttemptTimeout.Value);
                            using var perLinked = CancellationTokenSource.CreateLinkedTokenSource(
                                new[] { effectiveCt, perAttemptCts.Token });

                            var timeoutTask = Task.Delay(PerAttemptTimeout.Value, perLinked.Token);
                            task = action(perLinked.Token);
                            var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);

                            if (completed == timeoutTask)
                            {
                                effectiveCt.ThrowIfCancellationRequested();
                                throw new TimeoutException($"单次执行超时 ({PerAttemptTimeout.Value.TotalSeconds:F0}s)");
                            }
                        }
                        else
                        {
                            task = action(effectiveCt);
                        }

                        T result = await task.ConfigureAwait(false);

                        attemptSw.Stop();
                        ctx.Elapsed = attemptSw.Elapsed;
                        ctx.TotalElapsed = totalStopwatch.Elapsed;
                        ctx.WaitDuration = attempt == 1 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_lastWaitDurationMs);

                        CircuitBreaker?.RecordSuccess(OperationKey!);

                        report.IsSuccess = true;
                        report.TotalAttempts = attempt;
                        report.TotalElapsed = totalStopwatch.Elapsed;
                        report.Attempts.Add(ctx);
                        return result;
                    }
                    catch (Exception ex) when (
                        !(ex is CircuitBrokenException) &&
                        !(ex is OperationCanceledException && effectiveCt.IsCancellationRequested) &&
                        ShouldRetry(ex) && attempt < MaxRetries)
                    {
                        attemptSw.Stop();
                        ctx.Elapsed = attemptSw.Elapsed;
                        ctx.TotalElapsed = totalStopwatch.Elapsed;
                        ctx.Exception = ex;
                        ctx.WaitDuration = attempt == 1 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_lastWaitDurationMs);
                        report.Attempts.Add(ctx);

                        CircuitBreaker?.RecordFailure(OperationKey!);

                        try { OnRetryCallback?.Invoke(ctx); } catch { }

                        var wait = CalculateBackoff(attempt);
                        Interlocked.Exchange(ref _lastWaitDurationMs, (int)wait.TotalMilliseconds);

                        if (wait > TimeSpan.Zero)
                        {
                            try
                            {
                                await Task.Delay(wait, effectiveCt).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                report.IsSuccess = false;
                                report.TotalElapsed = totalStopwatch.Elapsed;
                                report.FinalException = new TimeoutException(
                                    $"重试总超时 ({TotalTimeout!.Value.TotalSeconds:F0}s)");
                                throw report.FinalException;
                            }
                        }
                    }
                }

                return await FinalAttemptAsync(action, report, effectiveCt, totalStopwatch).ConfigureAwait(false);
            }
            catch (CircuitBrokenException ex)
            {
                report.IsSuccess = false;
                report.TotalElapsed = totalStopwatch.Elapsed;
                report.FinalException = ex;
                throw;
            }
            finally
            {
                totalStopwatch.Stop();
                totalTimeoutReg?.Dispose();
            }
        }

        private async Task<T> FinalAttemptAsync<T>(Func<CancellationToken, Task<T>> action,
            RetryReport report, CancellationToken ct, Stopwatch totalStopwatch)
        {
            var ctx = new RetryContext { Attempt = MaxRetries, MaxRetries = MaxRetries };

            ct.ThrowIfCancellationRequested();
            CircuitBreaker?.CheckBeforeExecution(OperationKey!);

            var sw = Stopwatch.StartNew();
            try
            {
                Task<T> task;
                if (PerAttemptTimeout.HasValue)
                {
                    using var perAttemptCts = new CancellationTokenSource(PerAttemptTimeout.Value);
                    using var perLinked = CancellationTokenSource.CreateLinkedTokenSource(
                        new[] { ct, perAttemptCts.Token });

                    var timeoutTask = Task.Delay(PerAttemptTimeout.Value, perLinked.Token);
                    task = action(perLinked.Token);
                    var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);

                    if (completed == timeoutTask)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new TimeoutException($"单次执行超时 ({PerAttemptTimeout.Value.TotalSeconds:F0}s)");
                    }
                }
                else
                {
                    task = action(ct);
                }

                T result = await task.ConfigureAwait(false);

                sw.Stop();
                ctx.Elapsed = sw.Elapsed;
                ctx.TotalElapsed = totalStopwatch.Elapsed;
                ctx.WaitDuration = MaxRetries > 1 ? TimeSpan.FromMilliseconds(_lastWaitDurationMs) : TimeSpan.Zero;

                CircuitBreaker?.RecordSuccess(OperationKey!);

                report.IsSuccess = true;
                report.TotalAttempts = MaxRetries;
                report.TotalElapsed = totalStopwatch.Elapsed;
                report.Attempts.Add(ctx);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                ctx.Elapsed = sw.Elapsed;
                ctx.TotalElapsed = totalStopwatch.Elapsed;
                ctx.Exception = ex;
                ctx.WaitDuration = MaxRetries > 1 ? TimeSpan.FromMilliseconds(_lastWaitDurationMs) : TimeSpan.Zero;
                report.Attempts.Add(ctx);

                CircuitBreaker?.RecordFailure(OperationKey!);

                report.IsSuccess = false;
                report.TotalAttempts = MaxRetries;
                report.TotalElapsed = totalStopwatch.Elapsed;
                report.FinalException = ex;

                throw new RetryExhaustedException(
                    $"重试 {MaxRetries} 次后仍然失败", ex, report);
            }
        }

        #endregion

        #region Fallback

        /// <summary>同步执行，失败后调用降级函数</summary>
        public T ExecuteWithFallback<T>(Func<T> action, Func<RetryReport, T> fallback)
        {
            try { return Execute(action); }
            catch (RetryExhaustedException ex) { return fallback(ex.Report); }
            catch (CircuitBrokenException ex)
            {
                var report = new RetryReport { IsSuccess = false, FinalException = ex };
                return fallback(report);
            }
        }

        /// <summary>异步执行，失败后调用降级函数</summary>
        public async Task<T> ExecuteWithFallbackAsync<T>(Func<Task<T>> action,
            Func<RetryReport, Task<T>> fallback)
        {
            try { return await ExecuteAsync(action).ConfigureAwait(false); }
            catch (RetryExhaustedException ex) { return await fallback(ex.Report).ConfigureAwait(false); }
            catch (CircuitBrokenException ex)
            {
                var report = new RetryReport { IsSuccess = false, FinalException = ex };
                return await fallback(report).ConfigureAwait(false);
            }
        }

        #endregion

        #region Internal Methods

        private bool ShouldRetry(Exception ex)
        {
            if (RetryPredicates.Count == 0) return true;

            foreach (var pred in RetryPredicates)
            {
                try { if (pred(ex)) return true; }
                catch { }
            }
            return false;
        }

        internal TimeSpan CalculateBackoff(int attempt)
        {
            if (attempt <= 1) return TimeSpan.Zero;

            int retryIndex = attempt - 1;

            long ticks = BackoffType switch
            {
                BackoffType.Fixed => BaseInterval.Ticks,
                BackoffType.Linear => Math.Min(BaseInterval.Ticks * retryIndex, MaxInterval.Ticks),
                BackoffType.Exponential => Math.Min(
                    BaseInterval.Ticks * (long)Math.Pow(2, retryIndex - 1), MaxInterval.Ticks),
                _ => BaseInterval.Ticks
            };

            return ApplyJitter(TimeSpan.FromTicks(ticks));
        }

        private TimeSpan ApplyJitter(TimeSpan interval)
        {
            switch (JitterType)
            {
                case JitterType.None:
                    return interval;

                case JitterType.Full:
                    return TimeSpan.FromMilliseconds(RandomProvider() * interval.TotalMilliseconds);

                case JitterType.Equal:
                {
                    var half = interval.TotalMilliseconds / 2.0;
                    return TimeSpan.FromMilliseconds(half + RandomProvider() * half);
                }

                case JitterType.Decorrelated:
                {
                    var cap = Math.Min(interval.TotalMilliseconds, _lastWaitDurationMs * 3);
                    return TimeSpan.FromMilliseconds(cap * RandomProvider());
                }

                default:
                    return interval;
            }
        }

        private static CancellationTokenSource CreateLinkedTokenSource(CancellationToken ct)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        private static Func<double> CreateRandomProvider()
        {
            var rng = new Random();
            var rngLock = new object();
            return () => { lock (rngLock) return rng.NextDouble(); };
        }

        #endregion
    }

    /// <summary>重试耗尽异常（包含完整报告）</summary>
    public class RetryExhaustedException : Exception
    {
        /// <summary>重试报告</summary>
        public RetryReport Report { get; }

        /// <summary>创建重试耗尽异常</summary>
        public RetryExhaustedException(string message, Exception inner, RetryReport report)
            : base(message, inner)
        {
            Report = report;
        }
    }

    #endregion

    #region RetryPolicyBuilder

    /// <summary>
    /// 重试策略建造者 — 链式配置，最终 Build() 创建不可变的 RetryPolicy。
    /// </summary>
    public class RetryPolicyBuilder
    {
        internal int _maxRetries = 3;
        internal BackoffType _backoffType = BackoffType.Exponential;
        internal TimeSpan _baseInterval = TimeSpan.FromSeconds(1);
        internal TimeSpan _maxInterval = TimeSpan.FromSeconds(30);
        internal JitterType _jitter = JitterType.Full;
        internal CircuitBreaker? _circuitBreaker;
        internal string? _operationKey;
        internal TimeSpan? _perAttemptTimeout;
        internal TimeSpan? _totalTimeout;
        internal Action<RetryContext>? _onRetry;
        internal readonly List<Func<Exception, bool>> _retryPredicates = new();

        /// <summary>设置最大重试次数（含首次执行），默认 3</summary>
        public RetryPolicyBuilder WithMaxRetries(int maxRetries)
        {
            if (maxRetries < 1)
                throw new ArgumentOutOfRangeException(nameof(maxRetries), "重试次数必须 >= 1");
            _maxRetries = maxRetries;
            return this;
        }

        /// <summary>固定间隔退避</summary>
        public RetryPolicyBuilder WithFixedBackoff(TimeSpan interval)
        {
            _backoffType = BackoffType.Fixed;
            _baseInterval = interval;
            _maxInterval = interval;
            return this;
        }

        /// <summary>线性增长退避：interval × attempt</summary>
        public RetryPolicyBuilder WithLinearBackoff(TimeSpan baseInterval, TimeSpan maxInterval)
        {
            _backoffType = BackoffType.Linear;
            _baseInterval = baseInterval;
            _maxInterval = maxInterval;
            return this;
        }

        /// <summary>指数退避：interval × 2^(attempt-1)</summary>
        public RetryPolicyBuilder WithExponentialBackoff(TimeSpan baseInterval, TimeSpan maxInterval)
        {
            _backoffType = BackoffType.Exponential;
            _baseInterval = baseInterval;
            _maxInterval = maxInterval;
            return this;
        }

        /// <summary>设置抖动类型，默认 Full</summary>
        public RetryPolicyBuilder WithJitter(JitterType jitter)
        {
            _jitter = jitter;
            return this;
        }

        /// <summary>
        /// 配置断路器。
        /// </summary>
        /// <param name="failures">连续失败多少次后熔断</param>
        /// <param name="breakDuration">熔断持续时间</param>
        public RetryPolicyBuilder WithCircuitBreaker(int failures, TimeSpan breakDuration)
        {
            _circuitBreaker = new CircuitBreaker(failures, breakDuration);
            return this;
        }

        /// <summary>设置操作标识键（区分不同操作的断路器）</summary>
        public RetryPolicyBuilder WithOperationKey(string key)
        {
            _operationKey = key;
            return this;
        }

        /// <summary>订阅断路器状态变更事件</summary>
        public RetryPolicyBuilder OnCircuitStateChanged(
            EventHandler<CircuitStateChangedEventArgs> handler)
        {
            if (_circuitBreaker == null)
                throw new InvalidOperationException("请先调用 WithCircuitBreaker() 配置断路器");
            _circuitBreaker.StateChanged += handler;
            return this;
        }

        /// <summary>仅对指定异常类型重试（可多次调用添加多种类型）</summary>
        public RetryPolicyBuilder When<TException>() where TException : Exception
        {
            _retryPredicates.Add(ex => ex is TException);
            return this;
        }

        /// <summary>自定义重试判断条件（可多次调用，任一匹配即重试）</summary>
        public RetryPolicyBuilder When(Func<Exception, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            _retryPredicates.Add(predicate);
            return this;
        }

        /// <summary>单次执行超时</summary>
        public RetryPolicyBuilder WithPerAttemptTimeout(TimeSpan timeout)
        {
            _perAttemptTimeout = timeout;
            return this;
        }

        /// <summary>总重试超时（含所有等待）</summary>
        public RetryPolicyBuilder WithTotalTimeout(TimeSpan timeout)
        {
            _totalTimeout = timeout;
            return this;
        }

        /// <summary>重试回调（每次失败后、下次重试前触发）</summary>
        public RetryPolicyBuilder OnRetry(Action<RetryContext> callback)
        {
            _onRetry = callback;
            return this;
        }

        /// <summary>构建不可变的 RetryPolicy 实例</summary>
        public RetryPolicy Build()
        {
            if (_retryPredicates.Count == 0)
                _retryPredicates.Add(_ => true);

            return new RetryPolicy(this);
        }
    }

    #endregion

    #region Extension Methods

    /// <summary>
    /// RetryHelper 扩展方法 — 对 Func/Action 提供链式重试调用。
    /// </summary>
    public static class RetryHelperExtensions
    {
        /// <summary>使用默认策略重试</summary>
        public static T Retry<T>(this Func<T> action)
            => RetryPolicy.Default.Execute(action);

        /// <summary>使用默认策略重试（异步）</summary>
        public static Task<T> RetryAsync<T>(this Func<Task<T>> action)
            => RetryPolicy.Default.ExecuteAsync(action);

        /// <summary>使用指定策略重试</summary>
        public static T RetryWith<T>(this Func<T> action, RetryPolicy policy)
            => policy.Execute(action);

        /// <summary>使用指定策略重试（异步）</summary>
        public static Task<T> RetryWithAsync<T>(this Func<Task<T>> action, RetryPolicy policy)
            => policy.ExecuteAsync(action);

        /// <summary>无返回值默认重试</summary>
        public static void Retry(this Action action)
            => RetryPolicy.Default.Execute(action);
    }

    #endregion
}
