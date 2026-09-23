using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.Json;

namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>非侵入式 Fluent 映射入口。</summary>
    public static class ConfigBuilder
    {
        public static ConfigBuilder<T> For<T>(string filePath, ConfigFormat format = ConfigFormat.Json)
            where T : class, new()
            => new ConfigBuilder<T>(filePath, format);
    }

    /// <summary>非侵入式 Fluent 映射构建器（实体类无需任何特性）。</summary>
    public sealed class ConfigBuilder<T> where T : class, new()
    {
        private readonly string _filePath;
        private readonly ConfigFormat _format;
        private JsonSerializerOptions? _jsonOptions;
        private readonly Dictionary<string, PropertyConfigurator> _rules = new(StringComparer.Ordinal);

        internal ConfigBuilder(string filePath, ConfigFormat format)
        {
            _filePath = filePath;
            _format = format;
        }

        /// <summary>可选 JSON 选项。本库读写走自研解析器，仅 <see cref="JsonSerializerOptions.PropertyNamingPolicy"/> 生效，其余选项会被忽略。</summary>
        public ConfigBuilder<T> UseJsonOptions(JsonSerializerOptions options)
        {
            _jsonOptions = options;
            return this;
        }

        public ConfigBuilder<T> Property<TProp>(Expression<Func<T, TProp>> selector, Action<PropertyConfigurator> configure)
        {
            var name = GetPropertyName(selector);
            var configurator = new PropertyConfigurator();
            configure(configurator);
            _rules[name] = configurator;
            return this;
        }

        public ConfigHelper<T> Build() => new ConfigHelper<T>(_filePath, _format, _jsonOptions, autoLoad: true, _rules);

        private static string GetPropertyName<TProp>(Expression<Func<T, TProp>> selector)
        {
            var body = selector.Body;
            if (body is UnaryExpression un && un.NodeType == ExpressionType.Convert)
                body = un.Operand;
            if (body is MemberExpression me)
                return me.Member.Name;
            throw new ArgumentException("selector 必须是属性访问表达式，例如 x => x.Name");
        }
    }
}
