# ExcelMapper

原生对象→Excel映射引擎 — 将对象列表/字典导出为 .xlsx 文件，无需 COM/VSTO。

## 文件说明

| 文件 | 内容 |
|------|------|
| `ExcelMapper.cs` | 映射器、Fluent Builder、样式配置、Excel 生成引擎 |

## 核心特点

- **嵌套类自动展开**：嵌套属性自动生成父子合并表头
- **数组/List 独立 Sheet**：集合字段自动拆分为子 Sheet，通过关联列回溯父行
- **Dictionary 自适应**：自动判断横表/竖表模式
- **全可配置样式**：表头/边框/冻结/列宽/数字格式/日期格式/颜色
- **Fluent Builder API**：链式配置，语义清晰

## 快速开始

```csharp
// 快捷导出列表
ExcelMapper.ToFile(orders, "orders.xlsx", "Orders");

// Dictionary 导出
var dict = new Dictionary<string, object> { ["Key1"] = 123, ["Key2"] = "abc" };
ExcelMapper.DictionaryToFile(dict, "dict.xlsx");

// Fluent Builder — 自定义样式
ExcelMapper.Build(orders)
    .WithSheetName("订单列表")
    .WithStyle(new ExcelStyle
    {
        HeaderBackgroundColor = "#2E75B6",
        NumberFormat = "N2",
        DateTimeFormat = "yyyy-MM-dd HH:mm:ss",
        AutoFitColumns = true,
        FreezeTopRow = true
    })
    .ToFile("styled.xlsx");

// 自定义列名
public class Order
{
    [ExcelDisplay("订单号")]
    public long Id { get; set; }
    public string Customer { get; set; }
}
```
