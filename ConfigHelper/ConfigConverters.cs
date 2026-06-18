using System;

namespace VassasCo.Utility
{
    /// <summary>十六进制整数转换器：实体 int ↔ 配置字符串 "0x..."</summary>
    public class HexIntConverter : IConfigConverter
    {
        /// <inheritdoc />
        public string ConvertTo(object? value) => value is int v ? $"0x{v:X}" : "0x0";

        /// <inheritdoc />
        public object? ConvertFrom(string? configValue)
        {
            if (string.IsNullOrEmpty(configValue)) return 0;
            var s = configValue!.Replace("0x", "").Replace("0X", "");
            return Convert.ToInt32(s, 16);
        }
    }

    /// <summary>TimeSpan 字符串转换器：实体 TimeSpan ↔ 配置字符串 "hh:mm:ss"</summary>
    public class TimeSpanStringConverter : IConfigConverter
    {
        /// <inheritdoc />
        public string ConvertTo(object? value) => value is TimeSpan ts ? ts.ToString(@"hh\:mm\:ss") : "00:00:00";

        /// <inheritdoc />
        public object? ConvertFrom(string? configValue) => TimeSpan.TryParse(configValue, out var ts) ? ts : TimeSpan.Zero;
    }

    /// <summary>DateTime 字符串转换器：实体 DateTime ↔ 配置字符串 "yyyy-MM-dd HH:mm:ss"</summary>
    public class DateTimeStringConverter : IConfigConverter
    {
        /// <inheritdoc />
        public string ConvertTo(object? value) => value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss") : "";

        /// <inheritdoc />
        public object? ConvertFrom(string? configValue) => DateTime.TryParse(configValue, out var dt) ? dt : DateTime.MinValue;
    }

    /// <summary>布尔值格式转换器：实体 bool ↔ 自定义字符串（如 "1"/"0"、"true"/"false"）</summary>
    public class BoolFormatConverter(string trueText = "1", string falseText = "0") : IConfigConverter
    {
        /// <inheritdoc />
        public string ConvertTo(object? value) => value is true ? trueText : falseText;

        /// <inheritdoc />
        public object? ConvertFrom(string? configValue) =>
            string.Equals(configValue, trueText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>数字格式化转换器：实体数值 ↔ 配置字符串（如保留 2 位小数）</summary>
    public class NumberFormatConverter(string format = "F2") : IConfigConverter
    {
        /// <inheritdoc />
        public string ConvertTo(object? value) =>
            value is IFormattable f ? f.ToString(format, null) : (value?.ToString() ?? "");

        /// <inheritdoc />
        public object? ConvertFrom(string? configValue)
        {
            if (string.IsNullOrEmpty(configValue)) return null;
            return double.TryParse(configValue, out var d) ? d : null;
        }
    }

    /// <summary>版本号转换器：实体 Version ↔ 配置字符串 "x.x.x.x"</summary>
    public class VersionConverter : IConfigConverter
    {
        /// <inheritdoc />
        public string ConvertTo(object? value) => value?.ToString() ?? "0.0.0.0";

        /// <inheritdoc />
        public object? ConvertFrom(string? configValue) => Version.TryParse(configValue, out var v) ? v : new Version(1, 0);
    }

    /// <summary>类型名称转换器：实体 Type ↔ 程序集限定名</summary>
    public class TypeNameConverter : IConfigConverter
    {
        /// <inheritdoc />
        public string ConvertTo(object? value) => value is Type t ? t.AssemblyQualifiedName ?? t.FullName ?? "" : "";

        /// <inheritdoc />
        public object? ConvertFrom(string? configValue) => string.IsNullOrEmpty(configValue) ? null : Type.GetType(configValue);
    }

    /// <summary>枚举整数转换器：实体 Enum ↔ 配置字符串 "123"（枚举的 int 值）</summary>
    public class EnumIntConverter : IConfigConverter
    {
        /// <inheritdoc />
        public string ConvertTo(object? value) => value is Enum e ? Convert.ToInt32(e).ToString() : "0";

        /// <inheritdoc />
        public object? ConvertFrom(string? configValue)
        {
            if (int.TryParse(configValue, out var i)) return i;
            return null;
        }
    }
}
