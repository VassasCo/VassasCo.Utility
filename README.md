# VassasCo.Utility

C# 桌面开发工具库 — 面向 WinForm / WPF / Avalonia 的高质量通用组件集合。

## 包含模块

| 模块 | NuGet 包名 | 简介 |
|------|-----------|------|
| ConfigHelper | `VassasCo.Utility.ConfigHelper` | 零代码实体类 ⇋ JSON/XML 配置双向映射 |
| LogHelper | `VassasCo.Utility.LogHelper` | 异步高性能日志系统（14 种日志类型） |
| CrashDumpHelper | `VassasCo.Utility.CrashDumpHelper` | 崩溃捕获 + MiniDump 生成 |
| SnowflakeIdHelper | `VassasCo.Utility.SnowflakeIdHelper` | 分布式雪花 ID 生成器（防时钟回拨） |
| ScheduleHelper | `VassasCo.Utility.ScheduleHelper` | 全能定时任务调度器（CRON/固定速率） |
| RetryHelper | `VassasCo.Utility.RetryHelper` | 智能重试器（退避+抖动+断路器+降级） |
| EventBus | `VassasCo.Utility.EventBus` | 进程内事件总线（类型安全、特性注册、粘性事件） |
| ExcelMapper | `VassasCo.Utility.ExcelMapper` | 原生对象→Excel 映射引擎 |

## 安装

```bash
# 一键安装全部模块
dotnet add package VassasCo.Utility

# 或按需安装单个模块
dotnet add package VassasCo.Utility.SnowflakeIdHelper
dotnet add package VassasCo.Utility.RetryHelper
```

## 快速开始

```csharp
// 雪花 ID
long id = SnowflakeIdHelper.Next();

// 重试
await RetryPolicy.Default.ExecuteAsync(async () => await CallApiAsync());

// 配置
var cfg = ConfigFactory.Load<AppConfig>();
ConfigFactory.Save(cfg);

// 日志
LogHelper.LogInfo("服务启动", "System");

// Excel 导出
ExcelMapper.ToFile(orders, "orders.xlsx");

// 事件总线
EventBus.Default.Subscribe<OrderCreatedEvent>(e => HandleOrder(e));
EventBus.Default.Publish(new OrderCreatedEvent("ORD-001", 99.9m));
```

## 许可

MIT License
