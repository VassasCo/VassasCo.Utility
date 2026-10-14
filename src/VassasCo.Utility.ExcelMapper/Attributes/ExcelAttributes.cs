// SPDX-License-Identifier: MIT

using System;

namespace VassasCo.Utility
{
    /// <summary>类型级 Excel 配置（侵入式）：Sheet 名称、分页、集合渲染等</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
    public sealed class ExcelSheetAttribute : Attribute
    {
        /// <summary>Sheet 显示名称（默认使用类型名）</summary>
        public string? Name { get; set; }

        /// <summary>每页最大数据行数；0 表示不分页（默认 0）。单 Sheet 上限 1,048,576 行</summary>
        public int PageSize { get; set; }

        /// <summary>集合属性的渲染方式，默认 <see cref="ArrayRenderMode.ChildSheet"/></summary>
        public ArrayRenderMode ArrayRender { get; set; } = ArrayRenderMode.ChildSheet;

        /// <summary>是否开启自动筛选下拉箭头，默认 false</summary>
        public bool AutoFilter { get; set; }

        /// <summary>是否作为 Excel Table（结构化表格，自带隔行底色与筛选），默认 false</summary>
        public bool UseTable { get; set; }
    }

    /// <summary>Excel 列标题显示名称（轻量特性，仅设置列名）</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ExcelDisplayAttribute : Attribute
    {
        /// <summary>显示名称</summary>
        public string Name { get; }

        /// <param name="name">Excel 表头中显示的列名</param>
        public ExcelDisplayAttribute(string name)
        {
            Name = name;
        }
    }

    /// <summary>标记该属性不参与导出/导入</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ExcelIgnoreAttribute : Attribute
    {
    }

    /// <summary>列级 Excel 配置（侵入式）：名称、顺序、格式、强制文本等</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ExcelColumnAttribute : Attribute
    {
        /// <summary>列标题名称（null 使用属性名）</summary>
        public string? Name { get; set; }

        /// <summary>列顺序（升序，未标注的排在标注项之后并保持属性声明顺序）</summary>
        public int Order { get; set; }

        /// <summary>数字/日期格式字符串，如 "N2"、"yyyy-MM-dd"（null 不设置）</summary>
        public string? Format { get; set; }

        /// <summary>强制以文本形式写入（身份证、手机号、长 ID 等），默认 false</summary>
        public bool ForceText { get; set; }

        /// <summary>固定列宽（字符宽度，>0 时生效，且不再自动调整该列）</summary>
        public double Width { get; set; }

        /// <summary>文本过长时自动换行，默认 false</summary>
        public bool WrapText { get; set; }

        /// <summary>将该字符串列的值作为超链接地址写入单元格，默认 false</summary>
        public bool AsHyperlink { get; set; }
    }

    /// <summary>
    /// 图片列配置（侵入式）：标注后该属性的值被当作图片来源插入单元格，
    /// 支持文件路径（string）、字节数组（byte[]）、流（Stream）三种来源。
    /// </summary>
    /// <remarks>
    /// 该特性与 <c>[ExcelColumn]</c> 可叠加使用：<c>[ExcelColumn]</c> 控制列名/顺序，
    /// <c>[ExcelImage]</c> 控制图片尺寸与兜底。
    /// 图片写入依赖 DOM 引擎（ClosedXML），使用流式引擎时会自动降级（见 <see cref="ExcelExportOptions.Engine"/>）。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ExcelImageAttribute : Attribute
    {
        /// <summary>显示宽度（像素）；&lt;=0 时使用图片原始宽度</summary>
        public double Width { get; set; }

        /// <summary>显示高度（像素）；&lt;=0 时使用图片原始高度</summary>
        public double Height { get; set; }

        /// <summary>尺寸自适应模式，默认 <see cref="ImageSizeMode.Original"/></summary>
        public ImageSizeMode SizeMode { get; set; } = ImageSizeMode.Original;

        /// <summary>定位方式，默认 <see cref="ImagePlacement.Cell"/></summary>
        public ImagePlacement Placement { get; set; } = ImagePlacement.Cell;

        /// <summary>图片缺失时的处理策略，默认 <see cref="MissingImagePolicy.Skip"/></summary>
        public MissingImagePolicy OnMissing { get; set; } = MissingImagePolicy.Skip;

        /// <summary>是否自动调高行高以容纳图片，默认 true</summary>
        public bool AdjustRowHeight { get; set; } = true;

        /// <summary>是否自动加宽列宽以容纳图片，默认 true</summary>
        public bool AdjustColumnWidth { get; set; } = true;

        /// <summary>行高的额外留白（像素），默认 4</summary>
        public double RowPadding { get; set; } = 4;

        /// <summary>图片名称（同一 Sheet 内需唯一）；为空时自动生成</summary>
        public string? Name { get; set; }

        /// <summary>
        /// 相对路径的基准目录；为 null 时使用程序所在目录。
        /// 用于避免"相对路径以当前工作目录为基准"导致的运行时找不到图片。
        /// </summary>
        public string? BaseDirectory { get; set; }
    }

    /// <summary>条件格式规则（侵入式）：标注在数值属性上，可多次标注；如"大于 60 显示红色"</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    public sealed class ConditionalFormatAttribute : Attribute
    {
        /// <summary>比较操作符</summary>
        public ConditionOperator Operator { get; }

        /// <summary>比较值（Between 时为下界）</summary>
        public double Value { get; }

        /// <summary>比较值上界（仅 Between 使用）</summary>
        public double Value2 { get; set; }

        /// <summary>命中后的字体颜色（十六进制，如 "#FF0000"，null 不变）</summary>
        public string? FontColor { get; set; }

        /// <summary>命中后的背景色（十六进制，null 不变）</summary>
        public string? FillColor { get; set; }

        /// <summary>命中后是否加粗</summary>
        public bool Bold { get; set; }

        /// <param name="op">比较操作符</param>
        /// <param name="value">比较值</param>
        public ConditionalFormatAttribute(ConditionOperator op, double value)
        {
            Operator = op;
            Value = value;
        }
    }

    /// <summary>
    /// 使用自定义值转换器（侵入式）：转换器必须实现 <see cref="IExcelValueConverter"/>。
    /// 不传构造器参数时要求转换器具备公共无参构造函数。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ExcelConverterAttribute : Attribute
    {
        /// <summary>转换器类型</summary>
        public Type ConverterType { get; }

        /// <summary>构造转换器时使用的实参；为 null 时以无参构造函数创建</summary>
        public object?[]? Args { get; }

        /// <param name="converterType">实现 <see cref="IExcelValueConverter"/> 的类型</param>
        /// <param name="args">构造转换器时传入的实参，用于复用同一个参数化转换器产生不同输出</param>
        public ExcelConverterAttribute(Type converterType, params object?[] args)
        {
            ConverterType = converterType;
            Args = args is { Length: > 0 } ? args : null;
        }
    }

    /// <summary>值转换器接口：导出前对属性值做自定义转换（如脱敏、拼接、本地化）</summary>
    public interface IExcelValueConverter
    {
        /// <summary>转换属性值</summary>
        /// <param name="value">原始属性值（可能为 null）</param>
        /// <returns>转换后写入单元格的值</returns>
        object? Convert(object? value);
    }
}
