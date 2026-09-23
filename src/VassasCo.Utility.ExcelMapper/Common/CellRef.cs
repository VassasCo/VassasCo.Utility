// SPDX-License-Identifier: MIT

using System.Text;

namespace VassasCo.Utility.Internals
{
    /// <summary>Excel 单元格引用工具（1-based 列序号 ↔ 列字母）</summary>
    internal static class CellRef
    {
        /// <summary>列序号转字母：1→A，27→AA</summary>
        public static string ColumnName(int column)
        {
            var sb = new StringBuilder();
            while (column > 0)
            {
                column--;
                sb.Insert(0, (char)('A' + column % 26));
                column /= 26;
            }
            return sb.ToString();
        }
    }
}
