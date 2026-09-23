// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// 值服务：按计划解析属性值（PropertyChain + 编译 getter，零 Split 零查找反射），
    /// 应用转换器，并统一处理文本/数字/日期/布尔/枚举/二进制/字典等安全转换。
    /// </summary>
    internal static class ValueServices
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        #region Resolution

        /// <summary>从根行对象解析叶子原始值（循环引用标记节点直接返回 null）</summary>
        public static object? Resolve(object? root, ColumnPlan leaf)
        {
            if (root is null || leaf.IsCycleMarker)
                return null;

            var chain = leaf.PropertyChain;
            object? current = root;

            for (int i = 0; i < chain.Length; i++)
            {
                if (current is null)
                    return null;
                var prop = chain[i];
                // 类型可能是子类（多态）：链上的 PropertyInfo 来自声明类型，值对象可能不同；
                // 编译 getter 直接按声明类型转换。当前对象类型兼容即可。
                current = TypeMetadata.TryGetValue(prop, current);
            }

            return current;
        }

        /// <summary>应用叶子转换器（无转换器原样返回）</summary>
        public static object? ApplyConverter(object? value, ColumnPlan leaf)
        {
            return leaf.Converter is null ? value : leaf.Converter(value!);
        }

        #endregion

        #region Routing

        /// <summary>该值最终是否按文本写入单元格</summary>
        public static bool IsTextValue(object? value, ColumnPlan leaf, ExcelExportOptions options)
        {
            if (leaf.ForceText)
                return true;

            if (value is null || value is string || value is char)
                return true;

            if (value is bool || value is byte[] || value.GetType().IsEnum)
                return true;

            if (TypeMetadata.IsDictionary(value.GetType()))
                return true;

            if (options.AutoForceText != ForceTextAutoMode.None && IsLargeIntegerValue(value))
                return true;

            return false;
        }

        /// <summary>写入时值层面检测：实际整数达到 12 位十进制及以上（兜住 decimal/object 场景）</summary>
        private static bool IsLargeIntegerValue(object value)
        {
            int digits;
            switch (value)
            {
                case long l:
                    digits = Math.Abs(l).ToString(CultureInfo.InvariantCulture).Length;
                    break;
                case ulong ul:
                    digits = ul.ToString(CultureInfo.InvariantCulture).Length;
                    break;
                case decimal d:
                    // 只统计整数位
                    long truncated = (long)Math.Truncate(d);
                    digits = Math.Abs(truncated).ToString(CultureInfo.InvariantCulture).Length;
                    break;
                default:
                    return false;
            }

            return digits >= 12;
        }

        #endregion

        #region Text Formatting

        /// <summary>将值格式化为最终文本（布尔/枚举/二进制/字典/日期/数字），含超长保护</summary>
        public static string ToText(object? value, ColumnPlan leaf, ExcelStyle style, ExcelExportOptions options)
        {
            if (leaf.IsCycleMarker)
                return "(循环引用)";

            string text = FormatCore(value, leaf, style, options);
            return GuardLength(SanitizeXmlText(text), options);
        }

        private static string FormatCore(object? value, ColumnPlan leaf, ExcelStyle style, ExcelExportOptions options)
        {
            if (value is null)
                return "";

            Type t = value.GetType();
            Type underlying = Nullable.GetUnderlyingType(t) ?? t;

            if (underlying == typeof(string))
                return (string)value;

            if (underlying == typeof(char))
                return ((char)value).ToString();

            if (underlying == typeof(bool))
                return (bool)value ? style.TrueText : style.FalseText;

            if (underlying.IsEnum)
                return FormatEnum(value, underlying, style);

            if (value is byte[] bytes)
                return FormatBytes(bytes, options);

            if (TypeMetadata.IsDictionary(t))
                return SafeJson(value);

            if (value is DateTime dt)
                return dt.ToString(leaf.Format ?? style.DateTimeFormat ?? "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            if (value is DateTimeOffset dto)
                return dto.ToString(leaf.Format ?? style.DateTimeFormat ?? "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            if (value is IFormattable formattable)
            {
                string? fmt = leaf.Format ?? style.NumberFormat;
                return formattable.ToString(fmt, CultureInfo.InvariantCulture);
            }

            return value.ToString() ?? "";
        }

        private static string FormatEnum(object value, Type enumType, ExcelStyle style)
        {
            if (!style.UseEnumDescription)
                return value.ToString() ?? "";

            var member = enumType.GetMember(value.ToString()!).FirstOrDefault();
            if (member != null)
            {
                // [Description]
                var descAttr = member.GetCustomAttributes()
                    .FirstOrDefault(a => a.GetType().FullName == "System.ComponentModel.DescriptionAttribute");
                if (descAttr != null)
                {
                    var v = descAttr.GetType().GetProperty("Description")?.GetValue(descAttr) as string;
                    if (!string.IsNullOrEmpty(v)) return v!;
                }

                // [Display(Name=)]
                var displayAttr = member.GetCustomAttributes()
                    .FirstOrDefault(a => a.GetType().FullName == "System.ComponentModel.DataAnnotations.DisplayAttribute");
                if (displayAttr != null)
                {
                    var v = displayAttr.GetType().GetProperty("Name")?.GetValue(displayAttr) as string;
                    if (!string.IsNullOrEmpty(v)) return v!;
                }
            }

            return value.ToString() ?? "";
        }

        private static string FormatBytes(byte[] bytes, ExcelExportOptions options)
        {
            return options.BinaryRender switch
            {
                BinaryRenderMode.Empty => "",
                BinaryRenderMode.Json => "[" + string.Join(",", bytes) + "]",
                _ => Convert.ToBase64String(bytes)
            };
        }

        /// <summary>JSON 序列化（字典/复杂兜底），异常时回退 ToString</summary>
        public static string SafeJson(object value)
        {
            try
            {
                return JsonSerializer.Serialize(value, JsonOptions);
            }
            catch
            {
                return value.ToString() ?? "";
            }
        }

        /// <summary>将集合值序列化为单元格 JSON 文本（含超长截断保护），null 返回空字符串</summary>
        public static string ToJsonText(object? value, ExcelExportOptions options)
        {
            if (value is null)
                return "";
            return GuardLength(SanitizeXmlText(SafeJson(value)), options);
        }

        /// <summary>移除 XML 1.0 非法控制字符（\x00-\x08、\x0B、\x0C、\x0E-\x1F），防止 Office/WPS 无法打开文件</summary>
        public static string SanitizeXmlText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            bool dirty = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (IsInvalidXmlChar(text[i]))
                {
                    dirty = true;
                    break;
                }
            }

            if (!dirty)
                return text;

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (!IsInvalidXmlChar(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsInvalidXmlChar(char c)
        {
            return (c >= '\x0000' && c <= '\x0008')
                || c == '\x000B'
                || c == '\x000C'
                || (c >= '\x000E' && c <= '\x001F');
        }

        private static string GuardLength(string text, ExcelExportOptions options)
        {
            if (text.Length <= ExcelLimits.MaxCellTextLength)
                return text;

            if (!options.TruncateLongText)
            {
                throw new ExcelMappingException(
                    $"单元格文本长度 {text.Length} 超过 Excel 上限 {ExcelLimits.MaxCellTextLength} 字符；可开启 TruncateLongText 自动截断。");
            }

            return text.Substring(0, ExcelLimits.MaxCellTextLength);
        }

        #endregion

        #region Numeric / Date Helpers

        /// <summary>转 double（数字路径）；decimal/所有基元均可（char/bool 已被路由到文本，修复旧版 bool/char 变数字的 Bug）</summary>
        public static double ToDouble(object value)
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        /// <summary>尝试取 DateTime（含 DateTimeOffset）</summary>
        public static bool TryGetDateTime(object? value, out DateTime dateTime)
        {
            switch (value)
            {
                case DateTime dt:
                    dateTime = dt;
                    return true;
                case DateTimeOffset dto:
                    dateTime = dto.DateTime;
                    return true;
                default:
                    dateTime = default;
                    return false;
            }
        }

        /// <summary>取身份显示值（子 Sheet 父关联列）</summary>
        public static string GetIdentityString(object? row, Type rowType, int rowIndex, ExcelStyle style)
        {
            if (row is null)
                return $"Row{rowIndex + 1}";

            var idProp = TypeMetadata.FindIdentityProperty(rowType, style.ChildSheetParentProperty);
            if (idProp != null)
            {
                var val = TypeMetadata.TryGetValue(idProp, row);
                if (val != null)
                    return Convert.ToString(val, CultureInfo.InvariantCulture) ?? $"Row{rowIndex + 1}";
            }

            return $"Row{rowIndex + 1}";
        }

        /// <summary>获取子 Sheet 父关联列标题</summary>
        public static string GetParentColumnHeader(Type parentType, ExcelStyle style)
        {
            var prop = TypeMetadata.FindIdentityProperty(parentType, style.ChildSheetParentProperty);
            if (prop != null)
            {
                var display = prop.GetCustomAttribute<ExcelDisplayAttribute>();
                if (display != null && !string.IsNullOrEmpty(display.Name))
                    return display.Name;

                var column = prop.GetCustomAttribute<ExcelColumnAttribute>();
                if (column != null && !string.IsNullOrEmpty(column.Name))
                    return column.Name!;

                return prop.Name;
            }

            return style.ChildSheetParentProperty ?? "Id";
        }

        #endregion
    }
}
