// SPDX-License-Identifier: MIT

namespace VassasCo.Utility
{
    /// <summary>Excel 视觉样式配置（侵入式与非侵入式共用）</summary>
    public class ExcelStyle
    {
        /// <summary>表头加粗，默认 true</summary>
        public bool HeaderBold { get; set; } = true;

        /// <summary>表头背景色（十六进制），默认 "#4472C4"</summary>
        public string HeaderBackgroundColor { get; set; } = "#4472C4";

        /// <summary>表头字体颜色（十六进制），默认 "#FFFFFF"</summary>
        public string HeaderFontColor { get; set; } = "#FFFFFF";

        /// <summary>表头行高（磅，null 自动）</summary>
        public double? HeaderHeight { get; set; }

        /// <summary>自动调整列宽，默认 true（会被列宽上下限与固定列宽约束）</summary>
        public bool AutoFitColumns { get; set; } = true;

        /// <summary>自动列宽下限（字符宽度，null 不限制）</summary>
        public double? MinColumnWidth { get; set; }

        /// <summary>自动列宽上限（字符宽度，null 不限制；防止超长文本撑出超宽列）</summary>
        public double? MaxColumnWidth { get; set; } = 50;

        /// <summary>数据区文本自动换行，默认 false</summary>
        public bool WrapText { get; set; }

        /// <summary>显示边框，默认 true</summary>
        public bool ShowBorder { get; set; } = true;

        /// <summary>隔行底色（斑马纹），默认 false（开启 UseTable 时由表格主题提供）</summary>
        public bool BandedRows { get; set; }

        /// <summary>斑马纹偶数行背景色（十六进制），默认 "#F2F2F2"</summary>
        public string BandedRowColor { get; set; } = "#F2F2F2";

        /// <summary>冻结表头行，默认 true</summary>
        public bool FreezeTopRow { get; set; } = true;

        /// <summary>冻结首列，默认 false</summary>
        public bool FreezeFirstColumn { get; set; }

        /// <summary>全局数字格式化字符串（如 "N2"），null 表示不设置；建议用列级 Format 精确控制，避免污染 ID 列</summary>
        public string? NumberFormat { get; set; }

        /// <summary>日期时间格式化字符串（如 "yyyy-MM-dd HH:mm:ss"），null 表示不设置</summary>
        public string? DateTimeFormat { get; set; }

        /// <summary>布尔 true 的显示文本，默认 "是"</summary>
        public string TrueText { get; set; } = "是";

        /// <summary>布尔 false 的显示文本，默认 "否"</summary>
        public string FalseText { get; set; } = "否";

        /// <summary>枚举是否优先读取 [Description]/[Display] 特性输出，默认 true</summary>
        public bool UseEnumDescription { get; set; } = true;

        /// <summary>子 Sheet 第一列（关联父行）取值的属性名，null 则自动查找 Id/Uid/Vid 等</summary>
        public string? ChildSheetParentProperty { get; set; }

        /// <summary>默认样式</summary>
        public static ExcelStyle Default => new ExcelStyle();
    }
}
