using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using Xunit;

namespace VassasCo.Utility.Tests
{
    /// <summary>
    /// float 写入精度验证：float 只有约 7 位有效精度，直接放大为 double 会带出一长串无意义尾数，
    /// 既影响可读性，也会把自适应列宽撑到 20 个字符以上。这里验证写入的是最短往返表示。
    /// </summary>
    public class FloatPrecisionTests
    {
        private sealed class Reading
        {
            public string? Point { get; set; }
            public float Lv { get; set; }
            public float Cx { get; set; }
            public float Cy { get; set; }
            public float Duv { get; set; }
        }

        private static string NewTempFile(string label)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ExcelMapperFloat_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, label + ".xlsx");
        }

        private static List<Reading> Rows()
        {
            return new List<Reading>
            {
                new Reading { Point = "P1", Lv = 118.2f, Cx = 0.3351f, Cy = 0.6412f, Duv = 0.0032f },
                new Reading { Point = "P2", Lv = 305.1f, Cx = 0.3302f, Cy = 0.3418f, Duv = 0.0015f }
            };
        }

        [Fact]
        public void Float_WrittenWithoutPrecisionNoise()
        {
            string file = NewTempFile("noise");

            ExcelMapper.Build(Rows()).ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            // 第 1 行为表头，第 2 行起为数据；列序按属性声明顺序
            Assert.Equal("118.2", sheet.Cell(2, 2).Value.ToString());
            Assert.Equal("0.3351", sheet.Cell(2, 3).Value.ToString());
            Assert.Equal("0.6412", sheet.Cell(2, 4).Value.ToString());
            Assert.Equal("0.0032", sheet.Cell(2, 5).Value.ToString());
        }

        [Fact]
        public void Float_KeepsColumnWidthCompact()
        {
            // 噪声展开后该列宽会达到 20 以上，收敛后应回落到 10 以内
            string file = NewTempFile("width");

            ExcelMapper.Build(Rows()).ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            for (int column = 2; column <= 5; column++)
                Assert.True(sheet.Column(column).Width < 10,
                    "第 " + column + " 列宽度应小于 10，实际为 " + sheet.Column(column).Width);
        }

        [Fact]
        public void Float_RoundTripsForArbitraryValues()
        {
            // 覆盖：常规小数、整数级、多位有效数字、极小值、负数
            float[] samples =
            {
                0.3351f, 0.0032f, 118.2f, 305.1f, 0.05f, 1234.5677f,
                0.0000123f, -12.75f, 65535f, 1f / 3f, 7.7E-05f
            };

            string file = NewTempFile("roundtrip");
            var rows = new List<Reading>();
            for (int i = 0; i < samples.Length; i++)
                rows.Add(new Reading { Point = "S" + i, Lv = samples[i] });

            ExcelMapper.Build(rows).ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            for (int i = 0; i < samples.Length; i++)
            {
                double written = sheet.Cell(i + 2, 2).GetDouble();
                Assert.Equal(samples[i], (float)written);
            }
        }

        [Fact]
        public void Float_NonFiniteValuesFallBackToText()
        {
            // 数值单元格无法承载非有限数，底层写入会直接抛异常，故降级为文本
            string file = NewTempFile("special");

            var rows = new List<Reading>
            {
                new Reading { Point = "NaN", Lv = float.NaN },
                new Reading { Point = "Inf", Lv = float.PositiveInfinity },
                new Reading { Point = "NInf", Lv = float.NegativeInfinity },
                new Reading { Point = "Zero", Lv = 0f }
            };

            ExcelMapper.Build(rows).ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.Equal("NaN", sheet.Cell(2, 2).Value.ToString());
            Assert.Equal("Infinity", sheet.Cell(3, 2).Value.ToString());
            Assert.Equal("-Infinity", sheet.Cell(4, 2).Value.ToString());
            Assert.Equal(0d, sheet.Cell(5, 2).GetDouble());
        }

        [Fact]
        public void Double_KeepsFullPrecision()
        {
            // float 的收敛不应波及 double：Excel 保留约 15 位有效数字，远超 float 的 7 位
            string file = NewTempFile("double");

            var rows = new List<DoubleRow>
            {
                new DoubleRow { Label = "A", Value = 0.1 + 0.2 },
                new DoubleRow { Label = "B", Value = 1.0 / 3.0 }
            };

            ExcelMapper.Build(rows).ToFile(file);

            using var workbook = new XLWorkbook(file);
            var sheet = workbook.Worksheet(1);

            Assert.Equal(0.1 + 0.2, sheet.Cell(2, 2).GetDouble(), 12);
            Assert.Equal(1.0 / 3.0, sheet.Cell(3, 2).GetDouble(), 12);
        }

        private sealed class DoubleRow
        {
            public string? Label { get; set; }
            public double Value { get; set; }
        }
    }
}
