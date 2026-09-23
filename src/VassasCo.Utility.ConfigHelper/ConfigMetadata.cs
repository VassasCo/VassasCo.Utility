using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>非侵入式 Fluent 映射的单个属性配置器。</summary>
    public sealed class PropertyConfigurator
    {
        internal string? _alias;
        internal string? _jsonAlias;
        internal string? _description;
        internal object? _defaultValue;
        internal bool _hasDefault;
        internal bool _ignore;
        internal bool _hasIgnore;
        internal bool _required;
        internal bool _hasRequired;
        internal int? _stringLength;
        internal Type? _converterType;
        internal object?[]? _converterArgs;
        internal Type? _mapType;

        public PropertyConfigurator Alias(string value) { _alias = value; return this; }
        public PropertyConfigurator JsonAlias(string value) { _jsonAlias = value; return this; }
        public PropertyConfigurator Description(string value) { _description = value; return this; }
        public PropertyConfigurator Default(object value) { _defaultValue = value; _hasDefault = true; return this; }
        public PropertyConfigurator Ignore(bool ignore = true) { _ignore = ignore; _hasIgnore = true; return this; }
        public PropertyConfigurator Required(bool required = true) { _required = required; _hasRequired = true; return this; }
        public PropertyConfigurator StringLength(int max) { _stringLength = max; return this; }
        public PropertyConfigurator Converter(Type type, params object?[] args) { _converterType = type; _converterArgs = args; return this; }
        public PropertyConfigurator MapType(Type type) { _mapType = type; return this; }

        internal void ApplyTo(ConfigSpec spec)
        {
            if (_alias != null) spec.Alias = _alias;
            if (_jsonAlias != null) spec.JsonAlias = _jsonAlias;
            if (_description != null) spec.Description = _description;
            if (_hasDefault) { spec.DefaultValue = _defaultValue; spec.HasDefault = true; }
            if (_hasIgnore) spec.Ignore = _ignore;
            if (_hasRequired) spec.Required = _required;
            if (_stringLength.HasValue) spec.StringLength = _stringLength;
            if (_converterType != null) spec.ConverterType = _converterType;
            if (_converterArgs != null) spec.ConverterArgs = _converterArgs;
            if (_mapType != null) spec.MapType = _mapType;
        }
    }

    /// <summary>属性的有效配置（特性 + 兼容特性 + Fluent 合并后的结果）。</summary>
    internal sealed class ConfigSpec
    {
        public string? Alias;
        public string? JsonAlias;
        public string? Description;
        public object? DefaultValue;
        public bool HasDefault;
        public bool Ignore;
        public bool Required;
        public int? StringLength;
        public Type? ConverterType;
        public object?[]? ConverterArgs;
        public Type? MapType;
    }

    /// <summary>类型判定与名称工具。</summary>
    internal static class ConfigTypeUtil
    {
        public static bool IsCollectionType(Type t, out bool isDict, out Type? itemType)
        {
            isDict = false;
            itemType = null;
            if (t == typeof(string)) return false;

            if (typeof(IDictionary).IsAssignableFrom(t))
            {
                isDict = true;
                return true;
            }

            if (t.IsArray)
            {
                itemType = t.GetElementType();
                return true;
            }

            if (t.IsGenericType)
            {
                var ie = GetEnumerableInterface(t);
                if (ie != null)
                {
                    itemType = ie.GetGenericArguments()[0];
                    return true;
                }
            }

            if (typeof(IEnumerable).IsAssignableFrom(t))
            {
                itemType = typeof(object);
                return true;
            }

            return false;
        }

        public static Type? GetElementType(Type t)
        {
            if (t == typeof(string)) return null;
            if (t.IsArray) return t.GetElementType();
            if (t.IsGenericType)
            {
                var ie = GetEnumerableInterface(t);
                if (ie != null) return ie.GetGenericArguments()[0];
            }
            if (typeof(IEnumerable).IsAssignableFrom(t)) return typeof(object);
            return null;
        }

        public static bool TryGetDictionaryTypes(Type t, out Type keyType, out Type valueType)
        {
            keyType = typeof(string);
            valueType = typeof(object);

            Type? dictIface = null;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                dictIface = t;
            else
                dictIface = t.GetInterfaces().FirstOrDefault(
                    i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

            if (dictIface != null)
            {
                var args = dictIface.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }

            return false;
        }

        public static object ConvertList(IList list, Type collectionType, Type itemType)
        {
            if (collectionType.IsArray)
            {
                var arr = Array.CreateInstance(itemType, list.Count);
                list.CopyTo(arr, 0);
                return arr;
            }

            if (collectionType.IsInterface || collectionType.IsAbstract)
                return list;

            try
            {
                var result = Activator.CreateInstance(collectionType);
                if (result is IList rl)
                {
                    foreach (var it in list) rl.Add(it);
                    return rl;
                }
                var add = collectionType.GetMethod("Add", new[] { itemType });
                if (add != null && result != null)
                {
                    foreach (var it in list) add.Invoke(result, new[] { it });
                    return result;
                }
                return list;
            }
            catch { return list; }
        }

        public static string FriendlyTypeName(Type t)
        {
            if (t == typeof(string)) return "string";
            if (t == typeof(int)) return "int";
            if (t == typeof(long)) return "long";
            if (t == typeof(short)) return "short";
            if (t == typeof(byte)) return "byte";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(double)) return "double";
            if (t == typeof(float)) return "float";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(char)) return "char";
            if (t == typeof(DateTime)) return "DateTime";
            if (t == typeof(Guid)) return "Guid";
            if (t == typeof(TimeSpan)) return "TimeSpan";
            if (Nullable.GetUnderlyingType(t) is { } u) return FriendlyTypeName(u);
            return t.Name;
        }

        /// <summary>生成 present 集合的键：类型全名 + 属性名，避免不同层级的同名属性相互误判。</summary>
        public static string PresentKey(Type type, string propertyName) =>
            (type.FullName ?? type.Name) + "::" + propertyName;

        private static Type? GetEnumerableInterface(Type t)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return t;
            foreach (var iface in t.GetInterfaces())
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return iface;
            return null;
        }
    }

    /// <summary>
    /// 元数据提供者：从特性（含兼容特性）与 Fluent 规则中解析出统一的 <see cref="PropMeta"/> 列表。
    /// 读/写共用同一份元数据，从架构上保证读写一致。
    /// </summary>
    internal sealed class ConfigMetadataProvider
    {
        private readonly Type _topType;
        private readonly JsonNamingPolicy? _namingPolicy;
        private readonly IReadOnlyDictionary<string, PropertyConfigurator>? _fluentRules;
        private readonly ConcurrentDictionary<Type, List<PropMeta>> _nestedCache = new();
        private List<PropMeta> _topLevelMetas = new();

        public ConfigMetadataProvider(
            Type topType,
            JsonNamingPolicy? namingPolicy,
            IReadOnlyDictionary<string, PropertyConfigurator>? fluentRules)
        {
            _topType = topType;
            _namingPolicy = namingPolicy;
            _fluentRules = fluentRules;
            _topLevelMetas = Build(topType, applyFluent: true);
        }

        public List<PropMeta> TopLevelMetas => _topLevelMetas;

        public List<PropMeta> GetMetas(Type type)
        {
            if (type == _topType) return _topLevelMetas;
            return _nestedCache.GetOrAdd(type, t => Build(t, applyFluent: false));
        }

        private List<PropMeta> Build(Type type, bool applyFluent)
        {
            var list = new List<PropMeta>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (IsFrameworkIgnored(prop)) continue;

                var cfg = prop.GetCustomAttribute<ConfigAttribute>();
                var spec = BuildSpec(prop, cfg, applyFluent);

                if (spec.Ignore) continue;
                if (cfg != null) cfg.Validate(prop, type.Name);

                var meta = new PropMeta
                {
                    Property = prop,
                    CanWrite = prop.CanWrite,
                    HasConfigDefault = spec.HasDefault,
                    ConfigDefaultValue = spec.DefaultValue,
                    IsRequired = spec.Required,
                    MaxStringLength = spec.StringLength,
                    Description = spec.Description,
                    ConverterType = spec.ConverterType,
                    ConverterArgs = spec.ConverterArgs,
                    MapType = spec.MapType,
                };

                bool isCollection = ConfigTypeUtil.IsCollectionType(prop.PropertyType, out bool isDict, out Type? itemType);
                meta.IsCollection = isCollection;
                meta.IsDictionary = isDict;
                meta.ItemType = itemType;

                var alias = spec.Alias;
                var jsonAlias = spec.JsonAlias;
                if (isCollection)
                {
                    // 集合：Alias 作用于 XML 项名；JsonAlias 作用于 JSON 键名；容器名始终为属性名。
                    meta.JsonKey = string.IsNullOrEmpty(jsonAlias)
                        ? _namingPolicy?.ConvertName(prop.Name) ?? prop.Name
                        : jsonAlias!;
                    meta.XmlElementName = prop.Name;
                    meta.XmlItemName = alias ?? (itemType != null ? ConfigTypeUtil.FriendlyTypeName(itemType) : "Item");
                }
                else
                {
                    // 标量：JsonAlias 优先；否则回退到 Alias；否则用属性名。
                    meta.JsonKey = string.IsNullOrEmpty(jsonAlias)
                        ? alias ?? _namingPolicy?.ConvertName(prop.Name) ?? prop.Name
                        : jsonAlias!;
                    meta.XmlElementName = alias ?? prop.Name;
                    meta.XmlItemName = meta.XmlElementName;
                }

                list.Add(meta);
            }
            return list;
        }

        private ConfigSpec BuildSpec(PropertyInfo prop, ConfigAttribute? cfg, bool applyFluent)
        {
            var spec = new ConfigSpec();

            // 兼容旧特性
            if (prop.GetCustomAttribute<ConfigIgnoreAttribute>() != null) spec.Ignore = true;
            if (prop.GetCustomAttribute<ConfigKeyAttribute>() is { } ck) spec.Alias = ck.Key;
            if (prop.GetCustomAttribute<ConfigDefaultAttribute>() is { } cd) { spec.DefaultValue = cd.Value; spec.HasDefault = true; }
            if (prop.GetCustomAttribute<ConfigRequiredAttribute>() != null) spec.Required = true;
            if (prop.GetCustomAttribute<ConfigStringLengthAttribute>() is { } sl) spec.StringLength = sl.MaxLength;
            if (prop.GetCustomAttribute<ConfigConverterAttribute>() is { } cc) spec.ConverterType = cc.ConverterType;

            // 统一特性（优先于兼容特性）
            if (cfg != null)
            {
                if (cfg.Ignore) spec.Ignore = true;
                var alias = cfg.EffectiveAlias;
                if (!string.IsNullOrEmpty(alias)) spec.Alias = alias;
                if (!string.IsNullOrEmpty(cfg.JsonAlias)) spec.JsonAlias = cfg.JsonAlias;
                if (cfg.MapType != null) spec.MapType = cfg.MapType;
                if (cfg.Default != null) { spec.DefaultValue = cfg.Default; spec.HasDefault = true; }
                if (cfg.Required) spec.Required = true;
                if (cfg.StringLength > 0) spec.StringLength = cfg.StringLength;
                if (cfg.Converter != null) spec.ConverterType = cfg.Converter;
                if (cfg.ConverterArgs != null) spec.ConverterArgs = cfg.ConverterArgs;
                if (!string.IsNullOrEmpty(cfg.Desc)) spec.Description = cfg.Desc;
            }

            if (string.IsNullOrEmpty(spec.Description))
                spec.Description = prop.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;

            // Fluent 覆盖（最高优先级）
            if (applyFluent && _fluentRules != null && _fluentRules.TryGetValue(prop.Name, out var fc))
                fc.ApplyTo(spec);

            return spec;
        }

        private static bool IsFrameworkIgnored(PropertyInfo prop)
        {
            return prop.GetCustomAttribute<JsonIgnoreAttribute>() != null
                || prop.GetCustomAttribute<XmlIgnoreAttribute>() != null;
        }
    }
}
