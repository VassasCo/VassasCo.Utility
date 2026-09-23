// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ClosedXML.Excel;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// Excel(.xlsx) → 对象导入：首行为表头，按侵入式特性/约定/非侵入式 Fluent 映射列；
    /// 逐字段安全转换与错误收集；空白行跳过。仅支持单行表头（多层表头是导出侧能力）。
    /// </summary>
    internal static class ExcelImporter
    {
        public static ImportResult<T> ImportFile<T>(
            string filePath,
            string? sheetName,
            int? sheetIndex,
            EntityMap<T>? map,
            CancellationToken cancellationToken)
        {
            string fullPath = Guard.EnsureValidFilePath(filePath);
            if (!File.Exists(fullPath))
                throw new ExcelFileAccessException($"文件不存在：{fullPath}");

            using (var workbook = new XLWorkbook(fullPath))
            {
                var sheet = SelectSheet(workbook, sheetName, sheetIndex);
                return ImportSheet(sheet, map, cancellationToken);
            }
        }

        public static ImportResult<T> ImportStream<T>(
            Stream stream,
            string? sheetName,
            int? sheetIndex,
            EntityMap<T>? map,
            CancellationToken cancellationToken)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("输入流不可读。", nameof(stream));

            using (var workbook = new XLWorkbook(stream))
            {
                var sheet = SelectSheet(workbook, sheetName, sheetIndex);
                return ImportSheet(sheet, map, cancellationToken);
            }
        }

        private static IXLWorksheet SelectSheet(XLWorkbook workbook, string? sheetName, int? sheetIndex)
        {
            if (workbook.Worksheets.Count == 0)
                throw new ExcelImportException("工作簿中没有任何工作表。");

            IXLWorksheet? sheet;
            if (!string.IsNullOrEmpty(sheetName))
            {
                sheet = workbook.Worksheets.FirstOrDefault(w => w.Name == sheetName);
                if (sheet is null)
                    throw new ExcelImportException($"工作簿中不存在名为 \"{sheetName}\" 的工作表。");
                return sheet;
            }

            if (sheetIndex.HasValue)
            {
                int index = sheetIndex.Value;
                if (index < 0 || index >= workbook.Worksheets.Count)
                    throw new ExcelImportException($"工作表序号超出范围：{index}（共 {workbook.Worksheets.Count} 个）。");
                return workbook.Worksheets.ElementAt(index);
            }

            return workbook.Worksheets.First();
        }

        private static ImportResult<T> ImportSheet<T>(
            IXLWorksheet sheet,
            EntityMap<T>? map,
            CancellationToken cancellationToken)
        {
            var options = new ExcelExportOptions();
            var accessor = ImportAccessor<T>.Build(map, options);
            var result = new ImportResult<T>();

            var headerRow = sheet.FirstRowUsed();
            if (headerRow is null)
                return result; // 空表

            int headerRowNumber = headerRow.RowNumber();
            var lastHeaderCell = headerRow.LastCellUsed();
            int columnCount = lastHeaderCell?.Address.ColumnNumber ?? 0;
            if (columnCount == 0)
                return result;

            var headers = new string[columnCount];
            var bindings = new ColumnPlan?[columnCount];
            for (int c = 1; c <= columnCount; c++)
            {
                headers[c - 1] = headerRow.Cell(c).GetString().Trim();
                bindings[c - 1] = accessor.FindColumn(headers[c - 1]);
            }

            foreach (var warning in accessor.HeaderWarnings)
                result.Errors.Add(new ImportError(headerRowNumber, null, null, warning));

            var dataRows = sheet.RowsUsed()
                .Where(r => r.RowNumber() > headerRowNumber)
                .ToList();

            foreach (var row in dataRows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int rowNo = row.RowNumber();
                if (IsRowEmpty(row, columnCount))
                    continue;

                T item;
                try
                {
                    item = (T)Activator.CreateInstance(typeof(T))!;
                }
                catch (Exception ex) when (ex is MissingMethodException or MemberAccessException)
                {
                    throw new ExcelImportException($"类型 {typeof(T).Name} 缺少公共无参构造函数，无法创建实例。", ex);
                }

                for (int c = 1; c <= columnCount; c++)
                {
                    var leaf = bindings[c - 1];
                    object? raw = ReadRawValue(row.Cell(c));
                    string? header = headers[c - 1];

                    if (leaf is null)
                    {
                        if (raw != null && !(raw is string rawStr && rawStr.Length == 0))
                            result.Errors.Add(new ImportError(rowNo, header, raw, "未识别的列，已忽略"));
                        continue;
                    }

                    if (!ImportValueConverter.TryConvert(raw, leaf.ValueType, out object? converted, out string reason))
                    {
                        result.Errors.Add(new ImportError(rowNo, leaf.DisplayName, raw, reason));
                        continue;
                    }

                    if (!accessor.TrySetValue(item, leaf, converted, out reason))
                        result.Errors.Add(new ImportError(rowNo, leaf.DisplayName, raw, reason));
                }

                result.Items.Add(item);
            }

            return result;
        }

        private static object? ReadRawValue(IXLCell cell)
        {
            switch (cell.DataType)
            {
                case XLDataType.Blank:
                    return null;
                case XLDataType.Boolean:
                    return cell.GetBoolean();
                case XLDataType.DateTime:
                    return cell.GetDateTime();
                case XLDataType.Number:
                    return cell.GetDouble();
                case XLDataType.Text:
                    return cell.GetString();
                case XLDataType.TimeSpan:
                    return cell.GetString();
                case XLDataType.Error:
                    return cell.GetString();
                default:
                    string text = cell.GetString();
                    return text.Length == 0 ? null : text;
            }
        }

        private static bool IsRowEmpty(IXLRow row, int columnCount)
        {
            for (int c = 1; c <= columnCount; c++)
            {
                var cell = row.Cell(c);
                if (cell.DataType != XLDataType.Blank && cell.GetString().Length > 0)
                    return false;
            }
            return true;
        }
    }
}
