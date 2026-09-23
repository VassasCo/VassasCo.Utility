using System;
using System.Globalization;

namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>标量值双向转换（JSON/XML 共用，保证读写一致）。</summary>
    internal static class ConfigValueCodec
    {
        public static bool IsIntegral(Type t) =>
            t == typeof(byte) || t == typeof(sbyte) ||
            t == typeof(short) || t == typeof(ushort) ||
            t == typeof(int) || t == typeof(uint) ||
            t == typeof(long) || t == typeof(ulong);

        public static bool IsFloating(Type t) =>
            t == typeof(float) || t == typeof(double) || t == typeof(decimal);

        public static bool IsNumeric(Type t) => IsIntegral(t) || IsFloating(t);

        public static bool IsScalarType(Type t)
        {
            if (t.IsEnum) return true;
            if (t == typeof(string) || t == typeof(char) || t == typeof(bool)) return true;
            if (IsNumeric(t)) return true;
            if (t == typeof(DateTime) || t == typeof(DateTimeOffset)
                || t == typeof(Guid) || t == typeof(TimeSpan)) return true;
            return false;
        }

        /// <summary>将 null 规整为值类型的默认值（避免对非空值类型属性赋 null 抛异常）。</summary>
        public static object? NormalizeForType(object? value, Type targetType)
        {
            if (value == null && targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                return Activator.CreateInstance(targetType);
            return value;
        }

        /// <summary>实体值 → 文本（XML 文本节点 / JSON 字符串标量）。</summary>
        public static string SerializeScalar(object value)
        {
            switch (value)
            {
                case DateTime dt: return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case DateTimeOffset dto: return dto.ToString("O", CultureInfo.InvariantCulture);
                case TimeSpan ts: return ts.ToString();
                case Guid g: return g.ToString();
                case bool b: return b ? "true" : "false";
                case char c: return c.ToString();
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString() ?? "";
            }
        }

        /// <summary>枚举值 → 文本（mapType 为 null/string → 枚举名；整数类型 → 十进制数字）。</summary>
        public static string SerializeEnum(object value, Type? mapType)
        {
            if (mapType == null || mapType == typeof(string)) return value.ToString() ?? "";
            var number = Convert.ChangeType(value, mapType, CultureInfo.InvariantCulture);
            return Convert.ToString(number, CultureInfo.InvariantCulture) ?? "0";
        }

        /// <summary>文本 → 枚举（按 mapType 解析；null 时先名称后底层数字，兼容旧数据）。</summary>
        public static bool TryDeserializeEnum(string? text, Type enumType, Type? mapType, out object? value)
        {
            value = null;
            if (string.IsNullOrEmpty(text)) return false;

            if (mapType != null && mapType != typeof(string))
            {
                try
                {
                    var number = Convert.ChangeType(text, mapType, CultureInfo.InvariantCulture);
                    value = Enum.ToObject(enumType, number!);
                    return true;
                }
                catch { return false; }
            }

            if (mapType == typeof(string))
            {
                try { value = Enum.Parse(enumType, text, ignoreCase: true); return true; }
                catch { return false; }
            }

            // mapType 为 null：先按名称，再按底层类型数字（宽容解析）。
            try { value = Enum.Parse(enumType, text, ignoreCase: true); return true; }
            catch { }
            try
            {
                var underlying = Enum.GetUnderlyingType(enumType);
                var number = Convert.ChangeType(text, underlying, CultureInfo.InvariantCulture);
                value = Enum.ToObject(enumType, number!);
                return true;
            }
            catch { return false; }
        }

        /// <summary>文本 → 实体值。</summary>
        public static object? DeserializeScalar(string? text, Type type)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
            {
                if (string.IsNullOrEmpty(text)) return null;
                return DeserializeScalar(text, nullable);
            }

            if (text == null) return null;

            if (type == typeof(string)) return text;
            if (type == typeof(char)) return text.Length > 0 ? text[0] : '\0';
            if (type == typeof(bool)) return text == "1" || (bool.TryParse(text, out var b) && b);
            if (type == typeof(int)) return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
            if (type == typeof(long)) return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0L;
            if (type == typeof(short)) return short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : (short)0;
            if (type == typeof(byte)) return byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var by) ? by : (byte)0;
            if (type == typeof(sbyte)) return sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sb) ? sb : (sbyte)0;
            if (type == typeof(ushort)) return ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) ? us : (ushort)0;
            if (type == typeof(uint)) return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ui) ? ui : 0U;
            if (type == typeof(ulong)) return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul) ? ul : 0UL;
            if (type == typeof(float)) return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
            if (type == typeof(double)) return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
            if (type == typeof(decimal)) return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : 0m;
            if (type == typeof(DateTime)) return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : default(DateTime);
            if (type == typeof(DateTimeOffset)) return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) ? dto : default(DateTimeOffset);
            if (type == typeof(Guid)) return Guid.TryParse(text, out var g) ? g : Guid.Empty;
            if (type == typeof(TimeSpan)) return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts) ? ts : TimeSpan.Zero;

            if (type.IsEnum)
            {
                if (TryDeserializeEnum(text, type, null, out var enumValue))
                    return enumValue;
                return Activator.CreateInstance(type);
            }

            try { return Convert.ChangeType(text, type, CultureInfo.InvariantCulture); }
            catch { return null; }
        }
    }
}
