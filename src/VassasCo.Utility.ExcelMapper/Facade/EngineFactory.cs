// SPDX-License-Identifier: MIT

using System.Collections;

namespace VassasCo.Utility.Internals
{
    /// <summary>导出引擎选择：显式指定或按已知数据量自动切换</summary>
    internal static class EngineFactory
    {
        /// <summary>按选项与已知行数创建引擎；knownRows=null 表示数据量未知（惰性序列），回退 DOM 引擎</summary>
        public static IExcelExportEngine Create(ExcelExportOptions options, int? knownRows)
        {
            switch (options.Engine)
            {
                case ExportEngineKind.ClosedXml:
                    return new ClosedXmlExportEngine();

                case ExportEngineKind.OpenXmlStreaming:
                    return new OpenXmlStreamingExportEngine();

                default: // Auto
                    if (knownRows.HasValue && knownRows.Value >= options.StreamingThreshold)
                        return new OpenXmlStreamingExportEngine();
                    return new ClosedXmlExportEngine();
            }
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
