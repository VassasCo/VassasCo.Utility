// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using VassasCo.Utility.Internals;

namespace VassasCo.Utility
{
    /// <summary>内部访问接口（显式实现，对外不可见）：供 PlanBuilder 读取 Fluent 配置</summary>
    internal interface IFluentMap
    {
        /// <summary>按完整路径查找属性配置</summary>
        FluentPropertyConfig? FindProperty(string path);

        /// <summary>获取行级规则</summary>
        IReadOnlyList<FluentRowRule> GetRowRules();
    }

    /// <summary>
    /// 非侵入式实体映射配置：不修改业务类（不贴特性），在外部用 Fluent 方式声明
    /// 列名/忽略/顺序/格式/强制文本/超链接/转换/条件规则/行级规则。
    /// </summary>
    /// <typeparam name="T">行数据类型</typeparam>
    public sealed class EntityMap<T> : IFluentMap
    {
        internal Dictionary<string, FluentPropertyConfig> Properties { get; } =
            new Dictionary<string, FluentPropertyConfig>(StringComparer.Ordinal);

        internal List<FluentRowRule> RowRules { get; } = new List<FluentRowRule>();

        /// <summary>显式实现内部接口</summary>
        FluentPropertyConfig? IFluentMap.FindProperty(string path)
        {
            return Properties.TryGetValue(path, out var config) ? config : null;
        }

        /// <summary>显式实现内部接口</summary>
        IReadOnlyList<FluentRowRule> IFluentMap.GetRowRules() => RowRules;

        /// <summary>配置一个属性（支持嵌套，如 x =&gt; x.Customer.Name）</summary>
        public PropertyMapping<T, TProperty> Property<TProperty>(Expression<Func<T, TProperty>> selector)
        {
            string path = ExtractPath(selector);
            return new PropertyMapping<T, TProperty>(this, path);
        }

        /// <summary>忽略一个属性（不导出/不导入）</summary>
        public EntityMap<T> Ignore<TProperty>(Expression<Func<T, TProperty>> selector)
        {
            GetConfig(ExtractPath(selector)).Ignore = true;
            return this;
        }

        /// <summary>
        /// 行级条件规则：任意谓词命中时对整行应用样式（仅服务端求值，写为静态样式）。
        /// </summary>
        public EntityMap<T> WhenRow(Func<T, bool> predicate, Action<ConditionalStyleBuilder> style)
        {
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (style is null) throw new ArgumentNullException(nameof(style));

            var builder = new ConditionalStyleBuilder();
            style(builder);
            RowRules.Add(new FluentRowRule
            {
                Predicate = o => predicate((T)o),
                FontColor = builder.FontColor,
                FillColor = builder.FillColor,
                Bold = builder.IsBold
            });
            return this;
        }

        internal FluentPropertyConfig GetConfig(string path)
        {
            if (!Properties.TryGetValue(path, out var config))
            {
                config = new FluentPropertyConfig();
                Properties[path] = config;
            }
            return config;
        }

        internal static string ExtractPath<TProperty>(Expression<Func<T, TProperty>> selector)
        {
            if (selector?.Body is not MemberExpression member)
                throw new ArgumentException("映射表达式必须是属性访问表达式，如 x => x.Name 或 x => x.Customer.Name。", nameof(selector));

            var parts = new List<string>();
            Expression current = member;
            while (current is MemberExpression me && me.Member is PropertyInfo)
            {
                parts.Add(me.Member.Name);
                current = me.Expression!;
            }

            if (parts.Count == 0)
                throw new ArgumentException("映射表达式必须指向属性。", nameof(selector));

            parts.Reverse();
            return string.Join(".", parts);
        }
    }

    /// <summary>单个属性的 Fluent 配置链</summary>
    public sealed class PropertyMapping<T, TProperty>
    {
        private readonly EntityMap<T> _map;
        private readonly string _path;
        private FluentPropertyConfig Config => _map.GetConfig(_path);

        internal PropertyMapping(EntityMap<T> map, string path)
        {
            _map = map;
            _path = path;
        }

        /// <summary>设置列标题</summary>
        public PropertyMapping<T, TProperty> HasName(string name)
        {
            Config.DisplayName = name;
            return this;
        }

        /// <summary>设置列顺序（升序）</summary>
        public PropertyMapping<T, TProperty> Order(int order)
        {
            Config.Order = order;
            return this;
        }

        /// <summary>强制文本写入（身份证、手机号、长 ID 等）</summary>
        public PropertyMapping<T, TProperty> AsText()
        {
            Config.ForceText = true;
            return this;
        }

        /// <summary>数字/日期格式，如 "N2"、"yyyy-MM-dd"</summary>
        public PropertyMapping<T, TProperty> Format(string format)
        {
            Config.Format = format;
            return this;
        }

        /// <summary>固定列宽</summary>
        public PropertyMapping<T, TProperty> Width(double width)
        {
            Config.Width = width;
            return this;
        }

        /// <summary>自动换行</summary>
        public PropertyMapping<T, TProperty> WrapText()
        {
            Config.WrapText = true;
            return this;
        }

        /// <summary>作为超链接写入</summary>
        public PropertyMapping<T, TProperty> Hyperlink()
        {
            Config.AsHyperlink = true;
            return this;
        }

        /// <summary>忽略该属性</summary>
        public PropertyMapping<T, TProperty> Ignore()
        {
            Config.Ignore = true;
            return this;
        }

        /// <summary>自定义值转换</summary>
        public PropertyMapping<T, TProperty> ConvertUsing(Func<TProperty, object?> converter)
        {
            if (converter is null) throw new ArgumentNullException(nameof(converter));
            Config.Converter = o => converter(o is TProperty v ? v : default!);
            return this;
        }

        /// <summary>追加条件格式规则，如 Rule(ConditionOperator.GreaterThan, 60, s => s.FontColor("#FF0000"))</summary>
        public PropertyMapping<T, TProperty> Rule(ConditionOperator op, double value, Action<ConditionalStyleBuilder> style)
        {
            var builder = new ConditionalStyleBuilder();
            style(builder);
            Config.Rules.Add(new FluentRule
            {
                Operator = op,
                Value = value,
                FontColor = builder.FontColor,
                FillColor = builder.FillColor,
                Bold = builder.IsBold
            });
            return this;
        }
    }

    /// <summary>条件格式命中后的样式构造器</summary>
    public sealed class ConditionalStyleBuilder
    {
        internal string? FontColor;
        internal string? FillColor;
        internal bool IsBold;

        /// <summary>字体颜色（十六进制，如 "#FF0000"）</summary>
        public ConditionalStyleBuilder Font(string color)
        {
            FontColor = color;
            return this;
        }

        /// <summary>背景色（十六进制）</summary>
        public ConditionalStyleBuilder Fill(string color)
        {
            FillColor = color;
            return this;
        }

        /// <summary>加粗</summary>
        public ConditionalStyleBuilder Bold()
        {
            IsBold = true;
            return this;
        }
    }

    #region Internal Fluent Config DTOs

    internal sealed class FluentPropertyConfig
    {
        public string? DisplayName;
        public int? Order;
        public bool Ignore;
        public bool ForceText;
        public string? Format;
        public double? Width;
        public bool WrapText;
        public bool AsHyperlink;
        public Func<object, object?>? Converter;
        public List<FluentRule> Rules = new List<FluentRule>();
    }

    internal sealed class FluentRule
    {
        public ConditionOperator Operator;
        public double Value;
        public double Value2;
        public string? FontColor;
        public string? FillColor;
        public bool Bold;
    }

    internal sealed class FluentRowRule
    {
        public Func<object, bool> Predicate = null!;
        public string? FontColor;
        public string? FillColor;
        public bool Bold;
    }

    #endregion
}
