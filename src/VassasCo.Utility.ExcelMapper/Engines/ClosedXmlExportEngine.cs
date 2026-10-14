// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// ClosedXML DOM 引擎：多层合并表头、集合子 Sheet（支持递归分页）、批量 InsertData、
    /// 原生条件格式 + 服务端静态样式、行级规则、超链接、AutoFilter/Table、组合冻结。
    /// </summary>
    internal sealed class ClosedXmlExportEngine : IExcelExportEngine
    {
        private XLWorkbook _workbook = null!;
        private SheetNameRegistry _names = null!;
        private ExcelStyle _style = null!;
        private ExcelExportOptions _options = null!;
        private CancellationToken _ct;
        private IProgress<int>? _progress;
        private int _rowsDone;
        private int _totalRows;
        private int _tableCounter;

        #region Entry

        public ExportEngineKind Kind => ExportEngineKind.ClosedXml;

        public EngineExportStats Export(
            ExportDocument document,
            string? filePath,
            System.IO.Stream? stream,
            ExcelStyle style,
            ExcelExportOptions options,
            CancellationToken cancellationToken,
            IProgress<int>? progress)
        {
            _workbook = new XLWorkbook();
            _names = new SheetNameRegistry();
            _style = style;
            _options = options;
            _ct = cancellationToken;
            _progress = progress;
            _rowsDone = 0;
            _totalRows = 0;

            ProcessPart(document.Primary);
            foreach (var part in document.AdditionalParts)
            {
                _ct.ThrowIfCancellationRequested();
                ProcessPart(part);
            }

            SaveWorkbook(filePath, stream);

            return new EngineExportStats
            {
                RowsWritten = _totalRows,
                SheetsWritten = _workbook.Worksheets.Count
            };
        }

        private void SaveWorkbook(string? filePath, System.IO.Stream? stream)
        {
            if (filePath != null)
                _workbook.SaveAs(filePath);
            else if (stream != null)
                _workbook.SaveAs(stream);
            else
                throw new InvalidOperationException("必须指定文件路径或输出流。");
        }

        #endregion

        #region Part Processing

        private void ProcessPart(DocumentPart part)
        {
            _currentRowType = part.Sheet.RowType;
            var leaves = ColumnPlan.CollectAll(part.Sheet.Columns);
            string baseName = part.Sheet.RequestedName ?? part.Sheet.RowType.Name;

            if (leaves.Count == 0)
            {
                var s = _workbook.AddWorksheet(_names.Register(baseName));
                s.Cell(1, 1).Value = "(无可用属性)";
                return;
            }

            int headerDepth = leaves.Max(l => l.Column.Depth) + 1;
            int pageSize = RowPaginator.ClampPageSize(_options.PageSize, headerDepth);

            var buckets = new Dictionary<ColumnPlan, ChildBucket>();
            LeafColumnInfo? flattenLeaf = _options.ArrayRender == ArrayRenderMode.FlattenInPlace
                ? leaves.FirstOrDefault(l => l.Column.IsCollection)
                : null;

            int pageIndex = 0;
            foreach (var page in RowPaginator.Paginate(part.Sheet.Rows, pageSize))
            {
                _ct.ThrowIfCancellationRequested();
                string pageName = pageIndex == 0
                    ? baseName
                    : string.Format(CultureInfo.InvariantCulture, _options.SheetPageNameFormat, baseName, pageIndex + 1);

                WritePrimaryPage(part, leaves, page, pageName, headerDepth, buckets, flattenLeaf?.Column);
                _totalRows += page.Count;
                pageIndex++;
            }

            if (pageIndex == 0)
            {
                // 空数据：布局区 + 仅表头
                var emptySheet = _workbook.AddWorksheet(_names.Register(baseName));
                ApplySheetStyle(emptySheet);

                int preambleRows = WriteLayoutRows(emptySheet, part.Sheet.Preamble, leaves.Count, 1);
                int headerTop = preambleRows + 1;

                WriteMergedHeaders(emptySheet, part.Sheet.Columns, leaves, headerDepth, headerTop);
                ApplyColumnSettings(emptySheet, leaves, headerTop + headerDepth, headerTop + headerDepth - 1,
                    headerTop + headerDepth - 1);
                ApplyFreeze(emptySheet, headerTop - 1 + headerDepth);

                if (part.Sheet.Postamble.Count > 0)
                    WriteLayoutRows(emptySheet, part.Sheet.Postamble, leaves.Count, headerTop + headerDepth);
            }

            FlushChildBuckets(buckets);
        }

        #endregion

        #region Primary Page

        private void WritePrimaryPage(
            DocumentPart part,
            List<LeafColumnInfo> leaves,
            List<object> sourceRows,
            string sheetName,
            int headerDepth,
            Dictionary<ColumnPlan, ChildBucket> buckets,
            ColumnPlan? flattenLeaf)
        {
            var sheet = _workbook.AddWorksheet(_names.Register(sheetName));
            ApplySheetStyle(sheet);

            int preambleRows = WriteLayoutRows(sheet, part.Sheet.Preamble, leaves.Count, 1);
            int headerTop = preambleRows + 1;

            WriteMergedHeaders(sheet, part.Sheet.Columns, leaves, headerDepth, headerTop);

            int dataStart = headerTop + headerDepth;

            var writableRows = new List<object[]>(sourceRows.Count);
            var sourceInfos = new List<SourceRowInfo>(sourceRows.Count);

            for (int sourceRowIdx = 0; sourceRowIdx < sourceRows.Count; sourceRowIdx++)
            {
                var source = sourceRows[sourceRowIdx];
                _ct.ThrowIfCancellationRequested();

                var raw = new object?[leaves.Count];
                for (int i = 0; i < leaves.Count; i++)
                {
                    var leaf = leaves[i].Column;
                    var resolved = ValueServices.Resolve(source, leaf);
                    raw[i] = ValueServices.ApplyConverter(resolved, leaf);
                }

                int first = writableRows.Count;

                if (flattenLeaf != null)
                {
                    int flattenIdx = flattenLeaf.LeafIndex;
                    var items = ExtractItems(raw[flattenIdx]);

                    if (items.Count == 0)
                    {
                        var clone = (object?[])raw.Clone();
                        clone[flattenIdx] = null;
                        writableRows.Add(BuildWritableRow(clone, leaves));
                    }
                    else
                    {
                        foreach (var item in items)
                        {
                            var clone = (object?[])raw.Clone();
                            clone[flattenIdx] = item;
                            writableRows.Add(BuildWritableRow(clone, leaves));
                        }
                    }
                }
                else
                {
                    // 集合叶子：占位与子 Sheet 收集
                    for (int i = 0; i < leaves.Count; i++)
                    {
                        var leaf = leaves[i].Column;
                        if (leaf.IsCollection)
                            raw[i] = HandleCollectionLeaf(part, leaf, raw[i], source,
                                sourceRowIdx, buckets, sheetName);
                    }

                    writableRows.Add(BuildWritableRow(raw, leaves));
                }

                sourceInfos.Add(new SourceRowInfo
                {
                    Source = source,
                    FirstExcelRow = dataStart + first,
                    Count = writableRows.Count - first,
                    Raw = raw
                });
            }

            int dataEnd = dataStart + writableRows.Count - 1;

            // 批量写入（ClosedXML 推荐 API）
            if (writableRows.Count > 0)
                sheet.Cell(dataStart, 1).InsertData(writableRows);

            ApplyColumnSettings(sheet, leaves, dataStart, dataEnd, headerTop + headerDepth - 1);
            ApplyDataArea(sheet, dataStart, dataEnd, leaves.Count, headerDepth);
            ApplyHyperlinks(sheet, leaves, dataStart, sourceInfos);
            ApplyPictures(sheet, part, leaves, dataStart, sourceInfos);
            ApplyConditionalRules(sheet, part, leaves, dataStart, dataEnd, sourceInfos);
            ApplyFreeze(sheet, headerTop - 1 + headerDepth);

            // 无数据行时 dataEnd 已停留在表头下一行，取 dataEnd + 1 即可让表后区紧接表头
            if (part.Sheet.Postamble.Count > 0)
                WriteLayoutRows(sheet, part.Sheet.Postamble, leaves.Count, dataEnd + 1);

            AdvanceProgress(writableRows.Count);
        }

        /// <summary>将一行原始值转为 ClosedXML 可批量写入的值（文本/日期/数字）</summary>
        private object[] BuildWritableRow(object?[] raw, List<LeafColumnInfo> leaves)
        {
            var row = new object[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                var value = raw[i];
                var leaf = leaves[i].Column;

                if (leaf.IsCycleMarker)
                {
                    row[i] = "(循环引用)";
                    continue;
                }

                if (value is null)
                {
                    row[i] = "";
                    continue;
                }

                // 图片列：值交给 ApplyPictures 渲染成嵌入式图片，单元格本身保持空白
                if (leaf.AsImage)
                {
                    row[i] = "";
                    continue;
                }

                if (leaf.IsCollection && _options.ArrayRender != ArrayRenderMode.FlattenInPlace)
                {
                    row[i] = value; // 已转为占位字符串
                    continue;
                }

                if (ValueServices.IsTextValue(value, leaf, _options))
                {
                    row[i] = ValueServices.ToText(value, leaf, _style, _options);
                }
                else if (ValueServices.TryGetDateTime(value, out var dt))
                {
                    row[i] = dt.Year < 1900
                        ? ValueServices.ToText(value, leaf, _style, _options)
                        : dt;
                }
                else
                {
                    try
                    {
                        row[i] = ValueServices.ToDouble(value);
                    }
                    catch
                    {
                        row[i] = ValueServices.ToText(value, leaf, _style, _options);
                    }
                }
            }

            return row;
        }

        #endregion

        #region Collection Leaves & Child Buckets

        private string HandleCollectionLeaf(
            DocumentPart part,
            ColumnPlan leaf,
            object? value,
            object source,
            int rowIndex,
            Dictionary<ColumnPlan, ChildBucket> buckets,
            string parentSheetName)
        {
            if (_options.ArrayRender == ArrayRenderMode.Json)
                return ValueServices.ToJsonText(value, _options);

            var items = ExtractItems(value);
            int count = items.Count;

            if (_options.ArrayRender == ArrayRenderMode.PlaceholderOnly)
                return $"[{count} items]";

            // ChildSheet 模式
            bool canBuildChild = leaf.CollectionElementType != null &&
                                 leaf.CollectionElementType != typeof(object);
            if (!canBuildChild)
                return $"[{count} items]";

            if (count == 0)
                return "[0 items]";

            if (!buckets.TryGetValue(leaf, out var bucket))
            {
                // 子 Sheet 以集合属性名命名（如 "Addresses"）
                string childBase = _names.Register(leaf.Name);
                bucket = new ChildBucket
                {
                    Name = childBase,
                    ElementType = leaf.CollectionElementType!
                };
                buckets[leaf] = bucket;
            }

            string parentRef = ValueServices.GetIdentityString(
                source, part.Sheet.RowType, rowIndex, _style);

            foreach (var item in items)
                bucket.Rows.Add(new ChildRow(parentRef, item));

            return $"[{count} items → Sheet \"{bucket.Name}\"]";
        }

        private void FlushChildBuckets(Dictionary<ColumnPlan, ChildBucket> buckets)
        {
            foreach (var bucket in buckets.Values)
            {
                if (bucket.Rows.Count == 0)
                    continue;

                var childColumns = PlanBuilder.BuildChildColumns(bucket.ElementType, _options);
                var childLeaves = ColumnPlan.CollectAll(childColumns);
                int pageSize = RowPaginator.ClampPageSize(_options.PageSize, 1);

                var childRows = bucket.Rows.Select(r => (object)r).ToList();
                int pageIndex = 0;

                foreach (var page in RowPaginator.Paginate(childRows, pageSize))
                {
                    _ct.ThrowIfCancellationRequested();
                    string pageName = pageIndex == 0
                        ? bucket.Name
                        : string.Format(CultureInfo.InvariantCulture, _options.SheetPageNameFormat, bucket.Name, pageIndex + 1);

                    WriteChildPage(page.Cast<ChildRow>().ToList(), childColumns, childLeaves, pageName,
                        bucket.ElementType);
                    pageIndex++;
                }
            }
        }

        private void WriteChildPage(
            List<ChildRow> rows,
            List<ColumnPlan> childColumns,
            List<LeafColumnInfo> childLeaves,
            string sheetName,
            Type elementType)
        {
            // bucket 创建时（或分页命名时）名称已确定，此处不再重复注册
            var sheet = _workbook.AddWorksheet(sheetName);
            string parentHeader = ValueServices.GetParentColumnHeader(elementType, _style);

            // 单行表头：父关联列 + 元素列
            sheet.Cell(1, 1).Value = parentHeader;
            ApplyHeaderStyle(sheet.Cell(1, 1));
            for (int i = 0; i < childLeaves.Count; i++)
            {
                sheet.Cell(1, i + 2).Value = childLeaves[i].Column.DisplayName;
                ApplyHeaderStyle(sheet.Cell(1, i + 2));
            }

            var writable = new List<object[]>(rows.Count);
            foreach (var cr in rows)
            {
                var row = new object[childLeaves.Count + 1];
                row[0] = cr.ParentRef;

                for (int i = 0; i < childLeaves.Count; i++)
                {
                    var leaf = childLeaves[i].Column;
                    var value = TypeMetadata.TryGetValue(leaf.Property!, cr.Item);
                    value = ValueServices.ApplyConverter(value, leaf);

                    if (value is null)
                        row[i + 1] = "";
                    else if (ValueServices.IsTextValue(value, leaf, _options))
                        row[i + 1] = ValueServices.ToText(value, leaf, _style, _options);
                    else if (ValueServices.TryGetDateTime(value, out var dt))
                        row[i + 1] = dt.Year < 1900 ? ValueServices.ToText(value, leaf, _style, _options) : dt;
                    else
                        row[i + 1] = ValueServices.ToDouble(value);
                }

                writable.Add(row);
            }

            int dataEnd = writable.Count + 1;
            sheet.Cell(2, 1).InsertData(writable);

            // 列设置：父列文本格式 + 元素列
            sheet.Column(1).Style.NumberFormat.Format = "@";
            for (int i = 0; i < childLeaves.Count; i++)
                ApplyOneColumnSetting(sheet, i + 2, childLeaves[i].Column, 2, dataEnd, headerRow: 1);

            if (_style.ShowBorder)
            {
                var range = sheet.Range(1, 1, dataEnd, childLeaves.Count + 1);
                ApplyBorderToRange(range);
            }

            sheet.SheetView.Freeze(1, 0);
            AdvanceProgress(writable.Count);
        }

        #endregion

        #region Headers

        /// <summary>
        /// 写入多层表头。表头整体位于 <paramref name="startRow"/> 起的行内，
        /// 该值大于 1 表示表头之上还有布局行（标题/元信息），此时表头随之下移。
        /// </summary>
        private void WriteMergedHeaders(
            IXLWorksheet sheet,
            List<ColumnPlan> roots,
            List<LeafColumnInfo> leaves,
            int headerDepth,
            int startRow = 1)
        {
            var headerCells = new List<(int Row, int StartCol, int EndCol, string Text, bool IsLeaf)>();
            int leafIdx = 0;
            foreach (var col in roots)
                leafIdx = CollectHeaderCells(col, leafIdx, 0, headerCells);

            foreach (var hc in headerCells)
            {
                int excelRow = hc.Row + startRow;
                var cell = sheet.Cell(excelRow, hc.StartCol + 1);
                cell.Value = hc.Text;

                if (hc.StartCol != hc.EndCol)
                    sheet.Range(excelRow, hc.StartCol + 1, excelRow, hc.EndCol + 1).Merge();

                int lastHeaderRow = startRow + headerDepth - 1;
                if (hc.IsLeaf && lastHeaderRow > excelRow)
                    sheet.Range(excelRow, hc.StartCol + 1, lastHeaderRow, hc.EndCol + 1).Merge();

                ApplyHeaderStyle(cell);
            }

            if (_style.HeaderHeight.HasValue)
            {
                for (int r = startRow; r < startRow + headerDepth; r++)
                    sheet.Row(r).Height = _style.HeaderHeight.Value;
            }

            if (_style.ShowBorder && leaves.Count > 0)
            {
                var headerRange = sheet.Range(startRow, 1, startRow + headerDepth - 1, leaves.Count);
                ApplyBorderToRange(headerRange);
            }
        }

        private static int CollectHeaderCells(
            ColumnPlan column,
            int startLeafIndex,
            int depth,
            List<(int Row, int StartCol, int EndCol, string Text, bool IsLeaf)> result)
        {
            if (column.IsLeaf)
            {
                result.Add((depth, startLeafIndex, startLeafIndex, column.DisplayName, true));
                return startLeafIndex + 1;
            }

            int current = startLeafIndex;
            foreach (var child in column.Children)
                current = CollectHeaderCells(child, current, depth + 1, result);

            result.Add((depth, startLeafIndex, current - 1, column.DisplayName, false));
            return current;
        }

        #endregion

        #region Layout Rows

        /// <summary>
        /// 写入位于数据表之外的自由排版行（表前区/表后区），返回实际占用的行数。
        /// </summary>
        /// <remarks>
        /// 单元格按声明顺序从左到右排布；<see cref="ReportCell.ColumnSpan"/> 小于等于 0 时跨满整表，
        /// 超出剩余列数的合并范围会被收敛到表格边界，避免越写到数据列之外。
        /// </remarks>
        private int WriteLayoutRows(
            IXLWorksheet sheet,
            List<ReportRow> rows,
            int columnCount,
            int startRow)
        {
            if (rows.Count == 0 || columnCount == 0)
                return 0;

            int excelRow = startRow;
            foreach (var row in rows)
            {
                int col = 1;
                foreach (var cell in row.Cells)
                {
                    if (col > columnCount)
                        break;

                    int span = cell.ColumnSpan <= 0
                        ? columnCount - col + 1
                        : Math.Min(cell.ColumnSpan, columnCount - col + 1);

                    var target = sheet.Cell(excelRow, col);
                    target.Value = cell.Text ?? string.Empty;
                    ApplyLayoutCellStyle(target, cell);

                    if (span > 1)
                        sheet.Range(excelRow, col, excelRow, col + span - 1).Merge();

                    col += span;
                }
                excelRow++;
            }

            if (_style.ShowBorder)
                ApplyBorderToRange(sheet.Range(startRow, 1, excelRow - 1, columnCount));

            return rows.Count;
        }

        private void ApplyLayoutCellStyle(IXLCell target, ReportCell cell)
        {
            if (cell.Bold)
                target.Style.Font.Bold = true;

            if (cell.FontSize.HasValue)
                target.Style.Font.FontSize = cell.FontSize.Value;

            SafeSetColor(c => target.Style.Font.FontColor = c, cell.FontColor);
            SafeSetColor(c => target.Style.Fill.BackgroundColor = c, cell.FillColor);

            switch (cell.Align)
            {
                case CellAlign.Left:
                    target.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                    break;
                case CellAlign.Center:
                    target.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    break;
                case CellAlign.Right:
                    target.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    break;
            }

            if (cell.Align != CellAlign.Default)
                target.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        /// <summary>应用工作表级默认字体（表级新设的单元格样式可覆盖）</summary>
        private void ApplySheetStyle(IXLWorksheet sheet)
        {
            if (!string.IsNullOrEmpty(_style.FontName))
                sheet.Style.Font.FontName = _style.FontName;

            if (_style.FontSize.HasValue)
                sheet.Style.Font.FontSize = _style.FontSize.Value;
        }

        #endregion

        #region Styling

        private void ApplyHeaderStyle(IXLCell cell)
        {
            if (_style.HeaderBold)
                cell.Style.Font.Bold = true;

            if (_style.HeaderFontSize.HasValue)
                cell.Style.Font.FontSize = _style.HeaderFontSize.Value;

            SafeSetColor(c => cell.Style.Fill.BackgroundColor = c, _style.HeaderBackgroundColor);
            SafeSetColor(c => cell.Style.Font.FontColor = c, _style.HeaderFontColor);

            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        private static void SafeSetColor(Action<XLColor> apply, string? hex)
        {
            if (string.IsNullOrEmpty(hex))
                return;
            try { apply(XLColor.FromHtml(hex!)); }
            catch (FormatException) { }
        }

        private static void ApplyBorderToRange(IXLRange range)
        {
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        }

        private void ApplyDataArea(
            IXLWorksheet sheet,
            int dataStart,
            int dataEnd,
            int colCount,
            int headerDepth)
        {
            if (_style.ShowBorder)
                ApplyBorderToRange(sheet.Range(dataStart, 1, dataEnd, colCount));

            var dataRange = sheet.Range(dataStart, 1, dataEnd, colCount);

            var sheetAttr = GetSheetAttribute();
            bool useTable = sheetAttr?.UseTable ?? false;
            bool autoFilter = sheetAttr?.AutoFilter ?? false;

            if (useTable && headerDepth == 1)
            {
                string tableName = "ExcelTable" + (++_tableCounter);
                var table = dataRange.CreateTable(tableName);
                table.Theme = XLTableTheme.TableStyleMedium2;
            }
            else if (autoFilter)
            {
                // ClosedXML 0.105 的默认接口方法：对当前已用区域启用筛选
                sheet.SetAutoFilter();
            }
        }

        private ExcelSheetAttribute? GetSheetAttribute()
        {
            // UseTable/AutoFilter 无法经 options 区分"显式设置"与"默认值"，故直接从主类型读取特性。
            return _currentRowType?.GetCustomAttributes(typeof(ExcelSheetAttribute), true)
                       .FirstOrDefault() as ExcelSheetAttribute;
        }

        private Type? _currentRowType;

        private void ApplyColumnSettings(
            IXLWorksheet sheet,
            List<LeafColumnInfo> leaves,
            int dataStart,
            int dataEnd,
            int headerRow)
        {
            for (int i = 0; i < leaves.Count; i++)
                ApplyOneColumnSetting(sheet, i + 1, leaves[i].Column, dataStart, dataEnd, headerRow);
        }

        private void ApplyOneColumnSetting(
            IXLWorksheet sheet,
            int colNumber,
            ColumnPlan leaf,
            int dataStart,
            int dataEnd,
            int headerRow)
        {
            var column = sheet.Column(colNumber);

            if (leaf.ForceText)
                column.Style.NumberFormat.Format = "@";
            else if (!string.IsNullOrEmpty(leaf.Format))
                column.Style.NumberFormat.Format = leaf.Format;
            else if (IsDateType(leaf.ValueType) && !string.IsNullOrEmpty(_style.DateTimeFormat))
                column.Style.DateFormat.Format = _style.DateTimeFormat;
            else if (IsNumericType(leaf.ValueType) && !string.IsNullOrEmpty(_style.NumberFormat))
                column.Style.NumberFormat.Format = _style.NumberFormat;

            if (leaf.WrapText || _style.WrapText)
            {
                column.Style.Alignment.WrapText = true;
                column.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            }

            if (leaf.FixedWidth.HasValue)
            {
                column.Width = leaf.FixedWidth.Value;
                return;
            }

            if (_style.AutoFitColumns)
                ApplyAutoFitWidth(sheet, colNumber, dataStart, dataEnd, headerRow);
        }

        /// <summary>
        /// 自适应列宽：分别评估数据区与叶子表头行，取较宽的结果。
        /// 表头文本（尤其是中文）常常比数据更宽，只按数据区评估会让表头显示不全；
        /// 只取叶子表头行是为了避免跨列合并的上层表头把首列撑宽。
        /// </summary>
        private void ApplyAutoFitWidth(
            IXLWorksheet sheet,
            int colNumber,
            int dataStart,
            int dataEnd,
            int headerRow)
        {
            // 以默认列宽为下限，内容为空时保持原样
            double width = sheet.Column(colNumber).Width;

            if (dataStart <= dataEnd)
            {
                double w = MeasureRangeWidth(sheet, colNumber, dataStart, dataEnd, _style.FontSize);
                if (w > width)
                    width = w;
            }

            if (headerRow > 0)
            {
                double? headerSize = _style.HeaderFontSize ?? _style.FontSize;
                double w = MeasureRangeWidth(sheet, colNumber, headerRow, headerRow, headerSize);
                if (w > width)
                    width = w;
            }

            if (_style.MinColumnWidth.HasValue && width < _style.MinColumnWidth.Value)
                width = _style.MinColumnWidth.Value;
            if (_style.MaxColumnWidth.HasValue && width > _style.MaxColumnWidth.Value)
                width = _style.MaxColumnWidth.Value;

            sheet.Column(colNumber).Width = width;
        }

        /// <summary>扫描一段行范围，返回其中所需的最大列宽</summary>
        private static double MeasureRangeWidth(
            IXLWorksheet sheet,
            int colNumber,
            int firstRow,
            int lastRow,
            double? fontSize)
        {
            int scanEnd = lastRow - firstRow + 1 > ColumnWidthEstimator.MaxScanRows
                ? firstRow + ColumnWidthEstimator.MaxScanRows - 1
                : lastRow;

            double max = 0;

            for (int row = firstRow; row <= scanEnd; row++)
            {
                var cell = sheet.Cell(row, colNumber);

                // 跨列合并的文本由多列共同容纳，不能作为单列宽度的依据
                if (cell.IsMerged())
                    continue;

                double w = ColumnWidthEstimator.ToColumnWidth(cell.Value.ToString(), fontSize);
                if (w > max)
                    max = w;
            }

            return max;
        }

        private void ApplyHyperlinks(
            IXLWorksheet sheet,
            List<LeafColumnInfo> leaves,
            int dataStart,
            List<SourceRowInfo> sourceInfos)
        {
            var hyperlinkCols = leaves
                .Select((l, i) => (Leaf: l.Column, Col: i + 1))
                .Where(x => x.Leaf.AsHyperlink)
                .ToList();

            if (hyperlinkCols.Count == 0)
                return;

            int offset = 0;
            foreach (var info in sourceInfos)
            {
                for (int r = 0; r < info.Count; r++)
                {
                    int excelRow = info.FirstExcelRow + r;
                    foreach (var (leaf, col) in hyperlinkCols)
                    {
                        var cell = sheet.Cell(excelRow, col);
                        string url = cell.GetString();
                        if (url.Length > 0)
                        {
                            cell.SetHyperlink(new XLHyperlink(url));
                            cell.Style.Font.FontColor = XLColor.Blue;
                            cell.Style.Font.Underline = XLFontUnderlineValues.Single;
                        }
                    }
                }
                offset++;
            }
        }

        private void ApplyFreeze(IXLWorksheet sheet, int headerDepth)
        {
            int rows = _style.FreezeTopRow ? headerDepth : 0;
            int cols = _style.FreezeFirstColumn ? 1 : 0;
            if (rows > 0 || cols > 0)
                sheet.SheetView.Freeze(rows, cols);
        }

        #endregion

        #region Pictures

        /// <summary>
        /// 写入图片：先处理固定位置图片（Logo/水印），再按行处理被标记为图片的列。
        /// </summary>
        /// <remarks>
        /// 必须在 InsertData 之后调用，否则行高/列宽的自适应拿不到最终布局。
        /// 图片写入只在 DOM 引擎（ClosedXML）下可用，流式引擎由 EngineFactory 自动降级。
        /// </remarks>
        private void ApplyPictures(
            IXLWorksheet sheet,
            DocumentPart part,
            List<LeafColumnInfo> leaves,
            int dataStart,
            List<SourceRowInfo> sourceInfos)
        {
            // 1) 固定位置图片
            if (part.Sheet.Pictures.Count > 0)
            {
                foreach (var pic in part.Sheet.Pictures)
                {
                    if (pic is null) continue;
                    _ct.ThrowIfCancellationRequested();
                    // 名称需在同一 Sheet 内唯一，加前缀避免与图片列生成的名字撞车
                    InsertOnePicture(sheet, pic.ResolveBytes(), pic.Row, pic.Column,
                        pic.Options,
                        pic.Name ?? ("fixed_pic_" + pic.Row + "_" + pic.Column),
                        pic.FilePath);
                }
            }

            // 2) 图片列：每个数据行一张
            var imageCols = leaves
                .Select((l, i) => (Leaf: l.Column, Col: i + 1))
                .Where(x => x.Leaf.AsImage)
                .ToList();
            if (imageCols.Count == 0)
                return;

            foreach (var info in sourceInfos)
            {
                for (int r = 0; r < info.Count; r++)
                {
                    _ct.ThrowIfCancellationRequested();
                    int excelRow = info.FirstExcelRow + r;

                    foreach (var col in imageCols)
                    {
                        object? raw = info.Raw[col.Leaf.LeafIndex];
                        byte[]? bytes = ResolveValueBytes(raw, col.Leaf.Image);
                        InsertOnePicture(sheet, bytes, excelRow, col.Col,
                            col.Leaf.Image,
                            "col_pic_" + excelRow + "_" + col.Col,
                            raw as string);
                    }
                }
            }
        }

        /// <summary>把一行图片列的值解析为图片字节；无法解析返回 null</summary>
        private static byte[]? ResolveValueBytes(object? value, ExcelImageOptions? options)
        {
            switch (value)
            {
                case null:
                    return null;

                case byte[] bytes:
                    return bytes.Length == 0 ? null : bytes;

                case string path when path.Length > 0:
                    string full = ResolveImagePath(path, options?.BaseDirectory);
                    if (full.Length == 0 || !System.IO.File.Exists(full))
                        return null;
                    byte[] data;
                    try { data = System.IO.File.ReadAllBytes(full); }
                    catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException ||
                                               ex is ArgumentException || ex is NotSupportedException)
                    { return null; }
                    return data.Length == 0 ? null : data;

                case System.IO.Stream stream:
                    try
                    {
                        using (var buffer = new System.IO.MemoryStream())
                        {
                            stream.CopyTo(buffer);
                            return buffer.Length == 0 ? null : buffer.ToArray();
                        }
                    }
                    catch (Exception ex) when (ex is System.IO.IOException || ex is NotSupportedException ||
                                               ex is ObjectDisposedException)
                    { return null; }

                default:
                    return null;
            }
        }

        /// <summary>
        /// 把可能是相对路径的图片地址解析为全路径。
        /// 基准目录优先级：显式 BaseDirectory → 程序所在目录（AppContext.BaseDirectory）。
        /// </summary>
        /// <remarks>
        /// 刻意不使用 <see cref="Environment.CurrentDirectory"/>：当前工作目录会被
        /// 快捷方式起始位置、OpenFileDialog/FolderBrowserDialog 等操作改动，
        /// 是本项目此前 ".\res\green.png 找不到" 一类问题的根因。
        /// </remarks>
        private static string ResolveImagePath(string path, string? baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            if (System.IO.Path.IsPathRooted(path))
                return path;

            // 说明：netstandard2.0 / net472 的参考程序集未给 string.IsNullOrWhiteSpace 标注
            // [NotNullWhen(false)]，编译器无法自动收窄空值，这里显式合并空值以消除告警。
            string baseDir = baseDirectory ?? string.Empty;
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = AppContext.BaseDirectory ?? string.Empty;

            return string.IsNullOrEmpty(baseDir) ? path : System.IO.Path.Combine(baseDir, path);
        }

        /// <summary>插入单张图片到指定单元格</summary>
        private void InsertOnePicture(
            IXLWorksheet sheet,
            byte[]? bytes,
            int row,
            int col,
            ExcelImageOptions? options,
            string? name,
            string? sourceText)
        {
            var opt = options ?? ExcelImageOptions.Default;

            if (bytes is null || bytes.Length == 0 ||
                !ImageHeaderReader.TryParse(bytes, out var kind, out int srcW, out int srcH))
            {
                HandleMissingPicture(sheet, row, col, opt, sourceText);
                return;
            }

            double width = opt.Width;
            double height = opt.Height;

            if (opt.SizeMode == ImageSizeMode.Fit && width > 0 && height > 0)
            {
                // 等比缩放：取较小的缩放比，保证图片完整放入且不变形
                double scale = Math.Min(width / (double)srcW, height / (double)srcH);
                width = srcW * scale;
                height = srcH * scale;
            }
            else
            {
                if (width <= 0) width = srcW;
                if (height <= 0) height = srcH;
            }

            try
            {
                using (var stream = new System.IO.MemoryStream(bytes, writable: false))
                {
                    var picture = sheet.AddPicture(
                        stream,
                        MapPictureFormat(kind),
                        name ?? ("pic_" + row.ToString(CultureInfo.InvariantCulture) + "_" + col.ToString(CultureInfo.InvariantCulture)));

                    picture.MoveTo(sheet.Cell(row, col));
                    // ClosedXML 的 WithSize 以像素为单位且只接受整型，四舍五入避免累积误差
                    picture.WithSize((int)Math.Round(width), (int)Math.Round(height));
                    // MoveAndSize：随单元格移动并缩放，行高列宽变化时图片跟着变
                    picture.Placement = opt.Placement == ImagePlacement.Floating
                        ? XLPicturePlacement.FreeFloating
                        : XLPicturePlacement.MoveAndSize;
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is ArgumentException ||
                                       ex is NotSupportedException || ex is InvalidOperationException)
            {
                HandleMissingPicture(sheet, row, col, opt, sourceText);
                return;
            }

            // Excel 不会因插入图片自动撑开单元格，不调整会盖住下方内容
            if (opt.AdjustRowHeight)
            {
                double need = PixelsToPoints(height + opt.RowPadding);
                if (sheet.Row(row).Height < need)
                    sheet.Row(row).Height = need;
            }

            if (opt.AdjustColumnWidth)
            {
                double need = PixelsToColumnWidth(width);
                if (sheet.Column(col).Width < need)
                    sheet.Column(col).Width = need;
            }
        }

        /// <summary>按 <see cref="MissingImagePolicy"/> 处理缺失图片</summary>
        private static void HandleMissingPicture(
            IXLWorksheet sheet, int row, int col, ExcelImageOptions opt, string? sourceText)
        {
            switch (opt.OnMissing)
            {
                case MissingImagePolicy.Throw:
                    string detail = string.IsNullOrEmpty(sourceText) ? string.Empty : "（来源：" + sourceText + "）";
                    throw new ExcelMappingException(
                        $"插入图片失败：第 {row} 行第 {col} 列的图片缺失或格式不支持{detail}。");

                case MissingImagePolicy.WriteSourceText:
                    // 写回来源（通常是文件路径），让报表读者一眼看出"这张图本该是什么"
                    sheet.Cell(row, col).Value = sourceText ?? string.Empty;
                    break;

                default:
                    break;
            }
        }

        /// <summary>像素 → 磅（Excel 行高单位）。96 DPI 下 1pt = 4/3 px</summary>
        private static double PixelsToPoints(double pixels) => pixels * 0.75;

        /// <summary>像素 → 字符宽度（Excel 列宽单位）。经验值：1 字符宽 ≈ 7 像素</summary>
        private static double PixelsToColumnWidth(double pixels) => pixels / 7.0;

        /// <summary>把内部图片格式映射到 ClosedXML 的图片格式枚举</summary>
        private static XLPictureFormat MapPictureFormat(ImageFormatKind kind)
        {
            switch (kind)
            {
                case ImageFormatKind.Png: return XLPictureFormat.Png;
                case ImageFormatKind.Jpeg: return XLPictureFormat.Jpeg;
                case ImageFormatKind.Gif: return XLPictureFormat.Gif;
                case ImageFormatKind.Bmp: return XLPictureFormat.Bmp;
                case ImageFormatKind.Tiff: return XLPictureFormat.Tiff;
                default: return XLPictureFormat.Unknown;
            }
        }

        #endregion

        #region Conditional Rules

        private void ApplyConditionalRules(
            IXLWorksheet sheet,
            DocumentPart part,
            List<LeafColumnInfo> leaves,
            int dataStart,
            int dataEnd,
            List<SourceRowInfo> sourceInfos)
        {
            // 行级规则（任意谓词 → 静态样式）
            if (part.RowRules.Count > 0)
            {
                foreach (var info in sourceInfos)
                {
                    foreach (var rule in part.RowRules)
                    {
                        bool hit = false;
                        try { hit = rule.Predicate!(info.Source); }
                        catch { hit = false; }

                        if (hit)
                        {
                            for (int r = 0; r < info.Count; r++)
                            {
                                var rowRange = sheet.Range(info.FirstExcelRow + r, 1,
                                    info.FirstExcelRow + r, leaves.Count);
                                ApplyRuleStyle(rowRange.Style, rule);
                            }
                        }
                    }
                }
            }

            // 列级规则
            foreach (var leafInfo in leaves)
            {
                var leaf = leafInfo.Column;
                if (leaf.Rules.Count == 0)
                    continue;

                int col = leaf.LeafIndex + 1;
                string colLetter = sheet.Column(col).ColumnLetter();
                var range = sheet.Range(dataStart, col, dataEnd, col);

                foreach (var rule in leaf.Rules)
                {
                    if (_options.PreferNativeConditionalFormat && rule.IsNativeTranslatable)
                    {
                        string formula = BuildNativeFormula(rule, colLetter, dataStart);
                        var cf = range.AddConditionalFormat().WhenIsTrue(formula);
                        ApplyRuleStyle(cf, rule);
                    }
                    else
                    {
                        // 服务端逐行求值
                        foreach (var info in sourceInfos)
                        {
                            object? value = info.Raw[leaf.LeafIndex];
                            bool hit = EvaluateRule(rule, value);
                            if (!hit)
                                continue;

                            // 摊平场景下列值可能多份；静态样式只加给首个映射行
                            var cell = sheet.Cell(info.FirstExcelRow, col);
                            ApplyRuleStyle(cell.Style, rule);
                        }
                    }
                }
            }
        }

        private static string BuildNativeFormula(ConditionalRule rule, string colLetter, int firstRow)
        {
            string cellRef = $"{colLetter}{firstRow}";
            string v1 = NumberLiteral(rule.Value);

            return rule.Operator switch
            {
                ConditionOperator.GreaterThan => $"{cellRef}>{v1}",
                ConditionOperator.GreaterOrEqual => $"{cellRef}>={v1}",
                ConditionOperator.LessThan => $"{cellRef}<{v1}",
                ConditionOperator.LessOrEqual => $"{cellRef}<={v1}",
                ConditionOperator.Equal => $"{cellRef}={v1}",
                ConditionOperator.NotEqual => $"{cellRef}<>{v1}",
                ConditionOperator.Between =>
                    $"AND({cellRef}>={v1},{cellRef}<={NumberLiteral(rule.Value2)})",
                _ => $"{cellRef}>{v1}"
            };
        }

        private static string NumberLiteral(double d) =>
            d.ToString("0.##############", CultureInfo.InvariantCulture);

        /// <summary>服务端求值（用于不可原生翻译/显式关闭原生时）</summary>
        private static bool EvaluateRule(ConditionalRule rule, object? value)
        {
            if (value is null)
                return false;

            if (rule.Operator == ConditionOperator.Contains)
            {
                return value.ToString()?
                    .IndexOf(rule.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0;
            }

            if (!TryConvertDouble(value, out double d))
                return false;

            return rule.Operator switch
            {
                ConditionOperator.GreaterThan => d > rule.Value,
                ConditionOperator.GreaterOrEqual => d >= rule.Value,
                ConditionOperator.LessThan => d < rule.Value,
                ConditionOperator.LessOrEqual => d <= rule.Value,
                ConditionOperator.Equal => d == rule.Value,
                ConditionOperator.NotEqual => d != rule.Value,
                ConditionOperator.Between => d >= rule.Value && d <= rule.Value2,
                _ => false
            };
        }

        private static bool TryConvertDouble(object value, out double d)
        {
            try
            {
                d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                d = 0;
                return false;
            }
        }

        private static void ApplyRuleStyle(IXLStyle style, ConditionalRule rule)
        {
            if (rule.Bold)
                style.Font.Bold = true;
            if (!string.IsNullOrEmpty(rule.FontColor))
                SafeSetColor(c => style.Font.FontColor = c, rule.FontColor);
            if (!string.IsNullOrEmpty(rule.FillColor))
                SafeSetColor(c => style.Fill.BackgroundColor = c, rule.FillColor);
        }

        private static void ApplyRuleStyle(IXLConditionalFormat cf, ConditionalRule rule)
        {
            var style = cf.Style;
            if (rule.Bold)
                style.Font.Bold = true;
            if (!string.IsNullOrEmpty(rule.FontColor))
                SafeSetColor(c => style.Font.FontColor = c, rule.FontColor);
            if (!string.IsNullOrEmpty(rule.FillColor))
                SafeSetColor(c => style.Fill.BackgroundColor = c, rule.FillColor);
        }

        #endregion

        #region Helpers

        private static List<object> ExtractItems(object? value)
        {
            var result = new List<object>();
            if (value is IEnumerable en && value is not string)
            {
                foreach (var item in en)
                    result.Add(item);
            }
            return result;
        }

        private static bool IsDateType(Type type)
        {
            Type t = Nullable.GetUnderlyingType(type) ?? type;
            return t == typeof(DateTime) || t == typeof(DateTimeOffset);
        }

        private static bool IsNumericType(Type type)
        {
            Type t = Nullable.GetUnderlyingType(type) ?? type;
            if (t.IsPrimitive && t != typeof(char) && t != typeof(bool))
                return true;
            return t == typeof(decimal);
        }

        private void AdvanceProgress(int rows)
        {
            _rowsDone += rows;
            _progress?.Report(_rowsDone);
        }

        /// <summary>源行与写入 Excel 行的映射（用于服务端规则/超链接）</summary>
        private sealed class SourceRowInfo
        {
            public object Source = null!;
            public int FirstExcelRow;
            public int Count;
            public object?[] Raw = Array.Empty<object?>();
        }

        /// <summary>子 Sheet 收集桶</summary>
        private sealed class ChildBucket
        {
            public string Name = "";
            public Type ElementType = typeof(object);
            public List<ChildRow> Rows = new List<ChildRow>();
        }

        /// <summary>子 Sheet 一行：父关联值 + 元素对象</summary>
        private struct ChildRow
        {
            public readonly string ParentRef;
            public readonly object Item;

            public ChildRow(string parentRef, object item)
            {
                ParentRef = parentRef;
                Item = item;
            }
        }

        #endregion
    }
}
