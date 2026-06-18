# ExcelMapper

原生对象→Excel映射引擎 — 将对象列表/字典导出为 .xlsx 文件，无需 COM/VSTO，基于 ClosedXML。

## 核心功能

- **列表/字典映射**：IEnumerable&lt;T&gt; → Excel 表格，Dictionary → 自动判断横表或竖表
- **嵌套类展开**：嵌套对象自动展开为父子合并表头
- **数组/List 独立 Sheet**：数组字段在主表中显示占位符，详细数据写入独立 Sheet
- **Dictionary 自适应**：键数量 ≤10 用宽表（键为列头），>10 用竖表（Key/Value 两列）
- **[ExcelDisplay] 自定义标题**：`[ExcelDisplay("中文名")]` 自定义列标题，子 Sheet 名和关联列标题也自动使用
- **全可配置样式**：表头加粗/背景色/字体色/边框/冻结/自动列宽/数字格式/日期格式
- **Fluent Builder API**：链式配置 SheetName、Style 等
- **支持 netstandard2.0 / net6.0 / net8.0**

## 快速开始

### 列表导出

```csharp
public class Order
{
    public int Id { get; set; }
    public string Customer { get; set; }
    public decimal Amount { get; set; }
    public List<OrderItem> Items { get; set; }  // 自动生成独立 Sheet
}

// 静态快捷方法
ExcelMapper.ToFile(orders, "output.xlsx");

// Fluent Builder
ExcelMapper.Build(orders)
    .WithSheetName("订单表")
    .WithStyle(ExcelStyle.Default)
    .ToFile("output.xlsx");
```

### Dictionary 导出

```csharp
var dict = new Dictionary<string, object>
{
    ["Server"] = "192.168.1.1",
    ["Port"] = 8080,
};

// 键少时 → 宽表（一行）
ExcelMapper.DictionaryToFile(dict, "config.xlsx");

// 键多时 → 竖表（Key/Value 两列）
var large = new Dictionary<string, string>();
for (int i = 0; i < 50; i++)
    large[$"Key{i}"] = $"Value{i}";
ExcelMapper.DictionaryToFile(large, "large_config.xlsx");
```

### [ExcelDisplay] 自定义标题

使用 `[ExcelDisplay]` 特性自定义 Excel 列标题：

```csharp
public class Order
{
    [ExcelDisplay("订单编号")]
    public int Id { get; set; }

    [ExcelDisplay("客户名称")]
    public string Customer { get; set; }

    [ExcelDisplay("订单金额")]
    public decimal Amount { get; set; }

    [ExcelDisplay("订单明细")]
    public List<OrderItem> Items { get; set; }
}
// 导出后表头显示：订单编号 | 客户名称 | 订单金额 | 订单明细
// 子 Sheet 名：Orders.订单明细（而非 Orders.Items）
// 关联列标题：订单编号（自动取自 Id 属性的 [ExcelDisplay]）
```

### 子 Sheet 关联列配置

子 Sheet 第一列用于关联回父行。默认自动查找父类中以 `Id` 结尾的属性（`Id`/`Uid`/`Vid` 等，忽略大小写）作为关联列：

```csharp
// 自定义关联属性
var style = new ExcelStyle
{
    ChildSheetParentProperty = "Customer"  // 用 Customer 属性作为关联列
};
// 关联列标题自动取 [ExcelDisplay("客户名称")] → "客户名称"
```

优先级：`[ExcelDisplay]` 内容 → 属性名

### 自定义样式

```csharp
var style = new ExcelStyle
{
    HeaderBold = true,
    HeaderBackgroundColor = "#2E75B6",
    HeaderFontColor = "#FFFFFF",
    AutoFitColumns = true,
    ShowBorder = true,
    FreezeTopRow = true,
    NumberFormat = "N2",
    DateTimeFormat = "yyyy-MM-dd HH:mm:ss"
};

ExcelMapper.Build(data)
    .WithStyle(style)
    .ToFile("styled.xlsx");
```

### 嵌套类自动展开

```csharp
public class Customer
{
    public string Name { get; set; }
    public Address HomeAddress { get; set; }  // 自动展开为父子表头
}

public class Address
{
    public string City { get; set; }
    public string Street { get; set; }
}
// 表头：HomeAddress 合并 | City | Street
```

## API 参考

### ExcelMapper 静态方法

| 方法 | 说明 |
|------|------|
| `ToFile<T>(data, path, sheetName?)` | 列表导出到文件 |
| `ToStream<T>(data, stream, sheetName?)` | 列表导出到流 |
| `DictionaryToFile(dict, path, sheetName?)` | Dictionary 导出到文件 |
| `DictionaryToStream(dict, stream, sheetName?)` | Dictionary 导出到流 |
| `Build<T>(data)` | 创建 Fluent Builder |

### 特性

| 特性 | 说明 |
|------|------|
| `[ExcelDisplay("名称")]` | 自定义属性在 Excel 中的列标题，子 Sheet 名和关联列标题也基于此 |

### ExcelStyle 属性

| 属性 | 默认值 | 说明 |
|------|--------|------|
| `HeaderBold` | `true` | 表头加粗 |
| `HeaderBackgroundColor` | `"#4472C4"` | 表头背景色 |
| `HeaderFontColor` | `"#FFFFFF"` | 表头字体颜色 |
| `AutoFitColumns` | `true` | 自动调整列宽 |
| `ShowBorder` | `true` | 显示边框 |
| `FreezeTopRow` | `true` | 冻结表头行 |
| `FreezeFirstColumn` | `false` | 冻结首列 |
| `NumberFormat` | `null` | 数字格式（如 "N2"） |
| `DateTimeFormat` | `null` | 日期格式（如 "yyyy-MM-dd"） |
| `ChildSheetParentProperty` | `null` | 子 Sheet 关联父行的属性名，null 自动查找 Id/Uid/Vid |

## 依赖

- [ClosedXML](https://github.com/ClosedXML/ClosedXML) — 纯 .NET Excel 操作库
