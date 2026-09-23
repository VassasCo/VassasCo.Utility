// SPDX-License-Identifier: MIT

namespace VassasCo.Utility
{
    /// <summary>导出行为选项（机制类配置，与视觉类 <see cref="ExcelStyle"/> 分离）</summary>
    public class ExcelExportOptions
    {
        /// <summary>每页最大数据行数；null 不分页。单 Sheet 上限 1,048,576 行（表头不计入）</summary>
        public int? PageSize { get; set; }

        /// <summary>分页输出方式，默认 <see cref="PageMode.MultipleSheets"/></summary>
        public PageMode PageMode { get; set; } = PageMode.MultipleSheets;

        /// <summary>分页 Sheet 名称模板（{0}=基础名，{1}=页码，从 1 开始），默认 "{0}_{1}"</summary>
        public string SheetPageNameFormat { get; set; } = "{0}_{1}";

        /// <summary>集合属性渲染方式，默认 <see cref="ArrayRenderMode.ChildSheet"/></summary>
        public ArrayRenderMode ArrayRender { get; set; } = ArrayRenderMode.ChildSheet;

        /// <summary>导出引擎，默认 <see cref="ExportEngineKind.Auto"/></summary>
        public ExportEngineKind Engine { get; set; } = ExportEngineKind.Auto;

        /// <summary>DOM 引擎自动切换到流式引擎的数据量阈值（行数），默认 100000；Engine=Auto 时生效</summary>
        public int StreamingThreshold { get; set; } = 100000;

        /// <summary>写入文件时自动创建不存在的目录，默认 true</summary>
        public bool AutoCreateDirectory { get; set; } = true;

        /// <summary>长数字自动转文本策略，默认 <see cref="ForceTextAutoMode.Both"/></summary>
        public ForceTextAutoMode AutoForceText { get; set; } = ForceTextAutoMode.Both;

        /// <summary>单元格文本超长（&gt;32767 字符）时自动截断，默认 true；false 时超长单元格被转为文本仍超限将抛异常</summary>
        public bool TruncateLongText { get; set; } = true;

        /// <summary>byte[] 二进制属性渲染方式，默认 <see cref="BinaryRenderMode.Base64"/></summary>
        public BinaryRenderMode BinaryRender { get; set; } = BinaryRenderMode.Base64;

        /// <summary>简单条件格式优先翻译为 Excel 原生条件格式（DOM 引擎），默认 true；false 时全部服务端求值写为静态样式</summary>
        public bool PreferNativeConditionalFormat { get; set; } = true;

        /// <summary>导出前对计划做配置校验（列重名、多集合摊平等），默认 true</summary>
        public ValidatePlanMode ValidatePlan { get; set; } = ValidatePlanMode.Throw;

        /// <summary>默认选项</summary>
        public static ExcelExportOptions Default => new ExcelExportOptions();
    }

    /// <summary>计划校验模式</summary>
    public enum ValidatePlanMode
    {
        /// <summary>发现配置问题直接抛 <see cref="ExcelMappingException"/></summary>
        Throw,

        /// <summary>忽略校验问题（不推荐，可能生成错误数据）</summary>
        Skip
    }
}
