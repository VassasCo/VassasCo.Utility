// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;

namespace VassasCo.Utility.Internals
{
    /// <summary>导出计划校验：在写入前拦截会导致错误数据/异常文件的配置</summary>
    internal static class PlanValidator
    {
        /// <summary>校验整个文档（主 Sheet + 追加 Sheet）</summary>
        public static void Validate(ExportDocument doc, ExcelExportOptions options)
        {
            if (options.ValidatePlan == ValidatePlanMode.Skip)
                return;

            ValidatePart(doc.Primary, options);
            foreach (var part in doc.AdditionalParts)
                ValidatePart(part, options);
        }

        /// <summary>校验单个部件</summary>
        public static void ValidatePart(DocumentPart part, ExcelExportOptions options)
        {
            var errors = new List<string>();
            var sheet = part.Sheet;

            var leaves = ColumnPlan.CollectAll(sheet.Columns);

            if (leaves.Count > ExcelLimits.MaxColumns)
                errors.Add($"列数 {leaves.Count} 超过单 Sheet 上限 {ExcelLimits.MaxColumns}。");

            // 列标题重名
            var duplicated = leaves
                .GroupBy(l => l.Column.DisplayName)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicated.Count > 0)
                errors.Add("存在重复的列标题：" + string.Join("、", duplicated) + "（请用 Fluent/特性重命名）。");

            if (options.ArrayRender == ArrayRenderMode.FlattenInPlace)
            {
                var collectionLeaves = leaves.Where(l => l.Column.IsCollection).ToList();
                if (collectionLeaves.Count > 1)
                {
                    errors.Add(
                        "FlattenInPlace 模式只允许一个集合属性（当前有 " + collectionLeaves.Count +
                        " 个：" + string.Join("、", collectionLeaves.Select(l => l.Column.FullPath)) +
                        "），否则会产生笛卡尔积假数据；请改用 ChildSheet 模式。");
                }

                var complexCollection = collectionLeaves.FirstOrDefault(l =>
                    l.Column.CollectionElementType != null &&
                    !TypeMetadata.IsSimpleType(l.Column.CollectionElementType));
                if (complexCollection != null)
                {
                    errors.Add(
                        $"FlattenInPlace 暂不支持复杂元素集合（{complexCollection.Column.FullPath}），请使用 ChildSheet 模式。");
                }
            }

            if (options.PageSize.HasValue && (options.PageSize.Value <= 0 ||
                options.PageSize.Value > ExcelLimits.MaxRows))
            {
                errors.Add($"PageSize 取值非法：{options.PageSize}。");
            }

            if (errors.Count > 0)
                throw new ExcelPlanValidationException(errors);
        }
    }
}
