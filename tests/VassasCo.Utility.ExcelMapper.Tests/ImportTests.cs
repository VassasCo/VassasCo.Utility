// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace VassasCo.Utility.Tests
{
    public class ImportTests : IDisposable
    {
        private readonly string _dir;

        public ImportTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "import_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string FilePath(string name) => Path.Combine(_dir, name);

        [Fact]
        public void Csv_BasicRows_Mapped()
        {
            File.WriteAllText(FilePath("a.csv"),
                "Name,Age,Active,Birthday,Status\r\n" +
                "张三,20,是,2020-01-01,在职\r\n" +
                "李四,30,否,2020/02/03,离职\r\n",
                new UTF8Encoding(true));

            var result = ExcelMapper.ImportFromCsv<FlatRow>(FilePath("a.csv"));

            Assert.False(result.HasErrors);
            Assert.Equal(2, result.Items.Count);
            Assert.Equal("张三", result.Items[0].Name);
            Assert.Equal(20, result.Items[0].Age);
            Assert.True(result.Items[0].Active);
            Assert.Equal(new DateTime(2020, 1, 1), result.Items[0].Birthday);
            Assert.Equal(Status.Active, result.Items[0].Status);
            Assert.Equal(Status.Resigned, result.Items[1].Status);
        }

        [Fact]
        public void Csv_BadNumber_CollectedAsError_WithRowInfo()
        {
            File.WriteAllText(FilePath("a.csv"),
                "Name,Age\r\n张三,abc\r\n李四,30\r\n", new UTF8Encoding(true));

            var result = ExcelMapper.ImportFromCsv<FlatRow>(FilePath("a.csv"));

            Assert.True(result.HasErrors);
            var error = result.Errors.Single();
            Assert.Equal(2, error.Row);
            Assert.Equal("Age", error.Column);
            Assert.Equal("abc", error.RawValue);
            // 其余行正常导入
            Assert.Equal("李四", result.Items[1].Name);
        }

        [Fact]
        public void Csv_UnmappedColumn_WithValue_IsIgnoredWithNotice()
        {
            File.WriteAllText(FilePath("a.csv"),
                "Name,Age,Extra\r\n张三,20,hello\r\n", new UTF8Encoding(true));

            var result = ExcelMapper.ImportFromCsv<FlatRow>(FilePath("a.csv"));

            Assert.Contains(result.Errors, e => e.Column == "Extra");
            Assert.Single(result.Items);
        }

        [Fact]
        public void Csv_EmptyLines_AreSkipped()
        {
            File.WriteAllText(FilePath("a.csv"),
                "Name,Age\r\n张三,20\r\n\r\n李四,30\r\n", new UTF8Encoding(true));

            var result = ExcelMapper.ImportFromCsv<FlatRow>(FilePath("a.csv"));

            Assert.Equal(2, result.Items.Count);
        }

        [Fact]
        public void Csv_QuotedFields_Parsed()
        {
            File.WriteAllText(FilePath("a.csv"),
                "Name,Age\r\n\"张,三\",20\r\n\"a\"\"b\",30\r\n", new UTF8Encoding(true));

            var result = ExcelMapper.ImportFromCsv<FlatRow>(FilePath("a.csv"));

            Assert.False(result.HasErrors);
            Assert.Equal("张,三", result.Items[0].Name);
            Assert.Equal("a\"b", result.Items[1].Name);
        }

        [Fact]
        public void Csv_NullableEmpty_BecomesNull()
        {
            File.WriteAllText(FilePath("a.csv"),
                "Name,Age,Remark\r\n张三,20,\r\n", new UTF8Encoding(true));

            var result = ExcelMapper.ImportFromCsv<FlatRow>(FilePath("a.csv"));

            Assert.False(result.HasErrors);
            Assert.Null(result.Items[0].Remark);
        }

        [Fact]
        public void Excel_RoundTrip_FlatModel()
        {
            var rows = new List<FlatRow>
            {
                new FlatRow { Name = "张三", Age = 25, Active = true, Birthday = new DateTime(2021, 5, 6), Status = Status.Active }
            };

            ExcelMapper.SaveToFile(rows, FilePath("a.xlsx"));
            var result = ExcelMapper.ImportFromExcel<FlatRow>(FilePath("a.xlsx"));

            Assert.False(result.HasErrors);
            Assert.Single(result.Items);
            Assert.Equal("张三", result.Items[0].Name);
            Assert.Equal(25, result.Items[0].Age);
            Assert.True(result.Items[0].Active);
            Assert.Equal(new DateTime(2021, 5, 6), result.Items[0].Birthday);
        }

        [Fact]
        public void Excel_BadValue_CollectedAsError()
        {
            File.WriteAllText(FilePath("a.csv"),
                "Name,Age\r\n张三,oops\r\n", new UTF8Encoding(true));
            var result = ExcelMapper.ImportFromCsv<FlatRow>(FilePath("a.csv"));

            Assert.Contains(result.Errors, e => e.Row == 2 && e.Column == "Age");
        }

        [Fact]
        public void AutoImport_SelectsByExtension()
        {
            File.WriteAllText(FilePath("a.csv"),
                "Name,Age\r\n张三,20\r\n", new UTF8Encoding(true));

            var result = ExcelMapper.Import<FlatRow>(FilePath("a.csv"));
            Assert.Single(result.Items);
        }

        [Fact]
        public void MissingFile_Throws()
        {
            Assert.Throws<ExcelFileAccessException>(() =>
                ExcelMapper.ImportFromCsv<FlatRow>(FilePath("nope.csv")));
        }

        [Fact]
        public void NestedProperty_IntermediateCreated()
        {
            File.WriteAllText(FilePath("a.csv"),
                "Id,DeptName\r\n7,研发部\r\n", new UTF8Encoding(true));

            var result = ExcelMapper.ImportFromCsv<Person>(FilePath("a.csv"));

            Assert.False(result.HasErrors);
            Assert.Equal(7, result.Items[0].Id);
            Assert.Equal("研发部", result.Items[0].Department!.DeptName);
        }

        [Fact]
        public void Collection_JsonRoundTrip()
        {
            var data = new List<OrderContainer>
            {
                new OrderContainer { Id = 1, Tags = new List<string> { "a", "b" }, Codes = new List<int> { 10, 20 } },
                new OrderContainer { Id = 2, Tags = new List<string> { "c" }, Codes = new List<int> { 30 } }
            };

            ExcelMapper.Build(data)
                .WithOptions(o => o.ArrayRender = ArrayRenderMode.Json)
                .ToFile(FilePath("a.xlsx"));

            var result = ExcelMapper.ImportFromExcel<OrderContainer>(FilePath("a.xlsx"));

            Assert.False(result.HasErrors);
            Assert.Equal(2, result.Items.Count);
            Assert.Equal(new[] { "a", "b" }, result.Items[0].Tags);
            Assert.Equal(new[] { 10, 20 }, result.Items[0].Codes);
            Assert.Equal(new[] { "c" }, result.Items[1].Tags);
        }
    }
}
