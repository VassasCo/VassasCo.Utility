// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// 反射元数据缓存：属性列表、编译后的属性访问委托（Expression.Compile）、
    /// 显示名约定、身份属性查找。跨多次导出复用，避免热路径反射。
    /// </summary>
    internal static class TypeMetadata
    {
        private const BindingFlags InstancePublic = BindingFlags.Public | BindingFlags.Instance;

        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache =
            new ConcurrentDictionary<Type, PropertyInfo[]>();

        private static readonly ConcurrentDictionary<PropertyInfo, Func<object, object?>> GetterCache =
            new ConcurrentDictionary<PropertyInfo, Func<object, object?>>();

        private static readonly ConcurrentDictionary<(Type Type, string? Preferred), PropertyInfo?> IdentityCache =
            new ConcurrentDictionary<(Type, string?), PropertyInfo?>();

        /// <summary>敏感字段关键词（属性名包含即判定，忽略大小写）</summary>
        private static readonly string[] SensitiveKeywords =
        {
            "手机", "电话", "身份证", "证件", "银行卡", "信用卡", "邮编", "邮政编码", "账号", "帐号", "传真",
            "phone", "mobile", "tel", "fax", "idcard", "identitycard", "bankcard", "cardno",
            "postalcode", "zipcode", "postcode", "account", "accountno", "creditcard"
        };

        /// <summary>以 id 结尾但不是身份属性的常见英文词（修复旧版 Grid/Valid 误判）</summary>
        private static readonly HashSet<string> IdentityFalsePositives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "grid", "valid", "hybrid", "raid", "void", "avid", "fluid"
        };

        #region Type Classification

        /// <summary>是否为可直接写入单元格的简单类型</summary>
        public static bool IsSimpleType(Type type)
        {
            Type t = Nullable.GetUnderlyingType(type) ?? type;

            if (t.IsPrimitive || t.IsEnum)
                return true;

            return t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
                || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(Guid)
                || t == typeof(Uri) || t == typeof(Version) || t == typeof(char);
        }

        /// <summary>是否为字节数组（二进制数据）</summary>
        public static bool IsByteArray(Type type) => type == typeof(byte[]);

        /// <summary>是否为字典（泛型/非泛型 IDictionary）</summary>
        public static bool IsDictionary(Type type)
        {
            if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
                return true;

            return type.GetInterfaces().Any(i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        }

        /// <summary>是否为集合类型（不含 string、byte[]、IDictionary）</summary>
        public static bool IsCollection(Type type)
        {
            if (type == typeof(string) || IsByteArray(type) || IsDictionary(type))
                return false;

            if (type.IsArray)
                return true;

            return type.GetInterfaces().Any(i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        }

        /// <summary>获取集合元素类型（非泛型集合返回 typeof(object)）</summary>
        public static Type? GetCollectionElementType(Type type)
        {
            if (type.IsArray)
                return type.GetElementType();

            if (type.IsGenericType)
            {
                var gtype = type.GetGenericTypeDefinition();
                if (gtype == typeof(IEnumerable<>) || gtype == typeof(List<>) ||
                    gtype == typeof(IList<>) || gtype == typeof(ICollection<>))
                    return type.GetGenericArguments()[0];
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return iface.GetGenericArguments()[0];
            }

            // 非泛型集合（ArrayList 等）：元素类型不可静态确定
            return typeof(System.Collections.IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
        }

        #endregion

        #region Properties & Getters

        /// <summary>获取可导出的公开实例属性（带缓存）</summary>
        public static PropertyInfo[] GetExportableProperties(Type type)
        {
            return PropertyCache.GetOrAdd(type, t =>
                t.GetProperties(InstancePublic)
                 .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                 .ToArray());
        }

        /// <summary>获取（或编译并缓存）属性的零反射快速访问委托</summary>
        public static Func<object, object?> GetGetter(PropertyInfo prop)
        {
            return GetterCache.GetOrAdd(prop, p =>
            {
                // object target => (DeclaringType)target => prop => (object)value
                var targetParam = Expression.Parameter(typeof(object), "target");
                var converted = p.DeclaringType!.IsValueType
                    ? Expression.Unbox(targetParam, p.DeclaringType)
                    : Expression.Convert(targetParam, p.DeclaringType);
                var propertyAccess = Expression.Property(converted, p);
                var boxed = Expression.Convert(propertyAccess, typeof(object));
                return Expression.Lambda<Func<object, object?>>(boxed, targetParam).Compile();
            });
        }

        /// <summary>快速读取属性值（内部仍保留异常保护，但不再吞异常——仅捕获 TargetInvocationException）</summary>
        public static object? TryGetValue(PropertyInfo prop, object obj)
        {
            try
            {
                return GetGetter(prop)(obj);
            }
            catch (TargetInvocationException ex)
            {
                throw new ExcelMappingException(
                    $"读取属性 {prop.DeclaringType?.Name}.{prop.Name} 时抛出异常。", ex.InnerException ?? ex);
            }
        }

        #endregion

        #region Name Conventions

        /// <summary>获取属性的约定显示名：本包特性优先，其次 DataAnnotations，最后属性名</summary>
        public static string GetDisplayName(PropertyInfo prop)
        {
            var displayAttr = prop.GetCustomAttribute<ExcelDisplayAttribute>();
            if (displayAttr != null && !string.IsNullOrEmpty(displayAttr.Name))
                return displayAttr.Name;

            var columnAttr = prop.GetCustomAttribute<ExcelColumnAttribute>();
            if (columnAttr != null && !string.IsNullOrEmpty(columnAttr.Name))
                return columnAttr.Name!;

            // DataAnnotations 约定（通过类型名识别，避免额外程序集引用差异）
            foreach (var attr in prop.GetCustomAttributes())
            {
                string typeName = attr.GetType().FullName ?? string.Empty;
                if (typeName == "System.ComponentModel.DisplayNameAttribute")
                {
                    var v = attr.GetType().GetProperty("DisplayName")?.GetValue(attr) as string;
                    if (!string.IsNullOrEmpty(v)) return v!;
                }
                else if (typeName == "System.ComponentModel.DataAnnotations.DisplayAttribute")
                {
                    var v = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
                    if (!string.IsNullOrEmpty(v)) return v!;
                }
            }

            return prop.Name;
        }

        /// <summary>读取枚举字段的 [Description]/[Display(Name=)] 文本，无则 null</summary>
        public static string? GetEnumDescription(MemberInfo enumField)
        {
            foreach (var attr in enumField.GetCustomAttributes())
            {
                string typeName = attr.GetType().FullName ?? string.Empty;
                if (typeName == "System.ComponentModel.DescriptionAttribute")
                {
                    var v = attr.GetType().GetProperty("Description")?.GetValue(attr) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                else if (typeName == "System.ComponentModel.DataAnnotations.DisplayAttribute")
                {
                    var v = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            return null;
        }

        /// <summary>读取 DataAnnotations DataType 标记（PhoneNumber/CreditCard/PostalCode 等），无则 null</summary>
        public static string? GetDataTypeName(PropertyInfo prop)
        {
            foreach (var attr in prop.GetCustomAttributes())
            {
                if (attr.GetType().FullName == "System.ComponentModel.DataAnnotations.DataTypeAttribute")
                {
                    var v = attr.GetType().GetProperty("DataType")?.GetValue(attr);
                    return v?.ToString();
                }
            }
            return null;
        }

        /// <summary>属性名是否命中敏感字段关键词</summary>
        public static bool IsSensitiveName(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                return false;

            string lower = propertyName.ToLowerInvariant();
            foreach (var kw in SensitiveKeywords)
            {
                if (lower.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        #endregion

        #region Identity

        /// <summary>
        /// 查找身份属性：preferred 指定名 → 精确 "id" → 以 "id" 结尾（修复旧版运算符优先级 Bug，
        /// 并排除 Grid/Valid 等假阳性）。
        /// </summary>
        public static PropertyInfo? FindIdentityProperty(Type type, string? preferred = null)
        {
            return IdentityCache.GetOrAdd((type, preferred), key =>
            {
                var props = GetExportableProperties(key.Type);

                if (!string.IsNullOrEmpty(key.Preferred))
                {
                    var exact = props.FirstOrDefault(p =>
                        string.Equals(p.Name, key.Preferred, StringComparison.Ordinal));
                    if (exact != null)
                        return exact;
                }

                var exactId = props.FirstOrDefault(p => string.Equals(p.Name, "id", StringComparison.OrdinalIgnoreCase));
                if (exactId != null)
                    return exactId;

                return props.FirstOrDefault(p =>
                {
                    string name = p.Name;
                    if (name.Length <= 2 || !name.EndsWith("id", StringComparison.OrdinalIgnoreCase))
                        return false;
                    return !IdentityFalsePositives.Contains(name);
                });
            });
        }

        #endregion
    }
}
