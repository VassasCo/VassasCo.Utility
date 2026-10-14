# VassasCo.Utility.ExcelMapper

原生对象到 Excel/CSV 的映射引擎（桌面端/服务端通用，无需 COM/VSTO）。  
支持 **侵入式特性** 与 **非侵入式 Fluent** 两套并行配置方式，覆盖导出、导入、分页、异步、条件格式、大数据流式等场景。

[![MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 目录

- [功能总览](#功能总览)
- [目标框架与依赖](#目标框架与依赖)
- [快速开始](#快速开始)
- [侵入式：特性配置](#侵入式特性配置)
- [非侵入式：Fluent 配置](#非侵入式fluent-配置)
- [嵌套对象与集合](#嵌套对象与集合)
- [分页](#分页)
- [异步导出（取消 / 进度）](#异步导出取消--进度)
- [条件格式](#条件格式)
- [图片插入](#图片插入)
- [报表布局](#报表布局)
- [值转换器](#值转换器)
- [CSV 导出](#csv-导出)
- [字典导出](#字典导出)
- [Excel / CSV 导入](#excel--csv-导入)
- [引擎选择](#引擎选择)
- [安全策略](#安全策略)
- [配置参考](#配置参考)
- [API 速查](#api-速查)
- [异常类型](#异常类型)

---

## 功能总览

| 类别 | 能力 |
|------|------|
| **配置方式** | 侵入式特性（贴在实体上） + 非侵入式 Fluent（`EntityMap<T>` 外部声明），可混用 |
| **导出格式** | Excel (.xlsx) 与 CSV，共用同一套列配置 |
| **嵌套结构** | 自动递归展开嵌套对象，生成多层合并表头；循环引用自动标记不递归 |
| **集合处理** | 子 Sheet（默认）/ 原地摊平 / 仅占位文本 / JSON 单行存储 四种模式 |
| **分页** | 多 Sheet 分页 / 多文件分页，可按特性或选项配置 |
| **异步** | `ToFileAsync` / `ToStreamAsync`，支持 `CancellationToken` 与 `IProgress<int>` |
| **条件格式** | 大于/小于/等于/区间等运算符 + 字体色/背景色/加粗，支持 Excel 原生 CF 与服务端求值双路径 |
| **安全** | 长数字自动转文本、身份证/手机号敏感字段识别、CSV 公式注入防护、超长截断、路径校验 |
| **导入** | Excel/CSV → 对象，安全类型转换 + 逐行错误收集 |
| **引擎** | ClosedXML DOM（默认，样式最全）/ OpenXML SAX 流式（大数据低内存）自动切换 |
| **字典导出** | `IDictionary` 宽表/竖表两种布局 |
| **图片嵌入** | 数据列渲染为嵌入式图片（文件路径 / `byte[]` / `Stream`），或整表固定位置图片（Logo / 水印），自动适配行高列宽 |
| **报表布局** | 数据表之外写入标题、表前/表后自由排版区，支持跨列合并、字号与对齐，用于单据与检测报告 |
| **值转换** | 导出前改写单元格值：Fluent lambda 或可复用特性转换器（支持构造器实参），内置判定文案、脱敏、空值占位 |
| **报表布局** | 数据表之外写入标题与元信息行：`WithTitle` 跨列大标题、`WithPreamble` / `WithPostamble` 自由排版行，支持横向合并、字号、加粗、对齐与配色 |

---

## 目标框架与依赖

| 目标框架 | ClosedXML | DocumentFormat.OpenXml | System.Text.Json |
|----------|-----------|------------------------|-------------------|
| net472 | 0.102.3 | 2.19.0 | 8.0.5 |
| netstandard2.0 | 0.102.3 | 2.19.0 | 8.0.5 |
| net6.0 | 0.105.0 | 3.1.1 | — |
| net8.0 | 0.105.0 | 3.1.1 | — |

> 新增 **net472**：供 .NET Framework 4.8 / 4.7.2 等传统桌面项目（如 WinForms 上位机）直接引用。
> 关键收益：net472 会命中 ClosedXML 自带的 net46 原生资产，避免回落到 netstandard2.0 资产，
> 从而**不再拖入 `System.Memory` / `System.Buffers` / `System.Runtime.CompilerServices.Unsafe` 等一长串传递依赖**，
> 也省去在 packages.config 旧式项目里手写大量 binding redirect 的麻烦。
> 构建期通过 `Microsoft.NETFramework.ReferenceAssemblies`（`PrivateAssets=all`）提供目标包，**不会作为依赖传递给使用方**。

> DataAnnotations（`[DisplayName]`/`[Display]`/`[DataType]`）通过反射全名识别，**无硬依赖**，已有特性自动生效。

---

## 快速开始

```csharp
using VassasCo.Utility;

// 一行导出（配置全部来自实体特性/约定）
ExcelMapper.SaveToFile(people, "report.xlsx");

// Fluent 配置导出
ExcelMapper.Build(people)
    .WithSheetName("员工表")
    .WithStyle(s => s.HeaderBackgroundColor = "#2E75B6")
    .ToFile("report.xlsx");

// 导入
var result = ExcelMapper.Import<Person>("report.xlsx");
foreach (var p in result.Items) Console.WriteLine(p.Name);
foreach (var err in result.Errors) Console.WriteLine(err);
```

---

## 侵入式：特性配置

在实体类上贴特性，调用方零配置：

```csharp
[ExcelSheet(Name = "员工表", PageSize = 1000, AutoFilter = true)]
public class Person
{
    [ExcelColumn(Name = "编号", Order = 1, Width = 10)]
    public int Id { get; set; }

    [ExcelColumn(Name = "姓名", Order = 2, Width = 15)]
    public string Name { get; set; } = "";

    // 身份证号强制文本，防止科学计数法丢精度
    [ExcelColumn(Name = "身份证号", ForceText = true, Width = 22)]
    public string IdCard { get; set; } = "";

    [ExcelColumn(Name = "入职日期", Format = "yyyy-MM-dd")]
    public DateTime HireDate { get; set; }

    [ExcelColumn(Name = "绩效", Order = 3, Format = "N2")]
    [ConditionalFormat(ConditionOperator.GreaterThan, 90, FillColor = "#00B050", Bold = true)]
    [ConditionalFormat(ConditionOperator.LessThan, 60, FontColor = "#FF0000")]
    public double Score { get; set; }

    [ExcelIgnore]
    public string InternalNote { get; set; } = "";

    // byte[] 默认输出 Base64
    public byte[]? Avatar { get; set; }
}

// 导出
ExcelMapper.SaveToFile(people, "employees.xlsx");
```

### 可用特性

| 特性 | 级别 | 说明 |
|------|------|------|
| `[ExcelSheet]` | 类型 | Sheet 名称、PageSize、ArrayRender、AutoFilter、UseTable |
| `[ExcelDisplay]` | 属性 | 仅设列标题（轻量） |
| `[ExcelColumn]` | 属性 | Name/Order/Format/ForceText/Width/WrapText/AsHyperlink |
| `[ExcelIgnore]` | 属性 | 不参与导出/导入 |
| `[ConditionalFormat]` | 属性 | 条件格式（AllowMultiple，可多条） |
| `[ExcelConverter]` | 属性 | 自定义值转换器（实现 `IExcelValueConverter`），可附加构造器实参 |

### 配置优先级

```
Fluent EntityMap > 本包特性 > DataAnnotations 约定 > 属性名
```

---

## 非侵入式：Fluent 配置

不修改业务类，在外部用 Lambda 声明映射：

```csharp
// 注意：Property() 返回的是 PropertyMapping（单列配置器），不能再链式调用下一个 Property()，
// 必须逐条声明（或用 Builder 的 WithMap(Action<EntityMap<T>>) 形式）。
var map = new EntityMap<Person>();
map.Property(p => p.Id).HasName("编号").Order(1).Width(10);
map.Property(p => p.Name).HasName("姓名").Order(2).Width(15);
map.Property(p => p.IdCard).HasName("身份证号").AsText().Width(22);
map.Property(p => p.HireDate).Format("yyyy-MM-dd");
map.Property(p => p.Score).HasName("绩效").Format("N2")
    .Rule(ConditionOperator.GreaterThan, 90, s => s.Fill("#00B050").Bold())
    .Rule(ConditionOperator.LessThan, 60, s => s.Font("#FF0000"));
map.Ignore(p => p.InternalNote);

ExcelMapper.Build(people)
    .WithMap(map)
    .ToFile("employees.xlsx");
```

支持嵌套属性路径：

```csharp
.Property(p => p.Department.Manager.Name).HasName("部门经理")
```

> **注意**：上面的写法只改了叶子 `Name` 的标题；中间节点 `Department`、`Manager` 作为**分组表头**，默认显示属性名。要自定义分组表头，需分别对中间节点命名（或用 `[ExcelDisplay]` 特性）：

```csharp
.Property(p => p.Department).HasName("部门")                // 分组表头
.Property(p => p.Department.Manager).HasName("经理")        // 分组表头
.Property(p => p.Department.Manager.Name).HasName("部门经理"); // 叶子
```

生成表头：`部门 → 经理 → 部门经理`。同理 `.Ignore(p => p.Department)` 会忽略整个子树。

### PropertyMapping 链式方法

| 方法 | 说明 |
|------|------|
| `HasName(string)` | 列标题 |
| `Order(int)` | 列顺序 |
| `AsText()` | 强制文本（身份证/手机号/长 ID） |
| `Format(string)` | 数字/日期格式 |
| `Width(double)` | 固定列宽 |
| `WrapText()` | 自动换行 |
| `Hyperlink()` | 作为超链接写入 |
| `Ignore()` | 忽略该属性 |
| `ConvertUsing(Func<TProp, object?>)` | 自定义值转换 |
| `Rule(op, value, Action<ConditionalStyleBuilder>)` | 条件格式 |

### 行级规则

```csharp
var map = new EntityMap<Order>()
    .WhenRow(o => o.IsOverdue, s => s.Font("#FF0000").Bold())
    .WhenRow(o => o.Amount > 10000, s => s.Fill("#FFF2CC"));
```

> 行级规则始终服务端求值，写为静态样式。

---

## 嵌套对象与集合

嵌套对象自动递归展开，生成多层合并表头：

```csharp
public class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Department? Department { get; set; }   // 嵌套 → 合并表头
    public List<Address> Addresses { get; set; }   // 集合
}

public class Department
{
    public string DeptName { get; set; } = "";
    public Person? Manager { get; set; }  // 循环引用 → 自动标记"(循环引用)"
}
```

生成的表头：

```
| Id | Name | Department         | Addresses           |
|    |      | DeptName | Manager | (→ 子 Sheet "Addresses") |
```

### 集合渲染模式（`ArrayRender`）

| 模式 | 行为 |
|------|------|
| `ChildSheet`（默认） | 集合元素写入独立子 Sheet，主表显示 `[n items → Sheet "Addresses"]` |
| `FlattenInPlace` | 摊平到当前 Sheet，父列重复（**仅允许单个集合属性**，多集合抛配置异常） |
| `PlaceholderOnly` | 仅显示 `[n items]` 占位文本 |
| `Json` | 序列化为 JSON 字符串存入单元格（保留数据、单行展示、可导入还原） |

> `Json` 模式把整个集合写进单个单元格，不产生子 Sheet/多行，适合 `List<string>`/`List<int>` 等简单集合或需要整行紧凑展示的场景；复杂对象集合的 JSON 可能超过单元格 32767 字符上限而被截断。

---

## 分页

```csharp
// 方式一：选项配置
ExcelMapper.Build(people)
    .WithOptions(o =>
    {
        o.PageSize = 5000;               // 每页 5000 行
        o.PageMode = PageMode.MultipleSheets; // 多 Sheet（默认）
    })
    .ToFile("report.xlsx");
// → Sheet: Person, Person_2, Person_3 ...

// 方式二：特性配置
[ExcelSheet(PageSize = 5000)]

// 多文件分页
ExcelMapper.Build(people)
    .WithOptions(o =>
    {
        o.PageSize = 5000;
        o.PageMode = PageMode.MultipleFiles; // 每页一个文件
    })
    .ToFile("report.xlsx");
// → report_1.xlsx, report_2.xlsx, report_3.xlsx ...
```

| PageMode | 行为 |
|----------|------|
| `None` | 不分页（超单 Sheet 上限抛异常） |
| `MultipleSheets` | 同一工作簿多 Sheet（默认） |
| `MultipleFiles` | 每页一个 .xlsx 文件 |

---

## 异步导出（取消 / 进度）

面向桌面端，不阻塞 UI：

```csharp
using var cts = new CancellationTokenSource();
var progress = new Progress<int>(rows => {
    Dispatcher.Invoke(() => ProgressBar.Value = rows);
});

await ExcelMapper.SaveToFileAsync(people, "report.xlsx", cts.Token, progress);

// 取消
cts.Cancel();
```

所有导出方法均有异步重载：`SaveToFileAsync` / `SaveToStreamAsync` / `WriteCsvAsync` / `DictionaryToFileAsync`。

---

## 导出诊断信息

所有 Excel 导出方法返回 `ExportResult`，桌面端可据此展示结果：

```csharp
var result = ExcelMapper.SaveToFile(people, "report.xlsx");

Console.WriteLine($"导出 {result.RowCount} 条记录，共 {result.SheetCount} 个工作表");
Console.WriteLine($"引擎：{result.Engine}，耗时 {result.Elapsed.TotalSeconds:F2} 秒");
Console.WriteLine($"文件数：{result.FileCount}");
```

| 属性 | 说明 |
|------|------|
| `RowCount` | 数据记录总数（不含表头、不含集合子 Sheet 展开） |
| `SheetCount` | 工作表总数（含集合子 Sheet） |
| `Engine` | 实际使用的导出引擎（`ClosedXml` / `OpenXmlStreaming`） |
| `Elapsed` | 导出耗时 |
| `FileCount` | 生成的文件数（多文件分页时 &gt;1） |
| `FilePaths` | 生成的文件路径列表（导出到流时为空） |

---

## 条件格式

### 侵入式

```csharp
[ConditionalFormat(ConditionOperator.GreaterThan, 90, FillColor = "#00B050", Bold = true)]
[ConditionalFormat(ConditionOperator.Between, 60, 90, FontColor = "#FFC000")]
[ConditionalFormat(ConditionOperator.LessThan, 60, FontColor = "#FF0000")]
public double Score { get; set; }
```

### 非侵入式

```csharp
.Property(p => p.Score)
    .Rule(ConditionOperator.GreaterThan, 90, s => s.Fill("#00B050").Bold())
    .Rule(ConditionOperator.LessThan, 60, s => s.Font("#FF0000"))
```

### 运算符

`GreaterThan` / `GreaterOrEqual` / `LessThan` / `LessOrEqual` / `Equal` / `NotEqual` / `Contains` / `Between`

> 简单规则（除 `Contains` 外）默认翻译为 Excel 原生条件格式（`PreferNativeConditionalFormat=true`），可在应用端动态变色。流式引擎或关闭此选项时改为服务端求值写入静态样式。

---

## 图片插入

支持两类图片：**数据列图片**（每行一张，数据驱动）与 **固定位置图片**（整表一张，如 Logo / 水印）。

### 侵入式：特性

```csharp
public class TestResult
{
    [ExcelColumn(Name = "产品图", Order = 10, Width = 30)]
    [ExcelImage(Width = 120, Height = 90, SizeMode = ImageSizeMode.Fit)]
    public string PhotoPath { get; set; } = "";
}
```

### 非侵入式：Fluent

```csharp
var map = new EntityMap<TestResult>()
    .Property(x => x.PhotoPath).HasName("产品图")
        .AsImage(img => { img.Width = 120; img.Height = 90; img.SizeMode = ImageSizeMode.Fit; });
```

### 固定位置图片

```csharp
ExcelMapper.Build(results)
    .WithMap(map)
    .WithPicture(p => p.FromFile(@"C:\res\logo.png").At("A1").Size(140, 45))
    .WithPicture(p => p.FromBytes(watermarkBytes).At(2, 8).Placement(ImagePlacement.Floating))
    .ToFile("report.xlsx");
```

### 图片来源优先级

1. `FromBytes` / 属性值为 `byte[]`（最优先，不触碰磁盘）
2. `FromFile` / 属性值为 `string` 路径 —— **相对路径以程序所在目录为基准**，不受当前工作目录被 `OpenFileDialog` 等改动的影响

### 缺失兜底

| 策略 | 行为 |
|------|------|
| `MissingImagePolicy.Skip`（默认） | 跳过，单元格留空，不影响整个导出 |
| `MissingImagePolicy.WriteSourceText` | 把来源（通常是文件路径）写入单元格，便于事后排查 |
| `MissingImagePolicy.Throw` | 抛 `ExcelMappingException` |

### 引擎限制

> 图片只能在 **DOM 引擎（ClosedXML）** 下写入。
> `Engine = Auto`（默认）时，检测到文档含图片会**自动强制使用 DOM 引擎**；
> 若显式指定 `Engine = OpenXmlStreaming`，图片会被**跳过**（流式 SAX 只能顺序写入，无法回填图片部件）。

---

## 报表布局

业务单据类报表通常不是一张干净的二维表：标题要跨列贯通，表头上方要放单据编号、制表日期、负责人等元信息，
表尾还要留合计或签名栏。这类内容不属于任何数据列，因此用列映射表达不了。

本库把一张工作表划分为四个区域：

| 区域 | 位置 | 数据来源 |
|------|------|----------|
| 表前区 | 第 1 行起 | `WithTitle` / `WithPreamble`（自由排版） |
| 表头 | 紧随表前区 | 列映射自动生成（含多层合并） |
| 数据区 | 紧随表头 | 对象集合 |
| 表后区 | 数据区之后 | `WithPostamble`（自由排版） |

未使用任何布局方法时，表头仍从第 1 行开始，行为与旧版本完全一致。

### 标题与元信息行

```csharp
ExcelMapper.Build(orders)
    .WithSheetName("订单汇总")
    .WithTitle("月度订单汇总表", fontSize: 20)
    .WithPreamble(r => r
        .Cell("客户").Span(2).Cell(customerName).Span(2).NextRow()
        .Cell("制表日期").Span(2).Cell(DateTime.Today.ToString("yyyy-MM-dd")).Span(2).NextRow()
        .Cell("备注").Span(2).Cell("含税金额，单位：元").Span(2))
    .WithPostamble(r => r
        .Cell("合计").Span(2).Bold().Align(CellAlign.Right)
        .Cell(total.ToString("N2")).Span(2).Align(CellAlign.Right))
    .ToFile("report.xlsx");
```

`Span(n)` 为当前单元格横向合并的列数；写 `Span(0)` 表示从当前列一直合并到表格最后一列，
因此标题类内容无需预知表格有多少列。超出表格宽度的部分会被自动收敛，不会写到数据区之外。

### ReportRowBuilder 链式方法

| 方法 | 说明 |
|------|------|
| `Cell(text)` | 追加一个单元格，后续样式设置作用于该单元格 |
| `Span(columns)` | 横向合并列数；`<= 0` 表示跨到表格最后一列 |
| `FontSize(size)` | 字号（磅） |
| `Bold(bool)` | 加粗，默认 true |
| `Align(CellAlign)` | 水平对齐：`Default` / `Left` / `Center` / `Right` |
| `FontColor(hex)` / `FillColor(hex)` | 字体色 / 填充底色（如 `"#C00000"`） |
| `NextRow()` | 结束当前行；省略时最后一行会自动提交 |

`WithPreamble` 与 `WithPostamble` 均可多次调用，按调用顺序排列；`WithTitle` 声明的标题始终排在表前区最顶部。

### 工作表级字体

```csharp
ExcelMapper.Build(orders)
    .WithStyle(s => { s.FontName = "等线"; s.FontSize = 11; s.HeaderFontSize = 12; })
    .ToFile("report.xlsx");
```

### 引擎限制

> 布局行依赖随机写入与单元格合并，只能在 **DOM 引擎（ClosedXML）** 下写入。
> `Engine = Auto`（默认）时，检测到文档含布局行会**自动强制使用 DOM 引擎**；
> 若显式指定 `Engine = OpenXmlStreaming`，会在写入前抛 `ExcelPlanValidationException`，
> 避免导出一份缺少标题与表尾的文件。

---

## 值转换器

在导出前改写最终写入单元格的值。用于把内部表示换成人类可读文案，避免为了展示目的额外增加只读计算属性。

### 两种写法

**Fluent（推荐，非侵入）** —— 强类型 lambda，实体类保持干净：

```csharp
var map = new EntityMap<Result>();

map.Property(r => r.Passed)
   .HasName("判定")
   .ConvertUsing(v => v ? "OK" : "NG");

ExcelMapper.Build(results).WithMap(map).ToFile(path);
```

**特性（侵入式）** —— 复用性好，转换器与实体一起走：

```csharp
public sealed class TrendConverter : IExcelValueConverter
{
    public object? Convert(object? value) => value is int n && n > 0 ? "上升" : "下降";
}

public sealed class Result
{
    [ExcelConverter(typeof(TrendConverter))]
    public int Delta { get; set; }
}
```

两者同时存在时 Fluent 覆盖特性。

### 构造器实参复用

同一种转换器可通过不同的构造器实参产生不同输出，无需为每种文案新建类：

```csharp
public sealed class Verdict
{
    [ExcelConverter(typeof(BooleanTextConverter), "合格", "不合格")]
    public bool OpticalJudge { get; set; }

    [ExcelConverter(typeof(BooleanTextConverter), "是", "否")]
    public bool ElectricJudge { get; set; }

    [ExcelConverter(typeof(MaskConverter), 3, 4)]
    public string? Phone { get; set; }
}
```

实参个数或类型与任何公共构造函数都不匹配时抛 `ExcelMappingException`；不传实参时要求转换器具备公共无参构造函数。

### 内置转换器

| 转换器 | 可用构造器 | 说明 |
|---|---|---|
| `BooleanTextConverter` | `()`、`(trueText, falseText)` | 布尔判定文案；不传实参时默认 OK / NG。非布尔值原样返回 |
| `MaskConverter` | `()`、`(head, tail)`、`(head, tail, maskChar)` | 文本脱敏：保留首尾字符，其余替换为掩码（默认 `*`） |
| `NullPlaceholderConverter` | `()`、`(placeholder)` | null 与空白统一替换为占位文本，不传实参时默认 `-` |

```csharp
new MaskConverter(3, 4).Convert("13812345678");   // 138****5678
new NullPlaceholderConverter("-").Convert(null);  // -
new BooleanTextConverter("是", "否").Convert(true); // 是
```

`MaskConverter` 在首尾保留长度之和覆盖整串时全掩码；长度为 0 的输入原样返回。
转换结果为字符串时按文本写入单元格，不需要额外调用 `AsText()`。

---

## CSV 导出

```csharp
// 快速导出
ExcelMapper.WriteCsv(people, "report.csv");

// 配置选项
ExcelMapper.BuildCsv(people)
    .Configure(o =>
    {
        o.Delimiter = ';';              // 欧洲格式
        o.UseUtf8Bom = true;            // Excel 打开中文不乱码
        o.FormulaInjectionGuard = true; // 公式注入防护
        o.QuoteAllFields = false;       // 仅必要时加引号
    })
    .ToFile("report.csv");

// 异步
await ExcelMapper.WriteCsvAsync(people, "report.csv", cts.Token, progress);
```

CSV 导出复用侵入式特性 / 非侵入式 `EntityMap<T>` 的列配置。集合属性在 CSV 中无法表达，对应列输出空字符串。

---

## 字典导出

动态 `IDictionary` 无需实体类：

```csharp
var dict = new Dictionary<string, object>
{
    ["报告标题"] = "2024年销售统计",
    ["生成时间"] = DateTime.Now,
    ["总金额"] = 123456.78
};

// 键 ≤16 → 宽表（键作列头，单行值）
ExcelMapper.DictionaryToFile(dict, "summary.xlsx");

// 键 >16 → 竖表（Key/Value 两列）
```

---

## Excel / CSV 导入

```csharp
// 按扩展名自动选择
var result = ExcelMapper.Import<Person>("data.xlsx");

// 指定 Sheet
var r2 = ExcelMapper.ImportFromExcel<Person>("data.xlsx", sheetName: "员工表");

// CSV 导入（可指定分隔符）
var r3 = ExcelMapper.ImportFromCsv<Person>("data.csv", delimiter: ',');

// 从流导入
var r4 = ExcelMapper.ImportFromExcel<Person>(stream);

// 使用非侵入式映射绑定列名
var map = new EntityMap<Person>()
    .Property(p => p.Name).HasName("姓名");
var r5 = ExcelMapper.Import("data.xlsx", map);
```

### 导入结果

```csharp
public class ImportResult<T>
{
    public List<T> Items { get; }      // 成功导入的实体
    public List<ImportError> Errors { get; }  // 逐字段错误
    public bool HasErrors { get; }
}

// 错误不阻断整批导入，便于桌面端展示给用户修正
foreach (var err in result.Errors)
    Console.WriteLine(err); // "第 5 行 [Score]：无法转为 Double（原始值：N/A）"
```

### 支持的转换

string / char / bool（`1/true/yes/是`）/ 枚举（数值/名称/Description）/ DateTime（11 种格式）/ DateTimeOffset / TimeSpan / Guid / byte[]（Base64）/ 各数值类型（不丢精度）/ 可空类型 / 集合（JSON 反序列化，与 `ArrayRender.Json` 往返）。

---

## 引擎选择

```csharp
// 自动选择（默认）：行数 ≥ StreamingThreshold 时切换流式
ExcelMapper.Build(people)
    .WithOptions(o => o.StreamingThreshold = 100000)
    .ToFile("large.xlsx");

// 强制流式
ExcelMapper.Build(people)
    .WithOptions(o => o.Engine = ExportEngineKind.OpenXmlStreaming)
    .ToFile("large.xlsx");
```

| 引擎 | 特点 |
|------|------|
| `ClosedXml`（DOM） | 样式能力最全，支持原生条件格式、合并表头、Table 等；整个工作簿驻留内存 |
| `OpenXmlStreaming`（SAX） | 内存占用低，适合超大数据量；不支持原生条件格式（规则改为服务端求值） |
| `Auto`（默认） | 行数 ≥ 阈值（默认 10 万）或无法预知行数时用流式，否则用 DOM |

---

## 安全策略

| 策略 | 说明 |
|------|------|
| **长数字自动转文本** | `AutoForceText=Both`（默认）：按属性名敏感词（手机/电话/身份证/银行卡/邮编等）+ long 等大整数类型自动识别 |
| **CSV 公式注入防护** | 首字符为 `= + - @ Tab CR` 时前置单引号，防止 Excel 执行恶意公式 |
| **超长文本截断** | 单元格文本 >32767 字符自动截断（`TruncateLongText=true`） |
| **非法字符清理** | 自动移除 XML 1.0 非法控制字符（`\x00-\x08`、`\x0B`、`\x0C`、`\x0E-\x1F`），防止 Office/WPS 无法打开文件 |
| **循环引用保护** | 递归到已知类型时标记"(循环引用)"，不再继续展开 |
| **路径校验** | 写入前校验路径合法性，`AutoCreateDirectory=true` 时自动创建目录 |
| **异常不吞** | 类型转换/写入失败抛出具体异常类型，不留空 catch |
| **全局格式不污染 ID 列** | 全局 `NumberFormat` 仅作用于数值列，ID/编号列保持文本 |

---

## 配置参考

### ExcelStyle（视觉）

| 属性 | 默认值 | 说明 |
|------|--------|------|
| `HeaderBold` | `true` | 表头加粗 |
| `HeaderBackgroundColor` | `#4472C4` | 表头背景色 |
| `HeaderFontColor` | `#FFFFFF` | 表头字体色 |
| `AutoFitColumns` | `true` | 自动列宽 |
| `MinColumnWidth` / `MaxColumnWidth` | `null` / `50` | 列宽上下限 |
| `ShowBorder` | `true` | 显示边框 |
| `BandedRows` | `false` | 隔行底色 |
| `FreezeTopRow` | `true` | 冻结表头行 |
| `FreezeFirstColumn` | `false` | 冻结首列 |
| `NumberFormat` | `null` | 全局数字格式（建议用列级 Format） |
| `DateTimeFormat` | `null` | 日期格式 |
| `TrueText` / `FalseText` | `是` / `否` | 布尔显示文本 |
| `UseEnumDescription` | `true` | 枚举优先读 `[Description]` |
| `ChildSheetParentProperty` | `null` | 子 Sheet 父关联列属性名（null 自动查找 Id/Uid 等） |

### ExcelExportOptions（行为）

| 属性 | 默认值 | 说明 |
|------|--------|------|
| `PageSize` | `null` | 每页行数 |
| `PageMode` | `MultipleSheets` | 分页方式 |
| `SheetPageNameFormat` | `{0}_{1}` | 分页 Sheet 命名模板 |
| `ArrayRender` | `ChildSheet` | 集合渲染方式（ChildSheet/FlattenInPlace/PlaceholderOnly/Json） |
| `Engine` | `Auto` | 导出引擎 |
| `StreamingThreshold` | `100000` | 自动切换流式的行数阈值 |
| `AutoCreateDirectory` | `true` | 自动创建目录 |
| `AutoForceText` | `Both` | 长数字自动转文本策略 |
| `TruncateLongText` | `true` | 超长截断 |
| `BinaryRender` | `Base64` | byte[] 渲染方式 |
| `PreferNativeConditionalFormat` | `true` | 优先原生条件格式 |
| `ValidatePlan` | `Throw` | 计划校验模式 |

### ExcelImageOptions（图片）

| 属性 | 默认值 | 说明 |
|------|--------|------|
| `Width` | `0` | 显示宽度（像素）；&lt;=0 使用原图宽度 |
| `Height` | `0` | 显示高度（像素）；&lt;=0 使用原图高度 |
| `SizeMode` | `Original` | `Original` 原尺寸 / `Fit` 等比缩放放入 / `Stretch` 拉伸（可能变形） |
| `Placement` | `Cell` | `Cell` 随单元格移动缩放 / `Floating` 自由浮动 |
| `OnMissing` | `Skip` | 缺失策略：`Skip` / `WriteSourceText` / `Throw` |
| `AdjustRowHeight` | `true` | 自动调高行高以容纳图片 |
| `AdjustColumnWidth` | `true` | 自动加宽列宽以容纳图片 |
| `RowPadding` | `4` | 行高额外留白（像素） |
| `Name` | `null` | 图片名称（同一 Sheet 内需唯一）；为空时自动生成 |
| `BaseDirectory` | `null` | 相对路径基准目录；默认程序所在目录 |

### CsvExportOptions

| 属性 | 默认值 | 说明 |
|------|--------|------|
| `Delimiter` | `,` | 字段分隔符 |
| `UseUtf8Bom` | `true` | UTF-8 BOM |
| `QuoteAllFields` | `false` | 全字段加引号 |
| `FormulaInjectionGuard` | `true` | 公式注入防护 |
| `AutoCreateDirectory` | `true` | 自动创建目录 |
| `IncludeHeader` | `true` | 写入表头 |

---

## API 速查

### ExcelMapper（静态门面）

| 方法 | 说明 |
|------|------|
| `Build<T>(data)` | 创建 Excel 导出 Builder |
| `SaveToFile<T>(data, path)` | 快速导出 Excel 到文件，返回 `ExportResult` |
| `SaveToStream<T>(data, stream)` | 快速导出 Excel 到流，返回 `ExportResult` |
| `SaveToFileAsync<T>(data, path, ct, progress)` | 异步导出 Excel，返回 `Task<ExportResult>` |
| `BuildCsv<T>(data)` | 创建 CSV 导出 Builder |
| `WriteCsv<T>(data, path)` | 快速导出 CSV |
| `WriteCsvAsync<T>(data, path, ct, progress)` | 异步导出 CSV |
| `DictionaryToFile(dict, path, sheetName)` | 字典导出 |
| `DictionaryToFileAsync(dict, path, sheetName, ct)` | 异步字典导出 |
| `Import<T>(path, map, ct)` | 按扩展名自动导入 |
| `ImportFromExcel<T>(path/sream, sheetName, sheetIndex, map, ct)` | Excel 导入 |
| `ImportFromCsv<T>(path/stream, map, delimiter, ct)` | CSV 导入 |

### ExcelMapperBuilder\<T\>

| 方法 | 说明 |
|------|------|
| `WithSheetName(name)` | 设置 Sheet 名 |
| `WithStyle(style)` / `WithStyle(Action)` | 样式配置 |
| `WithOptions(options)` / `WithOptions(Action)` | 选项配置 |
| `WithMap(map)` / `WithMap(Action)` | 非侵入式映射 |
| `AddSheet<TOther>(data, name, map)` | 追加数据集为独立 Sheet |
| `WithPicture(configure)` | 在主 Sheet 插入固定位置图片（Logo / 水印），可多次调用 |
| `WithTitle(text, fontSize, mergeAcross)` | 在表前区顶部插入跨列大标题，重复调用以最后一次为准 |
| `WithPreamble(configure)` | 在表头之上追加自由排版行，可多次调用 |
| `WithPostamble(configure)` | 在数据区之下追加自由排版行，可多次调用 |
| `ToFile(path, ct, progress)` | 导出文件，返回 `ExportResult` |
| `ToStream(stream, ct, progress)` | 导出流，返回 `ExportResult` |
| `ToFileAsync(path, ct, progress)` | 异步导出文件，返回 `Task<ExportResult>` |
| `ToStreamAsync(stream, ct, progress)` | 异步导出流，返回 `Task<ExportResult>` |

---

## 异常类型

| 异常 | 说明 |
|------|------|
| `ExcelMappingException` | 基类，所有本库异常 |
| `ExcelPlanValidationException` | 计划校验失败（列重名、多集合摊平等），含 `Errors` 列表 |
| `ExcelFileAccessException` | 文件路径/权限问题 |
| `ExcelImportException` | 导入过程错误 |

```csharp
try
{
    ExcelMapper.SaveToFile(people, "report.xlsx");
}
catch (ExcelPlanValidationException ex)
{
    foreach (var err in ex.Errors)
        Console.WriteLine(err);
}
catch (ExcelFileAccessException ex)
{
    Console.WriteLine($"文件访问失败：{ex.Message}");
}
```

---

## 测试

```bash
cd VassasCo.Utility
dotnet test
```

69 个单元测试覆盖：敏感字段文本、长数字精度、布尔/枚举/byte[] 渲染、嵌套表头、集合子 Sheet、循环引用、分页（多 Sheet / 多文件 / 空数据）、条件格式、异步进度、CSV 安全、报表布局、值转换器（构造器实参与内置转换器）、导入类型转换与错误收集等场景。

---

## 更新说明

### 2.2.0（当前）

**新增**
- **报表布局能力**：在数据表之外写入自由排版内容，覆盖单据、台账、质检报告等表格加抬头/落款的场景。
  - 新增类型：`ReportRow`、`ReportCell`、`ReportRowBuilder`、`CellAlign`。
  - 新增 API：`WithTitle(...)`、`WithPreamble(...)`、`WithPostamble(...)`。
  - 单元格支持横向合并（`Span(0)` 表示跨到表格最后一列，超出表格宽度的部分自动收敛）、
    字号、加粗、水平对齐、字体色与填充色。
  - `WithTitle` 声明的标题恒位于表前区顶部；多个 `WithPreamble` / `WithPostamble` 按调用顺序排列。
  - 未使用任何布局方法时，表头仍从第 1 行开始，与旧版本行为完全一致（向后兼容）。
- **工作表级字体**：`ExcelStyle.FontName` / `FontSize` / `HeaderFontSize`。
- `Engine = Auto` 时检测到布局行自动选用 DOM 引擎；显式指定流式引擎时抛 `ExcelPlanValidationException`，
  避免静默丢标题。
- **值转换器构造器实参**：`[ExcelConverter(typeof(T), params object?[])]`，同一种转换器可用不同实参
  产生不同输出；实参不匹配或无可用构造函数时抛 `ExcelMappingException`。
- **内置转换器**：`BooleanTextConverter`、`MaskConverter`、`NullPlaceholderConverter`，覆盖判定文案、
  文本脱敏、空值占位三类常见场景。转换器在两个引擎下均生效。

### 2.1.0

**新增**
- **Excel 图片插入能力**
  - 新增类型：`ExcelImageOptions`、`ExcelImageAttribute`（`[ExcelImage]`）、`PicturePlan`、`PictureBuilder`。
  - 新增 API：Fluent 列级 `.AsImage(...)`、Builder 表级 `.WithPicture(...)`。
  - 两类图片：**数据列图片**（每行一张，值可为文件路径 / `byte[]` / `Stream`）与
    **固定位置图片**（整表一张，用于 Logo / 水印）。
  - 图片格式自动嗅探（PNG / JPEG / GIF / BMP / TIFF），无需调用方指定格式枚举。
  - 未指定尺寸时使用原图尺寸；`ImageSizeMode.Fit` 支持等比缩放，不变形。
  - 插入后自动按需调整行高 / 列宽，避免图片溢出覆盖相邻内容。
  - 缺失兜底三档：`Skip`（默认留空）/ `WriteSourceText`（写回来源便于排查）/ `Throw`。
- **新增 `net472` 目标框架**，供 .NET Framework 4.7.2+（含 4.8）传统桌面项目直接引用。

**修复**
- `ConfigHelper` 保存时用 `File.Move(tmp, target, overwrite: true)`，
  **该重载在 .NET Framework 下不存在**，导致 net472 目标无法编译（此前因未加该 TFM 而未被发现）。
  改为 `File.Replace(tmp, target, null)`：全目标框架可用、替换原子，
  并消除原"先删除再移动"在两步之间中断会丢失配置文件文件的隐患。
- 补齐 `ExcelMapper` / `LogHelper` 打包所需的 `VassasCoIcon.png`
  （此前仅 ConfigHelper 与 SnowflakeIdHelper 目录内有该图标），
  避免 `PackageIcon` 引用缺失导致 `dotnet pack` 失败。

**兼容性**
- 目标框架：`net472` / `netstandard2.0` / `net6.0` / `net8.0`
- 图片写入仅在 DOM 引擎（ClosedXML）下生效；`Engine=Auto` 时自动降级保护，
  显式 `OpenXmlStreaming` 时图片跳过。
- 本次均为**新增能力 + 内部实现替换**，未修改任何既有公开 API 签名，对现有调用方无破坏性变更。

---

## 许可证

MIT
