// SPDX-License-Identifier: MIT

using System;

namespace VassasCo.Utility
{
    /// <summary>图片在单元格中的定位方式</summary>
    public enum ImagePlacement
    {
        /// <summary>
        /// 嵌入单元格（默认）：跟随单元格移动，且随行高列宽缩放。
        /// 适合"每行一张产品图"这类需要与行绑定的报表。
        /// </summary>
        Cell,

        /// <summary>
        /// 自由浮动：锚定到指定单元格，但不随行高列宽变化。
        /// 适合 Logo、水印、说明性插图。
        /// </summary>
        Floating
    }

    /// <summary>图片缺失（文件不存在 / 字节为空 / 格式无法识别）时的处理策略</summary>
    public enum MissingImagePolicy
    {
        /// <summary>跳过，单元格保持空白（默认）</summary>
        Skip,

        /// <summary>把图片来源（通常是文件路径）以文本写入单元格，便于事后排查缺失原因</summary>
        WriteSourceText,

        /// <summary>抛出 <see cref="ExcelMappingException"/></summary>
        Throw
    }

    /// <summary>图片尺寸自适应模式</summary>
    public enum ImageSizeMode
    {
        /// <summary>按图片原始像素尺寸插入</summary>
        Original,

        /// <summary>
        /// 等比缩放并完整放入指定宽高（默认）：不变形，图片必定完整可见。
        /// </summary>
        Fit,

        /// <summary>拉伸到指定宽高（可能变形）</summary>
        Stretch
    }

    /// <summary>
    /// 图片插入选项：控制图片来源解析、显示尺寸、定位方式与缺失兜底。
    /// </summary>
    /// <remarks>
    /// 关键点：
    /// <list type="bullet">
    /// <item>图片只能由 <b>DOM 引擎（ClosedXML）</b> 写入；流式引擎（OpenXML SAX）不支持随机写入图片部件。</item>
    /// <item>插入时会自动按需调整行高/列宽，否则图片会溢出单元格、视觉上"跑版"。</item>
    /// <item>若图片无法解析，默认 <see cref="MissingImagePolicy.Skip"/>，避免因个别缺图导致整个导出失败。</item>
    /// </list>
    /// </remarks>
    public sealed class ExcelImageOptions
    {
        /// <summary>
        /// 显示宽度（像素）；&lt;=0 时使用图片原始宽度。
        /// 与 <see cref="SizeMode"/> 配合：Fit/Stretch 下必须 &gt; 0 才有意义。
        /// </summary>
        public double Width { get; set; }

        /// <summary>显示高度（像素）；&lt;=0 时使用图片原始高度</summary>
        public double Height { get; set; }

        /// <summary>尺寸自适应模式，默认 <see cref="ImageSizeMode.Original"/></summary>
        public ImageSizeMode SizeMode { get; set; } = ImageSizeMode.Original;

        /// <summary>定位方式，默认 <see cref="ImagePlacement.Cell"/></summary>
        public ImagePlacement Placement { get; set; } = ImagePlacement.Cell;

        /// <summary>图片缺失时的处理策略，默认 <see cref="MissingImagePolicy.Skip"/></summary>
        public MissingImagePolicy OnMissing { get; set; } = MissingImagePolicy.Skip;

        /// <summary>
        /// 是否自动调高行高以容纳图片，默认 true。
        /// Excel 不会因插入图片自动撑高行，不做这一步图片会盖住下方内容。
        /// </summary>
        public bool AdjustRowHeight { get; set; } = true;

        /// <summary>是否自动加宽列宽以容纳图片，默认 true</summary>
        public bool AdjustColumnWidth { get; set; } = true;

        /// <summary>行高的额外留白（像素），默认 4，避免图片紧贴单元格边框</summary>
        public double RowPadding { get; set; } = 4;

        /// <summary>图片名称（同一 Sheet 内需唯一）；为空时自动生成</summary>
        public string? Name { get; set; }

        /// <summary>
        /// 图片来源相对路径的基准目录；为 null 时使用程序所在目录。
        /// 用于解决"相对路径基于当前工作目录"导致的找不到图片问题。
        /// </summary>
        public string? BaseDirectory { get; set; }

        /// <summary>默认选项</summary>
        public static ExcelImageOptions Default => new ExcelImageOptions();
    }
}
