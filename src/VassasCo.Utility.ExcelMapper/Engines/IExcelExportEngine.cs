// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace VassasCo.Utility.Internals
{
    /// <summary>Excel 导出引擎抽象（DOM / 流式可替换，Builder 与工作簿 API 不变）</summary>
    internal interface IExcelExportEngine
    {
        /// <summary>引擎类型（用于导出诊断信息）</summary>
        ExportEngineKind Kind { get; }

        /// <summary>导出文档到文件或流（二选一），返回写入统计</summary>
        EngineExportStats Export(
            ExportDocument document,
            string? filePath,
            System.IO.Stream? stream,
            ExcelStyle style,
            ExcelExportOptions options,
            CancellationToken cancellationToken,
            IProgress<int>? progress);
    }

    /// <summary>引擎导出统计（供上层汇总为诊断信息）</summary>
    internal struct EngineExportStats
    {
        /// <summary>写入的数据记录总数（主数据集 + 追加数据集，不含表头、不含集合子 Sheet 展开）</summary>
        public int RowsWritten;

        /// <summary>生成的工作表总数（含集合子 Sheet）</summary>
        public int SheetsWritten;
    }
}
