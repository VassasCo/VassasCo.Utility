namespace VassasCo.Utility
{
    /// <summary>
    /// 估算文本在单元格中的显示宽度，供自适应列宽使用。
    /// 宽度单位与 Excel 列宽单位一致：默认字号下半角字符的宽度为 1。
    /// </summary>
    /// <remarks>
    /// 之所以不依赖生成库的自动列宽接口：DOM 引擎的底层库按字体度量计算宽度时，
    /// 会把 CJK 字符算成接近零宽，导致中文表头被严重压窄而显示不全。
    /// 按字符类别估算虽然略粗，但对中英混排表现稳定，且两个引擎的结果一致。
    /// </remarks>
    internal static class ColumnWidthEstimator
    {
        /// <summary>列宽计算所基于的默认字号（磅）</summary>
        private const double DefaultFontSize = 11.0;

        /// <summary>单元格左右留白折算出的宽度余量，避免文字紧贴列边界</summary>
        private const double Padding = 2.0;

        /// <summary>
        /// 自适应列宽时最多扫描的数据行数。
        /// 逐格度量显示文本在大批量导出时开销明显，超出后以前面的行作为样本。
        /// </summary>
        public const int MaxScanRows = 500;

        /// <summary>单个字符的显示宽度：全角 2，半角 1</summary>
        public static double Measure(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            double width = 0;
            foreach (char c in text!)
            {
                // 代理对的低位已随高位计入一个字符，避免重复累加
                if (c >= '\uDC00' && c <= '\uDFFF')
                    continue;

                width += IsFullWidth(c) ? 2.0 : 1.0;
            }

            return width;
        }

        /// <summary>
        /// 文本在指定字号下所需的列宽（含边距）。
        /// 列宽单位以默认字号为基准，因此字号越大，同样宽度能容纳的字符越少。
        /// </summary>
        public static double ToColumnWidth(string? text, double? fontSize)
        {
            double size = fontSize ?? DefaultFontSize;
            return Measure(text) * (size / DefaultFontSize) + Padding;
        }

        /// <summary>判断字符是否为全角（在等宽字体下占两个半角宽度）</summary>
        private static bool IsFullWidth(char c)
        {
            // 韩文字母与音节
            if (c >= '\u1100' && c <= '\u115F')
                return true;
            if (c >= '\uAC00' && c <= '\uD7A3')
                return true;

            // CJK 部首、汉字、日文假名、注音、CJK 符号与标点
            if (c >= '\u2E80' && c <= '\uA4CF')
                return true;

            // CJK 兼容表意文字与兼容形式
            if (c >= '\uF900' && c <= '\uFAFF')
                return true;
            if (c >= '\uFE30' && c <= '\uFE4F')
                return true;

            // 全角 ASCII 与全角标点
            if (c >= '\uFF00' && c <= '\uFF60')
                return true;
            if (c >= '\uFFE0' && c <= '\uFFE6')
                return true;

            // 代理项高位：代表 BMP 之外的字符，绝大多数是 CJK 扩展区汉字
            if (c >= '\uD800' && c <= '\uDBFF')
                return true;

            return false;
        }
    }
}
