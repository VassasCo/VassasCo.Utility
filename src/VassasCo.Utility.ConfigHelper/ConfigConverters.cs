using System;

namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>十六进制整数转换器。</summary>
    public sealed class HexIntConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value is int v ? $"0x{v:X}" : "0x0";

        public object? ConvertFrom(string? configValue)
        {
            if (string.IsNullOrEmpty(configValue)) return 0;
            var s = configValue!.Replace("0x", "").Replace("0X", "");
            try { return Convert.ToInt32(s, 16); }
            catch { return 0; }
        }
    }

    /// <summary>时间跨度字符串转换器。</summary>
    public sealed class TimeSpanStringConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value is TimeSpan ts ? ts.ToString(@"hh\:mm\:ss") : "00:00:00";

        public object? ConvertFrom(string? configValue) =>
            TimeSpan.TryParse(configValue, out var ts) ? ts : TimeSpan.Zero;
    }

    /// <summary>日期时间字符串转换器。</summary>
    public sealed class DateTimeStringConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss") : "";

        public object? ConvertFrom(string? configValue) =>
            DateTime.TryParse(configValue, out var dt) ? dt : DateTime.MinValue;
    }

    /// <summary>布尔格式化转换器。</summary>
    public sealed class BoolFormatConverter : IConfigConverter
    {
        private readonly string _trueText;
        private readonly string _falseText;

        public BoolFormatConverter(string trueText = "1", string falseText = "0")
        {
            _trueText = trueText;
            _falseText = falseText;
        }

        public string ConvertTo(object? value) => value is true ? _trueText : _falseText;

        public object? ConvertFrom(string? configValue) =>
            string.Equals(configValue, _trueText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>数字格式化转换器。</summary>
    public sealed class NumberFormatConverter : IConfigConverter
    {
        private readonly string _format;

        public NumberFormatConverter(string format = "F2") { _format = format; }

        public string ConvertTo(object? value) =>
            value is IFormattable f ? f.ToString(_format, null) : (value?.ToString() ?? "");

        public object? ConvertFrom(string? configValue)
        {
            if (string.IsNullOrEmpty(configValue)) return null;
            return double.TryParse(configValue, out var d) ? d : null;
        }
    }

    /// <summary>版本号转换器。</summary>
    public sealed class VersionConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value?.ToString() ?? "0.0.0.0";

        public object? ConvertFrom(string? configValue) =>
            Version.TryParse(configValue, out var v) ? v : new Version(1, 0);
    }

    /// <summary>类型名转换器。</summary>
    public sealed class TypeNameConverter : IConfigConverter
    {
        public string ConvertTo(object? value) =>
            value is Type t ? (t.AssemblyQualifiedName ?? t.FullName ?? "") : "";

        public object? ConvertFrom(string? configValue) =>
            string.IsNullOrEmpty(configValue) ? null : Type.GetType(configValue);
    }

    /// <summary>枚举整数转换器（枚举以整数形式存储）。</summary>
    public sealed class EnumIntConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value is Enum e ? Convert.ToInt32(e).ToString() : "0";

        public object? ConvertFrom(string? configValue)
        {
            if (int.TryParse(configValue, out var i)) return i;
            return null;
        }
    }
}
