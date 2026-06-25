# RetryHelper

智能重试器 — 支持退避策略 + 抖动 + 断路器 + 降级 + 超时，同时提供同步和异步 API。

## 文件说明

| 文件 | 内容 |
|------|------|
| `RetryHelper.cs` | 重试策略、Builder、断路器、退避算法、超时控制 |

## 核心特点

- **退避策略**：固定/线性/指数退避，可配置上下限
- **抖动**：全抖动/等量抖动/去相关抖动，避免雷群效应
- **断路器**：Closed → Open → HalfOpen 三态转换，熔断后自动恢复
- **超时控制**：单次超时 + 总超时双重保护
- **降级**：所有重试失败后执行 fallback
- **重试判断**：按异常类型/消息自定义重试条件
- **完整报告**：每次尝试的耗时/等待/异常全部记录
- **同步+异步**：`Execute` / `ExecuteAsync` 双 API

## 快速开始

```csharp
// 默认策略（3 次，指数退避 1s→30s，全抖动）
await RetryPolicy.Default.ExecuteAsync(async () =>
{
    await CallUnstableApiAsync();
});

// 自定义策略
var policy = RetryPolicy.Build()
    .WithMaxRetries(5)
    .WithBackoff(BackoffType.Exponential, baseInterval: TimeSpan.FromSeconds(1))
    .WithMaxInterval(TimeSpan.FromMinutes(1))
    .WithJitter(JitterType.Full)
    .WithPerAttemptTimeout(TimeSpan.FromSeconds(30))
    .WithTotalTimeout(TimeSpan.FromMinutes(5))
    .WithCircuitBreaker(failureThreshold: 3, breakDuration: TimeSpan.FromSeconds(60))
    .RetryOn<HttpRequestException>()
    .OnRetry(ctx => Console.WriteLine($"第 {ctx.Attempt} 次重试..."))
    .Build();

// 执行（有降级）
var result = policy.Execute(
    () => CallService(),
    fallback: () => "兜底值"
);
```

## 断路器

```csharp
var cb = new CircuitBreaker(
    failureThreshold: 3,        // 连续失败 3 次后熔断
    breakDuration: TimeSpan.FromSeconds(30)  // 30s 后半开试探
);

cb.StateChanged += (s, e) =>
    Console.WriteLine($"{e.OperationKey}: {e.OldState} → {e.NewState}");
```
