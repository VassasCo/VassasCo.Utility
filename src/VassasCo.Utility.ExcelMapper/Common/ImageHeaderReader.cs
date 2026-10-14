// SPDX-License-Identifier: MIT

using System;

namespace VassasCo.Utility.Internals
{
    /// <summary>图片容器格式（仅覆盖常用位图格式，满足报表嵌图场景即可）</summary>
    internal enum ImageFormatKind
    {
        /// <summary>无法识别或不支持</summary>
        Unknown,

        /// <summary>PNG（含透明通道，最常用）</summary>
        Png,

        /// <summary>JPEG（有损压缩，体积小，适合照片/产品图）</summary>
        Jpeg,

        /// <summary>GIF</summary>
        Gif,

        /// <summary>BMP（工业软件常见截图格式，无压缩，体积大）</summary>
        Bmp,

        /// <summary>TIFF</summary>
        Tiff
    }

    /// <summary>
    /// 图片头解析器：只读二进制头部若干字节，即可判定图片格式与原始像素宽高。
    /// </summary>
    /// <remarks>
    /// 存在的意义：插入图片到 Excel 时需要知道格式枚举，且"未指定宽高时使用原图尺寸"
    /// 是高频需求。若为此引入 System.Drawing.Common / ImageSharp 会新增依赖，
    /// 并在 net472 下带来额外部署负担，因此这里手写解析，保持零依赖与跨 TFM 一致。
    /// </remarks>
    internal static class ImageHeaderReader
    {
        /// <summary>尝试解析图片；成功时返回 true 并给出格式与宽高</summary>
        public static bool TryParse(byte[]? bytes, out ImageFormatKind format, out int width, out int height)
        {
            format = ImageFormatKind.Unknown;
            width = 0;
            height = 0;

            if (bytes is null || bytes.Length < 16)
                return false;

            if (IsPng(bytes))
            {
                format = ImageFormatKind.Png;
                return TryReadPngSize(bytes, out width, out height);
            }

            if (IsGif(bytes))
            {
                format = ImageFormatKind.Gif;
                width = ReadInt16LE(bytes, 6);
                height = ReadInt16LE(bytes, 8);
                return true;
            }

            if (IsJpeg(bytes))
            {
                format = ImageFormatKind.Jpeg;
                return TryReadJpegSize(bytes, out width, out height);
            }

            if (IsBmp(bytes))
            {
                format = ImageFormatKind.Bmp;
                return TryReadBmpSize(bytes, out width, out height);
            }

            if (IsTiff(bytes))
            {
                format = ImageFormatKind.Tiff;
                return true;
            }

            return false;
        }

        #region 格式判定

        private static bool IsPng(byte[] b) =>
            b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
            b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

        private static bool IsJpeg(byte[] b) => b[0] == 0xFF && b[1] == 0xD8;

        private static bool IsGif(byte[] b) =>
            b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38 &&
            (b[4] == 0x37 || b[4] == 0x39) && b[5] == 0x61;

        private static bool IsBmp(byte[] b) => b[0] == 0x42 && b[1] == 0x4D;

        private static bool IsTiff(byte[] b) =>
            (b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A && b[3] == 0x00) ||
            (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x00 && b[3] == 0x2A);

        #endregion

        #region 尺寸解析

        /// <summary>PNG：宽在 16..19，高在 20..23（网络字节序 / 大端）</summary>
        private static bool TryReadPngSize(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (b.Length < 24)
                return false;

            width = ReadInt32BE(b, 16);
            height = ReadInt32BE(b, 20);
            return width > 0 && height > 0;
        }

        /// <summary>BMP：判断 DIB 头类型后取宽高（绝对值为像素数，负值表示自上而下存储）</summary>
        private static bool TryReadBmpSize(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (b.Length < 26)
                return false;

            int headerSize = ReadInt32LE(b, 14);

            if (headerSize == 12) // BITMAPCOREHEADER
            {
                if (b.Length < 22)
                    return false;
                width = ReadInt16LE(b, 18);
                height = ReadInt16LE(b, 20);
            }
            else // BITMAPINFOHEADER / V4 / V5
            {
                width = ReadInt32LE(b, 18);
                height = ReadInt32LE(b, 22);
                if (height < 0)
                    height = -height;
            }

            return width > 0 && height > 0;
        }

        /// <summary>JPEG：遍历段标记，找到 SOFn（0xC0~0xCF，排除 DHT/JPG/DAC）后取高宽</summary>
        private static bool TryReadJpegSize(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;

            int offset = 2;
            while (offset + 9 < b.Length)
            {
                if (b[offset] != 0xFF)
                {
                    offset++;
                    continue;
                }

                byte marker = b[offset + 1];

                // 连续 0xFF 是填充字节，跳过
                if (marker == 0xFF)
                {
                    offset++;
                    continue;
                }

                // SOS：已进入压缩数据，说明没有 SOF，放弃
                if (marker == 0xDA || marker == 0xD9)
                    return false;

                // 无长度字段的独立标记：SOI / TEM / RSTn
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    offset += 2;
                    continue;
                }

                if (marker >= 0xC0 && marker <= 0xCF &&
                    marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    height = (b[offset + 5] << 8) | b[offset + 6];
                    width = (b[offset + 7] << 8) | b[offset + 8];
                    return width > 0 && height > 0;
                }

                int segLen = (b[offset + 2] << 8) | b[offset + 3];
                if (segLen < 2)
                    return false;

                offset += 2 + segLen;
            }

            return false;
        }

        #endregion

        #region 字节读取

        private static int ReadInt16LE(byte[] b, int offset) => b[offset] | (b[offset + 1] << 8);

        private static int ReadInt32LE(byte[] b, int offset) =>
            b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);

        private static int ReadInt32BE(byte[] b, int offset) =>
            (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

        #endregion
    }
}
