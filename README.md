# VassasCo.Utility

面向 .NET 桌面端 / 服务端的通用工具库集合，包含配置管理、Excel 导入导出、日志、
进程内事件总线、崩溃捕获五个独立组件。

各组件可单独安装、互不强制依赖（唯一的例外是 CrashDumpHelper 依赖 LogHelper，
用于崩溃前 flush 日志缓冲）。

[![MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 目录

- [项目概览](#项目概览)
- [解决方案结构](#解决方案结构)
- [构建](#构建)
- [测试](#测试)
- [许可证](#许可证)

---

## 项目概览

| 项目 | 说明 | 目标框架 | NuGet 版本 | 文档 |
|------|------|----------|-----------|------|
| **VassasCo.Utility.ConfigHelper** | 实体类与 JSON/XML 配置文件双向映射，自研解析器，读写严格对称 | net472 / netstandard2.0 / net6.0 / net8.0 / net9.0 / net10.0 | 2.1.0 | [README](src/VassasCo.Utility.ConfigHelper/README.md) |
| **VassasCo.Utility.ExcelMapper** | Excel / CSV 导入导出，支持 ClosedXML 与 OpenXml 流式引擎，支持插入图片、报表布局与值转换器 | net472 / netstandard2.0 / net6.0 / net8.0 | 2.2.0 | [README](src/VassasCo.Utility.ExcelMapper/README.md) |
| **VassasCo.Utility.LogHelper** | 异步文件日志，分类分文件 + 按天分目录 + 自动清理 | net472 / netstandard2.0 / net6.0 / net8.0 | 2.1.0 | [README](src/VassasCo.Utility.LogHelper/README.md) |
| **VassasCo.Utility.EventBus** | 进程内事件总线，支持特性注册 / 优先级 / 多通道 / 粘性事件 / 线程封送 / 弱引用订阅 | net472 / netstandard2.0 / net6.0 / net8.0 / net9.0 / net10.0 | 2.0.0 | [README](src/VassasCo.Utility.EventBus/README.md) |
| **VassasCo.Utility.CrashDumpHelper** | 崩溃捕获，FirstChance 异常链 + Windows MiniDump；自动接管未处理异常与未观察 Task 异常 | net472 / netstandard2.0 / net6.0 / net8.0 | 2.1.0 | [README](src/VassasCo.Utility.CrashDumpHelper/README.md) |

### 组件依赖关系

```
ConfigHelper      （零依赖）
ExcelMapper       （零依赖，内部用 ClosedXML / OpenXml）
LogHelper         （零依赖）
EventBus          （零依赖）
CrashDumpHelper   ──▶ LogHelper        ← 显式项目引用，打包后自动传递
```

---

## 解决方案结构

```
VassasCo.Utility.slnx
├── build/                                  # 共享构建文件（IsExternalInit.cs）
├── src/
│   ├── VassasCo.Utility.ConfigHelper/      # 配置管理库
│   ├── VassasCo.Utility.ExcelMapper/       # Excel 导入导出库
│   ├── VassasCo.Utility.LogHelper/         # 日志库
│   ├── VassasCo.Utility.EventBus/          # 事件总线
│   └── VassasCo.Utility.CrashDumpHelper/  # 崩溃捕获库
└── tests/
    ├── VassasCo.Utility.ConfigHelper.Tests/
    ├── VassasCo.Utility.ExcelMapper.Tests/
    ├── VassasCo.Utility.LogHelper.Tests/
    ├── VassasCo.Utility.EventBus.Tests/
    └── VassasCo.Utility.CrashDumpHelper.Tests/
```

---

## 构建

在解决方案根目录执行：

```bash
dotnet build VassasCo.Utility.slnx
```

> net472 目标框架通过 `Microsoft.NETFramework.ReferenceAssemblies`（`PrivateAssets=all`）
> 提供目标包，因此**未安装 VS / 目标包的机器（含 CI）也能构建**，
> 且该包不会作为依赖传递给使用方。

## 测试

```bash
dotnet test VassasCo.Utility.slnx
```

---

## 更新说明

### 2.2.0（当前）

**功能增强**

- **ExcelMapper 报表布局**：新增 `WithTitle` / `WithPreamble` / `WithPostamble`，
  可在数据表之外写入跨列标题与表前、表后自由排版区（抬头信息、落款、签名栏等），
  单元格支持横向合并、字号、加粗、水平对齐与字体色 / 填充色。
- **ExcelMapper 工作表级字体**：`ExcelStyle.FontName` / `FontSize` / `HeaderFontSize`。
- **ExcelMapper 值转换器**：Fluent `ConvertUsing` 与可带构造器实参的 `[ExcelConverter]`，
  同一种转换器可用不同实参产生不同输出；内置 `BooleanTextConverter`（判定文案）、
  `MaskConverter`（文本脱敏）、`NullPlaceholderConverter`（空值占位）。
  值转换器在两个引擎下均生效。

**兼容性**

- 新增能力均为新增成员，既有公开 API 签名不变。
- 报表布局与图片一致，仅在 DOM 引擎实现：`Engine = Auto` 时检测到布局行自动选用 DOM；
  显式指定流式引擎时抛 `ExcelPlanValidationException`，避免生成缺标题的文件。
- 未使用任何布局方法时，表头仍从第 1 行开始，与旧版本行为完全一致。

### 2.1.0

**新增组件**

- **VassasCo.Utility.EventBus**（2.0.0）—— 进程内事件总线，五个组件中唯一的"新成员"。
  支持特性自动注册、优先级、多通道隔离、条件过滤、粘性事件、异步处理、异常隔离；
  并针对本项目的使用场景补齐了 **线程封送**（自动回到订阅时的同步上下文，UI 订阅无需手写 Invoke）
  与 **弱引用订阅**（解决静态事件源 + 短命订阅者的经典泄漏）。
- **VassasCo.Utility.CrashDumpHelper**（2.1.0）—— 崩溃捕获，
  采集崩溃前最近 50 条 FirstChance 异常（O(1) 环形缓冲）+ 生成 Windows MiniDump。
  `Initialize()` 现已自动订阅 `UnhandledException` 与 `UnobservedTaskException`，
  并提供 `ShouldIgnore` 过滤器与缓冲写满自动落盘。

**目标框架**

- ConfigHelper / ExcelMapper / LogHelper / EventBus / CrashDumpHelper
  全部新增 **`net472`** 目标框架，供 .NET Framework 4.7.2+（含 4.8）传统桌面项目直接引用。

**功能增强**

- **ExcelMapper**：新增**图片插入**能力 —— 支持 `[ExcelImage]` 特性列（值可为文件路径 / `byte[]` / `Stream`）
  与 `WithPicture()` 固定位置图片；内置零依赖的图片格式与原始尺寸嗅探（PNG / JPEG / BMP / GIF / TIFF）；
  支持原始尺寸 / 等比适应 / 拉伸三种缩放模式、单元格内嵌 / 浮动两种布局、
  行列自适应与图片缺失兜底策略（跳过 / 写回来源文本 / 抛异常）；
  含图片时自动选用 DOM 引擎（流式 SAX 引擎无法回填图片部件）。
  另修正了 README 中无法编译的 Fluent 示例。

**缺陷修复**

- **ConfigHelper 原子保存（平台兼容性）**：原实现在 netstandard2.0 下走"先 `File.Delete` 再 `File.Move`"，
  而 net472（.NET Framework）落入另一分支调用 `File.Move(..., overwrite: true)` ——
  **该重载 .NET Framework 并不存在**，此缺陷因缺少 net472 目标框架而长期被掩盖，
  本次新增 net472 后才暴露。现统一改为 `File.Replace()`，在所有目标框架上均可用，
  替换过程原子，且顺势消除了原实现"删除后、移动前被中断 → 配置文件丢失"的隐患。
- **CrashDumpHelper**：详见其 README 的 2.0.0 更新说明（重入保护、环形缓冲、可注销、可配置、自动清理等）。
- **CrashDumpHelper 采集路径可能无限递归导致栈溢出**：用户通过 `ShouldIgnore` 传入的回调若自身抛异常，
  该抛出会再次触发 `FirstChanceException` → 再次进入采集 → 再次调用回调 …… 直至栈溢出。
  现改为进入采集时统一置重入标志，任何重入直接跳过。
- **CrashDumpHelper `Initialize()` 重复调用会覆盖生效中的配置**：与「重复调用已忽略」的语义矛盾 ——
  调用被忽略，配置却被换了。现改为只在真正接管时应用配置。

**兼容性**

- 既有公开 API 签名均保持不变，对现有调用方无破坏性变更。
- 行为变更一：同步 `EventBus.Publish` 不再等待异步订阅者（EventBus 为新增组件，不影响存量代码）。
- 行为变更二：`CrashDumpHelper.Initialize()` 现在自动接管未处理异常与未观察 Task 异常。
  若调用方已自行订阅 `UnhandledException`，请设 `HookUnhandledException = false`，
  否则同一次崩溃会写两份日志。
- CrashDumpHelper 新增对 LogHelper 的 NuGet 依赖（原为运行时反射集成）。

---

## 许可证

[MIT](LICENSE)
