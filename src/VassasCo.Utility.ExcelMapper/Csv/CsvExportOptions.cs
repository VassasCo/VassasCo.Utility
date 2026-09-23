// SPDX-License-Identifier: MIT

namespace VassasCo.Utility
{
    /// <summary>CSV 导出选项（RFC 4180 + 安全策略）</summary>
    public class CsvExportOptions
    {
        /// <summary>字段分隔符，默认 ','；欧洲表格习惯可用 ';'</summary>
        public char Delimiter { get; set; } = ',';

        /// <summary>写入 UTF-8 BOM（Excel 打开中文不乱码），默认 true</summary>
        public bool UseUtf8Bom { get; set; } = true;

        /// <summary>是否给所有字段加引号；false（默认）时仅在必要时（含分隔符/引号/换行）加引号</summary>
        public bool QuoteAllFields { get; set; }

        /// <summary>
        /// CSV 公式注入防护：首字符为 = + - @ 以及 Tab/CR 时前置单引号，默认 true。
        /// 防止 Excel 打开 CSV 时把恶意文本当公式执行。
        /// </summary>
        public bool FormulaInjectionGuard { get; set; } = true;

        /// <summary>写入文件时自动创建不存在的目录，默认 true</summary>
        public bool AutoCreateDirectory { get; set; } = true;

        /// <summary>是否写入表头行，默认 true</summary>
        public bool IncludeHeader { get; set; } = true;

        /// <summary>默认选项</summary>
        public static CsvExportOptions Default => new CsvExportOptions();
    }
}
