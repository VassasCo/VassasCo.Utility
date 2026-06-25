using System;

namespace VassasCo.Utility
{
    public class HexIntConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value is int v ? $"0x{v:X}" : "0x0";
        public object? ConvertFrom(string? configValue)
        {
            if (string.IsNullOrEmpty(configValue)) return 0;
            var s = configValue.Replace("0x", "").Replace("0X", "");
            return Convert.ToInt32(s, 16);
        }
    }

    public class TimeSpanStringConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value is TimeSpan ts ? ts.ToString(@"hh\:mm\:ss") : "00:00:00";
        public object? ConvertFrom(string? configValue) => TimeSpan.TryParse(configValue, out var ts) ? ts : TimeSpan.Zero;
    }

    public class DateTimeStringConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss") : "";
        public object? ConvertFrom(string? configValue) => DateTime.TryParse(configValue, out var dt) ? dt : DateTime.MinValue;
    }

    public class BoolFormatConverter(string trueText = "1", string falseText = "0") : IConfigConverter
    {
        public string ConvertTo(object? value) => value is true ? trueText : falseText;
        public object? ConvertFrom(string? configValue) =>
            string.Equals(configValue, trueText, StringComparison.OrdinalIgnoreCase);
    }

    public class NumberFormatConverter(string format = "F2") : IConfigConverter
    {
        public string ConvertTo(object? value) =>
            value is IFormattable f ? f.ToString(format, null) : (value?.ToString() ?? "");

        public object? ConvertFrom(string? configValue)
        {
            if (string.IsNullOrEmpty(configValue)) return null;
            return double.TryParse(configValue, out var d) ? d : null;
        }
    }

    public class VersionConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value?.ToString() ?? "0.0.0.0";
        public object? ConvertFrom(string? configValue) => Version.TryParse(configValue, out var v) ? v : new Version(1, 0);
    }

    public class TypeNameConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value is Type t ? t.AssemblyQualifiedName ?? t.FullName ?? "" : "";
        public object? ConvertFrom(string? configValue) => string.IsNullOrEmpty(configValue) ? null : Type.GetType(configValue);
    }

    public class EnumIntConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value is Enum e ? Convert.ToInt32(e).ToString() : "0";
        public object? ConvertFrom(string? configValue)
        {
            if (int.TryParse(configValue, out var i)) return i;
            return null;
        }
    }
}
