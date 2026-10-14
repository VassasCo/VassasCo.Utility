using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using Xunit;

namespace VassasCo.Utility.Tests
{
    /// <summary>报表布局能力验证：标题、表前区、表后区、合并收敛、字体与引擎降级</summary>
    public class ReportLayoutTests
    {
        private sealed class Order
        {
            public int Id { get; set; }
            public string? Customer { get; set; }
            public decimal Amount { get; set; }
        }

        private static List<Order> SampleOrders()
        {
            return new List<Order>
            {
                new Order { Id = 1, Customer = "Acme", Amount = 120.5m },
                new Order { Id = 2, Customer = "Globex", Amount = 88m }
            };
        }

        private static string NewTempFile(string label)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ExcelMapperLayout_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, label + ".xlsx");
        }

        [Fact]
        public void WithTitle_WritesMergedTitleAndPushesHeaderDown()
        {
            string file = NewTempFile("title");

            ExcelMapper.Build(SampleOrders())
                .WithTitle("订单汇总表", fontSize: 20)
                .ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.Equal("订单汇总表", sheet.Cell(1, 1).GetString());
            Assert.True(sheet.Cell(1, 1).IsMerged());
            Assert.Equal(20, sheet.Cell(1, 1).Style.Font.FontSize);
            Assert.Equal("Id", sheet.Cell(2, 1).GetString());
            Assert.Equal(1, sheet.Cell(3, 1).GetValue<int>());
        }

        [Fact]
        public void WithPreamble_WritesKeyValueRowsAboveHeader()
        {
            string file = NewTempFile("preamble");

            ExcelMapper.Build(SampleOrders())
                .WithPreamble(r => r
                    .Cell("客户").Span(1).Cell("Acme").Span(2).NextRow()
                    .Cell("币种").Span(1).Cell("CNY").Span(2))
                .ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.Equal("客户", sheet.Cell(1, 1).GetString());
            Assert.Equal("Acme", sheet.Cell(1, 2).GetString());
            Assert.True(sheet.Cell(1, 2).IsMerged());
            Assert.Equal("币种", sheet.Cell(2, 1).GetString());
            Assert.Equal("Id", sheet.Cell(3, 1).GetString());
            Assert.Equal(1, sheet.Cell(4, 1).GetValue<int>());
        }

        [Fact]
        public void WithTitle_AlwaysPrecedesPreambleRows()
        {
            string file = NewTempFile("order");

            ExcelMapper.Build(SampleOrders())
                .WithPreamble(r => r.Cell("制表人").Span(1).Cell("张三").Span(2))
                .WithTitle("汇总")
                .ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.Equal("汇总", sheet.Cell(1, 1).GetString());
            Assert.Equal("制表人", sheet.Cell(2, 1).GetString());
        }

        [Fact]
        public void WithPostamble_WritesRowsBelowDataArea()
        {
            string file = NewTempFile("postamble");

            ExcelMapper.Build(SampleOrders())
                .WithPostamble(r => r.Cell("合计").Span(1).Cell("208.5").Span(2))
                .ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.Equal(2, sheet.Cell(3, 1).GetValue<int>());
            Assert.Equal("合计", sheet.Cell(4, 1).GetString());
            Assert.Equal("208.5", sheet.Cell(4, 2).GetString());
        }

        [Fact]
        public void LayoutCells_OverflowSpanIsClampedToTableWidth()
        {
            string file = NewTempFile("clamp");

            ExcelMapper.Build(SampleOrders())
                .WithPreamble(r => r.Cell("客户").Span(1).Cell("Acme").Span(10))
                .ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            // 表格只有 3 列，第二个单元格的合并范围被收敛到第 2~3 列，不会溢出到表格之外
            Assert.Equal("Acme", sheet.Cell(1, 2).GetString());
            Assert.True(sheet.Cell(1, 2).IsMerged());
            Assert.Equal(string.Empty, sheet.Cell(1, 4).GetString());
        }

        [Fact]
        public void StyleFontName_AppliesToWholeSheet()
        {
            string file = NewTempFile("font");

            ExcelMapper.Build(SampleOrders())
                .WithTitle("汇总")
                .WithStyle(s => s.FontName = "Consolas")
                .ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.Equal("Consolas", sheet.Cell(1, 1).Style.Font.FontName);
            Assert.Equal("Consolas", sheet.Cell(2, 1).Style.Font.FontName);
        }

        [Fact]
        public void WithoutLayoutRows_HeaderStaysOnFirstRow()
        {
            string file = NewTempFile("plain");

            ExcelMapper.Build(SampleOrders()).ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.Equal("Id", sheet.Cell(1, 1).GetString());
            Assert.Equal(1, sheet.Cell(2, 1).GetValue<int>());
        }

        [Fact]
        public void EmptyDataSource_StillWritesTitleAndHeader()
        {
            string file = NewTempFile("empty");

            ExcelMapper.Build(new List<Order>())
                .WithTitle("空表")
                .ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.Equal("空表", sheet.Cell(1, 1).GetString());
            Assert.Equal("Id", sheet.Cell(2, 1).GetString());
        }

        [Fact]
        public void StreamingEngine_WithLayoutRows_ThrowsValidationException()
        {
            var builder = ExcelMapper.Build(SampleOrders())
                .WithTitle("汇总")
                .WithOptions(o => o.Engine = ExportEngineKind.OpenXmlStreaming);

            Assert.Throws<ExcelPlanValidationException>(() => builder.ToFile(NewTempFile("streaming")));
        }

        [Fact]
        public void ReportRowBuilder_StyleWithoutCell_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new ReportRowBuilder().FontSize(12));
        }
    }
}
