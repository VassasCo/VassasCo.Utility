// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace VassasCo.Utility
{
    /// <summary>单元格水平对齐方式</summary>
    public enum CellAlign
    {
        /// <summary>沿用导出样式既有规则（表头居中，数据区左对齐）</summary>
        Default,

        /// <summary>左对齐</summary>
        Left,

        /// <summary>居中</summary>
        Center,

        /// <summary>右对齐</summary>
        Right
    }

    /// <summary>
    /// 布局行中的一个单元格：位于数据表之外（表前区/表后区），内容由调用方直接给定，不参与列映射。
    /// </summary>
    public sealed class ReportCell
    {
        /// <summary>单元格文本；null 视为空串</summary>
        public string? Text { get; set; }

        /// <summary>
        /// 横向合并的列数；小于等于 0 时表示跨满表格全部列（用于标题这类整行贯通的单元格）。
        /// 实际列宽由导出时的叶子列数决定，因此布局行无需预知表格有多少列。
        /// </summary>
        public int ColumnSpan { get; set; } = 1;

        /// <summary>字号；null 沿用样式默认值</summary>
        public double? FontSize { get; set; }

        /// <summary>是否加粗</summary>
        public bool Bold { get; set; }

        /// <summary>水平对齐方式</summary>
        public CellAlign Align { get; set; } = CellAlign.Default;

        /// <summary>字体颜色（十六进制，如 "#C00000"）</summary>
        public string? FontColor { get; set; }

        /// <summary>填充底色（十六进制）</summary>
        public string? FillColor { get; set; }
    }

    /// <summary>布局中的一行：由若干 <see cref="ReportCell"/> 顺序拼接而成</summary>
    public sealed class ReportRow
    {
        /// <summary>该行的单元格，按声明顺序从左到右排列</summary>
        public List<ReportCell> Cells { get; } = new List<ReportCell>();
    }

    /// <summary>
    /// 布局行构建器：以链式调用声明表前区/表后区的自由排版行。
    /// 每次 <see cref="Cell"/> 开启一个单元格，随后的样式设置作用于该单元格；
    /// <see cref="NextRow"/> 结束当前行并开启下一行。
    /// </summary>
    public sealed class ReportRowBuilder
    {
        private readonly List<ReportRow> _rows = new List<ReportRow>();
        private ReportRow? _currentRow;
        private ReportCell? _currentCell;

        /// <summary>追加一个单元格到当前行（当前行不存在时自动开启）</summary>
        /// <param name="text">单元格文本</param>
        public ReportRowBuilder Cell(string? text)
        {
            if (_currentRow is null)
                _currentRow = new ReportRow();

            _currentCell = new ReportCell { Text = text };
            _currentRow.Cells.Add(_currentCell);
            return this;
        }

        /// <summary>设置当前单元格横向合并的列数；小于等于 0 表示跨满整表</summary>
        public ReportRowBuilder Span(int columns)
        {
            RequireCell().ColumnSpan = columns;
            return this;
        }

        /// <summary>设置当前单元格字号</summary>
        public ReportRowBuilder FontSize(double size)
        {
            RequireCell().FontSize = size;
            return this;
        }

        /// <summary>设置当前单元格是否加粗</summary>
        public ReportRowBuilder Bold(bool bold = true)
        {
            RequireCell().Bold = bold;
            return this;
        }

        /// <summary>设置当前单元格水平对齐</summary>
        public ReportRowBuilder Align(CellAlign align)
        {
            RequireCell().Align = align;
            return this;
        }

        /// <summary>设置当前单元格字体颜色（十六进制）</summary>
        public ReportRowBuilder FontColor(string? hex)
        {
            RequireCell().FontColor = hex;
            return this;
        }

        /// <summary>设置当前单元格填充底色（十六进制）</summary>
        public ReportRowBuilder FillColor(string? hex)
        {
            RequireCell().FillColor = hex;
            return this;
        }

        /// <summary>结束当前行；之后再次调用 <see cref="Cell"/> 时自动开启新行</summary>
        public ReportRowBuilder NextRow()
        {
            if (_currentRow != null)
            {
                _rows.Add(_currentRow);
                _currentRow = null;
                _currentCell = null;
            }
            return this;
        }

        /// <summary>收尾并返回声明好的行集合（自动提交未显式收尾的最后一行）</summary>
        internal List<ReportRow> Build()
        {
            NextRow();
            return _rows;
        }

        private ReportCell RequireCell()
        {
            if (_currentCell is null)
                throw new InvalidOperationException("请先调用 Cell() 声明单元格，再设置该单元格的样式。");

            return _currentCell;
        }
    }
}
