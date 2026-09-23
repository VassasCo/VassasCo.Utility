// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VassasCo.Utility.Tests
{
    public class CsvTests : IDisposable
    {
        private readonly string _dir;

        public CsvTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "csv_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string FilePath() => Path.Combine(_dir, "test.csv");

        private string ReadFile()
        {
            return File.ReadAllText(FilePath(), Encoding.UTF8);
        }

        [Fact]
        public void WritesUtf8Bom()
        {
            ExcelMapper.WriteCsv(SampleData.People(1), FilePath());
            byte[] bytes = File.ReadAllBytes(FilePath());

            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
        }

        [Fact]
        public void Header_AndRows_Present()
        {
            ExcelMapper.WriteCsv(SampleData.People(2), FilePath());
            string content = ReadFile();
            string[] lines = content.Replace("\r", "").Split('\n');

            Assert.StartsWith("Id,Name,IdCard", lines[0]);
            Assert.StartsWith("1,用户1", lines[1]);
            Assert.StartsWith("2,用户2", lines[2]);
        }

        [Fact]
        public void FieldWithDelimiter_IsQuoted()
        {
            var rows = new[] { new FlatRow { Name = "a,b", Age = 1 } };
            ExcelMapper.WriteCsv(rows, FilePath());

            string content = ReadFile();
            Assert.Contains("\"a,b\"", content);
        }

        [Fact]
        public void FieldWithQuote_IsDoubledAndQuoted()
        {
            var rows = new[] { new FlatRow { Name = "a\"b", Age = 1 } };
            ExcelMapper.WriteCsv(rows, FilePath());

            Assert.Contains("\"a\"\"b\"", ReadFile());
        }

        [Fact]
        public void FieldWithNewLine_IsQuoted()
        {
            var rows = new[] { new FlatRow { Name = "a\nb", Age = 1 } };
            ExcelMapper.WriteCsv(rows, FilePath());

            Assert.Contains("\"a\nb\"", ReadFile());
        }

        [Fact]
        public void FormulaLikeValue_GetsApostrophePrefix()
        {
            var rows = new[] { new FlatRow { Name = "=1+1", Age = 1 } };
            ExcelMapper.WriteCsv(rows, FilePath());

            string content = ReadFile();
            Assert.Contains("'=1+1", content);
        }

        [Fact]
        public void LeadingMinus_GetsApostrophePrefix()
        {
            var rows = new[] { new FlatRow { Name = "-2+cmd|", Age = 1 } };
            ExcelMapper.WriteCsv(rows, FilePath());

            Assert.Contains("'-2+cmd|", ReadFile());
        }

        [Fact]
        public void InjectionGuard_CanBeDisabled()
        {
            var rows = new[] { new FlatRow { Name = "=1", Age = 1 } };
            ExcelMapper.BuildCsv(rows)
                .Configure(o => o.FormulaInjectionGuard = false)
                .ToFile(FilePath());

            Assert.DoesNotContain("'=1", ReadFile());
        }

        [Fact]
        public async Task AsyncWrite_Works()
        {
            await ExcelMapper.WriteCsvAsync(SampleData.People(3), FilePath());
            string[] lines = ReadFile().Replace("\r", "").Split('\n');
            Assert.StartsWith("3,用户3", lines[3]);
        }

        [Fact]
        public void FluentMap_ChangesHeaderName()
        {
            ExcelMapper.BuildCsv(SampleData.People(1))
                .WithMap(m => m.Property(p => p.Name).HasName("姓名"))
                .ToFile(FilePath());

            Assert.Contains("姓名", ReadFile());
        }
    }
}
