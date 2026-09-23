using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>
    /// 配置工厂：从类特性自动识别格式，零代码加载/保存配置。
    /// 用法：[JsonConfig("config/app.json")] public class AppConfig { ... }
    /// </summary>
    public static class ConfigFactory
    {
        private static readonly ConcurrentDictionary<Type, object> _helpers = new();

        public static T Load<T>() where T : class, new()
        {
            var type = typeof(T);
            if (_helpers.TryGetValue(type, out var existing) && existing is ConfigHelper<T> h)
                return h.Value;

            var attr = GetConfigAttribute(type);
            var helper = new ConfigHelper<T>(attr.path, attr.format);
            _helpers[type] = helper;
            return helper.Value;
        }

        /// <summary>将给定实例保存到文件（尊重传入的实例，不再被忽略）。</summary>
        public static void Save<T>(T config) where T : class, new()
        {
            if (_helpers.TryGetValue(typeof(T), out var h) && h is ConfigHelper<T> helper)
            {
                helper.SaveCopy(config ?? helper.Value);
                return;
            }

            var attr = GetConfigAttribute(typeof(T));
            var temp = new ConfigHelper<T>(attr.path, attr.format, autoLoad: false);
            temp.SaveCopy(config ?? temp.Load());
        }

        public static ConfigHelper<T>? GetHelper<T>() where T : class, new()
            => _helpers.TryGetValue(typeof(T), out var h) ? h as ConfigHelper<T> : null;

        public static ConfigHelper<T> Register<T>(string filePath, ConfigFormat format = ConfigFormat.Json)
            where T : class, new()
        {
            var helper = new ConfigHelper<T>(filePath, format);
            _helpers[typeof(T)] = helper;
            return helper;
        }

        /// <summary>若配置文件不存在则生成默认文件。</summary>
        public static void Generate<T>() where T : class, new()
        {
            var attr = GetConfigAttribute(typeof(T));
            // 构造函数 autoLoad 默认开启：文件不存在时会自动写入带默认值的配置，无需额外判断。
            _ = new ConfigHelper<T>(attr.path, attr.format);
        }

        private static (string path, ConfigFormat format) GetConfigAttribute(Type type)
        {
            var jsonAttr = type.GetCustomAttribute<JsonConfigAttribute>();
            if (jsonAttr != null) return (jsonAttr.FilePath, ConfigFormat.Json);

            var xmlAttr = type.GetCustomAttribute<XmlConfigAttribute>();
            if (xmlAttr != null) return (xmlAttr.FilePath, ConfigFormat.Xml);

            throw new InvalidOperationException(
                $"类型 '{type.Name}' 未标注 [JsonConfig] 或 [XmlConfig] 特性");
        }
    }
}
