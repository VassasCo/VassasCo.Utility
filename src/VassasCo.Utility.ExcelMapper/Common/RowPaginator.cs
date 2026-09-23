// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// 行分页器：将任意 IEnumerable 惰性切成固定大小的批次，不全量物化数据。
    /// </summary>
    internal static class RowPaginator
    {
        /// <summary>按页大小分批（pageSize=0 时整体作为一页）</summary>
        public static IEnumerable<List<object>> Paginate(System.Collections.IEnumerable rows, int pageSize)
        {
            if (pageSize <= 0)
            {
                var all = new List<object>();
                foreach (var item in rows)
                    all.Add(item);
                yield return all;
                yield break;
            }

            List<object>? page = null;
            foreach (var item in rows)
            {
                page ??= new List<object>(pageSize);
                page.Add(item);

                if (page.Count >= pageSize)
                {
                    yield return page;
                    page = null;
                }
            }

            if (page != null && page.Count > 0)
                yield return page;
        }

        /// <summary>计算单 Sheet 每页可容纳的数据行数（扣除表头行）</summary>
        public static int ClampPageSize(int? requestedPageSize, int headerRowCount)
        {
            int maxDataRows = ExcelLimits.MaxRows - headerRowCount;
            if (maxDataRows < 1)
                throw new ExcelMappingException("表头行数超过 Excel 单 Sheet 行数上限。");

            if (!requestedPageSize.HasValue || requestedPageSize.Value <= 0)
                return maxDataRows;

            if (requestedPageSize.Value > maxDataRows)
                throw new ExcelMappingException(
                    $"每页行数 {requestedPageSize} 超过单 Sheet 上限（表头 {headerRowCount} 行，最多 {maxDataRows} 行数据）。");

            return requestedPageSize.Value;
        }
    }
}
