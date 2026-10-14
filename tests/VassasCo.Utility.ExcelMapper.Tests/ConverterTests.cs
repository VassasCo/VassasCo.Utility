// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Xunit;

namespace VassasCo.Utility.Tests
{
    /// <summary>值转换器验证：构造器实参复用、内置转换器行为、错误路径</summary>
    public class ConverterTests : IDisposable
    {
        private readonly string _dir;

        public ConverterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "excelmapper_conv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string FilePath(string name = "test.xlsx") => Path.Combine(_dir, name);

        private sealed class Verdict
        {
            [ExcelColumn(Name = "电参数判定")]
            [ExcelConverter(typeof(BooleanTextConverter), "是", "否")]
            public bool PowerOn { get; set; }

            [ExcelConverter(typeof(BooleanTextConverter))]
            public bool Optical { get; set; }

            [ExcelConverter(typeof(MaskConverter), 3, 4)]
            public string? Phone { get; set; }

            [ExcelConverter(typeof(NullPlaceholderConverter), "-")]
            public string? Remark { get; set; }
        }

        private sealed class MissingCtor
        {
            [ExcelConverter(typeof(BooleanTextConverter), 1, 2, 3)]
            public bool Flag { get; set; }
        }

        private sealed class WrongType
        {
            [ExcelConverter(typeof(NotAConverter))]
            public bool Flag { get; set; }
        }

        private sealed class NotAConverter
        {
            public object? Convert(object? value) => value;
        }

        /// <summary>按表头文本定位列号，避免依赖属性声明顺序</summary>
        private static int FindColumn(IXLWorksheet sheet, string header)
        {
            int last = sheet.LastColumnUsed()?.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int c = 1; c <= last; c++)
            {
                if (sheet.Cell(1, c).GetString() == header)
                    return c;
            }
            throw new Xunit.Sdk.XunitException($"未找到列 {header}");
        }

        [Fact]
        public void BooleanConverter_WithArgs_WritesCustomText()
        {
            var data = new[] { new Verdict { PowerOn = true, Optical = false } };
            ExcelMapper.SaveToFile(data, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();

            Assert.Equal("是", sheet.Cell(2, FindColumn(sheet, "电参数判定")).GetString());
            Assert.Equal("NG", sheet.Cell(2, FindColumn(sheet, "Optical")).GetString());
        }

        [Fact]
        public void BooleanConverter_WithoutArgs_UsesOkNgDefault()
        {
            var data = new[] { new Verdict { Optical = true } };
            ExcelMapper.SaveToFile(data, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();

            Assert.Equal("OK", sheet.Cell(2, FindColumn(sheet, "Optical")).GetString());
        }

        [Fact]
        public void MaskConverter_KeepsHeadAndTail()
        {
            var data = new[] { new Verdict { Phone = "13812345678" } };
            ExcelMapper.SaveToFile(data, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();

            Assert.Equal("138****5678", sheet.Cell(2, FindColumn(sheet, "Phone")).GetString());
        }

        [Fact]
        public void MaskConverter_WhenKeepRangeCoversWholeText_MasksAll()
        {
            var converter = new MaskConverter(4, 4);

            Assert.Equal("****", converter.Convert("abcd"));
            Assert.Equal(string.Empty, converter.Convert(""));
            Assert.Null(converter.Convert(null));
        }

        [Fact]
        public void NullPlaceholderConverter_ReplacesNullAndBlank()
        {
            var converter = new NullPlaceholderConverter("-");

            Assert.Equal("-", converter.Convert(null));
            Assert.Equal("-", converter.Convert("   "));
            Assert.Equal("-", converter.Convert(string.Empty));
            Assert.Equal("OK", converter.Convert("OK"));
        }

        [Fact]
        public void NullPlaceholderConverter_WritesTextCellForNull()
        {
            var data = new[] { new Verdict { Remark = null } };
            ExcelMapper.SaveToFile(data, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            var cell = sheet.Cell(2, FindColumn(sheet, "Remark"));

            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.Equal("-", cell.GetString());
        }

        [Fact]
        public void BooleanConverter_PassesThroughNonBooleanValue()
        {
            var converter = new BooleanTextConverter("是", "否");

            Assert.Equal(42, converter.Convert(42));
            Assert.Null(converter.Convert(null));
        }

        [Fact]
        public void ConverterWithoutMatchingCtor_ThrowsMappingException()
        {
            var data = new[] { new MissingCtor { Flag = true } };

            var ex = Assert.Throws<ExcelMappingException>(() => ExcelMapper.SaveToFile(data, FilePath()));
            Assert.Contains("实参", Assert.IsType<ExcelMappingException>(ex).Message);
        }

        [Fact]
        public void ConverterNotImplementingInterface_ThrowsMappingException()
        {
            var data = new[] { new WrongType { Flag = true } };

            Assert.Throws<ExcelMappingException>(() => ExcelMapper.SaveToFile(data, FilePath()));
        }
    }
}
