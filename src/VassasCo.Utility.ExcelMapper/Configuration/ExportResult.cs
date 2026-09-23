// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace VassasCo.Utility
{
    /// <summary>
    /// 导出诊断信息：行数、工作表数、实际引擎、耗时与生成的文件清单。
    /// 桌面端可据此展示导出结果（如"成功导出 N 条记录，耗时 X 秒"）。
    /// </summary>
    public sealed class ExportResult
    {
        /// <summary>写入的数据记录总数（主数据集 + 追加数据集，不含表头、不含集合子 Sheet 展开）</summary>
        public int RowCount { get; }

        /// <summary>生成的工作表总数（含集合子 Sheet）</summary>
        public int SheetCount { get; }

        /// <summary>实际使用的导出引擎</summary>
        public ExportEngineKind Engine { get; }

        /// <summary>导出耗时</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>生成的文件数（多文件分页时 &gt;1）</summary>
        public int FileCount { get; }

        /// <summary>生成的文件路径列表（导出到流时为空）</summary>
        public IReadOnlyList<string> FilePaths { get; }

        internal ExportResult(
            int rowCount,
            int sheetCount,
            ExportEngineKind engine,
            TimeSpan elapsed,
            IReadOnlyList<string> filePaths)
        {
            RowCount = rowCount;
            SheetCount = sheetCount;
            Engine = engine;
            Elapsed = elapsed;
            FilePaths = filePaths;
            FileCount = filePaths.Count;
        }
    }
}
