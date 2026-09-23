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

    /// <summary>使用自定义值转换器（侵入式）：转换器必须实现 <see cref="IExcelValueConverter"/> 且有无参构造函数</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ExcelConverterAttribute : Attribute
    {
        /// <summary>转换器类型</summary>
        public Type ConverterType { get; }

        /// <param name="converterType">实现 <see cref="IExcelValueConverter"/> 的类型</param>
        public ExcelConverterAttribute(Type converterType)
        {
            ConverterType = converterType;
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
