using System;
using System.Collections;
using System.Reflection;

namespace VassasCo.Utility
{
    /// <summary>实体类扩展方法（内部使用）</summary>
    internal static class ConfigExtensions
    {
        /// <summary>确保所有 List 属性不为 null</summary>
        public static T EnsureListsInitialized<T>(this T instance) where T : class
        {
            if (instance == null) return instance!;
            foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (IsPropertyIgnored(prop)) continue;
                var value = prop.GetValue(instance);
                if (value != null) continue;
                var pt = prop.PropertyType;
                if (pt.IsGenericType && typeof(IList).IsAssignableFrom(pt))
                {
                    try { prop.SetValue(instance, Activator.CreateInstance(pt)); }
                    catch { }
                }
            }
            return instance;
        }

        /// <summary>确保所有值类型/string 属性有非 null 的默认值</summary>
        public static T EnsureDefaultsForNulls<T>(this T instance) where T : class
        {
            if (instance == null) return instance!;
            foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (IsPropertyIgnored(prop)) continue;
                var value = prop.GetValue(instance);
                if (value != null) continue;
                var pt = prop.PropertyType;
                if (pt.IsValueType) prop.SetValue(instance, Activator.CreateInstance(pt));
                else if (pt == typeof(string)) prop.SetValue(instance, string.Empty);
            }
            return instance;
        }

        private static bool IsPropertyIgnored(PropertyInfo prop)
        {
            var cfg = prop.GetCustomAttribute<ConfigAttribute>();
            return cfg?.Ignore == true || (cfg == null && prop.GetCustomAttribute<ConfigIgnoreAttribute>() != null);
        }
    }
}
