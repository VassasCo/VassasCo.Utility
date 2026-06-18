# CrashDumpHelper

崩溃捕获工具。记录崩溃前的 FirstChance 异常链 + 生成 Windows MiniDump 文件。

## 文件说明

| 文件 | 内容 |
|------|------|
| `CrashDumpHelper.cs` | 全部功能：异常监听、崩溃日志、MiniDump 生成 |

## 快速开始

```csharp
// 1. 程序启动时初始化（越早越好）
CrashDumpHelper.Initialize();

// 2. 在 UnhandledException 中记录
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    var ex = (Exception)e.ExceptionObject;
    CrashDumpHelper.FlushAndWriteCrashLog(ex, "AppDomain.UnhandledException");
};
```

## API

```csharp
// 记录崩溃日志
CrashDumpHelper.WriteCrashLog(exception, "来源描述");

// 先 flush 日志系统再记录崩溃
CrashDumpHelper.FlushAndWriteCrashLog(exception, "来源描述");
```

## 生成的文件

崩溃后会在 `{程序目录}/CrashLogs/` 下生成：

| 文件 | 说明 |
|------|------|
| `Crash_20250617.log` | 崩溃日志（含 FirstChance 记录、异常详情、堆栈） |
| `CrashDump_20250617_143025_12345.dmp` | Windows MiniDump 文件 |

## 注意事项

- MiniDump 生成依赖 Windows `dbghelp.dll`，非 Windows 平台只生成日志
- FlushAndWriteCrashLog 通过反射调用 LogHelper，如果不存在则静默跳过
- 最多记录 50 条 FirstChance 异常（循环覆盖）
