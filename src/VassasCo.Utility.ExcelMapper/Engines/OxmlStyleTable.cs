// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// OpenXML 样式表动态注册器：按需注册数字格式/字体/填充/边框/单元格格式，
    /// 最终产出符合 Schema 顺序的 Stylesheet。写入开始前完成所有注册。
    /// </summary>
    internal sealed class OxmlStyleTable
    {
        // 自定义 numFmt 起始 ID
        private const int CustomFormatStartId = 176;

        private readonly Dictionary<string, int> _formatIds = new Dictionary<string, int>();
        private readonly List<string> _customFormats = new List<string>();

        private readonly Dictionary<FontKey, int> _fontIds = new Dictionary<FontKey, int>();
        private readonly List<FontKey> _fonts = new List<FontKey>();

        private readonly Dictionary<string, int> _fillIds = new Dictionary<string, int>();
        private readonly List<string?> _fills = new List<string?>();

        private readonly Dictionary<bool, int> _borderIds = new Dictionary<bool, int>();
        private readonly List<bool> _borders = new List<bool>();

        private readonly Dictionary<CellStyleKey, int> _cellFormatIds = new Dictionary<CellStyleKey, int>();
        private readonly List<CellStyleKey> _cellFormats = new List<CellStyleKey>();

        /// <summary>初始化：注册内置默认（0 号字体/填充/边框/格式必须就位）</summary>
        public OxmlStyleTable()
        {
            // 0 号字体：Calibri 11
            RegisterFont(new FontKey(false, null));
            // 0/1 号填充：none / gray125（Excel 强制要求）
            RegisterFill(null);
            RegisterFill("gray125");
            // 0 号边框：无
            RegisterBorder(false);
            // 0 号单元格格式：默认
            RegisterStyle(0, 0, 0, numFmtId: 0, center: false, wrap: false);
        }

        #region Registration

        /// <summary>注册数字/日期格式字符串，返回 numFmtId（"@" 使用内置 49）</summary>
        public int RegisterNumberFormat(string? format)
        {
            if (string.IsNullOrEmpty(format))
                return 0;
            if (format == "@")
                return 49;

            if (_formatIds.TryGetValue(format!, out var existing))
                return existing;

            int id = CustomFormatStartId + _customFormats.Count;
            _customFormats.Add(format!);
            _formatIds[format!] = id;
            return id;
        }

        /// <summary>注册字体，返回 fontId</summary>
        public int RegisterFont(FontKey key)
        {
            if (_fontIds.TryGetValue(key, out var id))
                return id;
            id = _fonts.Count;
            _fonts.Add(key);
            _fontIds[key] = id;
            return id;
        }

        /// <summary>注册填充色（null=none），返回 fillId</summary>
        public int RegisterFill(string? color)
        {
            string key = color ?? "__none__";
            if (_fillIds.TryGetValue(key, out var id))
                return id;
            id = _fills.Count;
            _fills.Add(color);
            _fillIds[key] = id;
            return id;
        }

        /// <summary>注册边框，返回 borderId</summary>
        public int RegisterBorder(bool thin)
        {
            if (_borderIds.TryGetValue(thin, out var id))
                return id;
            id = _borders.Count;
            _borders.Add(thin);
            _borderIds[thin] = id;
            return id;
        }

        /// <summary>注册单元格格式组合，返回 styleIndex</summary>
        public int RegisterStyle(int fontId, int fillId, int borderId, uint numFmtId, bool center, bool wrap)
        {
            var key = new CellStyleKey(fontId, fillId, borderId, numFmtId, center, wrap);
            if (_cellFormatIds.TryGetValue(key, out var id))
                return id;
            id = _cellFormats.Count;
            _cellFormats.Add(key);
            _cellFormatIds[key] = id;
            return id;
        }

        /// <summary>便捷组合：给定颜色/加粗/格式/边框，一步拿到 styleIndex</summary>
        public int Compose(
            bool bold,
            string? fontColor,
            string? fillColor,
            string? numberFormat,
            bool border,
            bool center,
            bool wrap)
        {
            int font = RegisterFont(new FontKey(bold, NormalizeColor(fontColor)));
            int fill = RegisterFill(NormalizeColor(fillColor));
            int bd = RegisterBorder(border);
            uint fmt = (uint)RegisterNumberFormat(numberFormat);
            return RegisterStyle(font, fill, bd, fmt, center, wrap);
        }

        private static string? NormalizeColor(string? hex)
        {
            if (string.IsNullOrEmpty(hex))
                return null;
            return hex!.StartsWith("#") ? hex.Substring(1) : hex;
        }

        #endregion

        #region Stylesheet Build

        /// <summary>构建 Stylesheet（在注册全部完成后调用一次）</summary>
        public Stylesheet BuildStylesheet()
        {
            var stylesheet = new Stylesheet();

            // Schema 顺序：NumberingFormats → Fonts → Fills → Borders → CellStyleFormats → CellFormats
            if (_customFormats.Count > 0)
            {
                var numberingFormats = new NumberingFormats();
                for (int i = 0; i < _customFormats.Count; i++)
                {
                    numberingFormats.AppendChild(new NumberingFormat
                    {
                        NumberFormatId = (uint)(CustomFormatStartId + i),
                        FormatCode = _customFormats[i]
                    });
                }
                stylesheet.AppendChild(numberingFormats);
            }

            var fonts = new Fonts { Count = (uint)_fonts.Count, KnownFonts = true };
            foreach (var f in _fonts)
            {
                var font = new Font();
                if (f.Bold)
                    font.AppendChild(new Bold());
                font.AppendChild(new FontSize { Val = 11 });
                font.AppendChild(new FontName { Val = "Calibri" });
                if (f.Color != null)
                    font.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = HexBinaryValue.FromString(f.Color) });
                fonts.AppendChild(font);
            }
            stylesheet.AppendChild(fonts);

            var fills = new Fills { Count = (uint)_fills.Count };
            foreach (var color in _fills)
            {
                if (color == null)
                    fills.AppendChild(new Fill(new PatternFill { PatternType = PatternValues.None }));
                else if (color == "gray125")
                    fills.AppendChild(new Fill(new PatternFill { PatternType = PatternValues.Gray125 }));
                else
                    fills.AppendChild(new Fill(new PatternFill
                    {
                        PatternType = PatternValues.Solid,
                        ForegroundColor = new ForegroundColor { Rgb = HexBinaryValue.FromString(color) },
                        BackgroundColor = new BackgroundColor { Auto = true }
                    }));
            }
            stylesheet.AppendChild(fills);

            var borders = new Borders { Count = (uint)_borders.Count };
            foreach (var thin in _borders)
            {
                if (!thin)
                {
                    borders.AppendChild(new Border(
                        new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(),
                        new DiagonalBorder()));
                }
                else
                {
                    borders.AppendChild(new Border(
                        new LeftBorder { Style = BorderStyleValues.Thin },
                        new RightBorder { Style = BorderStyleValues.Thin },
                        new TopBorder { Style = BorderStyleValues.Thin },
                        new BottomBorder { Style = BorderStyleValues.Thin },
                        new DiagonalBorder()));
                }
            }
            stylesheet.AppendChild(borders);

            // 必需的 CellStyleFormats
            stylesheet.AppendChild(new CellStyleFormats(
                new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }));

            var cellFormats = new CellFormats { Count = (uint)_cellFormats.Count };
            foreach (var cf in _cellFormats)
            {
                cellFormats.AppendChild(new CellFormat
                {
                    NumberFormatId = cf.NumFmtId,
                    FontId = (uint)cf.FontId,
                    FillId = (uint)cf.FillId,
                    BorderId = (uint)cf.BorderId,
                    ApplyFont = true,
                    ApplyFill = true,
                    ApplyBorder = true,
                    ApplyNumberFormat = cf.NumFmtId != 0,
                    ApplyAlignment = cf.Center || cf.Wrap,
                    Alignment = (cf.Center || cf.Wrap)
                        ? new Alignment
                        {
                            Horizontal = cf.Center ? HorizontalAlignmentValues.Center : null,
                            Vertical = VerticalAlignmentValues.Center,
                            WrapText = cf.Wrap
                        }
                        : null
                });
            }
            stylesheet.AppendChild(cellFormats);

            return stylesheet;
        }

        #endregion
    }

    #region Keys

    internal readonly struct FontKey
    {
        public readonly bool Bold;
        public readonly string? Color;

        public FontKey(bool bold, string? color)
        {
            Bold = bold;
            Color = color;
        }

        public override int GetHashCode() => (Bold, Color).GetHashCode();
        public override bool Equals(object? obj) => obj is FontKey other && other.Bold == Bold && other.Color == Color;
    }

    internal readonly struct CellStyleKey
    {
        public readonly int FontId;
        public readonly int FillId;
        public readonly int BorderId;
        public readonly uint NumFmtId;
        public readonly bool Center;
        public readonly bool Wrap;

        public CellStyleKey(int fontId, int fillId, int borderId, uint numFmtId, bool center, bool wrap)
        {
            FontId = fontId;
            FillId = fillId;
            BorderId = borderId;
            NumFmtId = numFmtId;
            Center = center;
            Wrap = wrap;
        }

        public override int GetHashCode() =>
            (FontId, FillId, BorderId, NumFmtId, Center, Wrap).GetHashCode();

        public override bool Equals(object? obj) =>
            obj is CellStyleKey other &&
            other.FontId == FontId && other.FillId == FillId && other.BorderId == BorderId &&
            other.NumFmtId == NumFmtId && other.Center == Center && other.Wrap == Wrap;
    }

    #endregion
}
