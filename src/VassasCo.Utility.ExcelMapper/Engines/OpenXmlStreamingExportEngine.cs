// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// OpenXML SAX 流式引擎：使用 OpenXmlWriter 前向写入，内存占用低，可导出百万级数据。
    /// 规则统一在服务端求值写为静态样式（不生成原生条件格式，这是流式模式的文档化限制）。
    /// </summary>
    internal sealed class OpenXmlStreamingExportEngine : IExcelExportEngine
    {
        private SpreadsheetDocument _package = null!;
        private WorkbookPart _workbookPart = null!;
        private OxmlStyleTable _styles = null!;
        private SheetNameRegistry _names = null!;
        private ExcelStyle _style = null!;
        private ExcelExportOptions _options = null!;
        private CancellationToken _ct;
        private IProgress<int>? _progress;

        private readonly List<SheetRecord> _sheetRecords = new List<SheetRecord>();
        private uint _nextSheetId = 1;
        private int _totalRows;

        #region Entry

        public ExportEngineKind Kind => ExportEngineKind.OpenXmlStreaming;

        public EngineExportStats Export(
            ExportDocument document,
            string? filePath,
            System.IO.Stream? stream,
            ExcelStyle style,
            ExcelExportOptions options,
            CancellationToken cancellationToken,
            IProgress<int>? progress)
        {
            _styles = new OxmlStyleTable();
            _names = new SheetNameRegistry();
            _style = style;
            _options = options;
            _ct = cancellationToken;
            _progress = progress;
            _totalRows = 0;

            _package = filePath != null
                ? SpreadsheetDocument.Create(filePath, SpreadsheetDocumentType.Workbook)
                : SpreadsheetDocument.Create(EnsureWritableStream(stream), SpreadsheetDocumentType.Workbook);

            _workbookPart = _package.AddWorkbookPart();

            ProcessPart(document.Primary);
            foreach (var part in document.AdditionalParts)
            {
                _ct.ThrowIfCancellationRequested();
                ProcessPart(part);
            }

            // 样式表
            var stylesPart = _workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = _styles.BuildStylesheet();

            WriteWorkbook();
            _package.Dispose();

            return new EngineExportStats
            {
                RowsWritten = _totalRows,
                SheetsWritten = _sheetRecords.Count
            };
        }

        private static System.IO.Stream EnsureWritableStream(System.IO.Stream? stream)
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite)
                throw new ArgumentException("输出流不可写。", nameof(stream));
            return stream;
        }

        private void WriteWorkbook()
        {
            var writer = OpenXmlWriter.Create(_workbookPart);
            writer.WriteStartDocument();
            writer.WriteStartElement(new Workbook());

            var sheets = new Sheets();
            foreach (var rec in _sheetRecords)
            {
                sheets.Append(new Sheet
                {
                    Name = rec.Name,
                    SheetId = rec.SheetId,
                    Id = rec.RelationshipId
                });
            }
            writer.WriteElement(sheets);
            writer.WriteEndElement();
            writer.Close();
        }

        #endregion

        #region Part Processing

        private void ProcessPart(DocumentPart part)
        {
            var leaves = ColumnPlan.CollectAll(part.Sheet.Columns);
            string baseName = part.Sheet.RequestedName ?? part.Sheet.RowType.Name;

            if (leaves.Count == 0)
            {
                AddWorksheet(baseName, (w, wsPart) =>
                {
                    WriteBareCell(w, 1, 1, "(无可用属性)", null);
                });
                return;
            }

            int headerDepth = leaves.Max(l => l.Column.Depth) + 1;
            int pageSize = RowPaginator.ClampPageSize(_options.PageSize, headerDepth);
            var ctx = PrepareContext(part, leaves);

            bool showFilter = (part.Sheet.RowType
                .GetCustomAttributes(typeof(ExcelSheetAttribute), true)
                .FirstOrDefault() as ExcelSheetAttribute)?.AutoFilter ?? false;

            var buckets = new Dictionary<ColumnPlan, ChildBucket>();
            LeafColumnInfo? flattenLeaf = _options.ArrayRender == ArrayRenderMode.FlattenInPlace
                ? leaves.FirstOrDefault(l => l.Column.IsCollection)
                : null;

            int pageIndex = 0;
            int rowsDone = 0;
            foreach (var page in RowPaginator.Paginate(part.Sheet.Rows, pageSize))
            {
                _ct.ThrowIfCancellationRequested();
                string pageName = pageIndex == 0
                    ? baseName
                    : string.Format(CultureInfo.InvariantCulture, _options.SheetPageNameFormat, baseName, pageIndex + 1);

                var finalRows = BuildFinalRows(part, leaves, page, buckets, flattenLeaf?.Column, pageName);
                WritePrimaryWorksheet(pageName, leaves, finalRows, headerDepth, ctx, showFilter);
                rowsDone += page.Count;
                _totalRows += page.Count;
                _progress?.Report(rowsDone);
                pageIndex++;
            }

            if (pageIndex == 0)
            {
                WriteHeaderOnlySheet(baseName, leaves, headerDepth, ctx);
            }

            FlushChildBuckets(buckets);
        }

        #endregion

        #region Style Context

        private PageContext PrepareContext(DocumentPart part, List<LeafColumnInfo> leaves)
        {
            var ctx = new PageContext
            {
                RootColumns = part.Sheet.Columns
            };
            ctx.SetRowRules(part.RowRules);

            ctx.HeaderStyle = _styles.Compose(
                    _style.HeaderBold, _style.HeaderFontColor, _style.HeaderBackgroundColor,
                    numberFormat: null, border: _style.ShowBorder, center: true, wrap: false);
            ctx.DataStyles = new int[leaves.Count];
            ctx.RuleStyles = new int[leaves.Count][];
            ctx.RowRuleStyles = new int[part.RowRules.Count];

            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i].Column;
                string? format = ResolveColumnFormat(leaf);
                bool wrap = leaf.WrapText || _style.WrapText;

                ctx.DataStyles[i] = _styles.Compose(false, null, null, format,
                    _style.ShowBorder, center: false, wrap: wrap);

                ctx.RuleStyles[i] = leaf.Rules.Select(rule => _styles.Compose(
                    rule.Bold, rule.FontColor, rule.FillColor, format,
                    _style.ShowBorder, center: false, wrap: wrap)).ToArray();
            }

            for (int i = 0; i < part.RowRules.Count; i++)
            {
                var rule = part.RowRules[i];
                ctx.RowRuleStyles[i] = _styles.Compose(
                    rule.Bold, rule.FontColor, rule.FillColor, numberFormat: null,
                    border: _style.ShowBorder, center: false, wrap: false);
            }

            return ctx;
        }

        private string? ResolveColumnFormat(ColumnPlan leaf)
        {
            if (leaf.ForceText)
                return "@";
            if (!string.IsNullOrEmpty(leaf.Format))
                return leaf.Format;
            if (IsDateType(leaf.ValueType))
                return _style.DateTimeFormat;
            if (IsNumericType(leaf.ValueType))
                return _style.NumberFormat;
            return null;
        }

        #endregion

        #region Final Row Building

        private List<FinalRow> BuildFinalRows(
            DocumentPart part,
            List<LeafColumnInfo> leaves,
            List<object> sourceRows,
            Dictionary<ColumnPlan, ChildBucket> buckets,
            ColumnPlan? flattenLeaf,
            string parentSheetName)
        {
            var result = new List<FinalRow>();

            foreach (var source in sourceRows)
            {
                _ct.ThrowIfCancellationRequested();

                var raw = new object?[leaves.Count];
                for (int i = 0; i < leaves.Count; i++)
                {
                    var leaf = leaves[i].Column;
                    var resolved = ValueServices.Resolve(source, leaf);
                    raw[i] = ValueServices.ApplyConverter(resolved, leaf);
                }

                if (flattenLeaf != null)
                {
                    int flattenIdx = flattenLeaf.LeafIndex;
                    var items = ExtractItems(raw[flattenIdx]);

                    if (items.Count == 0)
                    {
                        var clone = (object?[])raw.Clone();
                        clone[flattenIdx] = null;
                        result.Add(new FinalRow(source, ToFinalValues(clone, leaves)));
                    }
                    else
                    {
                        foreach (var item in items)
                        {
                            var clone = (object?[])raw.Clone();
                            clone[flattenIdx] = item;
                            result.Add(new FinalRow(source, ToFinalValues(clone, leaves)));
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < leaves.Count; i++)
                    {
                        var leaf = leaves[i].Column;
                        if (leaf.IsCollection)
                        {
                            raw[i] = HandleCollectionLeaf(part, leaf, raw[i], source,
                                buckets, parentSheetName);
                        }
                    }

                    result.Add(new FinalRow(source, ToFinalValues(raw, leaves)));
                }
            }

            return result;
        }

        private FinalValue[] ToFinalValues(object?[] raw, List<LeafColumnInfo> leaves)
        {
            var values = new FinalValue[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                var value = raw[i];
                var leaf = leaves[i].Column;

                if (leaf.IsCycleMarker)
                {
                    values[i] = FinalValue.Text("(循环引用)");
                }
                else if (value is null)
                {
                    values[i] = FinalValue.Empty;
                }
                else if (leaf.IsCollection && _options.ArrayRender != ArrayRenderMode.FlattenInPlace)
                {
                    values[i] = FinalValue.Text((string)value);
                }
                else if (ValueServices.IsTextValue(value, leaf, _options))
                {
                    values[i] = FinalValue.Text(ValueServices.ToText(value, leaf, _style, _options));
                }
                else if (ValueServices.TryGetDateTime(value, out var dt))
                {
                    if (dt.Year < 1900)
                        values[i] = FinalValue.Text(ValueServices.ToText(value, leaf, _style, _options));
                    else
                        values[i] = FinalValue.Date(dt);
                }
                else
                {
                    try { values[i] = FinalValue.Number(ValueServices.ToDouble(value)); }
                    catch { values[i] = FinalValue.Text(ValueServices.ToText(value, leaf, _style, _options)); }
                }
            }

            return values;
        }

        private string HandleCollectionLeaf(
            DocumentPart part,
            ColumnPlan leaf,
            object? value,
            object source,
            Dictionary<ColumnPlan, ChildBucket> buckets,
            string parentSheetName)
        {
            if (_options.ArrayRender == ArrayRenderMode.Json)
                return ValueServices.ToJsonText(value, _options);

            var items = ExtractItems(value);
            int count = items.Count;

            if (_options.ArrayRender == ArrayRenderMode.PlaceholderOnly)
                return $"[{count} items]";

            bool canBuildChild = leaf.CollectionElementType != null &&
                                 leaf.CollectionElementType != typeof(object);
            if (!canBuildChild)
                return $"[{count} items]";

            if (count == 0)
                return "[0 items]";

            if (!buckets.TryGetValue(leaf, out var bucket))
            {
                // 与 DOM 引擎一致：子 Sheet 以集合属性名命名（如 "Addresses"）
                string childBase = _names.Register(leaf.Name);
                bucket = new ChildBucket
                {
                    Name = childBase,
                    ElementType = leaf.CollectionElementType!
                };
                buckets[leaf] = bucket;
            }

            // 子 Sheet 第一列填父行身份值（如 Id），用于关联回父行
            string parentRef = ValueServices.GetIdentityString(source, part.Sheet.RowType, 0, _style);
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

                // 子表样式
                int parentHeaderStyle = _styles.Compose(_style.HeaderBold, _style.HeaderFontColor,
                    _style.HeaderBackgroundColor, null, _style.ShowBorder, true, false);
                int parentDataStyle = _styles.Compose(false, null, null, "@", _style.ShowBorder, false, false);

                var childDataStyles = new int[childLeaves.Count];
                for (int i = 0; i < childLeaves.Count; i++)
                {
                    var leaf = childLeaves[i].Column;
                    childDataStyles[i] = _styles.Compose(false, null, null,
                        ResolveColumnFormat(leaf), _style.ShowBorder, false,
                        leaf.WrapText || _style.WrapText);
                }

                var childRows = bucket.Rows.Select(r => (object)r).ToList();
                int pageIndex = 0;

                foreach (var page in RowPaginator.Paginate(childRows, pageSize))
                {
                    _ct.ThrowIfCancellationRequested();
                    string pageName = pageIndex == 0
                        ? bucket.Name
                        : string.Format(CultureInfo.InvariantCulture, _options.SheetPageNameFormat, bucket.Name, pageIndex + 1);

                    WriteChildWorksheet(page.Cast<ChildRow>().ToList(), childLeaves, pageName,
                        parentHeaderStyle, parentDataStyle, childDataStyles);
                    pageIndex++;
                }
            }
        }

        #endregion

        #region Worksheet Writing

        private void WritePrimaryWorksheet(
            string name,
            List<LeafColumnInfo> leaves,
            List<FinalRow> rows,
            int headerDepth,
            PageContext ctx,
            bool showFilter)
        {
            AddWorksheet(name, (writer, wsPart) =>
            {
                WriteFreezePanes(writer, headerDepth, leaves.Count);
                WriteColumns(writer, leaves, rows, ctx);

                // SheetData
                writer.WriteStartElement(new SheetData());

                var headerCells = new List<(int Row, int StartCol, int EndCol, string Text, bool IsLeaf)>();
                int hi = 0;
                foreach (var root in ctx.RootColumns)
                    hi = CollectHeaderCells(root, hi, 0, headerCells);

                // 表头按行输出
                for (int r = 0; r < headerDepth; r++)
                {
                    writer.WriteStartElement(new Row { RowIndex = (uint)(r + 1) });
                    foreach (var hc in headerCells.Where(h => h.Row == r))
                    {
                        WriteTextCell(writer, r + 1, hc.StartCol + 1, hc.Text, (uint)ctx.HeaderStyle);
                    }
                    writer.WriteEndElement();
                }

                // 数据行
                for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
                {
                    int excelRow = headerDepth + 1 + rowIdx;
                    writer.WriteStartElement(new Row { RowIndex = (uint)excelRow });
                    var fv = rows[rowIdx].Values;

                    for (int c = 0; c < fv.Length; c++)
                    {
                        uint style = (uint)ctx.DataStyles[c];

                        // 规则求值覆盖
                        var leaf = leaves[c].Column;
                        object? rawValue = GetRawForRule(rows[rowIdx], leaf);
                        for (int ri = 0; ri < leaf.Rules.Count; ri++)
                        {
                            if (EvaluateRule(leaf.Rules[ri], rawValue))
                            {
                                style = (uint)ctx.RuleStyles[c][ri];
                                break;
                            }
                        }

                        // 行级规则
                        for (int ri = 0; ri < ctx.RowRuleCount; ri++)
                        {
                            bool hit = false;
                            try { hit = ctx.GetRowRule(ri).Predicate!(rows[rowIdx].Source); }
                            catch { hit = false; }
                            if (hit)
                            {
                                style = (uint)ctx.RowRuleStyles[ri];
                                break;
                            }
                        }

                        WriteFinalCell(writer, excelRow, c + 1, fv[c], style);
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement(); // SheetData

                // 合并单元格
                var merges = BuildMerges(headerCells, headerDepth, leaves.Count);
                if (merges.Count > 0)
                {
                    var mergeCells = new MergeCells();
                    foreach (var m in merges)
                        mergeCells.Append(new MergeCell { Reference = m });
                    writer.WriteElement(mergeCells);
                }

                if (showFilter && rows.Count > 0)
                {
                    string range = $"{CellRef.ColumnName(1)}{headerDepth}:{CellRef.ColumnName(leaves.Count)}{headerDepth + rows.Count}";
                    writer.WriteElement(new AutoFilter { Reference = range });
                }
            });
        }

        private void WriteChildWorksheet(
            List<ChildRow> rows,
            List<LeafColumnInfo> childLeaves,
            string name,
            int parentHeaderStyle,
            int parentDataStyle,
            int[] childDataStyles)
        {
            string parentHeader = ValueServices.GetParentColumnHeader(
                rows.Count > 0 ? rows[0].Item.GetType() : typeof(object), _style);

            AddWorksheet(name, (writer, wsPart) =>
            {
                writer.WriteStartElement(new SheetData());

                // 单行表头
                writer.WriteStartElement(new Row { RowIndex = 1 });
                WriteTextCell(writer, 1, 1, parentHeader, (uint)parentHeaderStyle);
                for (int i = 0; i < childLeaves.Count; i++)
                    WriteTextCell(writer, 1, i + 2, childLeaves[i].Column.DisplayName, (uint)parentHeaderStyle);
                writer.WriteEndElement();

                for (int r = 0; r < rows.Count; r++)
                {
                    int excelRow = r + 2;
                    writer.WriteStartElement(new Row { RowIndex = (uint)excelRow });
                    WriteTextCell(writer, excelRow, 1, rows[r].ParentRef, (uint)parentDataStyle);

                    for (int i = 0; i < childLeaves.Count; i++)
                    {
                        var leaf = childLeaves[i].Column;
                        var value = TypeMetadata.TryGetValue(leaf.Property!, rows[r].Item);
                        value = ValueServices.ApplyConverter(value, leaf);

                        var final = SingleFinal(value, leaf);
                        WriteFinalCell(writer, excelRow, i + 2, final, (uint)childDataStyles[i]);
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            });
        }

        private FinalValue SingleFinal(object? value, ColumnPlan leaf)
        {
            if (value is null)
                return FinalValue.Empty;
            if (ValueServices.IsTextValue(value, leaf, _options))
                return FinalValue.Text(ValueServices.ToText(value, leaf, _style, _options));
            if (ValueServices.TryGetDateTime(value, out var dt))
                return dt.Year < 1900
                    ? FinalValue.Text(ValueServices.ToText(value, leaf, _style, _options))
                    : FinalValue.Date(dt);
            return FinalValue.Number(ValueServices.ToDouble(value));
        }

        private void WriteHeaderOnlySheet(
            string name,
            List<LeafColumnInfo> leaves,
            int headerDepth,
            PageContext ctx)
        {
            AddWorksheet(name, (writer, wsPart) =>
            {
                WriteFreezePanes(writer, headerDepth, leaves.Count);
                WriteColumns(writer, leaves, null, ctx);

                writer.WriteStartElement(new SheetData());
                var headerCells = new List<(int Row, int StartCol, int EndCol, string Text, bool IsLeaf)>();
                int hi = 0;
                foreach (var root in ctx.RootColumns)
                    hi = CollectHeaderCells(root, hi, 0, headerCells);

                for (int r = 0; r < headerDepth; r++)
                {
                    writer.WriteStartElement(new Row { RowIndex = (uint)(r + 1) });
                    foreach (var hc in headerCells.Where(h => h.Row == r))
                        WriteTextCell(writer, r + 1, hc.StartCol + 1, hc.Text, (uint)ctx.HeaderStyle);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();

                var merges = BuildMerges(headerCells, headerDepth, leaves.Count);
                if (merges.Count > 0)
                {
                    var mergeCells = new MergeCells();
                    foreach (var m in merges)
                        mergeCells.Append(new MergeCell { Reference = m });
                    writer.WriteElement(mergeCells);
                }
            });
        }

        #endregion

        #region Low-level OpenXml Helpers

        private void AddWorksheet(string name, Action<OpenXmlWriter, WorksheetPart> write)
        {
            string finalName = _names.Register(name);
            var wsPart = _workbookPart.AddNewPart<WorksheetPart>();
            string relId = _workbookPart.GetIdOfPart(wsPart);

            var writer = OpenXmlWriter.Create(wsPart);
            writer.WriteStartDocument();
            writer.WriteStartElement(new Worksheet());
            write(writer, wsPart);
            writer.WriteEndElement();
            writer.Close();

            _sheetRecords.Add(new SheetRecord(finalName, _nextSheetId++, relId));
        }

        private void WriteBareCell(OpenXmlWriter writer, int row, int col, string text, uint? style)
        {
            writer.WriteStartElement(new SheetData());
            writer.WriteStartElement(new Row { RowIndex = (uint)row });
            WriteTextCell(writer, row, col, text, style);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private void WriteFreezePanes(OpenXmlWriter writer, int headerDepth, int columnCount)
        {
            int rows = _style.FreezeTopRow ? headerDepth : 0;
            int cols = _style.FreezeFirstColumn ? 1 : 0;
            if (rows == 0 && cols == 0)
                return;

            var sheetViews = new SheetViews();
            var sheetView = new SheetView { WorkbookViewId = 0U };

            Pane pane;
            Selection selection;

            if (rows > 0 && cols > 0)
            {
                string top = $"{CellRef.ColumnName(cols + 1)}{rows + 1}";
                pane = new Pane
                {
                    VerticalSplit = (double)rows,
                    HorizontalSplit = (double)cols,
                    TopLeftCell = top,
                    ActivePane = PaneValues.BottomRight,
                    State = PaneStateValues.Frozen
                };
                selection = new Selection { Pane = PaneValues.BottomRight, ActiveCell = top, SequenceOfReferences = new ListValue<StringValue> { InnerText = top } };
            }
            else if (rows > 0)
            {
                string top = $"A{rows + 1}";
                pane = new Pane
                {
                    VerticalSplit = (double)rows,
                    TopLeftCell = top,
                    ActivePane = PaneValues.BottomLeft,
                    State = PaneStateValues.Frozen
                };
                selection = new Selection { Pane = PaneValues.BottomLeft, ActiveCell = top, SequenceOfReferences = new ListValue<StringValue> { InnerText = top } };
            }
            else
            {
                string top = $"{CellRef.ColumnName(cols + 1)}1";
                pane = new Pane
                {
                    HorizontalSplit = (double)cols,
                    TopLeftCell = top,
                    ActivePane = PaneValues.TopRight,
                    State = PaneStateValues.Frozen
                };
                selection = new Selection { Pane = PaneValues.TopRight, ActiveCell = top, SequenceOfReferences = new ListValue<StringValue> { InnerText = top } };
            }

            sheetView.Append(pane);
            sheetView.Append(selection);
            sheetViews.Append(sheetView);
            writer.WriteElement(sheetViews);
        }

        private void WriteColumns(
            OpenXmlWriter writer,
            List<LeafColumnInfo> leaves,
            List<FinalRow>? rows,
            PageContext ctx)
        {
            var columns = new Columns();

            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i].Column;
                double width;

                if (leaf.FixedWidth.HasValue)
                {
                    width = leaf.FixedWidth.Value;
                }
                else
                {
                    width = EstimateWidth(leaf.DisplayName, rows, i);
                    if (_style.MinColumnWidth.HasValue && width < _style.MinColumnWidth.Value)
                        width = _style.MinColumnWidth.Value;
                    if (_style.MaxColumnWidth.HasValue && width > _style.MaxColumnWidth.Value)
                        width = _style.MaxColumnWidth.Value;
                }

                columns.Append(new Column
                {
                    Min = (uint)(i + 1),
                    Max = (uint)(i + 1),
                    Width = width,
                    CustomWidth = true,
                    Style = (uint)ctx.DataStyles[i]
                });
            }

            writer.WriteElement(columns);
        }

        private static double EstimateWidth(string header, List<FinalRow>? rows, int col)
        {
            double width = DisplayWidth(header) + 2;

            if (rows != null)
            {
                int scan = Math.Min(rows.Count, 200);
                for (int r = 0; r < scan; r++)
                {
                    double w = rows[r].Values[col] switch
                    {
                        { Kind: FinalKind.Text } tv => DisplayWidth(tv.TextValue) + 2,
                        { Kind: FinalKind.Date } => 12,
                        { Kind: FinalKind.Number } nv =>
                            nv.NumberValue.ToString("0.##", CultureInfo.InvariantCulture).Length + 2,
                        _ => 0
                    };
                    if (w > width) width = w;
                }
            }

            return Math.Max(width, 8);
        }

        private static int DisplayWidth(string text)
        {
            int width = 0;
            foreach (char c in text)
                width += c > 127 ? 2 : 1;
            return width;
        }

        private static void WriteTextCell(OpenXmlWriter writer, int row, int col, string text, uint? style)
        {
            string reference = $"{CellRef.ColumnName(col)}{row}";
            var cell = new Cell
            {
                CellReference = reference,
                StyleIndex = style,
                DataType = CellValues.InlineString
            };

            var inline = new InlineString();
            inline.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            cell.AppendChild(inline);

            writer.WriteElement(cell);
        }

        private static void WriteFinalCell(OpenXmlWriter writer, int row, int col, FinalValue value, uint style)
        {
            string reference = $"{CellRef.ColumnName(col)}{row}";

            switch (value.Kind)
            {
                case FinalKind.Text:
                    var tcell = new Cell
                    {
                        CellReference = reference,
                        StyleIndex = style,
                        DataType = CellValues.InlineString
                    };
                    var inline = new InlineString();
                    inline.AppendChild(new Text(value.TextValue) { Space = SpaceProcessingModeValues.Preserve });
                    tcell.AppendChild(inline);
                    writer.WriteElement(tcell);
                    break;

                case FinalKind.Number:
                    writer.WriteElement(new Cell
                    {
                        CellReference = reference,
                        StyleIndex = style,
                        CellValue = new CellValue(value.NumberValue.ToString("R", CultureInfo.InvariantCulture))
                    });
                    break;

                case FinalKind.Date:
                    writer.WriteElement(new Cell
                    {
                        CellReference = reference,
                        StyleIndex = style,
                        CellValue = new CellValue(value.DateValue.ToOADate().ToString(CultureInfo.InvariantCulture))
                    });
                    break;

                default:
                    // 空单元格也输出样式引用（保证边框/区域完整）
                    writer.WriteElement(new Cell { CellReference = reference, StyleIndex = style });
                    break;
            }
        }

        private static List<string> BuildMerges(
            List<(int Row, int StartCol, int EndCol, string Text, bool IsLeaf)> headerCells,
            int headerDepth,
            int leafCount)
        {
            var merges = new List<string>();

            foreach (var hc in headerCells)
            {
                if (hc.StartCol != hc.EndCol)
                {
                    merges.Add($"{CellRef.ColumnName(hc.StartCol + 1)}{hc.Row + 1}:" +
                               $"{CellRef.ColumnName(hc.EndCol + 1)}{hc.Row + 1}");
                }

                if (hc.IsLeaf && headerDepth > hc.Row + 1)
                {
                    merges.Add($"{CellRef.ColumnName(hc.StartCol + 1)}{hc.Row + 1}:" +
                               $"{CellRef.ColumnName(hc.EndCol + 1)}{headerDepth}");
                }
            }

            return merges;
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

        private static bool EvaluateRule(ConditionalRule rule, object? value)
        {
            if (value is null)
                return false;

            if (rule.Operator == ConditionOperator.Contains)
                return value.ToString()?
                    .IndexOf(rule.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0;

            double d;
            try { d = Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return false; }

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

        private static object? GetRawForRule(FinalRow row, ColumnPlan leaf)
        {
            // 规则基于原始属性值；FinalRow 保留了源对象，按属性链重新取值成本低
            var resolved = ValueServices.Resolve(row.Source, leaf);
            return ValueServices.ApplyConverter(resolved, leaf);
        }

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

        #endregion

        #region Internal Types

        private sealed class PageContext
        {
            public int HeaderStyle;
            public int[] DataStyles = Array.Empty<int>();
            public int[][] RuleStyles = Array.Empty<int[]>();
            public int[] RowRuleStyles = Array.Empty<int>();
            public List<ColumnPlan> RootColumns = new List<ColumnPlan>();
            public int RowRuleCount;

            public ConditionalRule GetRowRule(int index) => _rowRules[index];
            private List<ConditionalRule> _rowRules = new List<ConditionalRule>();

            public void SetRowRules(List<ConditionalRule> rules)
            {
                _rowRules = rules;
                RowRuleCount = rules.Count;
            }
        }

        private enum FinalKind { Empty, Text, Number, Date }

        private readonly struct FinalValue
        {
            public readonly FinalKind Kind;
            public readonly string TextValue;
            public readonly double NumberValue;
            public readonly DateTime DateValue;

            private FinalValue(FinalKind kind, string text, double number, DateTime date)
            {
                Kind = kind;
                TextValue = text;
                NumberValue = number;
                DateValue = date;
            }

            public static FinalValue Empty => new FinalValue(FinalKind.Empty, "", 0, default);
            public static FinalValue Text(string text) => new FinalValue(FinalKind.Text, text, 0, default);
            public static FinalValue Number(double number) => new FinalValue(FinalKind.Number, "", number, default);
            public static FinalValue Date(DateTime date) => new FinalValue(FinalKind.Date, "", 0, date);
        }

        private sealed class FinalRow
        {
            public readonly object Source;
            public readonly FinalValue[] Values;

            public FinalRow(object source, FinalValue[] values)
            {
                Source = source;
                Values = values;
            }
        }

        private sealed class ChildBucket
        {
            public string Name = "";
            public Type ElementType = typeof(object);
            public List<ChildRow> Rows = new List<ChildRow>();
        }

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

        private readonly struct SheetRecord
        {
            public readonly string Name;
            public readonly uint SheetId;
            public readonly string RelationshipId;

            public SheetRecord(string name, uint sheetId, string relationshipId)
            {
                Name = name;
                SheetId = sheetId;
                RelationshipId = relationshipId;
            }
        }

        #endregion
    }
}
