// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using ClosedXML.Excel;

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
                // 空数据：仅表头
                var emptySheet = _workbook.AddWorksheet(_names.Register(baseName));
                WriteMergedHeaders(emptySheet, part.Sheet.Columns, leaves, headerDepth);
                ApplyColumnSettings(emptySheet, leaves, 1, 0);
                ApplyFreeze(emptySheet, headerDepth);
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
            WriteMergedHeaders(sheet, part.Sheet.Columns, leaves, headerDepth);

            int dataStart = headerDepth + 1;

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

            ApplyColumnSettings(sheet, leaves, dataStart, dataEnd);
            ApplyDataArea(sheet, dataStart, dataEnd, leaves.Count, headerDepth);
            ApplyHyperlinks(sheet, leaves, dataStart, sourceInfos);
            ApplyConditionalRules(sheet, part, leaves, dataStart, dataEnd, sourceInfos);
            ApplyFreeze(sheet, headerDepth);

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
                ApplyOneColumnSetting(sheet, i + 2, childLeaves[i].Column, 2, dataEnd);

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

        private void WriteMergedHeaders(
            IXLWorksheet sheet,
            List<ColumnPlan> roots,
            List<LeafColumnInfo> leaves,
            int headerDepth)
        {
            var headerCells = new List<(int Row, int StartCol, int EndCol, string Text, bool IsLeaf)>();
            int leafIdx = 0;
            foreach (var col in roots)
                leafIdx = CollectHeaderCells(col, leafIdx, 0, headerCells);

            foreach (var hc in headerCells)
            {
                int excelRow = hc.Row + 1;
                var cell = sheet.Cell(excelRow, hc.StartCol + 1);
                cell.Value = hc.Text;

                if (hc.StartCol != hc.EndCol)
                    sheet.Range(excelRow, hc.StartCol + 1, excelRow, hc.EndCol + 1).Merge();

                if (hc.IsLeaf && headerDepth > excelRow)
                    sheet.Range(excelRow, hc.StartCol + 1, headerDepth, hc.EndCol + 1).Merge();

                ApplyHeaderStyle(cell);
            }

            if (_style.HeaderHeight.HasValue)
            {
                for (int r = 1; r <= headerDepth; r++)
                    sheet.Row(r).Height = _style.HeaderHeight.Value;
            }

            if (_style.ShowBorder && leaves.Count > 0)
            {
                var headerRange = sheet.Range(1, 1, headerDepth, leaves.Count);
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

        #region Styling

        private void ApplyHeaderStyle(IXLCell cell)
        {
            if (_style.HeaderBold)
                cell.Style.Font.Bold = true;

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
            int dataEnd)
        {
            for (int i = 0; i < leaves.Count; i++)
                ApplyOneColumnSetting(sheet, i + 1, leaves[i].Column, dataStart, dataEnd);
        }

        private void ApplyOneColumnSetting(
            IXLWorksheet sheet,
            int colNumber,
            ColumnPlan leaf,
            int dataStart,
            int dataEnd)
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

            if (_style.AutoFitColumns && dataStart <= dataEnd)
            {
                column.AdjustToContents(dataStart, dataEnd);
                if (_style.MinColumnWidth.HasValue && column.Width < _style.MinColumnWidth.Value)
                    column.Width = _style.MinColumnWidth.Value;
                if (_style.MaxColumnWidth.HasValue && column.Width > _style.MaxColumnWidth.Value)
                    column.Width = _style.MaxColumnWidth.Value;
            }
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
