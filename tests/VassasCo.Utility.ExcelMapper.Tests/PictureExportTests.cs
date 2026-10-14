using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using Xunit;

namespace VassasCo.Utility.Tests
{
    /// <summary>图片插入功能验证（特性列、Fluent 列、固定位置图片、缺失兜底）</summary>
    public class PictureExportTests
    {
        private sealed class Product
        {
            public int Id { get; set; }
            public string? Name { get; set; }

            [ExcelImage(Width = 60, Height = 60, SizeMode = ImageSizeMode.Fit)]
            public string? PhotoPath { get; set; }
        }

        private sealed class PlainProduct
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public string? PhotoPath { get; set; }
        }

        /// <summary>在工作目录下准备一张最小的 24bpp BMP，用于验证图片确实被写入 xlsx</summary>
        private static string PrepareImage(string dir)
        {
            string path = Path.Combine(dir, "sample.bmp");
            File.WriteAllBytes(path, CreateSampleBmp(4, 4));
            return path;
        }

        private static byte[] CreateSampleBmp(int width, int height)
        {
            int rowSize = ((width * 3 + 3) / 4) * 4;
            int pixelSize = rowSize * height;
            int fileSize = 54 + pixelSize;

            var buffer = new byte[fileSize];
            buffer[0] = (byte)'B';
            buffer[1] = (byte)'M';
            WriteInt32(buffer, 2, fileSize);
            WriteInt32(buffer, 10, 54);
            WriteInt32(buffer, 14, 40);
            WriteInt32(buffer, 18, width);
            WriteInt32(buffer, 22, height);
            WriteInt16(buffer, 26, 1);
            WriteInt16(buffer, 28, 24);
            WriteInt32(buffer, 34, pixelSize);
            return buffer;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value) =>
            BitConverter.GetBytes(value).CopyTo(buffer, offset);

        private static void WriteInt16(byte[] buffer, int offset, short value) =>
            BitConverter.GetBytes(value).CopyTo(buffer, offset);

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "ExcelMapperPic_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void ExcelImageAttribute_InsertsPicture()
        {
            string dir = NewTempDir();
            string img = PrepareImage(dir);
            string xlsx = Path.Combine(dir, "attr.xlsx");

            var data = new List<Product>
            {
                new Product { Id = 1, Name = "左前灯", PhotoPath = img }
            };

            var result = ExcelMapper.SaveToFile(data, xlsx);
            Assert.Equal(1, result.RowCount);

            using var wb = new XLWorkbook(xlsx);
            var ws = wb.Worksheet(1);
            Assert.True(ws.Pictures.Count >= 1, "标注 [ExcelImage] 的列应写入嵌入式图片");
        }

        [Fact]
        public void FluentAsImage_InsertsPicture()
        {
            string dir = NewTempDir();
            string img = PrepareImage(dir);
            string xlsx = Path.Combine(dir, "fluent.xlsx");

            var data = new List<PlainProduct>
            {
                new PlainProduct { Id = 2, Name = "右前灯", PhotoPath = img }
            };

            // 注意：Property() 返回 PropertyMapping，不能再链式调用 Property()，需逐条声明
            var map = new EntityMap<PlainProduct>();
            map.Property(x => x.Id).HasName("编号");
            map.Property(x => x.Name).HasName("名称");
            map.Property(x => x.PhotoPath).HasName("产品图")
                .AsImage(img => { img.Width = 60; img.Height = 60; img.SizeMode = ImageSizeMode.Fit; });

            var result = ExcelMapper.Build(data).WithMap(map).ToFile(xlsx);
            Assert.Equal(1, result.RowCount);

            using var wb = new XLWorkbook(xlsx);
            var ws = wb.Worksheet(1);
            Assert.True(ws.Pictures.Count >= 1, "Fluent AsImage() 的列应写入嵌入式图片");
        }

        [Fact]
        public void WithPicture_InsertsFixedImage()
        {
            string dir = NewTempDir();
            string img = PrepareImage(dir);
            string xlsx = Path.Combine(dir, "logo.xlsx");

            var data = new List<PlainProduct>
            {
                new PlainProduct { Id = 3, Name = "尾灯" }
            };

            var result = ExcelMapper.Build(data)
                .WithPicture(p => p.FromFile(img).At("H1").Size(40, 40))
                .ToFile(xlsx);

            Assert.Equal(1, result.RowCount);

            using var wb = new XLWorkbook(xlsx);
            var ws = wb.Worksheet(1);
            Assert.True(ws.Pictures.Count >= 1, "WithPicture 应在指定锚定单元格插入图片");
        }

        [Fact]
        public void MissingImageFile_SkipPolicy_DoesNotThrow()
        {
            string dir = NewTempDir();
            string xlsx = Path.Combine(dir, "missing.xlsx");

            var data = new List<Product>
            {
                new Product { Id = 4, Name = "图已丢失", PhotoPath = @"Z:\not-exist-dir-9527\no.bmp" }
            };

            // 默认 MissingImagePolicy.Skip：图片缺失不应导致整个导出失败
            var ex = Record.Exception(() => ExcelMapper.SaveToFile(data, xlsx));
            Assert.Null(ex);
            Assert.True(File.Exists(xlsx));
        }
    }
}
