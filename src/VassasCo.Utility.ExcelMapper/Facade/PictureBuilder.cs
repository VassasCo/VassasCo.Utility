// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Text;
using VassasCo.Utility.Internals;

namespace VassasCo.Utility
{
    /// <summary>
    /// 固定位置图片构造器：声明图片来源（文件或字节）、锚定单元格与显示尺寸。
    /// 通过 <c>ExcelMapperBuilder&lt;T&gt;.WithPicture(...)</c> 使用。
    /// </summary>
    /// <example>
    /// <code>
    /// ExcelMapper.Build(products)
    ///     .WithPicture(p =&gt; p.FromFile(@"C:\res\logo.png").At("A1").Size(140, 45))
    ///     .ToFile("report.xlsx");
    /// </code>
    /// </example>
    public sealed class PictureBuilder
    {
        private string? _filePath;
        private byte[]? _bytes;
        private int _row = 1;
        private int _column = 1;
        private ExcelImageOptions _options = new ExcelImageOptions();

        /// <summary>图片来源：磁盘文件路径。相对路径会结合 BaseDirectory 解析</summary>
        public PictureBuilder FromFile(string path)
        {
            _filePath = path;
            return this;
        }

        /// <summary>图片来源：内存字节（PNG/JPEG/GIF/BMP/TIFF）</summary>
        public PictureBuilder FromBytes(byte[] bytes)
        {
            _bytes = bytes;
            return this;
        }

        /// <summary>锚定位置：Excel 行列号（1 起）</summary>
        public PictureBuilder At(int row, int column)
        {
            if (row < 1) throw new ArgumentOutOfRangeException(nameof(row), "行号从 1 开始。");
            if (column < 1) throw new ArgumentOutOfRangeException(nameof(column), "列号从 1 开始。");

            _row = row;
            _column = column;
            return this;
        }

        /// <summary>锚定位置：A1 形式的单元格引用，如 "B3"</summary>
        public PictureBuilder At(string cellReference)
        {
            if (string.IsNullOrWhiteSpace(cellReference))
                throw new ArgumentException("单元格引用不能为空。", nameof(cellReference));

            if (!TryParseCellReference(cellReference.Trim(), out int row, out int column))
                throw new ArgumentException($"无法解析单元格引用：{cellReference}", nameof(cellReference));

            _row = row;
            _column = column;
            return this;
        }

        /// <summary>显示尺寸（像素）。配合 SizeMode 生效</summary>
        public PictureBuilder Size(double width, double height)
        {
            _options.Width = width;
            _options.Height = height;
            return this;
        }

        /// <summary>尺寸自适应模式；指定 Size 后常用 <see cref="ImageSizeMode.Fit"/> 等比缩放</summary>
        public PictureBuilder SizeMode(ImageSizeMode mode)
        {
            _options.SizeMode = mode;
            return this;
        }

        /// <summary>定位方式：随单元格缩放（默认）或自由浮动</summary>
        public PictureBuilder Placement(ImagePlacement placement)
        {
            _options.Placement = placement;
            return this;
        }

        /// <summary>图片名称（同一 Sheet 内需唯一）</summary>
        public PictureBuilder Name(string name)
        {
            _options.Name = name;
            return this;
        }

        /// <summary>相对路径的基准目录；默认程序所在目录</summary>
        public PictureBuilder BaseDirectory(string directory)
        {
            _options.BaseDirectory = directory;
            return this;
        }

        /// <summary>图片缺失时的处理策略</summary>
        public PictureBuilder OnMissing(MissingImagePolicy policy)
        {
            _options.OnMissing = policy;
            return this;
        }

        /// <summary>是否自动调高行高以容纳图片，默认 true</summary>
        public PictureBuilder AdjustRowHeight(bool enable)
        {
            _options.AdjustRowHeight = enable;
            return this;
        }

        /// <summary>是否自动加宽列宽以容纳图片，默认 true</summary>
        public PictureBuilder AdjustColumnWidth(bool enable)
        {
            _options.AdjustColumnWidth = enable;
            return this;
        }

        /// <summary>构建图片计划</summary>
        internal PicturePlan Build()
        {
            return new PicturePlan
            {
                FilePath = _filePath,
                Bytes = _bytes,
                Row = _row,
                Column = _column,
                Options = _options,
                Name = _options.Name
            };
        }

        /// <summary>解析 A1 形式的单元格引用；失败返回 false</summary>
        private static bool TryParseCellReference(string reference, out int row, out int column)
        {
            row = 0;
            column = 0;
            if (string.IsNullOrEmpty(reference))
                return false;

            int i = 0;
            int col = 0;
            while (i < reference.Length)
            {
                char ch = reference[i];
                if (ch >= 'A' && ch <= 'Z') { col = col * 26 + (ch - 'A' + 1); i++; continue; }
                if (ch >= 'a' && ch <= 'z') { col = col * 26 + (ch - 'a' + 1); i++; continue; }
                break;
            }

            if (col == 0 || i >= reference.Length)
                return false;

            if (!int.TryParse(reference.Substring(i), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int r) || r < 1)
                return false;

            row = r;
            column = col;
            return true;
        }
    }
}
