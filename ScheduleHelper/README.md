# ScheduleHelper

全能定时任务调度器。支持 CRON/FixedRate/FixedDelay 三种模式，内置重试、超时、日历过滤、线程池管理。

## 文件说明

| 文件 | 内容 |
|------|------|
| `Models.cs` | 枚举、数据结构（JobStats、JobContext、JobResult、JobEventArgs）、委托 |
| `CronExpression.cs` | CRON 表达式解析器（5 段格式，支持 *,-/?L 特殊字符） |
| `CronBuilder.cs` | CRON 表达式生成器（20+ 预设 + 流式 API） |
| `ScheduleCore.cs` | JobBuilder、ScheduleBuilder、ScheduleHelper 主类、ScheduleLogWriter |
| `HolidayCalendar.cs` | 节假日日历（JSON 加载、调休冲突检测、安全加载） |

## 快速开始

```csharp
// 创建调度器
var scheduler = ScheduleHelper.Build()
    .SetMaxConcurrency(4)
    .SetLogPath("D:/SchedulerLogs")
    .AddJob(j => j
        .SetName("OrderSync")
        .SetCron("0 */5 * * *")                    // 每 5 分钟
        .SetJob(ctx => SyncOrdersAsync(ctx))        // 任务体
        .SetTimeout(TimeSpan.FromMinutes(2))         // 超时
        .SetRetry(3, TimeSpan.FromSeconds(5)))       // 重试 3 次，间隔 5s
    .AddJob(j => j
        .SetName("CacheRefresh")
        .SetFixedDelay(TimeSpan.FromSeconds(30))     // 每次完成后等 30s
        .SetJob(ctx => RefreshCache(ctx))
        .SetPolicy(JobPolicy.MultiInstance))         // 允许多例并发
    .Build();

// 优雅关闭
await scheduler.ShutdownAsync();
```

## 调度模式

```csharp
// CRON: 在指定的时间点触发
.SetCron("0 9 * * 1-5")     // 工作日早 9 点

// FixedRate: 按固定速率触发（不管上次是否完成）
.SetFixedRate(TimeSpan.FromMinutes(1))

// FixedDelay: 上次完成后等 N 秒再触发
.SetFixedDelay(TimeSpan.FromSeconds(30))
```

## 事件监听

```csharp
scheduler.OnStarting += (s, e) => Console.WriteLine($"任务 {e.JobName} 开始");
scheduler.OnCompleted += (s, e) => Console.WriteLine($"任务 {e.JobName} 完成");
scheduler.OnError += (s, e) => Console.WriteLine($"任务 {e.JobName} 出错");
scheduler.OnSkipped += (s, e) => Console.WriteLine($"任务 {e.JobName} 被跳过");
```

## 执行统计

```csharp
var stats = scheduler.GetAllStats();
foreach (var s in stats)
{
    Console.WriteLine($"{s.JobName}: 成功率 {s.SuccessRate:P}");
    Console.WriteLine($"  平均耗时 {s.AverageDuration.TotalMilliseconds:F1}ms");
}
```

## 动态修改

```csharp
// 运行时修改 CRON 表达式
scheduler.UpdateCron("OrderSync", "0 */10 * * *");

// 运行时修改间隔
scheduler.UpdateInterval("CacheRefresh", TimeSpan.FromMinutes(1));
```

## 重试配置

```csharp
.SetRetry(5, TimeSpan.FromSeconds(2), exponentialBackoff: true)
.AddRetryableException<TimeoutException>(shouldRetry: true)
.AddRetryableException<ArgumentException>(shouldRetry: false) // 参数错误不重试
```

## 日历过滤

```csharp
// 使用 HolidayCalendar 跳过节假日
var calendar = HolidayCalendar.Load("holidays.json");
.SetCalendarFilter(calendar.ToWorkdayFilter())

// 自定义过滤
.SetCalendarFilter(dt => dt.DayOfWeek != DayOfWeek.Sunday)
```

## CRON 表达式快捷生成

```csharp
CronBuilder.EveryDayAt(9, 0)        // "0 9 * * *"
CronBuilder.EveryWeekdayAt(10, 0)   // "0 10 * * 1-5"
CronBuilder.Every5Minutes()          // "*/5 * * * *"
CronBuilder.LastDayOfMonth(23, 59)   // "59 23 L * ?"

// 流式构建
CronBuilder.Create()
    .AtMinute(0).AtHour(8, 12, 16)
    .OnWeekdays()
    .Build()                         // "0 8,12,16 * * 1-5"
```

## HolidayCalendar

```json
{
  "holidays": [
    { "date": "2025-01-01", "name": "元旦" },
    { "date": "2025-10-01", "name": "国庆节" }
  ],
  "workdays": ["2025-01-26", "2025-02-08"]
}
```

```csharp
// 加载
var calendar = HolidayCalendar.Load("holidays.json");

// 或代码添加
var calendar = new HolidayCalendar()
    .AddHoliday(DateTime.Today, "测试假日")
    .AddWorkday(new DateTime(2025, 6, 28));

// 查询
bool isHoliday = calendar.IsHoliday(DateTime.Today);
bool isWorkday = calendar.IsWorkday(DateTime.Today);  // 调休日优先

// 安全加载（不抛异常）
if (HolidayCalendar.TryLoad("bad.json", out var cal, out var error))
    Console.WriteLine("加载成功");
else
    Console.WriteLine($"加载失败: {error}");

// 冲突检测（holidays 和 workdays 都有同一日期）
var conflicts = calendar.GetConflicts(); // 調休规则下视为工作日
```

## 日志

调度器内置独立日志系统，不依赖 LogHelper。格式：
```
[2025-06-17 14:30:00.123] [Schedule] [INFO] 任务 [OrderSync] 开始执行 (第 5 次)
```

目录结构：`LogPath/yyyy-MM/MM-dd/Schedule.log`
