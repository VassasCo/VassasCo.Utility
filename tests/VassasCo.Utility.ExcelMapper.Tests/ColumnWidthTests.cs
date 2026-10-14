using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using Xunit;

namespace VassasCo.Utility.Tests
{
    /// <summary>
    /// 自适应列宽验证：重点覆盖中文表头、字号缩放、跨列合并行与宽度上下限。
    /// </summary>
    /// <remarks>
    /// 列宽单位与 Excel 一致：默认字号下半角字符宽为 1，全角字符按 2 计，另加 2 的边距。
    /// </remarks>
    public class ColumnWidthTests
    {
        private sealed class Sample
        {
            public int Id { get; set; }
            public string? Name { get; set; }
        }

        private static List<Sample> SampleRows()
        {
            return new List<Sample>
            {
                new Sample { Id = 1, Name = "P-01" },
                new Sample { Id = 2, Name = "P-02" }
            };
        }

        private static EntityMap<Sample> ChineseHeaderMap()
        {
            var map = new EntityMap<Sample>();
            map.Property(x => x.Id).HasName("序号").Order(0);
            map.Property(x => x.Name).HasName("监测点名称").Order(1);
            return map;
        }

        private static string NewTempFile(string label)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ExcelMapperWidth_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, label + ".xlsx");
        }

        [Fact]
        public void AutoFit_GivesChineseHeaderEnoughWidth()
        {
            string file = NewTempFile("chinese-header");

            ExcelMapper.Build(SampleRows())
                .WithMap(ChineseHeaderMap())
                .ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            // “监测点名称” 为 5 个全角字符：5 * 2 + 2 = 12
            Assert.True(sheet.Column(2).Width >= 12,
                $"中文表头列宽不足，实际 {sheet.Column(2).Width}，应不小于 12");
        }

        [Fact]
        public void AutoFit_ScalesWithFontSize()
        {
            string normal = NewTempFile("font-normal");
            string large = NewTempFile("font-large");

            ExcelMapper.Build(SampleRows()).WithMap(ChineseHeaderMap()).ToFile(normal);
            ExcelMapper.Build(SampleRows())
                .WithMap(ChineseHeaderMap())
                .WithStyle(s => s.FontSize = 22)
                .ToFile(large);

            using var wbNormal = new XLWorkbook(normal);
            using var wbLarge = new XLWorkbook(large);

            double normalWidth = wbNormal.Worksheet(1).Column(2).Width;
            double largeWidth = wbLarge.Worksheet(1).Column(2).Width;

            // 字号翻倍后，同样内容需要的列宽也应接近翻倍
            Assert.True(largeWidth > normalWidth * 1.5,
                $"字号放大后列宽未同步增加：{normalWidth} -> {largeWidth}");
        }

        [Fact]
        public void AutoFit_IgnoresMergedLayoutRows()
        {
            string withLayout = NewTempFile("merged-layout");
            string plain = NewTempFile("plain");

            ExcelMapper.Build(SampleRows())
                .WithMap(ChineseHeaderMap())
                .WithPreamble(r => r.Cell("这是一段很长的元信息说明文本，横跨整张表格").Span(0))
                .ToFile(withLayout);

            ExcelMapper.Build(SampleRows())
                .WithMap(ChineseHeaderMap())
                .ToFile(plain);

            using var wbLayout = new XLWorkbook(withLayout);
            using var wbPlain = new XLWorkbook(plain);

            // 跨列合并的文本由多列共同容纳，不应把单列撑开
            Assert.Equal(
                wbPlain.Worksheet(1).Column(2).Width,
                wbLayout.Worksheet(1).Column(2).Width);
        }

        [Fact]
        public void AutoFit_UsesHeaderWidthWhenThereAreNoRows()
        {
            string file = NewTempFile("no-rows");

            ExcelMapper.Build(new List<Sample>())
                .WithMap(ChineseHeaderMap())
                .ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.True(sheet.Column(2).Width >= 12,
                $"无数据时仍应按表头定宽，实际 {sheet.Column(2).Width}");
        }

        [Fact]
        public void FixedWidth_TakesPrecedenceOverAutoFit()
        {
            var map = ChineseHeaderMap();
            map.Property(x => x.Name).Width(30);

            string file = NewTempFile("fixed-width");
            ExcelMapper.Build(SampleRows()).WithMap(map).ToFile(file);

            using var workbook = new XLWorkbook(file);

            Assert.Equal(30, workbook.Worksheet(1).Column(2).Width, 1);
        }

        [Fact]
        public void MinColumnWidth_RaisesNarrowColumns()
        {
            string file = NewTempFile("min-width");

            ExcelMapper.Build(SampleRows())
                .WithMap(ChineseHeaderMap())
                .WithStyle(s => s.MinColumnWidth = 40)
                .ToFile(file);

            using var workbook = new XLWorkbook(file);

            Assert.True(workbook.Worksheet(1).Column(2).Width >= 40,
                $"未生效的 MinColumnWidth，实际 {workbook.Worksheet(1).Column(2).Width}");
        }

        [Fact]
        public void MaxColumnWidth_CapsWideColumns()
        {
            string file = NewTempFile("max-width");

            ExcelMapper.Build(SampleRows())
                .WithMap(ChineseHeaderMap())
                .WithStyle(s => s.MaxColumnWidth = 6)
                .ToFile(file);

            using var workbook = new XLWorkbook(file);

            Assert.True(workbook.Worksheet(1).Column(2).Width <= 6,
                $"未生效的 MaxColumnWidth，实际 {workbook.Worksheet(1).Column(2).Width}");
        }

        [Fact]
        public void AutoFitDisabled_KeepsDefaultColumnWidth()
        {
            string file = NewTempFile("autofit-off");

            ExcelMapper.Build(SampleRows())
                .WithMap(ChineseHeaderMap())
                .WithStyle(s => s.AutoFitColumns = false)
                .ToFile(file);

            using var workbook = new XLWorkbook(file);

            Assert.True(workbook.Worksheet(1).Column(2).Width < 12,
                $"关闭自适应后不应再按内容调整，实际 {workbook.Worksheet(1).Column(2).Width}");
        }
    }
}
