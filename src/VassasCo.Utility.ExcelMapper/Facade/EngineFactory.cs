// SPDX-License-Identifier: MIT

using System.Collections;

namespace VassasCo.Utility.Internals
{
    /// <summary>导出引擎选择：显式指定或按已知数据量自动切换</summary>
    internal static class EngineFactory
    {
        /// <summary>按选项与已知行数创建引擎；knownRows=null 表示数据量未知（惰性序列），回退 DOM 引擎</summary>
        /// <param name="options">导出选项</param>
        /// <param name="knownRows">已知识别行数；null 表示惰性序列</param>
        /// <param name="hasPictures">文档是否包含图片；包含时自动模式强制选用 DOM 引擎</param>
        /// <param name="hasLayoutRows">文档是否包含布局行（表前区/表后区）；包含时自动模式强制选用 DOM 引擎</param>
        public static IExcelExportEngine Create(
            ExcelExportOptions options,
            int? knownRows,
            bool hasPictures = false,
            bool hasLayoutRows = false)
        {
            switch (options.Engine)
            {
                case ExportEngineKind.ClosedXml:
                    return new ClosedXmlExportEngine();

                case ExportEngineKind.OpenXmlStreaming:
                    // 流式（SAX）引擎只支持顺序写入，无法回填图片部件。
                    // 这里仍按用户显式指定返回流式引擎，图片会被跳过，
                    // 是否报错由 PlanValidator 根据 ValidatePlan 策略决定。
                    return new OpenXmlStreamingExportEngine();

                default: // Auto
                    // 图片与布局行都依赖随机写/合并，必须走 DOM 引擎：
                    // 否则这些内容会被静默丢弃，排查成本极高
                    if (hasPictures || hasLayoutRows)
                        return new ClosedXmlExportEngine();

                    if (knownRows.HasValue && knownRows.Value >= options.StreamingThreshold)
                        return new OpenXmlStreamingExportEngine();
                    return new ClosedXmlExportEngine();
            }
        }

        /// <summary>判断文档中是否存在需要写入的图片（固定图片或图片列）</summary>
        public static bool HasPictures(ExportDocument? document)
        {
            if (document is null)
                return false;

            if (HasPictures(document.Primary))
                return true;

            foreach (var part in document.AdditionalParts)
            {
                if (HasPictures(part))
                    return true;
            }

            return false;
        }

        private static bool HasPictures(DocumentPart part)
        {
            if (part.Sheet.Pictures.Count > 0)
                return true;

            var leaves = ColumnPlan.CollectAll(part.Sheet.Columns);
            foreach (var leaf in leaves)
            {
                if (leaf.Column.AsImage)
                    return true;
            }

            return false;
        }

        /// <summary>判断文档中是否存在布局行（表前区/表后区）</summary>
        public static bool HasLayout(ExportDocument? document)
        {
            if (document is null)
                return false;

            if (document.Primary.Sheet.HasLayoutRows)
                return true;

            foreach (var part in document.AdditionalParts)
            {
                if (part.Sheet.HasLayoutRows)
                    return true;
            }

            return false;
        }

        /// <summary>在不全量枚举的前提下尽力获取行数（集合类型直接读 Count）</summary>
        public static int? TryGetCount(ExportDocument document)
        {
            int total = 0;
            if (!TryCountRows(document.Primary.Sheet.Rows, out int primaryCount))
                return null;
            total += primaryCount;

            foreach (var part in document.AdditionalParts)
            {
                if (!TryCountRows(part.Sheet.Rows, out int count))
                    return null;
                total += count;
            }
            return total;
        }

        private static bool TryCountRows(IEnumerable rows, out int count)
        {
            if (rows is ICollection collection)
            {
                count = collection.Count;
                return true;
            }
            count = 0;
            return false;
        }
    }
}
