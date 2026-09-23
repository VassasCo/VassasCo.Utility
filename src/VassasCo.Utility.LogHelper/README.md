# VassasCo.Utility.LogHelper

轻量级异步文件日志库（桌面端/服务端通用），零外部依赖。  
「分类」决定文件、「级别」写入时配置，支持按天分目录、跨天自动切换、批量刷盘、定时清理与可靠关闭。

[![MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 目录

- [功能总览](#功能总览)
- [目标框架与依赖](#目标框架与依赖)
- [快速开始](#快速开始)
- [核心设计：分类与级别](#核心设计分类与级别)
- [写入 API](#写入-api)
- [文件组织与日志格式](#文件组织与日志格式)
- [配置参考](#配置参考)
- [可靠性特性](#可靠性特性)
- [安全策略](#安全策略)
- [API 速查](#api-速查)
- [测试](#测试)

---

## 功能总览

| 类别 | 能力 |
|------|------|
| **异步写入** | 有界队列 + 后台单线程消费，调用方不阻塞 |
| **分类分文件** | 分类决定文件名（`Api.log`、`Database.log`…），级别作为写入参数 |
| **级别过滤** | 低于最低级别的日志直接丢弃 |
| **文件组织** | 按「分类」分文件、按「天」分目录（`yyyy-MM/MM-dd`） |
| **跨天切换** | 零点自动关闭旧文件、新建当天文件 |
| **批量刷盘** | 定时批量 `Flush`，避免每条日志触发 IO |
| **定时清理** | 按「月/日」目录安全解析并删除过期日志 |
| **可靠关闭** | 停止接收 → 排空队列 → 关闭 writer，不丢日志 |
| **异常回调** | 后台写入/清理错误经 `OnError` 上报，不静默 |
| **线程安全** | `AddLog` 可任意线程并发调用 |

---

## 目标框架与依赖

| 目标框架 | 依赖 |
|----------|------|
| netstandard2.0 | 无（仅 BCL） |
| net6.0 | 无（仅 BCL） |
| net8.0 | 无（仅 BCL） |

---

## 快速开始

```csharp
using VassasCo.Utility;

// 启动全局日志器
LogManager.Current = LogHelper.Build()
    .SetLogPath("D:/Logs")
    .SetMinLevel(LogLevel.Info)
    .SetRetentionDays(30)
    .Start();

// 扩展方法：方法名即分类，级别作为参数
"用户登录成功".LogSecurity(LogLevel.Info);
"数据库连接失败".LogDatabase(LogLevel.Error);
"接口响应超时".LogApi(LogLevel.Error, "订单服务");   // 模块为可选字段

// 程序退出时可靠关闭（排空队列不丢日志）
LogManager.Shutdown();
```

---

## 核心设计：分类与级别

「分类」与「级别」是两个正交维度：

| 维度 | 类型 | 作用 |
|------|------|------|
| 分类 | `LogCategories` 常量（自由字符串） | **决定写入哪个文件**（`Api.log`、`Database.log`…） |
| 级别 | `LogLevel` 枚举 | **写入时作为参数传入**，用于最低级别过滤与内容标注 |

`LogLevel` 枚举：`Trace < Debug < Info < Warning < Error < Fatal`。

`LogCategories` 常量：

| 常量 | 说明 |
|------|------|
| `General` | 默认/通用 |
| `Security` | 安全/鉴权 |
| `Performance` | 性能指标 |
| `Business` | 业务逻辑 |
| `Audit` | 审计 |
| `Operation` | 运维/操作 |
| `TimerTask` | 定时任务 |
| `System` | 系统/框架 |
| `Database` | 数据库访问 |
| `Api` | 接口调用 |
| `Network` | 网络通信 |

---

## 写入 API

### 扩展方法（按分类命名，方法名决定文件）

```csharp
message.Log()                    // General
message.LogSecurity(level)       // Security
message.LogPerformance(level)    // Performance
message.LogBusiness(level)       // Business
message.LogAudit(level)          // Audit
message.LogOperation(level)      // Operation
message.LogTimerTask(level)      // TimerTask
message.LogSystem(level)         // System
message.LogDatabase(level)       // Database
message.LogApi(level)            // Api
message.LogNetwork(level)        // Network
```

统一签名：

```csharp
Log{Category}(this string message, LogLevel level = LogLevel.Info, string? module = null)
```

### 直接写入（任意自定义分类）

```csharp
logger.AddLog("Payment", LogLevel.Error, "支付失败", "微信支付");
//             ↑分类(决定文件)  ↑级别        ↑消息       ↑可选模块
```

### 异常写入

```csharp
exception.LogError(LogCategories.Api);   // Error 级别，递归展开 InnerException
```

---

## 文件组织与日志格式

```
LogPath/
└── 2025-06/
    └── 06-17/
        ├── General.log
        ├── Api.log
        ├── Database.log
        ├── Security.log
        └── ...
```

按「分类」分文件、按「天」分目录（`yyyy-MM/MM-dd`）。

日志格式：

```
[2025-06-17 12:30:45.123] [Error] [订单服务] 接口响应超时
```

- 分类体现在**文件名**里，不再写入每行内容。
- `[级别]` 始终写入；`[模块]` 仅在传入 `module` 时出现。

---

## 配置参考

### LogHelperBuilder（链式配置）

| 方法 | 默认值 | 说明 |
|------|--------|------|
| `SetLogPath(path)` | `"Logs"` | 日志根目录 |
| `SetMinLevel(level)` | `Info` | 最低记录级别，低于则丢弃 |
| `SetRetentionDays(days)` | `30` | 日志保留天数（最小 1） |
| `SetQueueCapacity(n)` | `100000` | 有界队列容量（最小 100），满时丢弃新日志并触发回调 |
| `SetAutoFlushInterval(t)` | `2s` | 批量刷盘间隔 |
| `SetCleanupInterval(t)` | `1h` | 过期日志清理扫描间隔 |
| `EnableDailyCleanup(bool)` | `true` | 是否启用按天自动清理 |
| `OnError(handler)` | `null` | 后台写入/清理错误回调 |
| `Start()` | — | 构建并启动日志器 |

---

## 可靠性特性

- **异步写入**：有界队列 + 后台单线程消费，`AddLog` 立即返回不阻塞调用方。
- **跨天自动切换**：`StreamWriter` 按「分类 + 日期」缓存，零点后自动关闭旧文件、新建当天文件，杜绝日志写进前一天的 bug。
- **批量刷盘**：不再每条 `Flush`，改为定时批量落盘，显著降低 IO 开销。
- **定时清理**：按「月/日」目录安全解析并删除过期日志。
- **可靠关闭**：`Dispose` / `Shutdown` 先停止接收、再排空队列、最后关闭所有 writer，不丢日志。

---

## 安全策略

| 策略 | 说明 |
|------|------|
| **分类文件名清洗** | 分类被用作文件名，自动移除非法文件名字符、路径分隔符、路径穿越（`..`），从根源杜绝路径注入 |
| **异常不静默** | 后台错误经 `OnError` 回调上报；回调自身异常被吞掉不影响主流程 |
| **线程安全** | `AddLog` 可任意线程并发调用，内部用 `BlockingCollection` + 锁保证安全 |
| **内存可见性** | `_disposed` 等跨线程标志用 `Volatile` / `Interlocked` 保证可见性 |

---

## API 速查

### LogHelper（实例）

| 方法 | 说明 |
|------|------|
| `Build()` | 创建链式构建器 `LogHelperBuilder` |
| `AddLog(category, level, message, module?)` | 写入一条日志（线程安全） |
| `Flush()` | 立即将所有缓冲日志刷盘 |
| `Dispose()` | 停止接收、排空队列、释放资源（幂等） |

### LogManager（静态门面）

| 成员 | 说明 |
|------|------|
| `Current` | 获取/设置全局日志器；读取未初始化时抛 `InvalidOperationException` |
| `IsInitialized` | 是否已初始化 |
| `Shutdown()` | 停止并释放全局日志器（幂等） |

### LogHelperExtensions（扩展方法）

| 方法 | 说明 |
|------|------|
| `Log(level?, module?)` | General 分类 |
| `LogSecurity / LogPerformance / ...` | 对应分类，共 11 个 |
| `LogError(category?, module?)` | 记录异常为 Error 级别，递归展开 `InnerException` |

---

## 测试

```bash
cd VassasCo.Utility
dotnet test
```

12 个单元测试覆盖：基本写入、按分类分文件、最低级别过滤、空分类回退、模块字段、分类文件名清洗、写入失败回调、可靠关闭排空、并发线程安全、分类扩展方法、异常链记录、全局管理器生命周期。

---

## 许可证

MIT
