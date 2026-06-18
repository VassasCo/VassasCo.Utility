using System;
using System.Reflection;

namespace VassasCo.Utility
{
    public enum ConfigFormat { None, Json, Xml }

    public enum ConfigChangeType
    {
        Initial,
        Saved,
        HotReload,
        Reloaded
    }

    /// <summary>配置值转换器接口：实体 ↔ 配置字符串双向转换</summary>
    public interface IConfigConverter
    {
        string ConvertTo(object? value);
        object? ConvertFrom(string? configValue);
    }

    public class ConfigValidationException : Exception
    {
        public ConfigValidationException(string message) : base(message) { }
        public ConfigValidationException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>属性元数据（供序列化器使用）</summary>
    public class PropMeta
    {
        public PropertyInfo Property { get; init; } = null!;
        public string JsonKey { get; init; } = null!;
        public string XmlElementName { get; init; } = null!;
        public bool HasConfigDefault { get; init; }
        public object? ConfigDefaultValue { get; init; }
        public bool IsRequired { get; init; }
        public int? MaxStringLength { get; init; }
        public string? Description { get; init; }
        public Type? ConverterType { get; init; }
        public object?[]? ConverterArgs { get; init; }
        public bool CanWrite { get; init; }

        internal IConfigConverter? GetConverter()
        {
            if (ConverterType == null) return null;
            try
            {
                if (ConverterArgs != null && ConverterArgs.Length > 0)
                    return (IConfigConverter)Activator.CreateInstance(ConverterType, ConverterArgs)!;
                return (IConfigConverter)Activator.CreateInstance(ConverterType)!;
            }
            catch { return null; }
        }
    }

    /// <summary>标记配置类使用 JSON 格式，并指定文件路径</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class JsonConfigAttribute : Attribute
    {
        public string FilePath { get; }
        public bool WriteIndented { get; set; } = true;

        public JsonConfigAttribute(string filePath)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }
    }

    /// <summary>标记配置类使用 XML 格式，并指定文件路径</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class XmlConfigAttribute(string filePath) : Attribute
    {
        public string FilePath { get; } = filePath ?? throw new ArgumentNullException(nameof(filePath));
        public string? RootName { get; set; }
    }

    /// <summary>
    /// 统一配置属性特性，替代 6 个旧分散特性。
    /// 用法：[Config(Desc = "描述", Key = "键名", Default = ...)]
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ConfigAttribute : Attribute
    {
        public string? Desc { get; set; }
        public string? Key { get; set; }
        public object? Default { get; set; }
        public bool Ignore { get; set; }
        public bool Required { get; set; }
        public int StringLength { get; set; }
        public Type? Converter { get; set; }
        public object?[]? ConverterArgs { get; set; }

        internal void Validate(PropertyInfo prop, string className)
        {
            var propType = prop.PropertyType;
            var propName = $"{className}.{prop.Name}";

            if (Ignore && Required)
                throw new ConfigValidationException(
                    $"[Config] 在 '{propName}' 上: Ignore 和 Required 不能同时为 true");

            if (StringLength > 0 && propType != typeof(string))
                throw new ConfigValidationException(
                    $"[Config] 在 '{propName}' 上: StringLength 仅适用于 string 类型，当前类型为 {propType.Name}");

            if (Default != null && !CanHaveDefault(propType))
                throw new ConfigValidationException(
                    $"[Config] 在 '{propName}' 上: 复杂类型 (class/List/Dictionary) 不能设置 Default 值");

            if (Default != null && CanHaveDefault(propType))
            {
                var defaultValueType = Default.GetType();
                if (!propType.IsAssignableFrom(defaultValueType))
                {
                    try { Default = Convert.ChangeType(Default, propType); }
                    catch
                    {
                        throw new ConfigValidationException(
                            $"[Config] 在 '{propName}' 上: Default 值类型不兼容");
                    }
                }
            }

            if (Converter != null && !typeof(IConfigConverter).IsAssignableFrom(Converter))
                throw new ConfigValidationException(
                    $"[Config] 在 '{propName}' 上: Converter 类型必须实现 IConfigConverter");

            if (ConverterArgs != null && ConverterArgs.Length > 0 && Converter == null)
                throw new ConfigValidationException(
                    $"[Config] 在 '{propName}' 上: ConverterArgs 必须配合 Converter 一起使用");
        }

        internal static bool CanHaveDefault(Type type)
        {
            return !(type.IsClass && type != typeof(string));
        }
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ConfigKeyAttribute(string key) : Attribute
    {
        public string Key { get; } = key;
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ConfigDefaultAttribute(object? value) : Attribute
    {
        public object? Value { get; } = value;
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ConfigIgnoreAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ConfigRequiredAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ConfigStringLengthAttribute(int maxLength) : Attribute
    {
        public int MaxLength { get; } = maxLength;
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ConfigConverterAttribute : Attribute
    {
        public Type ConverterType { get; }
        public ConfigConverterAttribute(Type converterType)
        {
            if (!typeof(IConfigConverter).IsAssignableFrom(converterType))
                throw new ArgumentException($"转换器必须实现 IConfigConverter", nameof(converterType));
            ConverterType = converterType;
        }
    }

    /// <summary>配置变更事件参数</summary>
    public class ConfigChangedEventArgs<T> : EventArgs where T : class
    {
        public T? OldConfig { get; }
        public T NewConfig { get; }
        public ConfigChangeType ChangeType { get; }

        public ConfigChangedEventArgs(T? oldConfig, T newConfig, ConfigChangeType changeType)
        {
            OldConfig = oldConfig;
            NewConfig = newConfig;
            ChangeType = changeType;
        }
    }

    /// <summary>配置保存前事件参数（可取消保存）</summary>
    public class ConfigSavingEventArgs<T>(T? config) : EventArgs where T : class
    {
        public T? Config { get; set; } = config;
        public bool Cancel { get; set; }
    }
}
