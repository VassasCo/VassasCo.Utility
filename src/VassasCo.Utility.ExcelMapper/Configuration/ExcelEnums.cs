// SPDX-License-Identifier: MIT

namespace VassasCo.Utility
{
    /// <summary>集合属性（数组/List 等）在 Excel 中的渲染方式</summary>
    public enum ArrayRenderMode
    {
        /// <summary>集合元素写入独立子 Sheet（默认；多个集合属性时唯一安全的选择）</summary>
        ChildSheet,

        /// <summary>摊平到当前 Sheet：每个集合元素一行，父列重复（仅允许单个集合属性，多集合会抛配置异常以防笛卡尔积）</summary>
        FlattenInPlace,

        /// <summary>只在单元格内显示 "[n items]" 占位文本，不导出明细</summary>
        PlaceholderOnly,

        /// <summary>序列化为 JSON 字符串存入单元格（保留数据、单行展示；复杂元素集合可能超长被截断）</summary>
        Json
    }

    /// <summary>导出引擎选择</summary>
    public enum ExportEngineKind
    {
        /// <summary>自动：开启分页流式、IAsyncEnumerable 或大数据量时使用 OpenXML 流式引擎，否则使用 ClosedXML DOM 引擎</summary>
        Auto,

        /// <summary>ClosedXML DOM 引擎：样式能力最全，整个工作簿驻留内存，适合中小数据量</summary>
        ClosedXml,

        /// <summary>OpenXML SAX 流式引擎：内存占用低，支持超大数据量，但不支持原生条件格式等高级特性（规则改为服务端求值）</summary>
        OpenXmlStreaming
    }

    /// <summary>分页输出方式</summary>
    public enum PageMode
    {
        /// <summary>不分页（若数据超过单 Sheet 上限会抛异常）</summary>
        None,

        /// <summary>按页拆分为同一工作簿内的多个 Sheet（默认分页方式）</summary>
        MultipleSheets,

        /// <summary>按页拆分为多个 .xlsx 文件</summary>
        MultipleFiles
    }

    /// <summary>条件格式比较操作符</summary>
    public enum ConditionOperator
    {
        /// <summary>大于（可转 Excel 原生条件格式）</summary>
        GreaterThan,

        /// <summary>大于等于（可转 Excel 原生条件格式）</summary>
        GreaterOrEqual,

        /// <summary>小于（可转 Excel 原生条件格式）</summary>
        LessThan,

        /// <summary>小于等于（可转 Excel 原生条件格式）</summary>
        LessOrEqual,

        /// <summary>等于（可转 Excel 原生条件格式）</summary>
        Equal,

        /// <summary>不等于（可转 Excel 原生条件格式）</summary>
        NotEqual,

        /// <summary>包含子串（仅服务端求值，不能转原生条件格式）</summary>
        Contains,

        /// <summary>介于两个值之间（含边界）</summary>
        Between
    }

    /// <summary>byte[] 二进制属性的渲染方式</summary>
    public enum BinaryRenderMode
    {
        /// <summary>转为 Base64 字符串</summary>
        Base64,

        /// <summary>输出空文本</summary>
        Empty,

        /// <summary>以 JSON 数组形式输出</summary>
        Json
    }

    /// <summary>长数字自动转文本策略</summary>
    public enum ForceTextAutoMode
    {
        /// <summary>不自动识别（仅按特性/Fluent 显式配置）</summary>
        None,

        /// <summary>仅按属性名敏感词识别（手机/电话/身份证/银行卡/邮编等）</summary>
        SensitiveNames,

        /// <summary>仅按整数容量识别（long/ulong 等可容纳 12 位以上数字的整数类型）</summary>
        LongIntegers,

        /// <summary>敏感词与长整数同时识别（默认）</summary>
        Both
    }
}
