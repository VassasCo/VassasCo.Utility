using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace VassasCo.Utility
{
    #region ExcelDisplayAttribute

    /// <summary>Excel 列标题显示名称</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ExcelDisplayAttribute : Attribute
    {
        /// <summary>显示名称</summary>
        public string Name { get; }

        /// <param name="name">Excel 表头中显示的列名</param>
        public ExcelDisplayAttribute(string name)
        {
            Name = name;
        }
    }

    #endregion

    #region ExcelStyle

    /// <summary>Excel 样式配置</summary>
    public class ExcelStyle
    {
        /// <summary>表头加粗，默认 true</summary>
        public bool HeaderBold { get; set; } = true;
        /// <summary>表头背景色（十六进制），默认 "#4472C4"</summary>
        public string HeaderBackgroundColor { get; set; } = "#4472C4";
        /// <summary>表头字体颜色（十六进制），默认 "#FFFFFF"</summary>
        public string HeaderFontColor { get; set; } = "#FFFFFF";
        /// <summary>自动调整列宽，默认 true</summary>
        public bool AutoFitColumns { get; set; } = true;
        /// <summary>显示边框，默认 true</summary>
        public bool ShowBorder { get; set; } = true;
        /// <summary>冻结表头行，默认 true</summary>
        public bool FreezeTopRow { get; set; } = true;
        /// <summary>冻结首列，默认 false</summary>
        public bool FreezeFirstColumn { get; set; }
        /// <summary>数字格式化字符串（如 "N2"），null 表示不设置</summary>
        public string? NumberFormat { get; set; }
        /// <summary>日期时间格式化字符串（如 "yyyy-MM-dd HH:mm:ss"），null 表示不设置</summary>
        public string? DateTimeFormat { get; set; }
        /// <summary>子 Sheet 第一列（关联父行）取值的属性名，null 则自动查找 Id/Uid/Vid 等</summary>
        public string? ChildSheetParentProperty { get; set; }
        /// <summary>默认样式</summary>
        public static ExcelStyle Default => new ExcelStyle();
    }

    #endregion

    #region ExcelMapper

    /// <summary>Excel 映射器 — 将对象列表/字典导出为 .xlsx 文件</summary>
    public static class ExcelMapper
    {
        /// <summary>创建 Fluent Builder</summary>
        public static ExcelMapperBuilder<T> Build<T>(IEnumerable<T> data)
        {
            return new ExcelMapperBuilder<T>(data);
        }

        /// <summary>快捷导出列表到文件</summary>
        public static void ToFile<T>(IEnumerable<T> data, string filePath, string? sheetName = null)
        {
            Build(data).WithSheetName(sheetName ?? typeof(T).Name).ToFile(filePath);
        }

        /// <summary>快捷导出列表到流</summary>
        public static void ToStream<T>(IEnumerable<T> data, Stream stream, string? sheetName = null)
        {
            Build(data).WithSheetName(sheetName ?? typeof(T).Name).ToStream(stream);
        }

        /// <summary>快捷导出 Dictionary 到文件（自动判断横/竖表）</summary>
        public static void DictionaryToFile(IDictionary dictionary, string filePath, string? sheetName = null)
        {
            var mapper = new ExcelMapperBuilder<object>(null!);
            mapper.WithSheetName(sheetName ?? "Dictionary");
            ExcelGenerator.WriteDictionary(dictionary, filePath, null, mapper._sheetName, mapper._style);
        }

        /// <summary>快捷导出 Dictionary 到流</summary>
        public static void DictionaryToStream(IDictionary dictionary, Stream stream, string? sheetName = null)
        {
            var mapper = new ExcelMapperBuilder<object>(null!);
            mapper.WithSheetName(sheetName ?? "Dictionary");
            ExcelGenerator.WriteDictionary(dictionary, null, stream, mapper._sheetName, mapper._style);
        }
    }

    #endregion

    #region Fluent Builder

    /// <summary>Fluent Builder — 链式配置并导出 Excel</summary>
    public class ExcelMapperBuilder<T>
    {
        internal readonly IEnumerable<T> _data;
        internal string _sheetName = typeof(T).Name;
        internal ExcelStyle _style = ExcelStyle.Default;

        internal ExcelMapperBuilder(IEnumerable<T> data)
        {
            _data = data;
        }

        /// <summary>设置 Sheet 名称</summary>
        public ExcelMapperBuilder<T> WithSheetName(string sheetName)
        {
            _sheetName = string.IsNullOrWhiteSpace(sheetName) ? typeof(T).Name : sheetName;
            return this;
        }

        /// <summary>设置样式</summary>
        public ExcelMapperBuilder<T> WithStyle(ExcelStyle style)
        {
            _style = style ?? ExcelStyle.Default;
            return this;
        }

        /// <summary>导出到文件</summary>
        public void ToFile(string filePath)
        {
            ExcelGenerator.WriteList(_data, filePath, null, _sheetName, _style);
        }

        /// <summary>导出到流</summary>
        public void ToStream(Stream stream)
        {
            ExcelGenerator.WriteList(_data, null, stream, _sheetName, _style);
        }
    }

    #endregion

    #region Column Definitions

    /// <summary>列定义基类 — 构建属性树用于合并表头</summary>
    internal abstract class ColumnDef
    {
        /// <summary>属性名</summary>
        public string Name;
        /// <summary>显示名称（优先使用 Description，无则用属性名）</summary>
        public string DisplayName;
        /// <summary>反射 PropertyInfo</summary>
        public PropertyInfo? PropertyInfo;
        /// <summary>该节点下的叶子列数量</summary>
        public abstract int LeafCount { get; }
        /// <summary>递归收集所有叶子列到扁平列表</summary>
        public abstract void CollectLeaves(List<LeafColumn> leaves, string parentPath, int depth);

        protected ColumnDef(string name, string displayName, PropertyInfo? prop)
        {
            Name = name;
            DisplayName = displayName;
            PropertyInfo = prop;
        }
    }

    /// <summary>叶子列 — 最终对应数据表中的一个单元格列（无子列）</summary>
    internal class LeafColumn : ColumnDef
    {
        /// <summary>在叶子列表中的索引（填充后）</summary>
        public int LeafIndex;
        /// <summary>完整属性路径（如 "Customer.Name"）</summary>
        public string FullPath;
        /// <summary>是否为数组/List 类型</summary>
        public bool IsArray;
        /// <summary>数组元素类型（仅 IsArray=true 时有效）</summary>
        public Type? ElementType;
        /// <summary>在属性树中的层级深度</summary>
        public int Depth;

        public override int LeafCount => 1;

        public LeafColumn(string name, string displayName, PropertyInfo? prop, bool isArray, Type? elementType)
            : base(name, displayName, prop)
        {
            IsArray = isArray;
            ElementType = elementType;
            FullPath = name;
        }

        public override void CollectLeaves(List<LeafColumn> leaves, string parentPath, int depth)
        {
            FullPath = string.IsNullOrEmpty(parentPath) ? Name : parentPath + "." + Name;
            Depth = depth;
            LeafIndex = leaves.Count;
            leaves.Add(this);
        }
    }

    /// <summary>嵌套列 — 包含子列的非叶子节点（如嵌套类）</summary>
    internal class NestedColumn : ColumnDef
    {
        public List<ColumnDef> Children;
        private int _leafCount = -1;

        public override int LeafCount
        {
            get
            {
                if (_leafCount < 0)
                    _leafCount = Children.Sum(c => c.LeafCount);
                return _leafCount;
            }
        }

        public NestedColumn(string name, string displayName, PropertyInfo? prop, List<ColumnDef> children)
            : base(name, displayName, prop)
        {
            Children = children;
        }

        public override void CollectLeaves(List<LeafColumn> leaves, string parentPath, int depth)
        {
            var childPath = string.IsNullOrEmpty(parentPath) ? Name : parentPath + "." + Name;
            foreach (var child in Children)
            {
                child.CollectLeaves(leaves, childPath, depth + 1);
            }
        }
    }

    #endregion

    #region Excel Generation Engine

    /// <summary>内部 Excel 生成逻辑</summary>
    internal static class ExcelGenerator
    {
        private const int DictWideThreshold = 10;

        #region List Writing (Export)

        /// <summary>将对象列表写入 Excel</summary>
        internal static void WriteList<T>(IEnumerable<T> data, string? filePath, Stream? stream, string sheetName, ExcelStyle style)
        {
            var workbook = new ClosedXML.Excel.XLWorkbook();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dataList = data.ToList();

            if (dataList.Count == 0)
            {
                var emptySheet = workbook.AddWorksheet(RegisterSheetName(usedNames, sheetName));
                WriteHeadersOnly(typeof(T), emptySheet, style);
            }
            else
            {
                WriteListToSheet(dataList, typeof(T), workbook, sheetName, style, usedNames);
            }

            SaveWorkbook(workbook, filePath, stream);
        }

        /// <summary>将列表写入一个 Sheet，同时处理数组属性生成子 Sheet</summary>
        private static void WriteListToSheet(IList dataList, Type elementType, ClosedXML.Excel.XLWorkbook workbook, string sheetName, ExcelStyle style, HashSet<string> usedNames)
        {
            var sheet = workbook.AddWorksheet(RegisterSheetName(usedNames, sheetName));

            var rootColumns = BuildColumnTree(elementType);
            var leaves = new List<LeafColumn>();
            foreach (var col in rootColumns)
                col.CollectLeaves(leaves, "", 0);

            if (leaves.Count == 0)
            {
                sheet.Cell(1, 1).Value = "(无可用属性)";
                return;
            }

            int maxDepth = leaves.Max(l => l.Depth) + 1;

            WriteMergedHeaders(sheet, rootColumns, leaves, maxDepth, style);

            var arrayCollector = new Dictionary<LeafColumn, (string RegisteredName, List<(string ParentRef, IList Items)> Rows)>();

            int dataStartRow = maxDepth + 1;
            for (int rowIdx = 0; rowIdx < dataList.Count; rowIdx++)
            {
                var rowObj = dataList[rowIdx];
                int excelRow = dataStartRow + rowIdx;

                for (int leafIdx = 0; leafIdx < leaves.Count; leafIdx++)
                {
                    var leaf = leaves[leafIdx];
                    object? value = ResolveLeafValue(rowObj, leaf);
                    int excelCol = leafIdx + 1;

                    if (leaf.IsArray && value is IEnumerable enumerable && !(value is string))
                    {
                        var list = CastToIList(enumerable);
                        int count = list?.Count ?? 0;

                        if (!arrayCollector.ContainsKey(leaf))
                        {
                            string childName = RegisterSheetName(usedNames, sheetName + "." + leaf.DisplayName);
                            arrayCollector[leaf] = (childName, new List<(string, IList)>());
                        }
                        string registeredName = arrayCollector[leaf].RegisteredName;

                        sheet.Cell(excelRow, excelCol).Value = $"[{count} items → Sheet \"{registeredName}\"]";

                        if (count > 0 && leaf.ElementType != null && list != null)
                        {
                            string parentRef = GetIdentityValue(rowObj, elementType, rowIdx, style);
                            arrayCollector[leaf].Rows.Add((parentRef, list));
                        }
                    }
                    else
                    {
                        SetCellValue(sheet.Cell(excelRow, excelCol), value, leaf.PropertyInfo?.PropertyType, style);
                    }
                }
            }

            foreach (var kv in arrayCollector)
            {
                var leaf = kv.Key;
                var (registeredName, allRows) = kv.Value;

                var consolidated = new List<(string ParentRef, object Item)>();
                foreach (var (parentRef, items) in allRows)
                {
                    foreach (var item in items)
                        consolidated.Add((parentRef, item));
                }

                if (consolidated.Count > 0 && leaf.ElementType != null)
                {
                    string parentColumnHeader = GetParentColumnHeader(elementType, style);
                    WriteConsolidatedChildSheet(consolidated, leaf.ElementType, workbook, registeredName, parentColumnHeader, style);
                }
            }

            if (dataList.Count > 0)
                ApplyDataAreaStyle(sheet, dataStartRow, dataStartRow + dataList.Count - 1, leaves.Count, style);

            if (style.AutoFitColumns)
            {
                for (int c = 1; c <= leaves.Count; c++)
                    sheet.Column(c).AdjustToContents();
            }

            if (style.FreezeTopRow)
                sheet.SheetView.FreezeRows(maxDepth);
            if (style.FreezeFirstColumn)
                sheet.SheetView.FreezeColumns(1);
        }

        /// <summary>仅写表头（空数据时）</summary>
        private static void WriteHeadersOnly(Type elementType, ClosedXML.Excel.IXLWorksheet sheet, ExcelStyle style)
        {
            var rootColumns = BuildColumnTree(elementType);
            var leaves = new List<LeafColumn>();
            foreach (var col in rootColumns)
                col.CollectLeaves(leaves, "", 0);

            if (leaves.Count == 0)
            {
                sheet.Cell(1, 1).Value = "(无可用属性)";
                return;
            }

            int maxDepth = leaves.Max(l => l.Depth) + 1;
            WriteMergedHeaders(sheet, rootColumns, leaves, maxDepth, style);
        }

        /// <summary>写入合并单元格父子表头</summary>
        private static void WriteMergedHeaders(ClosedXML.Excel.IXLWorksheet sheet, List<ColumnDef> rootColumns, List<LeafColumn> leaves, int maxDepth, ExcelStyle style)
        {
            var headerCells = new List<(int Row, int StartCol, int EndCol, string Text, bool IsLeaf)>();

            int leafIdx = 0;
            foreach (var col in rootColumns)
                leafIdx = CollectHeaderCells(col, leafIdx, 0, headerCells);

            foreach (var hc in headerCells)
            {
                int excelRow = hc.Row + 1;
                int excelStartCol = hc.StartCol + 1;
                int excelEndCol = hc.EndCol + 1;

                var cell = sheet.Cell(excelRow, excelStartCol);
                cell.Value = hc.Text;

                if (excelStartCol != excelEndCol)
                    sheet.Range(excelRow, excelStartCol, excelRow, excelEndCol).Merge();

                if (hc.IsLeaf)
                {
                    int rowSpanEnd = maxDepth;
                    if (rowSpanEnd > excelRow)
                        sheet.Range(excelRow, excelStartCol, rowSpanEnd, excelEndCol).Merge();
                }

                ApplyHeaderStyleWithoutBorder(cell, style);
            }

            if (style.ShowBorder && leaves.Count > 0)
            {
                var headerRange = sheet.Range(1, 1, maxDepth, leaves.Count);
                headerRange.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                headerRange.Style.Border.InsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            }
        }

        /// <summary>递归收集表头单元格信息，返回下一个可用的叶子列索引</summary>
        private static int CollectHeaderCells(ColumnDef column, int startLeafIndex, int depth,
            List<(int Row, int StartCol, int EndCol, string Text, bool IsLeaf)> result)
        {
            if (column is LeafColumn)
            {
                result.Add((depth, startLeafIndex, startLeafIndex, column.DisplayName, true));
                return startLeafIndex + 1;
            }
            else if (column is NestedColumn nested)
            {
                int childStart = startLeafIndex;
                int currentLeafIndex = startLeafIndex;

                for (int i = 0; i < nested.Children.Count; i++)
                    currentLeafIndex = CollectHeaderCells(nested.Children[i], currentLeafIndex, depth + 1, result);

                result.Add((depth, childStart, currentLeafIndex - 1, column.DisplayName, false));
                return currentLeafIndex;
            }

            return startLeafIndex;
        }

        #endregion

        #region Child Sheet Writing

        /// <summary>将所有行的数组元素合并写入一个独立 Sheet</summary>
        private static void WriteConsolidatedChildSheet(List<(string ParentRef, object Item)> consolidated, Type elementType,
            ClosedXML.Excel.XLWorkbook workbook, string sheetName, string parentColumnHeader, ExcelStyle style)
        {
            var sheet = workbook.AddWorksheet(SanitizeSheetName(sheetName));

            var rootColumns = BuildColumnTree(elementType);
            var leaves = new List<LeafColumn>();
            foreach (var col in rootColumns)
                col.CollectLeaves(leaves, "", 0);

            int totalCols = leaves.Count + 1;
            int headerRow = 1;

            var parentHeaderCell = sheet.Cell(headerRow, 1);
            parentHeaderCell.Value = parentColumnHeader;
            ApplyHeaderStyle(parentHeaderCell, style);

            for (int i = 0; i < leaves.Count; i++)
            {
                var cell = sheet.Cell(headerRow, i + 2);
                cell.Value = leaves[i].DisplayName;
                ApplyHeaderStyle(cell, style);
            }

            for (int rowIdx = 0; rowIdx < consolidated.Count; rowIdx++)
            {
                var (parentRef, rowObj) = consolidated[rowIdx];
                int excelRow = headerRow + 1 + rowIdx;

                sheet.Cell(excelRow, 1).Value = parentRef;

                for (int leafIdx = 0; leafIdx < leaves.Count; leafIdx++)
                {
                    var leaf = leaves[leafIdx];
                    object? value = GetPropertyValue(rowObj, leaf.PropertyInfo);
                    SetCellValue(sheet.Cell(excelRow, leafIdx + 2), value, leaf.PropertyInfo?.PropertyType, style);
                }
            }

            if (consolidated.Count > 0)
                ApplyDataAreaStyle(sheet, headerRow + 1, headerRow + consolidated.Count, totalCols, style);

            if (style.AutoFitColumns)
            {
                for (int c = 1; c <= totalCols; c++)
                    sheet.Column(c).AdjustToContents();
            }

            if (style.FreezeTopRow)
                sheet.SheetView.FreezeRows(1);
        }

        #endregion

        #region Dictionary Writing

        /// <summary>将 Dictionary 写入 Excel</summary>
        internal static void WriteDictionary(IDictionary dictionary, string? filePath, Stream? stream, string sheetName, ExcelStyle style)
        {
            var workbook = new ClosedXML.Excel.XLWorkbook();

            if (dictionary.Count <= DictWideThreshold)
                WriteDictionaryAsWide(dictionary, workbook, sheetName, style);
            else
                WriteDictionaryAsVertical(dictionary, workbook, sheetName, style);

            SaveWorkbook(workbook, filePath, stream);
        }

        /// <summary>Dictionary 作为宽表（键少时）</summary>
        private static void WriteDictionaryAsWide(IDictionary dictionary, ClosedXML.Excel.XLWorkbook workbook, string sheetName, ExcelStyle style)
        {
            var sheet = workbook.AddWorksheet(SanitizeSheetName(sheetName));

            var keys = new List<object>();
            var values = new List<object?>();

            foreach (DictionaryEntry entry in dictionary)
            {
                keys.Add(entry.Key);
                values.Add(entry.Value);
            }

            for (int i = 0; i < keys.Count; i++)
            {
                var cell = sheet.Cell(1, i + 1);
                cell.Value = keys[i]?.ToString() ?? "";
                ApplyHeaderStyle(cell, style);
            }

            for (int i = 0; i < values.Count; i++)
            {
                var cell = sheet.Cell(2, i + 1);
                SetCellValue(cell, values[i], null, style);
            }

            ApplyDataAreaStyle(sheet, 2, 2, keys.Count, style);

            if (style.AutoFitColumns)
            {
                for (int c = 1; c <= keys.Count; c++)
                    sheet.Column(c).AdjustToContents();
            }

            if (style.FreezeTopRow)
                sheet.SheetView.FreezeRows(1);
        }

        /// <summary>Dictionary 作为竖表（键多时）</summary>
        private static void WriteDictionaryAsVertical(IDictionary dictionary, ClosedXML.Excel.XLWorkbook workbook, string sheetName, ExcelStyle style)
        {
            var sheet = workbook.AddWorksheet(SanitizeSheetName(sheetName));

            var headerKey = sheet.Cell(1, 1);
            headerKey.Value = "Key";
            ApplyHeaderStyle(headerKey, style);

            var headerValue = sheet.Cell(1, 2);
            headerValue.Value = "Value";
            ApplyHeaderStyle(headerValue, style);

            int row = 2;
            foreach (DictionaryEntry entry in dictionary)
            {
                sheet.Cell(row, 1).Value = entry.Key?.ToString() ?? "";
                SetCellValue(sheet.Cell(row, 2), entry.Value, null, style);
                row++;
            }

            if (dictionary.Count > 0)
                ApplyDataAreaStyle(sheet, 2, row - 1, 2, style);

            if (style.AutoFitColumns)
            {
                sheet.Column(1).AdjustToContents();
                sheet.Column(2).AdjustToContents();
            }

            if (style.FreezeTopRow)
                sheet.SheetView.FreezeRows(1);
        }

        #endregion

        #region Type Helpers

        private static bool IsSimpleType(Type type)
        {
            if (type.IsPrimitive || type.IsEnum)
                return true;

            return type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
                || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid)
                || type == typeof(Uri) || type == typeof(Version)
                || Nullable.GetUnderlyingType(type) != null;
        }

        private static bool IsEnumerableType(Type type)
        {
            if (type == typeof(string)) return false;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return true;

            foreach (var iface in type.GetInterfaces())
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return true;

            if (type.IsArray)
                return true;

            return false;
        }

        private static Type? GetElementType(Type type)
        {
            if (type.IsGenericType)
            {
                var gtype = type.GetGenericTypeDefinition();
                if (gtype == typeof(IEnumerable<>) || gtype == typeof(List<>) || gtype == typeof(IList<>) || gtype == typeof(ICollection<>))
                    return type.GetGenericArguments()[0];

                foreach (var iface in type.GetInterfaces())
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                        return iface.GetGenericArguments()[0];
            }

            if (type.IsArray)
                return type.GetElementType();

            return null;
        }

        private static List<PropertyInfo> GetExportableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .ToList();
        }

        /// <summary>从反射属性获取显示名称（优先 [ExcelDisplay]）</summary>
        private static string GetDisplayName(PropertyInfo prop)
        {
            var displayAttr = prop.GetCustomAttribute<ExcelDisplayAttribute>();
            if (displayAttr != null && !string.IsNullOrEmpty(displayAttr.Name))
                return displayAttr.Name!;

            return prop.Name;
        }

        private static List<ColumnDef> BuildColumnTree(Type type)
        {
            var columns = new List<ColumnDef>();
            var properties = GetExportableProperties(type);

            foreach (var prop in properties)
            {
                var propType = prop.PropertyType;
                var displayName = GetDisplayName(prop);

                if (IsEnumerableType(propType))
                {
                    var elementType = GetElementType(propType);
                    columns.Add(new LeafColumn(prop.Name, displayName, prop, isArray: true, elementType));
                }
                else if (IsSimpleType(propType))
                {
                    columns.Add(new LeafColumn(prop.Name, displayName, prop, isArray: false, null));
                }
                else
                {
                    var children = BuildColumnTree(propType);
                    if (children.Count > 0)
                        columns.Add(new NestedColumn(prop.Name, displayName, prop, children));
                    else
                        columns.Add(new LeafColumn(prop.Name, displayName, prop, isArray: false, null));
                }
            }

            return columns;
        }

        private static object? ResolveLeafValue(object? obj, LeafColumn leaf)
        {
            if (obj == null)
                return null;

            string[] parts = leaf.FullPath.Split('.');
            object? current = obj;

            for (int i = 0; i < parts.Length && current != null; i++)
            {
                var prop = current.GetType().GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanRead)
                    return null;
                current = prop.GetValue(current, null);
            }

            return current;
        }

        private static object? GetPropertyValue(object? obj, PropertyInfo? prop)
        {
            if (obj == null || prop == null || !prop.CanRead)
                return null;

            try { return prop.GetValue(obj, null); }
            catch { return null; }
        }

        private static string GetIdentityValue(object? obj, Type type, int rowIndex, ExcelStyle style)
        {
            if (obj == null)
                return $"Row{rowIndex + 1}";

            PropertyInfo? idProp = null;

            if (!string.IsNullOrEmpty(style.ChildSheetParentProperty))
            {
                idProp = type.GetProperty(style.ChildSheetParentProperty, BindingFlags.Public | BindingFlags.Instance);
            }
            else
            {
                // 自动查找 Id/Uid/Vid 等（忽略大小写）
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    string name = prop.Name.ToLowerInvariant();
                    if (name == "id" || name.EndsWith("id") && name.Length > 2)
                    {
                        idProp = prop;
                        break;
                    }
                }
            }

            if (idProp != null && idProp.CanRead)
            {
                try
                {
                    var val = idProp.GetValue(obj, null);
                    return val?.ToString() ?? $"Row{rowIndex + 1}";
                }
                catch { }
            }

            return $"Row{rowIndex + 1}";
        }

        /// <summary>获取子 Sheet 第一列的列标题（[ExcelDisplay] → 属性名）</summary>
        private static string GetParentColumnHeader(Type parentType, ExcelStyle style)
        {
            PropertyInfo? prop = null;

            if (!string.IsNullOrEmpty(style.ChildSheetParentProperty))
            {
                prop = parentType.GetProperty(style.ChildSheetParentProperty, BindingFlags.Public | BindingFlags.Instance);
            }
            else
            {
                foreach (var p in parentType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    string name = p.Name.ToLowerInvariant();
                    if (name == "id" || (name.EndsWith("id") && name.Length > 2))
                    {
                        prop = p;
                        break;
                    }
                }
            }

            if (prop != null)
            {
                var displayAttr = prop.GetCustomAttribute<ExcelDisplayAttribute>();
                if (displayAttr != null && !string.IsNullOrEmpty(displayAttr.Name))
                    return displayAttr.Name!;
                return prop.Name;
            }

            return style.ChildSheetParentProperty ?? "Id";
        }

        private static IList? CastToIList(IEnumerable enumerable)
        {
            if (enumerable is IList list)
                return list;

            var result = new List<object>();
            foreach (var item in enumerable)
                result.Add(item);
            return result;
        }

        #endregion

        #region Cell Value Writing

        private static void SetCellValue(ClosedXML.Excel.IXLCell cell, object? value, Type? propType, ExcelStyle style)
        {
            if (value == null)
            {
                cell.Value = "";
                return;
            }

            Type actualType = propType ?? value.GetType();
            Type underlyingType = Nullable.GetUnderlyingType(actualType) ?? actualType;

            if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
            {
                if (value is DateTime dt)
                    cell.Value = dt;
                else if (value is DateTimeOffset dto)
                    cell.Value = dto.DateTime;
                else
                    cell.Value = value.ToString();

                if (!string.IsNullOrEmpty(style.DateTimeFormat))
                    cell.Style.DateFormat.Format = style.DateTimeFormat;
            }
            else if (underlyingType.IsEnum)
            {
                cell.Value = value.ToString();
            }
            else if (underlyingType.IsPrimitive || underlyingType == typeof(decimal))
            {
                cell.Value = Convert.ToDouble(value);

                if (!string.IsNullOrEmpty(style.NumberFormat))
                    cell.Style.NumberFormat.Format = style.NumberFormat;
            }
            else if (value is string s)
            {
                cell.Value = s;
            }
            else if (IsSimpleType(underlyingType))
            {
                cell.Value = value.ToString();
            }
            else
            {
                try { cell.Value = JsonSerializer.Serialize(value); }
                catch { cell.Value = value.ToString() ?? ""; }
            }
        }

        #endregion

        #region Style Application

        private static void ApplyHeaderStyle(ClosedXML.Excel.IXLCell cell, ExcelStyle style)
        {
            ApplyHeaderStyleWithoutBorder(cell, style);

            if (style.ShowBorder)
                ApplyBorder(cell);
        }

        /// <summary>应用表头样式（不含边框）</summary>
        private static void ApplyHeaderStyleWithoutBorder(ClosedXML.Excel.IXLCell cell, ExcelStyle style)
        {
            if (style.HeaderBold)
                cell.Style.Font.Bold = true;

            if (!string.IsNullOrEmpty(style.HeaderBackgroundColor))
            {
                try { cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml(style.HeaderBackgroundColor); }
                catch { }
            }

            if (!string.IsNullOrEmpty(style.HeaderFontColor))
            {
                try { cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml(style.HeaderFontColor); }
                catch { }
            }

            cell.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
        }

        private static void ApplyDataAreaStyle(ClosedXML.Excel.IXLWorksheet sheet, int startRow, int endRow, int colCount, ExcelStyle style)
        {
            if (!style.ShowBorder || colCount <= 0 || startRow > endRow)
                return;

            var range = sheet.Range(startRow, 1, endRow, colCount);
            ApplyBorderToRange(range);
        }

        private static void ApplyBorder(ClosedXML.Excel.IXLCell cell)
        {
            cell.Style.Border.LeftBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            cell.Style.Border.RightBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            cell.Style.Border.TopBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
        }

        private static void ApplyBorderToRange(ClosedXML.Excel.IXLRange range)
        {
            range.Style.Border.LeftBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            range.Style.Border.RightBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            range.Style.Border.TopBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            range.Style.Border.BottomBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
        }

        #endregion

        #region Utility Methods

        private static string RegisterSheetName(HashSet<string> usedNames, string name)
        {
            string baseName = SanitizeSheetName(name);
            string candidate = baseName;
            int suffix = 1;

            while (usedNames.Contains(candidate))
            {
                string truncated = baseName;
                string suffixStr = $"({suffix})";
                if (truncated.Length + suffixStr.Length > 31)
                    truncated = truncated.Substring(0, 31 - suffixStr.Length);
                candidate = truncated + suffixStr;
                suffix++;
            }

            usedNames.Add(candidate);
            return candidate;
        }

        private static string SanitizeSheetName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Sheet1";

            var invalid = new[] { '\\', '/', '*', '?', ':', '[', ']' };
            foreach (var ch in invalid)
                name = name.Replace(ch.ToString(), "");

            if (name.Length > 31)
                name = name.Substring(0, 31);

            name = name.Trim('\'');

            if (string.IsNullOrEmpty(name))
                return "Sheet1";

            return name;
        }

        private static void SaveWorkbook(ClosedXML.Excel.XLWorkbook workbook, string? filePath, Stream? stream)
        {
            if (filePath != null)
                workbook.SaveAs(filePath);
            else if (stream != null)
                workbook.SaveAs(stream);
        }

        #endregion
    }

    #endregion
}
