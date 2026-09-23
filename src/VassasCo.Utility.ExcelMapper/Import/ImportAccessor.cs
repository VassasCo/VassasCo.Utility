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
    /// 导入映射访问器：复用 PlanBuilder 的列计划（侵入式特性/约定/非侵入式 Fluent 同源），
    /// 提供"表头文本 → 属性链"绑定，以及沿属性链安全写入（中间对象按需创建、setter 编译缓存）。
    /// </summary>
    internal sealed class ImportAccessor<T>
    {
        private static readonly ConcurrentDictionary<PropertyInfo, Action<object, object?>> SetterCache =
            new ConcurrentDictionary<PropertyInfo, Action<object, object?>>();

        private readonly Dictionary<string, ColumnPlan> _byHeader =
            new Dictionary<string, ColumnPlan>(StringComparer.OrdinalIgnoreCase);

        public List<string> HeaderWarnings { get; } = new List<string>();

        private ImportAccessor()
        {
        }

        /// <summary>从实体映射构建（不依赖数据行）</summary>
        public static ImportAccessor<T> Build(EntityMap<T>? map, ExcelExportOptions options)
        {
            var accessor = new ImportAccessor<T>();

            var document = PlanBuilder.BuildDocument(Array.Empty<T>(), null, map, options);
            var leaves = ColumnPlan.CollectAll(document.Primary.Sheet.Columns);

            foreach (var leaf in leaves.Select(l => l.Column))
            {
                string header = leaf.DisplayName.Trim();
                if (!accessor._byHeader.ContainsKey(header))
                    accessor._byHeader[header] = leaf;
                else
                    accessor.HeaderWarnings.Add($"存在重复列标题：{header}");
            }

            if (typeof(T).GetTypeInfo().IsValueType)
                throw new ExcelImportException("导入目标类型不能是值类型（需要无参构造与属性写入）。");

            return accessor;
        }

        /// <summary>按表头文本查找列计划；无匹配返回 null（调用方记录为未识别列）</summary>
        public ColumnPlan? FindColumn(string? headerText)
        {
            if (string.IsNullOrWhiteSpace(headerText))
                return null;
            return _byHeader.TryGetValue(headerText!.Trim(), out var leaf) ? leaf : null;
        }

        /// <summary>
        /// 沿属性链写入已转换好的强类型值；中间复杂对象为 null 时用无参构造创建。
        /// 失败返回原因（如缺少无参构造、值类型中间属性），不抛异常。
        /// </summary>
        public bool TrySetValue(T target, ColumnPlan leaf, object? value, out string reason)
        {
            if (target is null)
            {
                reason = "目标实例为 null";
                return false;
            }

            var chain = leaf.PropertyChain;
            if (chain.Length == 0)
            {
                reason = $"列 {leaf.DisplayName} 没有可写入的属性链";
                return false;
            }

            object current = target!;

            for (int i = 0; i < chain.Length; i++)
            {
                var prop = chain[i];
                var setter = GetSetter(prop);
                if (setter is null)
                {
                    reason = $"属性 {prop.Name} 为只读，无法写入";
                    return false;
                }

                if (i == chain.Length - 1)
                {
                    try
                    {
                        setter(current, value);
                        reason = "";
                        return true;
                    }
                    catch (InvalidCastException ex)
                    {
                        reason = $"写入类型不匹配：{ex.Message}";
                        return false;
                    }
                }

                // 取中间值
                object? intermediate = TypeMetadata.TryGetValue(prop, current);
                if (intermediate is null)
                {
                    Type propType = prop.PropertyType;
                    Type? innerNullable = Nullable.GetUnderlyingType(propType);
                    if (innerNullable != null)
                        propType = innerNullable;

                    if (propType.GetTypeInfo().IsValueType)
                    {
                        reason = $"嵌套属性 {prop.Name} 为值类型且当前为 null，无法自动创建中间对象";
                        return false;
                    }

                    if (!HasParameterlessConstructor(propType))
                    {
                        reason = $"嵌套类型 {propType.Name} 缺少公共无参构造函数，无法自动创建中间对象";
                        return false;
                    }

                    intermediate = Activator.CreateInstance(propType)!;
                    try
                    {
                        setter(current, intermediate);
                    }
                    catch (Exception ex)
                    {
                        reason = $"写入中间对象失败：{ex.Message}";
                        return false;
                    }
                }

                current = intermediate;
            }

            reason = "";
            return true;
        }

        private static Action<object, object?>? GetSetter(PropertyInfo prop)
        {
            if (!prop.CanWrite)
                return null;

            return SetterCache.GetOrAdd(prop, static p =>
            {
                var instance = Expression.Parameter(typeof(object), "instance");
                var value = Expression.Parameter(typeof(object), "value");

                Type ownerType = p.DeclaringType!;
                var instanceAccess = ownerType.GetTypeInfo().IsValueType
                    ? Expression.Unbox(instance, ownerType)
                    : Expression.Convert(instance, ownerType);

                bool useUnbox = p.PropertyType.GetTypeInfo().IsValueType
                    && Nullable.GetUnderlyingType(p.PropertyType) is null;
                var valueAccess = useUnbox
                    ? Expression.Unbox(value, p.PropertyType)
                    : Expression.Convert(value, p.PropertyType);

                var assign = Expression.Assign(Expression.Property(instanceAccess, p), valueAccess);
                return Expression.Lambda<Action<object, object?>>(assign, instance, value).Compile();
            });
        }

        private static bool HasParameterlessConstructor(Type type)
        {
            return type.GetTypeInfo()
                .GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) != null;
        }
    }
}
