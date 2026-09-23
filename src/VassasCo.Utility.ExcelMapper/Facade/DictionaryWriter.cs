// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// 动态字典导出：键少 → 单行宽表（键作列头）；键多 → Key/Value 竖表。
    /// 使用 ClosedXML 直接写入（动态键无法预编译属性计划）；应用公共 ExcelStyle 的视觉配置。
    /// </summary>
    internal static class DictionaryWriter
    {
        private const int WideThreshold = 16;

        public static void Write(
            IDictionary dictionary,
            string? filePath,
            Stream? stream,
            string sheetName,
            ExcelStyle style,
            ExcelExportOptions options)
        {
            if (dictionary is null) throw new ArgumentNullException(nameof(dictionary));

            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet(SheetNameRegistry.Sanitize(sheetName));

                if (dictionary.Count <= WideThreshold)
                    WriteWide(dictionary, sheet, style);
                else
                    WriteVertical(dictionary, sheet, style);

                if (filePath != null)
                    workbook.SaveAs(filePath);
                else if (stream != null)
                    workbook.SaveAs(stream);
                else
                    throw new InvalidOperationException("必须指定文件路径或输出流。");
            }
        }

        public static Task WriteAsync(
            IDictionary dictionary,
            string? filePath,
            Stream? stream,
            string sheetName,
            ExcelStyle style,
            ExcelExportOptions options,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Write(dictionary, filePath, stream, sheetName, style, options);
            }, cancellationToken);
        }

        private static void WriteWide(IDictionary dictionary, IXLWorksheet sheet, ExcelStyle style)
        {
            int col = 1;
            foreach (DictionaryEntry entry in dictionary)
            {
                var headerCell = sheet.Cell(1, col);
                headerCell.Value = ValueServices.SanitizeXmlText(entry.Key?.ToString() ?? "");
                StyleHeader(headerCell, style);

                var valueCell = sheet.Cell(2, col);
                SetValue(valueCell, entry.Value);
                col++;
            }

            StyleDataArea(sheet, dataFirstRow: 2, dataLastRow: 2, lastColumn: col - 1, style);
        }

        private static void WriteVertical(IDictionary dictionary, IXLWorksheet sheet, ExcelStyle style)
        {
            StyleHeader(sheet.Cell(1, 1).SetValue("Key"), style);
            StyleHeader(sheet.Cell(1, 2).SetValue("Value"), style);

            int row = 2;
            foreach (DictionaryEntry entry in dictionary)
            {
                sheet.Cell(row, 1).Value = ValueServices.SanitizeXmlText(entry.Key?.ToString() ?? "");
                SetValue(sheet.Cell(row, 2), entry.Value);
                row++;
            }

            StyleDataArea(sheet, dataFirstRow: 2, dataLastRow: row - 1, lastColumn: 2, style);
        }

        private static void SetValue(IXLCell cell, object? value)
        {
            switch (value)
            {
                case null:
                    cell.Value = "";
                    break;
                case string s:
                    cell.Value = ValueServices.SanitizeXmlText(s);
                    break;
                case bool b:
                    cell.Value = b;
                    break;
                case DateTime dt:
                    cell.Value = dt;
                    break;
                case DateTimeOffset dto:
                    cell.Value = dto.DateTime;
                    break;
                case byte[] bytes:
                    cell.Value = Convert.ToBase64String(bytes);
                    break;
                case IDictionary nested:
                    cell.Value = ValueServices.SanitizeXmlText(ValueServices.SafeJson(nested));
                    break;
                case IEnumerable enumerable when value.GetType() != typeof(string):
                    cell.Value = ValueServices.SanitizeXmlText(ValueServices.SafeJson(value));
                    break;
                case IFormattable formattable when IsNumber(value.GetType()):
                    cell.Value = XLCellValue.FromObject(value);
                    break;
                default:
                    cell.Value = ValueServices.SanitizeXmlText(value.ToString() ?? "");
                    break;
            }
        }

        private static bool IsNumber(Type type)
        {
            Type underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
                || underlying == typeof(byte) || underlying == typeof(uint) || underlying == typeof(ulong)
                || underlying == typeof(ushort) || underlying == typeof(sbyte)
                || underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float);
        }

        private static void StyleHeader(IXLCell cell, ExcelStyle style)
        {
            if (style.HeaderBold)
                cell.Style.Font.Bold = true;
            SetSafeColor(c => cell.Style.Fill.BackgroundColor = c, style.HeaderBackgroundColor);
            SetSafeColor(c => cell.Style.Font.FontColor = c, style.HeaderFontColor);

            if (style.ShowBorder)
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }

        private static void StyleDataArea(IXLWorksheet sheet, int dataFirstRow, int dataLastRow, int lastColumn, ExcelStyle style)
        {
            if (lastColumn <= 0)
                return;

            if (style.ShowBorder)
            {
                sheet.Range(dataFirstRow, 1, dataLastRow, lastColumn)
                    .Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                sheet.Range(dataFirstRow, 1, dataLastRow, lastColumn)
                    .Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            if (style.AutoFitColumns)
            {
                for (int c = 1; c <= lastColumn; c++)
                    sheet.Column(c).AdjustToContents();
            }

            if (style.FreezeTopRow)
                sheet.SheetView.FreezeRows(1);
        }

        private static void SetSafeColor(Action<XLColor> apply, string htmlColor)
        {
            if (string.IsNullOrEmpty(htmlColor))
                return;
            try
            {
                apply(XLColor.FromHtml(htmlColor));
            }
            catch (FormatException)
            {
                // 非法颜色值不阻断导出（与 DOM 引擎颜色容错策略一致）
            }
        }
    }
}
