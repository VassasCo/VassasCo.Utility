// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace VassasCo.Utility
{
    /// <summary>单个单元格/字段的导入错误</summary>
    public sealed class ImportError
    {
        /// <summary>源数据行号（含表头时从 2 开始，与 Excel 行号对齐）</summary>
        public int Row { get; }

        /// <summary>列名（表头文本；无法识别时为 null）</summary>
        public string? Column { get; }

        /// <summary>原始值</summary>
        public object? RawValue { get; }

        /// <summary>失败原因</summary>
        public string Reason { get; }

        /// <summary>初始化实例</summary>
        public ImportError(int row, string? column, object? rawValue, string reason)
        {
            Row = row;
            Column = column;
            RawValue = rawValue;
            Reason = reason;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"第 {Row} 行 [{Column ?? "未知列"}]：{Reason}（原始值：{RawValue}）";
        }
    }

    /// <summary>导入结果：成功实体与错误收集（错误不阻断整批导入，便于桌面端展示给用户修正）</summary>
    public sealed class ImportResult<T>
    {
        /// <summary>成功导入的实体</summary>
        public List<T> Items { get; } = new List<T>();

        /// <summary>逐字段错误记录</summary>
        public List<ImportError> Errors { get; } = new List<ImportError>();

        /// <summary>是否存在错误</summary>
        public bool HasErrors => Errors.Count > 0;

        /// <summary>初始化实例</summary>
        public ImportResult()
        {
        }

        internal ImportResult(List<T> items, List<ImportError> errors)
        {
            Items = items;
            Errors = errors;
        }
    }
}
