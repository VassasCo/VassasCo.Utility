namespace VassasCo.Utility
{
    /// <summary>
    /// 把布尔判定渲染成一对可读文本，用于替代界面/报表中 True、False 的原始形态。
    /// 同一个类型通过不同的构造器实参复用为多种判定文案（OK/NG、是/否、合格/不合格等）。
    /// </summary>
    /// <remarks>
    /// <code>[ExcelConverter(typeof(BooleanTextConverter), "合格", "不合格")]</code>
    /// 不传实参时使用 OK / NG。可空布尔 boxing 后即为 bool 或 null，无需额外处理。
    /// 非布尔值原样返回，便于把同一个转换器挂在不确定的 object 属性上。
    /// </remarks>
    public sealed class BooleanTextConverter : IExcelValueConverter
    {
        private readonly string _trueText;
        private readonly string _falseText;

        /// <summary>使用默认文案 OK / NG</summary>
        public BooleanTextConverter()
            : this("OK", "NG")
        {
        }

        /// <summary>使用指定的成对文案</summary>
        /// <param name="trueText">值为 true 时写入的文本</param>
        /// <param name="falseText">值为 false 时写入的文本</param>
        public BooleanTextConverter(string trueText, string falseText)
        {
            _trueText = trueText ?? string.Empty;
            _falseText = falseText ?? string.Empty;
        }

        /// <inheritdoc/>
        public object? Convert(object? value)
        {
            if (value is bool flag)
                return flag ? _trueText : _falseText;

            return value;
        }
    }

    /// <summary>
    /// 文本脱敏：保留首尾指定长度的原文，其余以掩码字符替换。
    /// 常用于手机号、身份证、银行卡等敏感信息的报表导出。
    /// </summary>
    /// <remarks>
    /// <code>[ExcelConverter(typeof(MaskConverter), 3, 4)]</code> 表示保留前 3 位与后 4 位。
    /// 首尾保留长度之和不小于文本长度时整串替换为掩码。
    /// </remarks>
    public sealed class MaskConverter : IExcelValueConverter
    {
        private readonly int _headLength;
        private readonly int _tailLength;
        private readonly char _maskChar;

        /// <summary>整串替换为掩码字符</summary>
        public MaskConverter()
            : this(0, 0)
        {
        }

        /// <summary>保留首尾指定长度，中间以星号替换</summary>
        /// <param name="headLength">开头保留的字符数</param>
        /// <param name="tailLength">结尾保留的字符数</param>
        public MaskConverter(int headLength, int tailLength)
            : this(headLength, tailLength, '*')
        {
        }

        /// <summary>保留首尾指定长度，中间以指定字符替换</summary>
        /// <param name="headLength">开头保留的字符数</param>
        /// <param name="tailLength">结尾保留的字符数</param>
        /// <param name="maskChar">替换中间部分的字符</param>
        public MaskConverter(int headLength, int tailLength, char maskChar)
        {
            _headLength = headLength < 0 ? 0 : headLength;
            _tailLength = tailLength < 0 ? 0 : tailLength;
            _maskChar = maskChar;
        }

        /// <inheritdoc/>
        public object? Convert(object? value)
        {
            string? text = value as string ?? value?.ToString();
            if (text == null || text.Length == 0)
                return text;

            if (_headLength + _tailLength >= text.Length)
                return new string(_maskChar, text.Length);

            int maskedLength = text.Length - _headLength - _tailLength;
            return string.Concat(
                text.Substring(0, _headLength),
                new string(_maskChar, maskedLength),
                text.Substring(text.Length - _tailLength));
        }
    }

    /// <summary>
    /// 把 null 与空白统一替换为占位文本，避免报表中出现无法区分语义的空单元格。
    /// </summary>
    /// <remarks>
    /// 数值属性为 null 时同样落到占位文本；非空原值保持不变。
    /// 占位文本按文本写入，不会触发数字或日期解析。
    /// </remarks>
    public sealed class NullPlaceholderConverter : IExcelValueConverter
    {
        private readonly string _placeholder;

        /// <summary>使用占位文本“-”</summary>
        public NullPlaceholderConverter()
            : this("-")
        {
        }

        /// <summary>使用指定的占位文本</summary>
        /// <param name="placeholder">替换 null 与空白的文本</param>
        public NullPlaceholderConverter(string placeholder)
        {
            _placeholder = placeholder;
        }

        /// <inheritdoc/>
        public object? Convert(object? value)
        {
            if (value == null)
                return _placeholder;

            if (value is string text && text.Trim().Length == 0)
                return _placeholder;

            return value;
        }
    }
}
