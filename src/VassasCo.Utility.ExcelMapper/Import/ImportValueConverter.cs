// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// 导入值安全转换：覆盖数字（不丢精度的长整型解析）、布尔（是/否/1/0）、
    /// 日期、枚举（名称与 [Description]/[Display]）、Guid、byte[]、可空类型。
    /// 任何失败都返回原因而非抛异常（由导入器决定错误收集策略）。
    /// </summary>
    internal static class ImportValueConverter
    {
        private static readonly string[] DatePatterns =
        {
            "yyyy-MM-dd", "yyyy/M/d", "yyyy/M/d HH:mm:ss", "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm", "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss",
            "yyyy.MM.dd", "yyyyMMdd", "M/d/yyyy", "d/M/yyyy"
        };

        public static bool TryConvert(object? raw, Type target, out object? result, out string reason)
        {
            result = null;
            reason = "";

            Type underlying = Nullable.GetUnderlyingType(target) ?? target;

            if (raw is null || (raw is string s && s.Length == 0))
            {
                if (target != underlying || !underlying.GetTypeInfo().IsValueType)
                    return true; // 空 → null
                reason = "值为空但目标类型不可空";
                return false;
            }

            if (raw is string rawString)
                rawString = rawString.Trim();

            if (underlying == typeof(string))
            {
                result = raw is string str ? str : Convert.ToString(raw, CultureInfo.InvariantCulture);
                return true;
            }

            if (underlying == typeof(char))
            {
                string text = raw!.ToString()!;
                if (text.Length == 1)
                {
                    result = text[0];
                    return true;
                }
                reason = $"无法转为字符：{text}";
                return false;
            }

            if (underlying == typeof(bool))
                return TryBool(raw, out result, out reason);

            if (underlying.GetTypeInfo().IsEnum)
                return TryEnum(raw, underlying, out result, out reason);

            if (underlying == typeof(DateTime))
                return TryDateTime(raw, out result, out reason);

            if (underlying == typeof(DateTimeOffset))
                return TryDateTimeOffset(raw, out result, out reason);

            if (underlying == typeof(TimeSpan))
                return TryTimeSpan(raw, out result, out reason);

            if (underlying == typeof(Guid))
                return TryGuid(raw, out result, out reason);

            if (underlying == typeof(byte[]))
                return TryBytes(raw, out result, out reason);

            if (TypeMetadata.IsCollection(underlying))
                return TryCollection(raw, underlying, out result, out reason);

            if (IsNumeric(underlying))
                return TryNumber(raw, underlying, out result, out reason);

            return TryFallback(raw, underlying, out result, out reason);
        }

        private static bool TryBool(object raw, out object? result, out string reason)
        {
            if (raw is bool b)
            {
                result = b;
                reason = "";
                return true;
            }

            string text = raw.ToString()!.Trim().ToLowerInvariant();
            switch (text)
            {
                case "1": case "true": case "yes": case "y": case "是":
                    result = true; reason = ""; return true;
                case "0": case "false": case "no": case "n": case "否":
                    result = false; reason = ""; return true;
            }

            result = null;
            reason = $"无法识别的布尔值：{raw}";
            return false;
        }

        private static bool TryNumber(object raw, Type target, out object? result, out string reason)
        {
            try
            {
                if (raw is string text)
                {
                    text = text.Trim().Replace(",", "");
                    result = ParseNumber(text, target);
                    reason = "";
                    return true;
                }

                if (raw is IConvertible)
                {
                    result = Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
                    reason = "";
                    return true;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
            {
                result = null;
                reason = $"数值转换失败：{ex.Message}";
                return false;
            }

            result = null;
            reason = $"无法转为数值：{raw}";
            return false;
        }

        private static object ParseNumber(string text, Type target)
        {
            if (target == typeof(long)) return long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(ulong)) return ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(int)) return int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(uint)) return uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(short)) return short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(ushort)) return ushort.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(byte)) return byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(sbyte)) return sbyte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(decimal)) return decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
            if (target == typeof(double)) return double.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
            if (target == typeof(float)) return float.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
            return Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
        }

        private static bool TryDateTime(object raw, out object? result, out string reason)
        {
            if (raw is DateTime dt)
            {
                result = dt; reason = ""; return true;
            }

            string text = raw.ToString()!.Trim();
            if (DateTime.TryParseExact(text, DatePatterns, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed)
                || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                || DateTime.TryParse(text, new CultureInfo("zh-CN"), DateTimeStyles.None, out parsed))
            {
                result = parsed; reason = ""; return true;
            }

            result = null;
            reason = $"无法解析日期：{text}";
            return false;
        }

        private static bool TryDateTimeOffset(object raw, out object? result, out string reason)
        {
            if (raw is DateTimeOffset dto)
            {
                result = dto; reason = ""; return true;
            }

            if (DateTimeOffset.TryParse(raw.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                result = parsed; reason = ""; return true;
            }

            result = null;
            reason = $"无法解析带时区日期：{raw}";
            return false;
        }

        private static bool TryTimeSpan(object raw, out object? result, out string reason)
        {
            if (TimeSpan.TryParse(raw.ToString(), CultureInfo.InvariantCulture, out var ts))
            {
                result = ts; reason = ""; return true;
            }
            result = null;
            reason = $"无法解析时间段：{raw}";
            return false;
        }

        private static bool TryGuid(object raw, out object? result, out string reason)
        {
            if (raw is Guid g)
            {
                result = g; reason = ""; return true;
            }
            if (Guid.TryParse(raw.ToString(), out var parsed))
            {
                result = parsed; reason = ""; return true;
            }
            result = null;
            reason = $"无法解析 Guid：{raw}";
            return false;
        }

        private static bool TryBytes(object raw, out object? result, out string reason)
        {
            if (raw is byte[] bytes)
            {
                result = bytes; reason = ""; return true;
            }
            try
            {
                result = Convert.FromBase64String(raw.ToString()!);
                reason = "";
                return true;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentNullException)
            {
                result = null;
                reason = "无法按 Base64 解析二进制内容";
                return false;
            }
        }

        private static bool TryEnum(object raw, Type enumType, out object? result, out string reason)
        {
            if (raw.GetType() == enumType)
            {
                result = raw; reason = ""; return true;
            }

            string text = raw.ToString()!.Trim();

            // 数值 → 直接按底层值转换
            if (double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                try
                {
                    result = Enum.ToObject(enumType, Convert.ChangeType(raw, Enum.GetUnderlyingType(enumType), CultureInfo.InvariantCulture));
                    reason = "";
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    // 落到名称匹配
                }
            }

            // 名称匹配（netstandard2.0 没有非泛型 Enum.TryParse(Type,...) 重载，用泛型方法反射调用）
            if (TryParseEnum(enumType, text, out var parsed))
            {
                result = parsed; reason = ""; return true;
            }

            // Description/Display 文本匹配
            foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                string? description = TypeMetadata.GetEnumDescription(field);
                if (description != null && string.Equals(description, text, StringComparison.OrdinalIgnoreCase))
                {
                    result = field.GetValue(null);
                    reason = "";
                    return true;
                }
            }

            result = null;
            reason = $"无法匹配枚举 {enumType.Name}：{text}";
            return false;
        }

        private static bool TryParseEnum(Type enumType, string text, out object? value)
        {
            var genericMethod = typeof(Enum).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(Enum.TryParse)
                    && m.IsGenericMethod
                    && m.GetParameters().Length == 3)
                .MakeGenericMethod(enumType);

            var args = new object?[] { text, true, null };
            bool ok = (bool)genericMethod.Invoke(null, args)!;
            value = ok ? args[2] : null;
            return ok;
        }

        private static bool TryFallback(object raw, Type target, out object? result, out string reason)
        {
            try
            {
                result = Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
                reason = "";
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                result = null;
                reason = $"类型转换失败（{target.Name}）：{ex.Message}";
                return false;
            }
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(int) || type == typeof(long) || type == typeof(short)
                || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong)
                || type == typeof(ushort) || type == typeof(sbyte)
                || type == typeof(decimal) || type == typeof(double) || type == typeof(float);
        }

        /// <summary>集合属性：从 JSON 文本反序列化（与 ArrayRenderMode.Json 导出往返）</summary>
        private static bool TryCollection(object raw, Type target, out object? result, out string reason)
        {
            string json = raw is string s ? s : raw.ToString()!;
            json = json.Trim();
            if (json.Length == 0)
            {
                result = null;
                reason = "";
                return true; // 空文本 → null（集合属性置空）
            }

            Type concreteType = ResolveCollectionConcreteType(target);
            try
            {
                result = JsonSerializer.Deserialize(json, concreteType);
                reason = "";
                return true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                result = null;
                reason = $"无法将 JSON 反序列化为 {target.Name}：{ex.Message}";
                return false;
            }
        }

        /// <summary>确定可反序列化的具体集合类型（接口/抽象类型回退到 List&lt;T&gt;）</summary>
        private static Type ResolveCollectionConcreteType(Type target)
        {
            if (target.IsArray)
                return target;

            TypeInfo ti = target.GetTypeInfo();
            if (!ti.IsInterface && !ti.IsAbstract)
                return target;

            Type? elementType = TypeMetadata.GetCollectionElementType(target);
            if (elementType is null || elementType == typeof(object))
                return typeof(List<object>);

            return typeof(List<>).MakeGenericType(elementType);
        }
    }
}
