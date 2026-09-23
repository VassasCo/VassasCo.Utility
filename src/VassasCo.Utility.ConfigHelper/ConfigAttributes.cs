using System;
using System.Collections;
using System.Reflection;

namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>配置文件格式。</summary>
    public enum ConfigFormat
    {
        None = 0,
        Json = 1,
        Xml = 2
    }

    /// <summary>配置变更类型。</summary>
    public enum ConfigChangeType
    {
        /// <summary>首次加载。</summary>
        Initial,
        /// <summary>主动保存。</summary>
        Saved,
        /// <summary>热重载（文件被外部修改）。</summary>
        HotReload,
        /// <summary>手动重载。</summary>
        Reloaded
    }

    /// <summary>配置值转换器：实体值 ⇄ 配置字符串双向转换。</summary>
    public interface IConfigConverter
    {
        /// <summary>实体值 → 配置字符串。</summary>
        string ConvertTo(object? value);

        /// <summary>配置字符串 → 实体值。</summary>
        object? ConvertFrom(string? configValue);
    }

    /// <summary>配置校验失败时抛出的异常。</summary>
    public class ConfigValidationException : Exception
    {
        public ConfigValidationException(string message) : base(message) { }
        public ConfigValidationException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>单个属性的解析后元数据（读/写共用，保证读写一致）。</summary>
    public sealed class PropMeta
    {
        public PropertyInfo Property { get; set; } = null!;

        /// <summary>JSON 键名（已解析别名/命名策略）。</summary>
        public string JsonKey { get; set; } = null!;

        /// <summary>XML 元素名（标量属性自身元素名；集合属性为容器元素名）。</summary>
        public string XmlElementName { get; set; } = null!;

        /// <summary>XML 集合项元素名（仅集合属性有效）。</summary>
        public string XmlItemName { get; set; } = null!;

        /// <summary>是否为集合（数组 / List / 字典）。</summary>
        public bool IsCollection { get; set; }

        /// <summary>是否为字典。</summary>
        public bool IsDictionary { get; set; }

        /// <summary>集合元素类型。</summary>
        public Type? ItemType { get; set; }

        public bool HasConfigDefault { get; set; }
        public object? ConfigDefaultValue { get; set; }
        public bool IsRequired { get; set; }
        public int? MaxStringLength { get; set; }
        public string? Description { get; set; }
        public Type? ConverterType { get; set; }
        public object?[]? ConverterArgs { get; set; }

        /// <summary>枚举属性在文件中的映射类型（null/string → 枚举名，整数类型 → 数字）。</summary>
        public Type? MapType { get; set; }

        public bool CanWrite { get; set; }

        private IConfigConverter? _converter;

        internal IConfigConverter? GetConverter()
        {
            if (ConverterType == null) return null;
            return _converter ??= CreateConverter();
        }

        private IConfigConverter CreateConverter()
        {
            try
            {
                if (ConverterArgs != null && ConverterArgs.Length > 0)
                    return (IConfigConverter)Activator.CreateInstance(ConverterType!, ConverterArgs)!;
                return (IConfigConverter)Activator.CreateInstance(ConverterType!)!;
            }
            catch (Exception ex)
            {
                throw new ConfigValidationException(
                    $"无法创建转换器 '{ConverterType!.FullName}'，请检查 Converter 类型及 ConverterArgs 参数。", ex);
            }
        }
    }

    /// <summary>标记配置类使用 JSON 格式，并指定文件路径。</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class JsonConfigAttribute : Attribute
    {
        public string FilePath { get; }

        public JsonConfigAttribute(string filePath)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }
    }

    /// <summary>标记配置类使用 XML 格式，并指定文件路径。</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class XmlConfigAttribute : Attribute
    {
        public string FilePath { get; }
        public string? RootName { get; set; }

        public XmlConfigAttribute(string filePath)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }
    }

    /// <summary>
    /// 统一配置属性特性。
    /// 用法：[Config(Desc = "描述", Alias = "别名", Default = ...)]
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ConfigAttribute : Attribute
    {
        /// <summary>
        /// 属性在文件中的名称（别名）。
        /// - 普通属性：JSON 键名 / XML 元素名（按字面量使用）。
        /// - 集合属性：XML 列表项元素名（默认 = 元素类型名，如 string/int/ServerInfo）。
        /// 未设置时普通属性默认用属性名（JSON 下转为 camelCase）。
        /// </summary>
        public string? Alias { get; set; }

        /// <summary>
        /// JSON 专用键名（优先级高于 <see cref="Alias"/>），仅作用于 JSON 键名，不影响 XML。
        /// - 普通属性：覆盖 JSON 键名（XML 仍用 <see cref="Alias"/> 或属性名）。
        /// - 集合属性：覆盖 JSON 键名（<see cref="Alias"/> 仍作用于 XML 列表项元素名）。
        /// 未设置时回退到 <see cref="Alias"/>（普通属性）或属性名转 camelCase。
        /// </summary>
        public string? JsonAlias { get; set; }

        /// <summary>
        /// 枚举属性在文件中的映射类型（仿 FreeSql 的 MapType）。
        /// - null 或 typeof(string)：以枚举名（字符串）存储（默认）。
        /// - typeof(int) / typeof(byte) / typeof(long) 等整数类型：以对应数字存储。
        /// 仅对枚举属性生效。
        /// </summary>
        public Type? MapType { get; set; }

        /// <summary>兼容旧版：等价于 <see cref="Alias"/>，请改用 Alias。</summary>
        [Obsolete("请使用 Alias 代替 Key。")]
        public string? Key { get; set; }

        public string? Desc { get; set; }
        public object? Default { get; set; }
        public bool Ignore { get; set; }
        public bool Required { get; set; }
        public int StringLength { get; set; }
        public Type? Converter { get; set; }
        public object?[]? ConverterArgs { get; set; }

        internal string? EffectiveAlias => !string.IsNullOrEmpty(Alias) ? Alias : Key;

        internal void Validate(PropertyInfo prop, string className)
        {
            var propType = prop.PropertyType;
            var propName = $"{className}.{prop.Name}";

            if (Ignore && Required)
                throw new ConfigValidationException($"[Config] 在 '{propName}' 上: Ignore 和 Required 不能同时为 true");

            if (StringLength > 0 && propType != typeof(string))
                throw new ConfigValidationException($"[Config] 在 '{propName}' 上: StringLength 仅适用于 string 类型，当前类型为 {propType.Name}");

            if (Default != null && !CanHaveDefault(propType))
                throw new ConfigValidationException($"[Config] 在 '{propName}' 上: 复杂类型 (class/List/Dictionary) 不能设置 Default 值");

            if (Default != null && CanHaveDefault(propType))
            {
                var defaultValueType = Default.GetType();
                if (!propType.IsAssignableFrom(defaultValueType))
                {
                    try { Default = Convert.ChangeType(Default, propType); }
                    catch
                    {
                        throw new ConfigValidationException($"[Config] 在 '{propName}' 上: Default 值类型不兼容");
                    }
                }
            }

            if (Converter != null && !typeof(IConfigConverter).IsAssignableFrom(Converter))
                throw new ConfigValidationException($"[Config] 在 '{propName}' 上: Converter 类型必须实现 IConfigConverter");

            if (ConverterArgs != null && ConverterArgs.Length > 0 && Converter == null)
                throw new ConfigValidationException($"[Config] 在 '{propName}' 上: ConverterArgs 必须配合 Converter 一起使用");

            if (MapType != null)
            {
                var enumCheckType = Nullable.GetUnderlyingType(propType) ?? propType;
                if (!enumCheckType.IsEnum)
                    throw new ConfigValidationException($"[Config] 在 '{propName}' 上: MapType 仅适用于枚举类型，当前类型为 {propType.Name}");
                if (MapType != typeof(string) && !ConfigValueCodec.IsIntegral(MapType))
                    throw new ConfigValidationException($"[Config] 在 '{propName}' 上: MapType 必须是 string 或整数类型，当前为 {MapType.Name}");
            }
        }

        internal static bool CanHaveDefault(Type type)
        {
            return !(type.IsClass && type != typeof(string));
        }
    }

    // ── 以下为兼容旧版 API 的特性（保留并标记过时）──

    [Obsolete("请使用 ConfigAttribute 的 Alias 属性。")]
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ConfigKeyAttribute : Attribute
    {
        public string Key { get; }
        public ConfigKeyAttribute(string key) { Key = key; }
    }

    [Obsolete("请使用 ConfigAttribute 的 Default 属性。")]
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ConfigDefaultAttribute : Attribute
    {
        public object? Value { get; }
        public ConfigDefaultAttribute(object? value) { Value = value; }
    }

    [Obsolete("请使用 ConfigAttribute 的 Ignore 属性。")]
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ConfigIgnoreAttribute : Attribute { }

    [Obsolete("请使用 ConfigAttribute 的 Required 属性。")]
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ConfigRequiredAttribute : Attribute { }

    [Obsolete("请使用 ConfigAttribute 的 StringLength 属性。")]
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ConfigStringLengthAttribute : Attribute
    {
        public int MaxLength { get; }
        public ConfigStringLengthAttribute(int maxLength) { MaxLength = maxLength; }
    }

    [Obsolete("请使用 ConfigAttribute 的 Converter 属性。")]
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ConfigConverterAttribute : Attribute
    {
        public Type ConverterType { get; }
        public ConfigConverterAttribute(Type converterType)
        {
            if (!typeof(IConfigConverter).IsAssignableFrom(converterType))
                throw new ArgumentException("转换器必须实现 IConfigConverter", nameof(converterType));
            ConverterType = converterType;
        }
    }

    /// <summary>配置变更事件参数。</summary>
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

    /// <summary>配置保存前事件参数（可取消保存）。</summary>
    public class ConfigSavingEventArgs<T> : EventArgs where T : class
    {
        public T? Config { get; set; }
        public bool Cancel { get; set; }

        public ConfigSavingEventArgs(T? config)
        {
            Config = config;
        }
    }
}
