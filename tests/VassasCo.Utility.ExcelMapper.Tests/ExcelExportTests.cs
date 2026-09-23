// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Xunit;

namespace VassasCo.Utility.Tests
{
    public class ExcelExportTests : IDisposable
    {
        private readonly string _dir;

        public ExcelExportTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "excelmapper_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string FilePath(string name = "test.xlsx") => Path.Combine(_dir, name);

        private static int FindColumn(IXLWorksheet sheet, int headerDepth, string header)
        {
            int last = sheet.LastColumnUsed()?.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int c = 1; c <= last; c++)
            {
                // 深度0叶子表头会跨多行合并，值只在合并区左上角，因此逐表头行扫描
                for (int r = 1; r <= headerDepth; r++)
                {
                    if (sheet.Cell(r, c).GetString() == header)
                        return c;
                }
            }
            throw new Xunit.Sdk.XunitException($"未找到列 {header}");
        }

        [Fact]
        public void SensitiveIdCard_ExportedAsText_WithValueIntact()
        {
            var people = SampleData.People(1);
            ExcelMapper.SaveToFile(people, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            int col = FindColumn(sheet, 2, "IdCard");
            var cell = sheet.Cell(3, col);

            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.Equal(people[0].IdCard, cell.GetString());
        }

        [Fact]
        public void LongInteger_ExportedAsText_NoPrecisionLoss()
        {
            var people = SampleData.People(1);
            ExcelMapper.SaveToFile(people, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            int col = FindColumn(sheet, 2, "BigNumber");
            var cell = sheet.Cell(3, col);

            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.Equal(people[0].BigNumber.ToString(), cell.GetString());
        }

        [Fact]
        public void Bool_UsesConfiguredText()
        {
            var people = SampleData.People(2);
            ExcelMapper.SaveToFile(people, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            int col = FindColumn(sheet, 2, "Active");

            Assert.Equal("否", sheet.Cell(3, col).GetString());
            Assert.Equal("是", sheet.Cell(4, col).GetString());
        }

        [Fact]
        public void Char_ExportedAsCharacter_NotAsciiNumber()
        {
            var people = SampleData.People(1);
            ExcelMapper.SaveToFile(people, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            int col = FindColumn(sheet, 2, "Grade");

            Assert.Equal(people[0].Grade.ToString(), sheet.Cell(3, col).GetString());
        }

        [Fact]
        public void ByteArray_RenderedAsBase64()
        {
            var people = SampleData.People(1);
            people[0].Thumbnail = new byte[] { 1, 2, 3, 250 };
            ExcelMapper.SaveToFile(people, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            int col = FindColumn(sheet, 2, "Thumbnail");

            Assert.Equal(Convert.ToBase64String(people[0].Thumbnail!), sheet.Cell(3, col).GetString());
        }

        [Fact]
        public void Collection_GeneratesChildSheet_WithParentColumn()
        {
            ExcelMapper.SaveToFile(SampleData.People(2), FilePath());

            using var wb = new XLWorkbook(FilePath());
            Assert.Contains(wb.Worksheets, w => w.Name == "Addresses");
            var child = wb.Worksheets.First(w => w.Name == "Addresses");

            Assert.Equal("Id", child.Cell(1, 1).GetString());
            Assert.Equal("City", child.Cell(1, 2).GetString());
            Assert.Equal("Street", child.Cell(1, 3).GetString());
            // 父关联列统一文本格式（防止长 Id / Guid 丢精度）
            Assert.Equal("1", child.Cell(2, 1).GetString());
            Assert.Equal("上海", child.Cell(2, 2).GetString());
        }

        [Fact]
        public void Collection_JsonMode_SerializesToJsonCell()
        {
            var data = new List<OrderContainer>
            {
                new OrderContainer { Id = 1, Tags = new List<string> { "a", "b" }, Codes = new List<int> { 10, 20 } }
            };

            ExcelMapper.Build(data)
                .WithOptions(o => o.ArrayRender = ArrayRenderMode.Json)
                .ToFile(FilePath());

            using var wb = new XLWorkbook(FilePath());
            Assert.Single(wb.Worksheets); // 不产生子 Sheet

            var sheet = wb.Worksheets.First();
            int tagsCol = FindColumn(sheet, 1, "Tags");
            int codesCol = FindColumn(sheet, 1, "Codes");
            Assert.Equal("[\"a\",\"b\"]", sheet.Cell(2, tagsCol).GetString());
            Assert.Equal("[10,20]", sheet.Cell(2, codesCol).GetString());
        }

        [Fact]
        public void Paging_CreatesMultipleSheets_WithNumberedNames()
        {
            ExcelMapper.Build(SampleData.People(5))
                .WithOptions(o => o.PageSize = 2)
                .ToFile(FilePath());

            using var wb = new XLWorkbook(FilePath());
            // 第 1 页保留基础名，后续页追加页码
            Assert.Contains(wb.Worksheets, w => w.Name == "Person");
            Assert.Contains(wb.Worksheets, w => w.Name == "Person_2");
            Assert.Contains(wb.Worksheets, w => w.Name == "Person_3");
        }

        [Fact]
        public void Paging_MultipleFiles_CreatesNumberedFiles()
        {
            ExcelMapper.Build(SampleData.People(5))
                .WithOptions(o =>
                {
                    o.PageSize = 2;
                    o.PageMode = PageMode.MultipleFiles;
                })
                .ToFile(FilePath());

            // 所有页（含第 1 页）统一命名为 test_1/test_2/test_3.xlsx
            Assert.True(File.Exists(Path.Combine(_dir, "test_1.xlsx")));
            Assert.True(File.Exists(Path.Combine(_dir, "test_2.xlsx")));
            Assert.True(File.Exists(Path.Combine(_dir, "test_3.xlsx")));
            Assert.False(File.Exists(FilePath()));

            // 每页含表头 2 行 + 数据 2/2/1 行
            using var wb3 = new XLWorkbook(Path.Combine(_dir, "test_3.xlsx"));
            var sheet = wb3.Worksheets.First();
            Assert.Equal("Id", sheet.Cell(1, 1).GetString());
            Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());
        }

        [Fact]
        public void Paging_MultipleFiles_EmptyData_WritesSingleHeaderFile()
        {
            ExcelMapper.Build(new List<Person>())
                .WithOptions(o =>
                {
                    o.PageSize = 2;
                    o.PageMode = PageMode.MultipleFiles;
                })
                .ToFile(FilePath());

            // 空数据：输出原始文件名的单文件表头
            Assert.True(File.Exists(FilePath()));
            using var wb = new XLWorkbook(FilePath());
            Assert.Equal("Id", wb.Worksheets.First().Cell(1, 1).GetString());
        }

        [Fact]
        public void FlattenInPlace_MultipleCollections_ThrowsPlanValidation()
        {
            var data = new List<OrderContainer>
            {
                new OrderContainer { Id = 1, Tags = new List<string> { "a" }, Codes = new List<int> { 1 } }
            };

            var ex = Assert.Throws<ExcelPlanValidationException>(() =>
                ExcelMapper.Build(data)
                    .WithOptions(o => o.ArrayRender = ArrayRenderMode.FlattenInPlace)
                    .ToFile(FilePath()));

            Assert.Contains("FlattenInPlace", ex.Message);
        }

        [Fact]
        public void ConditionalRule_ServerSide_AppliesStaticStyle()
        {
            var people = SampleData.People(5);

            ExcelMapper.Build(people)
                .WithOptions(o => o.PreferNativeConditionalFormat = false)
                .WithMap(map => map
                    .Property(p => p.Score)
                    .Rule(ConditionOperator.GreaterThan, 100, s => s.Fill("#FF0000")))
                .ToFile(FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            int col = FindColumn(sheet, 2, "Score");

            // i=5 → Score=105 命中
            var hitColor = sheet.Cell(7, col).Style.Fill.BackgroundColor.Color;
            Assert.Equal(255, hitColor.R);
            Assert.Equal(0, hitColor.G);
            Assert.Equal(0, hitColor.B);

            // i=1 → Score=65 未命中
            var missColor = sheet.Cell(3, col).Style.Fill.BackgroundColor.Color;
            Assert.False(missColor.R == 255 && missColor.G == 0 && missColor.B == 0);
        }

        [Fact]
        public void StreamingEngine_ProducesReadableFile()
        {
            var people = SampleData.People(3);

            ExcelMapper.Build(people)
                .WithOptions(o => o.Engine = ExportEngineKind.OpenXmlStreaming)
                .ToFile(FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            int col = FindColumn(sheet, 2, "Name");
            Assert.Equal("用户1", sheet.Cell(3, col).GetString());
        }

        [Fact]
        public void CycleReference_ExportsMarker_DoesNotHang()
        {
            var person = new Person { Id = 1, Name = "经理" };
            var dept = new Department { DeptName = "技术部", Manager = person };
            person.Department = dept;

            ExcelMapper.SaveToFile(new List<Person> { person }, FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            int col = FindColumn(sheet, 2, "Manager");
            Assert.Equal("(循环引用)", sheet.Cell(3, col).GetString());
        }

        [Fact]
        public async Task AsyncExport_CancelledToken_Throws()
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ExcelMapper.SaveToFileAsync(
                    SampleData.People(10),
                    FilePath(),
                    new CancellationToken(canceled: true)));
        }

        [Fact]
        public async Task AsyncExport_ReportsProgress()
        {
            int last = 0;
            var progress = new Progress<int>(v => last = v);

            await ExcelMapper.Build(SampleData.People(10))
                .WithOptions(o => o.Engine = ExportEngineKind.OpenXmlStreaming)
                .ToFileAsync(FilePath(), CancellationToken.None, progress);

            Assert.Equal(10, last);
        }

        [Fact]
        public void EmptyData_WritesHeaderOnlySheet()
        {
            ExcelMapper.SaveToFile(new List<Person>(), FilePath());

            using var wb = new XLWorkbook(FilePath());
            var sheet = wb.Worksheets.First();
            // Id 表头跨 1-2 行合并，值在左上角
            Assert.Equal("Id", sheet.Cell(1, 1).GetString());
            // 无数据：最后使用行即表头行（Person 计划表头深度 2）
            Assert.Equal(2, sheet.LastRowUsed()!.RowNumber());
        }

        [Fact]
        public void InvalidPath_ThrowsFileAccessException()
        {
            Assert.Throws<ExcelFileAccessException>(() =>
                ExcelMapper.SaveToFile(SampleData.People(1), Path.Combine(_dir, "bad|name.xlsx")));
        }

        [Fact]
        public void AutoCreateDirectory_CreatesNestedFolders()
        {
            string target = Path.Combine(_dir, "a", "b", "c.xlsx");
            ExcelMapper.SaveToFile(SampleData.People(1), target);
            Assert.True(File.Exists(target));
        }

        [Fact]
        public void MultipleDataSets_AllSheetsPresent()
        {
            var people = SampleData.People(2);
            var rows = new List<FlatRow>
            {
                new FlatRow { Name = "张三", Age = 20 },
                new FlatRow { Name = "李四", Age = 30 }
            };

            ExcelMapper.Build(people)
                .AddSheet(rows, "附加数据")
                .ToFile(FilePath());

            using var wb = new XLWorkbook(FilePath());
            Assert.Contains(wb.Worksheets, w => w.Name == "附加数据");
        }

        [Fact]
        public void InvalidControlChars_AreStrippedFromCells()
        {
            var rows = new List<FlatRow>
            {
                new FlatRow { Name = "A\u0000B\u000BC\u000CD\u000EE" }
            };

            ExcelMapper.SaveToFile(rows, FilePath());

            using var wb = new XLWorkbook(FilePath()); // 能打开说明 XML 合法
            var sheet = wb.Worksheets.First();
            int col = FindColumn(sheet, 1, "Name");
            Assert.Equal("ABCDE", sheet.Cell(2, col).GetString());
        }

        [Fact]
        public void Export_ReturnsDiagnostics_WithChildSheets()
        {
            var people = SampleData.People(5);

            var result = ExcelMapper.SaveToFile(people, FilePath());

            Assert.Equal(5, result.RowCount);
            Assert.Equal(2, result.SheetCount); // Person + Addresses 子 Sheet
            Assert.Equal(ExportEngineKind.ClosedXml, result.Engine);
            Assert.Equal(1, result.FileCount);
            Assert.Single(result.FilePaths);
            Assert.True(result.Elapsed >= TimeSpan.Zero);
        }

        [Fact]
        public void Export_StreamingEngine_ReportsEngineKind()
        {
            var people = SampleData.People(10);

            var result = ExcelMapper.Build(people)
                .WithOptions(o => o.Engine = ExportEngineKind.OpenXmlStreaming)
                .ToFile(FilePath());

            Assert.Equal(ExportEngineKind.OpenXmlStreaming, result.Engine);
            Assert.Equal(10, result.RowCount);
        }

        [Fact]
        public void Export_MultipleFiles_ReportsFileCount()
        {
            var people = SampleData.People(5);

            var result = ExcelMapper.Build(people)
                .WithOptions(o =>
                {
                    o.PageSize = 2;
                    o.PageMode = PageMode.MultipleFiles;
                })
                .ToFile(FilePath("report.xlsx"));

            Assert.Equal(3, result.FileCount);
            Assert.Equal(5, result.RowCount);
            Assert.Equal(3, result.FilePaths.Count);
        }
    }
}
