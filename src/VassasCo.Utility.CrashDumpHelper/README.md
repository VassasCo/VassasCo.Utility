# VassasCo.Utility.CrashDumpHelper

崩溃捕获工具 —— 记录崩溃前的 **FirstChance 异常链** + 生成 **Windows MiniDump** 文件。

程序崩了之后最缺的往往不是当前异常，而是**它之前发生了什么**。本库在进程存活期间
持续采集 FirstChance 异常（O(1) 环形缓冲），崩溃时把它们连同异常详情、进程状态、
调用堆栈一起落到日志文件，并在 Windows 上生成可供 WinDbg/VS 分析的 `.dmp`。

[![MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 目录

- [功能总览](#功能总览)
- [目标框架与依赖](#目标框架与依赖)
- [捕获范围与盲区](#捕获范围与盲区)
- [快速开始](#快速开始)
- [生成的文件](#生成的文件)
- [配置参考](#配置参考)
- [API 速查](#api-速查)
- [注意事项](#注意事项)
- [测试](#测试)

---

## 功能总览

| 类别 | 能力 |
|------|------|
| **异常链追踪** | 采集崩溃前最近 50 条 FirstChance 异常（O(1) 环形缓冲，覆盖式） |
| **崩溃日志** | 崩溃时间、来源、进程 ID / 名、运行时长、内存、线程数、完整堆栈 |
| **MiniDump** | Windows 下生成 `.dmp`，转储类型可配置（默认 / 完整内存） |
| **可配置** | 输出目录、是否生成 dump、dump 类型、保留天数、是否 flush 日志、是否开启内置跟踪日志 |
| **自动清理** | 按保留天数自动删除过期的 `Crash_*.log` 与 `CrashDump_*.dmp` |
| **自动接管** | 初始化时自动订阅 `UnhandledException` 与 `UnobservedTaskException`（可关闭） |
| **异常降噪** | `ShouldIgnore` 过滤器剔除把异常当控制流产生的噪声 |
| **缓冲保护** | 环形缓冲写满时自动落盘，避免崩溃前的线索被覆盖 |
| **重入保护** | `Initialize()` 重复调用安全，不会重复注册 handler，也不会覆盖生效中的配置 |
| **可注销** | `Deinitialize()` 移除监听，适用于插件卸载 / 热重载 |
| **诊断回调** | 目录创建失败、dump 失败等隐性问题可经回调上报 |
| **内置跟踪日志** | 可选把本库自身的运行节点写入独立的 `Trace_*.log`；默认关闭，零开销 |
| **零依赖** | 不引用任何第三方包或自家日志库，多项目并存不会重复安装 |

---

## 目标框架与依赖

| 目标框架 | 依赖 |
|----------|------|
| net472 | 无 |
| netstandard2.0 | 无 |
| net6.0 | 无 |
| net8.0 | 无 |

> 新增 **net472**：供 .NET Framework 4.7.2+（含 4.8）传统桌面项目直接引用。
> 构建期通过 `Microsoft.NETFramework.ReferenceAssemblies`（`PrivateAssets=all`）提供目标包，
> **不会作为依赖传递给使用方**。
>
> **变更（2.2.0）**：本包已**零外部依赖**。此前为了「写崩溃日志前 flush 日志队列」这一个动作，
> 引入了 `VassasCo.Utility.LogHelper` 的项目引用，代价是 NuGet 自动生成传递依赖、
> 多项目并存时会重复安装同一份日志库。该职责现由库内的 `CrashDumpTrace` 承担
> （可选开启，见 `EnableTraceLog`）。

---

## 捕获范围与盲区

### 能捕获的

`FirstChanceException` 在**异常抛出时、被任何 `catch` 处理之前**触发，且是
**进程级、线程无关**的。因此下列场景都会被采集进环形缓冲：

| 场景 | 说明 |
|------|------|
| 后台 `new Thread(...)` / 线程池 / `Task.Run` | 不受线程影响 |
| `async / await` 中的异常 | 在抛出瞬间即被采集 |
| `async void` 中的异常 | 同上 |
| **已被 `try/catch` 吞掉的异常** | 这是 FirstChance 相对 `UnhandledException` 的独特价值 |
| 第三方库内部 catch 的异常 | 同上 |

此外 `Initialize()` 默认自动订阅：

- `AppDomain.UnhandledException` —— 未处理异常兜底落盘并生成 dump；
- `TaskScheduler.UnobservedTaskException` —— .NET Framework 4.5 起未观察的 Task 异常
  默认**不再终止进程**而是静默丢弃，只有订阅该事件才能把它们留下。

### 捕获不到的

| 盲区 | 原因 |
|------|------|
| **WinForms UI 线程异常** | 消息泵默认兜底到 `Application.ThreadException`，不冒泡到 `UnhandledException`。需调用方自行订阅，见上文示例 |
| `StackOverflowException` | .NET Framework 无法捕获，进程立即终止，来不及落盘 |
| `Environment.FailFast()` | 不触发任何托管事件 |
| 非托管崩溃 | 堆损坏、`AccessViolationException`（corrupted state）、第三方 native DLL |
| 进程被外部终止 | 任务管理器、`Process.Kill()`、系统关机、OOM |
| `Initialize()` 之前抛出的异常 | 监听尚未注册 |
| 其它 AppDomain 的异常 | 该事件按 AppDomain 注册，仅覆盖当前 AppDomain |

环形缓冲容量为 50 条且为覆盖式。若业务代码把异常当控制流使用，崩溃前的关键线索会被噪声冲掉；
此时建议配置 `ShouldIgnore` 过滤，或依赖缓冲写满时的自动落盘。

---

## 快速开始

### 安装

```bash
dotnet add package VassasCo.Utility.CrashDumpHelper
```

### 最小用法

```csharp
using VassasCo.Utility;

static void Main()
{
    // 一行接管：FirstChance 采集 + 未处理异常 + 未观察 Task 异常
    CrashDumpHelper.Initialize();

    Application.Run(new MainForm());
}
```

`Initialize()` 已自动订阅 `AppDomain.UnhandledException` 与
`TaskScheduler.UnobservedTaskException`，因此上面的写法即为完整接管，无需再手动挂事件。

### 带配置

```csharp
CrashDumpHelper.Initialize(new CrashDumpOptions
{
    LogDirectory              = @"D:\Logs\Crash",   // 不填则落到 {程序目录}\CrashLogs
    GenerateDump             = true,
    DumpType                 = MiniDumpType.Default,
    RetentionDays            = 30,                  // 0 或负数 = 不清理
    FlushLogger              = true,                // 崩溃前先把内置跟踪日志缓冲排空
    EnableTraceLog           = false,               // 本库自身的运行轨迹写入 Trace_*.log
    HookUnhandledException   = true,                // 自动接管未处理异常
    HookUnobservedTaskException = true,             // 自动接管未观察 Task 异常
    AutoFlushWhenFull        = true,                // 缓冲写满时自动落盘
    ShouldIgnore             = ex => ex is OperationCanceledException,
    Diagnostics               = msg => Console.WriteLine(msg)
});
```

若调用方已自行订阅 `UnhandledException`，请把 `HookUnhandledException` 设为 `false`，
否则同一次崩溃会写两份日志。

### WinForms：补充接管 UI 线程异常

WinForms 的消息泵会自行兜底未处理异常：默认模式下 UI 线程抛出的异常走
`Application.ThreadException`，**不会**冒泡到 `AppDomain.UnhandledException`。

本库不在内部订阅该事件，原因是它会让所有下游项目（含控制台、Windows 服务、
.NET 6+ 非 WinForms 工程）都被迫引用 `System.Windows.Forms`。调用方只需补一行：

```csharp
[STAThread]
static void Main()
{
    CrashDumpHelper.Initialize(new CrashDumpOptions { RetentionDays = 30 });

    // 必须在创建任何窗体之前设置，否则 SetUnhandledExceptionMode 会抛异常
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    Application.ThreadException += (s, e) =>
        CrashDumpHelper.FlushAndWriteCrashLog(e.Exception, "Application.ThreadException");

    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new MainForm());
}
```

注意：接管 `ThreadException` 后进程默认不再退出，通常需要在此提示用户选择继续或退出，
避免带着已损坏的状态继续运行。

---

## 生成的文件

默认在 `{程序目录}\CrashLogs\` 下生成：

| 文件 | 说明 |
|------|------|
| `Crash_20250617.log` | 崩溃日志（含 FirstChance 记录、异常详情、进程状态、堆栈） |
| `CrashDump_20250617_143025_12345.dmp` | Windows MiniDump 文件（仅 Windows） |
| `Trace_20250617.log` | 本库自身的运行轨迹（`EnableTraceLog = true` 时才有） |

跟踪日志记录的是「本库在做了什么」，与崩溃日志相互独立，便于区分
「程序为什么崩」和「崩溃库有没有正常工作」。

日志结构：

```
========================================
崩溃时间: 2025-06-17 14:30:25.123
来源: AppDomain.UnhandledException
进程ID: 12345
进程名: TestSystem
运行时长: 3600 秒
内存使用: 256 MB
线程数: 32
========================================

【崩溃前最近异常记录 (FirstChance)】
----------------------------------------
[14:30:24.900] IOException: 文件正由另一进程使用  ← DeviceBase.cs:第128行
[14:30:25.010] NullReferenceException: 未将对象引用设置到对象的实例  ← MainForm.cs:第452行
----------------------------------------

【崩溃异常详情】
----------------------------------------
...（递归展开 InnerException / AggregateException）...
----------------------------------------

【崩溃处理时的调用堆栈】
----------------------------------------
...
========================================
```

---

## 配置参考

### CrashDumpOptions

| 属性 | 默认值 | 说明 |
|------|--------|------|
| `LogDirectory` | `null` | 输出目录；为 null 时用 `{程序目录}/CrashLogs`。相对路径基于**程序目录**解析（不是当前工作目录） |
| `GenerateDump` | `true` | 是否生成 MiniDump（非 Windows 平台自动跳过） |
| `DumpType` | `MiniDumpType.Default` | 转储类型；`Full` 信息最全但文件极大 |
| `RetentionDays` | `30` | 崩溃文件保留天数；`<= 0` 表示不清理 |
| `FlushLogger` | `true` | 写崩溃日志前后是否排空内置跟踪日志的缓冲 |
| `EnableTraceLog` | `false` | 是否把本库自身的运行轨迹写入 `Trace_yyyyMMdd.log`。关闭时零开销、零文件 |
| `HookUnhandledException` | `true` | 初始化时自动订阅 `AppDomain.UnhandledException` |
| `HookUnobservedTaskException` | `true` | 初始化时自动订阅 `TaskScheduler.UnobservedTaskException` |
| `AutoFlushWhenFull` | `true` | 环形缓冲写满时自动落盘并清空 |
| `ShouldIgnore` | `null` | 异常过滤器；返回 true 表示不纳入缓冲。回调自身抛异常时按「不忽略」处理 |
| `Diagnostics` | `null` | 诊断回调；未配置时静默忽略 |

### MiniDumpType

常用取值：

| 值 | 说明 |
|------|------|
| `Normal` | 仅基本信息（堆栈、模块列表、线程上下文），文件最小 |
| `Default` | 覆盖线程/模块/代码段信息，忽略不可访问内存以保证成功率（**推荐**） |
| `Full` | 完整内存转储，信息最全但文件极大（约等于进程工作集大小） |

其余标志位与 dbghelp `MINIDUMP_TYPE` 一一对应，可自由组合。

---

## API 速查

| 成员 | 说明 |
|------|------|
| `Initialize()` | 使用默认配置注册全部监听（幂等） |
| `Initialize(CrashDumpOptions)` | 使用指定配置初始化 |
| `Deinitialize()` | 移除全部监听并复位状态与缓冲（幂等） |
| `IsInitialized` | 是否已初始化 |
| `Options` | 当前生效的配置 |
| `WriteCrashLog(Exception, source)` | 记录崩溃日志（自动异常展开） |
| `WriteCrashLog(string, source)` | 记录崩溃日志（自定义消息） |
| `FlushAndWriteCrashLog(Exception, source)` | 先排空内置跟踪日志缓冲，再记录崩溃日志 |
| `GetFirstChanceSnapshot()` | 取出当前 FirstChance 缓冲快照（旧 → 新） |
| `FlushFirstChanceLog(source)` | 把当前缓冲落盘并清空，返回条数；不生成 MiniDump |
| `CleanupExpiredFiles()` | 手动清理过期的崩溃文件，返回删除数量 |
| `FirstChanceMaxCount` | 环形缓冲容量常量（50） |

---

## 注意事项

- **MiniDump 依赖 Windows `dbghelp.dll`**，非 Windows 平台只生成日志、跳过 dump（会在日志里写明原因）。
- **零依赖，不与任何日志库耦合**：本库既不引用也不假设宿主用了什么日志框架。
  若需要在崩溃前排空宿主日志队列，请自行在 `Diagnostics` 回调或自己的
  `UnhandledException` 处理里调用 —— 由宿主掌握自己的生命周期，比由本库反向依赖更合理。
  本库自身的可选 `Trace_*.log` 与宿主日志互不干扰。
- **崩溃文件清理只匹配本库前缀**（`Crash_*.log` / `CrashDump_*.dmp` / `Trace_*.log`），
  不会误删同目录下的其他文件。
- **FirstChance 采集有性能代价**：每次异常都会构造一行字符串。正常业务里异常应该罕见，但若代码把异常当控制流用，开销会放大。
- **环形缓冲覆盖式**：超过 50 条后最旧的会被挤出 —— 开启 `AutoFlushWhenFull`（默认）时，
  被挤出前会先落盘，因此不会真正丢失。若异常极其频繁（如把异常当控制流使用），
  建议设 `AutoFlushWhenFull = false` 并配置 `ShouldIgnore` 降噪，避免频繁写磁盘。
- **自动落盘只写日志、不生成 MiniDump**：缓冲满是预警信号而非崩溃，
  生成 dump 既慢又会迅速撑满磁盘。真正的崩溃仍会走 `FlushAndWriteCrashLog` 生成 dump。

---

## 测试

```bash
cd VassasCo.Utility
dotnet test
```

CrashDumpHelper 单元测试覆盖：初始化重入保护（重复调用只注册一次）、
Deinitialize 后不再采集、FirstChance 环形缓冲覆盖与快照顺序、
崩溃日志写出与文件命名、保留天数清理、内置跟踪日志的开启/关闭/注销复位与过期清理。

---

## 更新说明

### 2.2.0（当前）

**新增**

- **内置跟踪日志**（`EnableTraceLog`，默认关闭）：把本库自身的运行节点
  （初始化、注销、FirstChance 缓冲落盘、崩溃日志写入、dump 生成结果、过期清理）
  写入独立的 `Trace_yyyyMMdd.log`。采用「缓冲 + 阈值自动落盘 + 崩溃时显式落盘」，
  不使用后台线程，因此没有额外的生命周期管理与进程退出竞态。
- 该文件与崩溃日志分离，便于区分「程序为什么崩」和「崩溃库有没有正常工作」，
  并同样受 `RetentionDays` 约束。

**变更**

- **移除对 `VassasCo.Utility.LogHelper` 的依赖，本包转为零外部依赖。**
  此前为了「写崩溃日志前 flush 日志队列」这一个动作引入了完整的项目引用，
  其代价是 NuGet 自动生成传递依赖、多项目并存时重复安装同一份日志库。
  该职责现由库内的跟踪日志模块承担。
- `FlushLogger` 属性名保留（避免破坏既有调用方代码），语义改为
  「写崩溃日志前后是否排空**内置**跟踪日志的缓冲」，默认值仍为 `true`。

**兼容性**

- 目标框架不变：`net472` / `netstandard2.0` / `net6.0` / `net8.0`。
- 既有公开 API 签名不变；`FlushAndWriteCrashLog` 行为不变（未启用跟踪日志时排空为空操作）。
- **反向不兼容的一点**：升级后不再自动向 `VassasCo.Utility.LogHelper` 传递依赖。
  若此前依赖「装了 CrashDumpHelper 就顺带有了 LogHelper」，请改为显式引用 LogHelper。
- 若此前依赖本库在崩溃前 flush **宿主的** LogHelper 队列，升级后该行为消失。
  请在自己的 `UnhandledException` 处理或 `Diagnostics` 回调里显式调用。

### 2.1.0

**新增**

- **自动接管 `AppDomain.UnhandledException`**：未处理异常现在会自动落盘并生成 dump，
  不必再由调用方手动订阅。可用 `HookUnhandledException = false` 关闭
  （调用方已自行订阅时应关闭，否则同一次崩溃会写两份日志）。
- **自动接管 `TaskScheduler.UnobservedTaskException`**：.NET Framework 4.5 起未观察的
  Task 异常默认不再终止进程而是静默丢弃，订阅该事件才能把它们留下。
  可用 `HookUnobservedTaskException = false` 关闭。
- **`ShouldIgnore` 过滤器**：剔除把异常当控制流产生的噪声，避免冲掉崩溃前的关键线索。
  回调自身抛异常时按「不忽略」处理 —— 宁可多记，不可漏记。
- **缓冲写满时自动落盘**（`AutoFlushWhenFull`，默认开启）：缓冲是覆盖式的，
  及时落盘可把记录固化到日志文件。仅写日志，不生成 MiniDump —— 缓冲满是预警，不是崩溃。
- **`FlushFirstChanceLog(source)`**：手动把当前缓冲落盘并清空，返回写入条数。

**修复**

- **`Initialize()` 重复调用会覆盖生效中的配置**：与「重复调用已忽略」的语义矛盾 ——
  调用被忽略，配置却被换了。现改为只在真正接管时应用配置。
- **采集路径可能无限递归导致栈溢出**：用户通过 `ShouldIgnore` 传入的回调若自身抛异常，
  该抛出会再次触发 `FirstChanceException` → 再次进入采集 → 再次调用回调 …… 直至栈溢出。
  现改为进入采集时统一置重入标志，任何重入直接跳过。

**行为变更（升级请注意）**

`Initialize()` 现在会额外订阅 `UnhandledException` 与 `UnobservedTaskException`。
若调用方此前已自行订阅 `UnhandledException` 并在其中调用 `FlushAndWriteCrashLog`，
升级后同一次崩溃会写两份日志。解决办法二选一：

```csharp
// 方案 1：关掉自动接管，保持原有写法
CrashDumpHelper.Initialize(new CrashDumpOptions { HookUnhandledException = false });
AppDomain.CurrentDomain.UnhandledException += (s, e) => /* 原有逻辑 */;

// 方案 2：删掉自己的订阅，交给库接管（推荐）
CrashDumpHelper.Initialize();
```

### 2.0.0

**新增**

- **`Deinitialize()`**：可注销 FirstChance 监听并清空缓冲，满足插件卸载 / 热重载场景。
- **`CrashDumpOptions`**：输出目录、是否生成 dump、dump 类型、保留天数、是否 flush 日志均可配置。
  相对路径基于**程序目录**解析，避免当前工作目录被 `OpenFileDialog` 等悄悄改动导致日志写错地方。
- **过期文件自动清理**：按 `RetentionDays`（默认 30 天）删除本库生成的旧崩溃文件，
  解决长期运行撑满磁盘的问题；也可用 `CleanupExpiredFiles()` 手动触发。
- **`GetFirstChanceSnapshot()`**：可随时取出当前缓冲快照。
- **`IsInitialized` / `Options`**：便于在运行期检查与排查。
- **`Diagnostics` 回调**：目录创建失败、dump 生成失败等隐性问题可上报。
- **新增 `net472` 目标框架**。

**修复**

- **A1 `Initialize()` 无重入保护**：旧实现重复调用会重复 `+=` 同一个 handler，
  导致每条 FirstChance 异常被记录 N 次 —— 日志体积爆炸、50 条缓冲被瞬间冲掉，
  **真正崩溃前的线索反而丢失**。现用 `Interlocked.CompareExchange` 守卫，重复调用安全。
- **A2 常量与容量不一致**：旧实现 `new List<string>(64)` 但上限常量是 50，
  易误读且 64 的容量永远用不满。现统一为 `FirstChanceMaxCount`。
- **A3 淘汰最旧记录是 O(n)**：旧实现用 `List.RemoveAt(0)`，每次 FirstChance 异常都可能
  触发整体搬移。现改为固定长度数组 + 写指针的环形缓冲，写入与淘汰都是 O(1)。
- **B3 平台判断靠吞异常**：旧实现通过 `catch (DllNotFoundException)` 判断平台，
  语义不清且会掩盖真实错误。现改为显式 `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`。
- **B4 反射集成 LogHelper**：旧实现用反射字符串
  `"VassasCo.Utility.LogManager, VassasCo.Utility"` 调用，无编译期检查，
  LogHelper 改名或换程序集即静默失效。现改为显式项目引用。
- **C1 空 catch 吞掉一切**：旧实现所有异常路径都是 `catch { }`，
  "崩溃了但什么都没留下"。现统一转交诊断通道。
- **C3 dump 失败无提示**：旧实现 `MiniDumpWriteDump` 失败后不做任何记录，
  排查者无法区分"没生成"和"生成失败"。现记录 `Marshal.GetLastWin32Error()`。

**兼容性**

- 目标框架：`net472` / `netstandard2.0` / `net6.0` / `net8.0`
- 既有公开 API（`Initialize()` / `WriteCrashLog` / `FlushAndWriteCrashLog`）签名与行为保持不变。
- 唯一的**依赖变更**：新增对 `VassasCo.Utility.LogHelper` 的 NuGet 依赖
  （由原来的运行时反射改为编译期引用）。安装本包时会自动带上 LogHelper。

---

## 许可证

MIT
