// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// 计划构建器：合并三种配置来源 ——
    /// 侵入式特性（[ExcelSheet]/[ExcelColumn]/[ExcelIgnore]/[ConditionalFormat]/[ExcelConverter]）、
    /// DataAnnotations 约定、非侵入式 Fluent <see cref="EntityMap{T}"/>，产出 <see cref="ExportDocument"/>。
    /// </summary>
    internal static class PlanBuilder
    {
        /// <summary>为单个数据集构建导出文档</summary>
        public static ExportDocument BuildDocument<T>(
            IEnumerable<T> data,
            string? requestedName,
            EntityMap<T>? map,
            ExcelExportOptions options)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            if (options is null) throw new ArgumentNullException(nameof(options));

            var doc = new ExportDocument();
            doc.Primary.Sheet.RequestedName = requestedName;
            doc.Primary.Sheet.RowType = typeof(T);
            doc.Primary.Sheet.Rows = (System.Collections.IEnumerable)data;

            var ancestors = new HashSet<Type> { typeof(T) };
            doc.Primary.Sheet.Columns = BuildTree(typeof(T), map, "", ancestors, options, depth: 0);

            if (map != null)
            {
                foreach (var rowRule in ((IFluentMap)map).GetRowRules())
                {
                    doc.Primary.RowRules.Add(new ConditionalRule
                    {
                        IsRowRule = true,
                        Predicate = rowRule.Predicate,
                        FontColor = rowRule.FontColor,
                        FillColor = rowRule.FillColor,
                        Bold = rowRule.Bold
                    });
                }
            }

            return doc;
        }

        /// <summary>为追加的数据集构建一个文档部件</summary>
        public static DocumentPart BuildPart<TPart>(
            IEnumerable<TPart> data,
            string? requestedName,
            EntityMap<TPart>? map,
            ExcelExportOptions options)
        {
            var part = new DocumentPart
            {
                Sheet =
                {
                    RequestedName = requestedName,
                    RowType = typeof(TPart),
                    Rows = (System.Collections.IEnumerable)data
                }
            };

            var ancestors = new HashSet<Type> { typeof(TPart) };
            part.Sheet.Columns = BuildTree(typeof(TPart), map, "", ancestors, options, depth: 0);

            if (map != null)
            {
                foreach (var rowRule in ((IFluentMap)map).GetRowRules())
                {
                    part.RowRules.Add(new ConditionalRule
                    {
                        IsRowRule = true,
                        Predicate = rowRule.Predicate,
                        FontColor = rowRule.FontColor,
                        FillColor = rowRule.FillColor,
                        Bold = rowRule.Bold
                    });
                }
            }

            return part;
        }

        /// <summary>为集合子 Sheet 构建元素列计划</summary>
        public static List<ColumnPlan> BuildChildColumns(Type elementType, ExcelExportOptions options)
        {
            var ancestors = new HashSet<Type> { elementType };
            return BuildTree(elementType, null, "", ancestors, options, depth: 0);
        }

        private static List<ColumnPlan> BuildTree(
            Type ownerType,
            IFluentMap? map,
            string prefix,
            HashSet<Type> ancestors,
            ExcelExportOptions options,
            int depth)
        {
            var nodes = new List<ColumnPlan>();
            var properties = TypeMetadata.GetExportableProperties(ownerType);

            foreach (var prop in properties)
            {
                string path = prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name;
                FluentPropertyConfig? fluent = map?.FindProperty(path);

                // 忽略：[ExcelIgnore] 或 Fluent Ignore
                bool ignored = prop.GetCustomAttributes(typeof(ExcelIgnoreAttribute), true).Length > 0
                               || (fluent?.Ignore ?? false);
                if (ignored)
                    continue;

                var node = new ColumnPlan
                {
                    Name = prop.Name,
                    DisplayName = TypeMetadata.GetDisplayName(prop),
                    Property = prop,
                    Depth = depth
                };

                var columnAttr = prop.GetCustomAttributes(typeof(ExcelColumnAttribute), true)
                                    .FirstOrDefault() as ExcelColumnAttribute;

                // 基础显式配置（特性）
                if (columnAttr != null)
                {
                    if (!string.IsNullOrEmpty(columnAttr.Name))
                        node.DisplayName = columnAttr.Name!;
                    if (columnAttr.Order != 0)
                        node.Order = columnAttr.Order;
                    node.ForceText = columnAttr.ForceText;
                    node.Format = columnAttr.Format;
                    node.WrapText = columnAttr.WrapText;
                    node.AsHyperlink = columnAttr.AsHyperlink;
                    if (columnAttr.Width > 0)
                        node.FixedWidth = columnAttr.Width;
                }

                // 侵入式条件格式特性
                foreach (var attr in prop.GetCustomAttributes(typeof(ConditionalFormatAttribute), true)
                                          .Cast<ConditionalFormatAttribute>())
                {
                    node.Rules.Add(new ConditionalRule
                    {
                        Operator = attr.Operator,
                        Value = attr.Value,
                        Value2 = attr.Value2,
                        FontColor = attr.FontColor,
                        FillColor = attr.FillColor,
                        Bold = attr.Bold,
                        Column = node
                    });
                }

                // 侵入式转换器特性
                var converterAttr = prop.GetCustomAttributes(typeof(ExcelConverterAttribute), true)
                                        .FirstOrDefault() as ExcelConverterAttribute;
                if (converterAttr != null)
                    node.Converter = CreateConverter(converterAttr.ConverterType, prop);

                // Fluent 覆盖（优先级最高）
                if (fluent != null)
                    ApplyFluent(node, fluent);

                var propType = prop.PropertyType;

                // 类型分类
                if (TypeMetadata.IsByteArray(propType))
                {
                    MakeLeaf(node, propType);
                }
                else if (TypeMetadata.IsDictionary(propType))
                {
                    MakeLeaf(node, propType);
                }
                else if (TypeMetadata.IsCollection(propType))
                {
                    node.IsLeaf = true;
                    node.IsCollection = true;
                    node.CollectionElementType = TypeMetadata.GetCollectionElementType(propType);
                    node.ValueType = propType;
                }
                else if (TypeMetadata.IsSimpleType(propType))
                {
                    MakeLeaf(node, propType);
                }
                else
                {
                    // 复杂类型：递归；循环引用保护
                    Type underlying = Nullable.GetUnderlyingType(propType) ?? propType;
                    if (depth + 1 >= ExcelLimits.MaxColumnDepth || ancestors.Contains(underlying))
                    {
                        node.IsLeaf = true;
                        node.IsCycleMarker = true;
                        node.ValueType = propType;
                    }
                    else
                    {
                        ancestors.Add(underlying);
                        var children = BuildTree(underlying, map, path, ancestors, options, depth + 1);
                        ancestors.Remove(underlying);

                        if (children.Count > 0)
                        {
                            node.IsLeaf = false;
                            node.Children = children;
                            foreach (var c in children)
                                c.Parent = node;
                        }
                        else
                        {
                            MakeLeaf(node, propType);
                        }
                    }
                }

                // 自动强制文本
                if (!node.ForceText && node.IsLeaf && !node.IsCollection)
                    node.ForceText = ShouldAutoForceText(prop, propType, options);

                // 表头文本清理（防止特性/Fluent 名称含非法控制字符导致文件打不开）
                node.DisplayName = ValueServices.SanitizeXmlText(node.DisplayName);

                nodes.Add(node);
            }

            return SortNodes(nodes);
        }

        private static void MakeLeaf(ColumnPlan node, Type valueType)
        {
            node.IsLeaf = true;
            node.ValueType = valueType;
        }

        private static bool ShouldAutoForceText(
            System.Reflection.PropertyInfo prop,
            Type propType,
            ExcelExportOptions options)
        {
            // DataAnnotations DataType 显式语义（任何模式下都生效）
            string? dataType = TypeMetadata.GetDataTypeName(prop);
            if (dataType == "PhoneNumber" || dataType == "CreditCard" || dataType == "PostalCode")
                return true;

            var mode = options.AutoForceText;
            if (mode == ForceTextAutoMode.None)
                return false;

            bool byName = mode is ForceTextAutoMode.SensitiveNames or ForceTextAutoMode.Both
                          && TypeMetadata.IsSensitiveName(prop.Name);

            bool byCapacity = mode is ForceTextAutoMode.LongIntegers or ForceTextAutoMode.Both
                              && (propType == typeof(long) || propType == typeof(ulong));

            return byName || byCapacity;
        }

        private static List<ColumnPlan> SortNodes(List<ColumnPlan> nodes)
        {
            // 显式 Order 优先，未指定按声明顺序（OrderBy 稳定排序）
            return nodes
                .Select((n, i) => (Node: n, Index: i))
                .OrderBy(x => x.Node.Order ?? int.MaxValue)
                .ThenBy(x => x.Index)
                .Select(x => x.Node)
                .ToList();
        }

        private static void ApplyFluent(ColumnPlan node, FluentPropertyConfig fluent)
        {
            if (fluent.DisplayName != null)
                node.DisplayName = fluent.DisplayName;
            if (fluent.Order.HasValue)
                node.Order = fluent.Order;
            if (fluent.ForceText)
                node.ForceText = true;
            if (fluent.Format != null)
                node.Format = fluent.Format;
            if (fluent.WrapText)
                node.WrapText = true;
            if (fluent.AsHyperlink)
                node.AsHyperlink = true;
            if (fluent.Width.HasValue)
                node.FixedWidth = fluent.Width;
            if (fluent.Converter != null)
                node.Converter = fluent.Converter;

            foreach (var rule in fluent.Rules)
            {
                node.Rules.Add(new ConditionalRule
                {
                    Operator = rule.Operator,
                    Value = rule.Value,
                    Value2 = rule.Value2,
                    FontColor = rule.FontColor,
                    FillColor = rule.FillColor,
                    Bold = rule.Bold,
                    Column = node
                });
            }
        }

        private static Func<object, object?> CreateConverter(Type converterType, System.Reflection.PropertyInfo prop)
        {
            if (!typeof(IExcelValueConverter).IsAssignableFrom(converterType))
            {
                throw new ExcelMappingException(
                    $"属性 {prop.DeclaringType?.Name}.{prop.Name} 的转换器 {converterType.Name} 未实现 IExcelValueConverter。");
            }

            IExcelValueConverter instance;
            try
            {
                instance = (IExcelValueConverter)Activator.CreateInstance(converterType)!;
            }
            catch (Exception ex) when (ex is MissingMethodException or MemberAccessException)
            {
                throw new ExcelMappingException(
                    $"转换器 {converterType.Name} 必须提供公共无参构造函数。", ex);
            }

            return instance.Convert;
        }
    }
}
