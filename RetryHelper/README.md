# RetryHelper

智能重试器 — 支持退避策略（固定/线性/指数）+ 抖动 + 断路器 + 降级 + 超时。

## 核心功能

- **退避策略**：固定间隔 / 线性增长 / 指数退避
- **抖动类型**：Full（全抖动）/ Equal（等量）/ Decorrelated（去相关）/ None（无）
- **断路器**：Closed → Open → HalfOpen 三态自动切换
- **超时控制**：单次执行超时 + 总超时
- **降级 Fallback**：重试耗尽后执行备选逻辑
- **重试回调**：每次失败后可执行自定义逻辑
- **同步 + 异步**：完整的同步/异步 API，支持 CancellationToken
- **扩展方法**：`Func<T>.Retry()` / `Func<T>.RetryWith(policy)` 等链式调用

## 快速开始

### 基础用法

```csharp
// 使用默认策略（3次，指数退避 1s→30s，全抖动）
var result = RetryPolicy.Default.Execute(() => CallRemoteService());

// 自定义策略
var policy = RetryPolicy
    .Build()
    .WithMaxRetries(5)
    .WithExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1))
    .WithJitter(JitterType.Full)
    .When<HttpRequestException>()
    .When<TimeoutException>()
    .Build();

var result = policy.Execute(() => CallRemoteService());
```

### 异步使用

```csharp
var result = await policy.ExecuteAsync(async ct =>
{
    return await httpClient.GetAsync(url, ct);
});
```

### 断路器

```csharp
var policy = RetryPolicy
    .Build()
    .WithCircuitBreaker(failures: 3, breakDuration: TimeSpan.FromSeconds(30))
    .WithOperationKey("PaymentService")
    .OnCircuitStateChanged((s, e) =>
    {
        Console.WriteLine($"断路器: {e.OldState} → {e.NewState}");
    })
    .Build();
```

### 降级 Fallback

```csharp
var result = policy.ExecuteWithFallback(
    () => CallPrimaryService(),
    report => CallBackupService()
);
```

### 扩展方法

```csharp
// 使用默认策略重试
var result = (() => CallService()).Retry();

// 使用自定义策略
var result = (() => CallService()).RetryWith(policy);

// 异步
var result = await (() => CallServiceAsync()).RetryAsync();
```

## Builder 配置选项

| 方法 | 说明 |
|------|------|
| `WithMaxRetries(n)` | 设置最大重试次数（含首次执行），默认 3 |
| `WithFixedBackoff(interval)` | 固定间隔退避 |
| `WithLinearBackoff(base, max)` | 线性增长退避 |
| `WithExponentialBackoff(base, max)` | 指数退避（默认） |
| `WithJitter(type)` | 设置抖动类型，默认 Full |
| `WithCircuitBreaker(failures, duration)` | 配置断路器 |
| `WithOperationKey(key)` | 设置操作标识键 |
| `OnCircuitStateChanged(handler)` | 订阅断路器状态变更 |
| `When<TException>()` | 仅对指定异常类型重试 |
| `When(predicate)` | 自定义重试判断条件 |
| `WithPerAttemptTimeout(timeout)` | 单次执行超时 |
| `WithTotalTimeout(timeout)` | 总重试超时 |
| `OnRetry(callback)` | 重试回调 |

## 类型

| 类型 | 说明 |
|------|------|
| `RetryPolicy` | 重试策略，不可变，线程安全 |
| `RetryPolicyBuilder` | Builder，链式配置 |
| `CircuitBreaker` | 断路器，三态切换 |
| `RetryContext` | 单次尝试上下文 |
| `RetryReport` | 重试最终报告 |
| `RetryExhaustedException` | 重试耗尽异常（含报告） |
| `CircuitBrokenException` | 断路器熔断异常 |
