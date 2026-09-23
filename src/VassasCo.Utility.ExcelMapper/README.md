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

---

## 目标框架与依赖

| 目标框架 | ClosedXML | DocumentFormat.OpenXml | System.Text.Json |
|----------|-----------|------------------------|-------------------|
| netstandard2.0 | 0.102.3 | 2.19.0 | 8.0.5 |
| net6.0 | 0.105.0 | 3.1.1 | — |
| net8.0 | 0.105.0 | 3.1.1 | — |

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
| `[ExcelConverter]` | 属性 | 自定义值转换器（实现 `IExcelValueConverter`） |

### 配置优先级

```
Fluent EntityMap > 本包特性 > DataAnnotations 约定 > 属性名
```

---

## 非侵入式：Fluent 配置

不修改业务类，在外部用 Lambda 声明映射：

```csharp
var map = new EntityMap<Person>()
    .Property(p => p.Id).HasName("编号").Order(1).Width(10)
    .Property(p => p.Name).HasName("姓名").Order(2).Width(15)
    .Property(p => p.IdCard).HasName("身份证号").AsText().Width(22)
    .Property(p => p.HireDate).Format("yyyy-MM-dd")
    .Property(p => p.Score).HasName("绩效").Format("N2")
        .Rule(ConditionOperator.GreaterThan, 90, s => s.Fill("#00B050").Bold())
        .Rule(ConditionOperator.LessThan, 60, s => s.Font("#FF0000"))
    .Ignore(p => p.InternalNote);

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

40 个单元测试覆盖：敏感字段文本、长数字精度、布尔/枚举/byte[] 渲染、嵌套表头、集合子 Sheet、循环引用、分页（多 Sheet / 多文件 / 空数据）、条件格式、异步进度、CSV 安全、导入类型转换与错误收集等场景。

---

## 许可证

MIT
