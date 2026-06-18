# LogHelper

高性能异步日志系统。建造者模式配置、异步队列写入、自动清理过期日志。

## 文件说明

| 文件 | 内容 |
|------|------|
| `LogHelper.cs` | 核心类：建造者、异步队列、文件写入、自动清理 |
| `LogManager.cs` | 全局管理器，静态 Current 属性 |
| `LogTypes.cs` | 14 种日志类型常量 |
| `LogExtensions.cs` | string/Exception 扩展方法 |

## 快速开始

```csharp
// 初始化
LogManager.Current = LogHelper.Build()
    .SetLogPath("D:/Logs")
    .SetRetentionMonths(3)
    .Start();

// 记录日志
"服务启动完成".LogInfo("App");
ex.LogError("Database");
"设备连接超时".LogPerOperation("Network");
"调试信息".LogDebug("Module");

// 手动添加
LogManager.Current.AddLog(LogTypes.Warning, "内存使用超过80%", "Monitor");
```

## 配置选项

```csharp
LogHelper.Build()
    .SetLogPath("D:/Logs")           // 必填：日志根路径
    .AddLogType("MyCustom")           // 添加自定义日志类型
    .SetRetentionMonths(6)            // 保留月数，默认 6
    .SetCleanupTime(3, 0)             // 每日清理时间，默认凌晨 2:00
    .EnableDailyCleanup(false)        // 禁用自动清理
    .UseConfigFile("logConfig.json")  // 从 JSON 加载日志类型
    .Start();
```

## 日志类型

| 类型 | 用途 |
|------|------|
| Debug | 调试信息 |
| Info | 一般信息 |
| Error | 错误 |
| Warning | 警告 |
| Performance | 性能 |
| Security | 安全 |
| Business | 业务 |
| Audit | 审计 |
| Operation | 操作记录 |
| TimerTask | 定时任务 |
| System | 系统 |
| Database | 数据库 |
| Api | API 调用 |
| Network | 网络 |

## 目录结构

```
D:/Logs/
  2025-06/
    06-17/
      Debug.log
      Info.log
      Error.log
      ...
```

## 安全操作

```csharp
// 安全添加（未初始化不抛异常）
LogManager.SafeAddLog(LogTypes.Error, "消息", "Module");

// 关闭
LogManager.Shutdown();
```
