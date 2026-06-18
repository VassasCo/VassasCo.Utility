# VassasCo.Utility

C# 桌面开发工具库（WinForm / WPF / Avalonia），包含 7 个功能模块。

## NuGet 安装

### 安装全部功能

```bash
dotnet add package VassasCo.Utility
```

### 按需安装单独模块

```bash
dotnet add package VassasCo.Utility.ConfigHelper
dotnet add package VassasCo.Utility.LogHelper
dotnet add package VassasCo.Utility.CrashDumpHelper
dotnet add package VassasCo.Utility.SnowflakeIdHelper
dotnet add package VassasCo.Utility.ScheduleHelper
dotnet add package VassasCo.Utility.RetryHelper
dotnet add package VassasCo.Utility.ExcelMapper
```

## 模块概览

| 模块 | 功能 |
|------|------|
| **ConfigHelper** | 零代码实体类与 JSON/XML 配置文件双向映射。支持注释、热重载、原子保存、列表展开 |
| **LogHelper** | 异步高性能日志系统。建造者模式、异步队列、自动清理、14 种日志类型 |
| **CrashDumpHelper** | 崩溃捕获 + FirstChance 异常追踪 + MiniDump 生成 |
| **SnowflakeIdHelper** | 分布式雪花 ID 生成器。单调时钟杜绝回拨、集群 WorkerId 分配、ID 反解 |
| **ScheduleHelper** | 全能定时任务调度器。CRON / 固定速率 / 固定延迟、重试退避、超时取消、日历过滤、线程池、优雅关闭 |
| **RetryHelper** | 智能重试器。指数退避 + 抖动 + 断路器三态 + 降级 + 超时，同步 / 异步 |
| **ExcelMapper** | 原生对象到 Excel 映射。`[ExcelDisplay]` 自定义列标题、嵌套类子表头合并、数组独立 Sheet（子 Sheet 自动关联父行）、Dictionary 自适应、全可配置样式 |

## 兼容性

- .NET Standard 2.0
- .NET 6
- .NET 8

支持 WinForm / WPF / Avalonia 桌面应用。

## 许可证

MIT License
