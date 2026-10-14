// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VassasCo.Utility.Internals
{
    /// <summary>条件格式规则（计划层）</summary>
    internal sealed class ConditionalRule
    {
        /// <summary>比较操作符</summary>
        public ConditionOperator Operator;

        /// <summary>比较值（Between 下界）</summary>
        public double Value;

        /// <summary>Between 上界</summary>
        public double Value2;

        /// <summary>字体颜色（十六进制）</summary>
        public string? FontColor;

        /// <summary>背景色（十六进制）</summary>
        public string? FillColor;

        /// <summary>加粗</summary>
        public bool Bold;

        /// <summary>是否为行级规则（带任意谓词）</summary>
        public bool IsRowRule;

        /// <summary>行级/服务端求值谓词（入参为整行对象）</summary>
        public Func<object, bool>? Predicate;

        /// <summary>所属叶子列（行级规则时为 null）</summary>
        public ColumnPlan? Column;

        /// <summary>是否可翻译为 Excel 原生条件格式</summary>
        public bool IsNativeTranslatable =>
            !IsRowRule && Predicate is null && Operator != ConditionOperator.Contains;
    }

    /// <summary>列计划节点：描述一个属性节点如何导出（叶子=一列，嵌套=表头分组）</summary>
    internal sealed class ColumnPlan
    {
        /// <summary>属性名</summary>
        public string Name = "";

        /// <summary>表头显示名</summary>
        public string DisplayName = "";

        /// <summary>显式排序值（null 未指定）</summary>
        public int? Order;

        /// <summary>是否忽略不导出</summary>
        public bool Ignore;

        /// <summary>父节点</summary>
        public ColumnPlan? Parent;

        /// <summary>子节点</summary>
        public List<ColumnPlan> Children = new List<ColumnPlan>();

        /// <summary>是否为叶子节点</summary>
        public bool IsLeaf;

        /// <summary>叶子是否为集合类型</summary>
        public bool IsCollection;

        /// <summary>集合元素类型</summary>
        public Type? CollectionElementType;

        /// <summary>该节点对应的反射属性（相对父节点所属类型）</summary>
        public PropertyInfo? Property;

        /// <summary>叶子值类型</summary>
        public Type ValueType = typeof(object);

        /// <summary>强制文本写入</summary>
        public bool ForceText;

        /// <summary>列级数字/日期格式</summary>
        public string? Format;

        /// <summary>固定列宽</summary>
        public double? FixedWidth;

        /// <summary>自动换行</summary>
        public bool WrapText;

        /// <summary>作为超链接</summary>
        public bool AsHyperlink;

        /// <summary>叶子是否作为图片插入（属性值可为文件路径、byte[] 或 Stream）</summary>
        public bool AsImage;

        /// <summary>图片插入选项（<see cref="AsImage"/> 为 true 时生效）</summary>
        public ExcelImageOptions? Image;

        /// <summary>叶子条件规则</summary>
        public List<ConditionalRule> Rules = new List<ConditionalRule>();

        /// <summary>值转换委托（导出前）</summary>
        public Func<object, object?>? Converter;

        /// <summary>循环引用标记节点（值固定输出 "(循环引用)"）</summary>
        public bool IsCycleMarker;

        /// <summary>在属性树中的深度（根=0）</summary>
        public int Depth;

        /// <summary>叶子序号（CollectLeaves 后赋值）</summary>
        public int LeafIndex;

        /// <summary>从根对象到该叶子的属性链（用于零 Split 取值）</summary>
        public PropertyInfo[] PropertyChain = Array.Empty<PropertyInfo>();

        /// <summary>完整属性路径（点分隔）</summary>
        public string FullPath = "";

        private int? _leafCount;

        /// <summary>该节点下叶子列数</summary>
        public int LeafCount
        {
            get
            {
                if (_leafCount.HasValue)
                    return _leafCount.Value;
                if (IsLeaf)
                    return 1;
                int sum = 0;
                foreach (var c in Children)
                    sum += c.LeafCount;
                _leafCount = sum;
                return sum;
            }
        }

        /// <summary>递归收集叶子列</summary>
        public void CollectLeaves(List<LeafColumnInfo> leaves, List<PropertyInfo> chain)
        {
            if (Property != null)
                chain.Add(Property);

            if (IsLeaf)
            {
                LeafIndex = leaves.Count;
                PropertyChain = chain.ToArray();
                FullPath = string.Join(".", chain.Select(p => p.Name));
                leaves.Add(new LeafColumnInfo(this));
            }
            else
            {
                foreach (var child in Children)
                    child.CollectLeaves(leaves, chain);
            }

            if (Property != null)
                chain.RemoveAt(chain.Count - 1);
        }

        /// <summary>从树根收集叶子（便捷入口）</summary>
        public static List<LeafColumnInfo> CollectAll(IReadOnlyList<ColumnPlan> roots)
        {
            var leaves = new List<LeafColumnInfo>();
            var chain = new List<PropertyInfo>();
            foreach (var root in roots)
                root.CollectLeaves(leaves, chain);
            return leaves;
        }
    }

    /// <summary>叶子列运行时信息</summary>
    internal sealed class LeafColumnInfo
    {
        /// <summary>对应的列计划</summary>
        public readonly ColumnPlan Column;

        /// <summary>叶子序号</summary>
        public int Index => Column.LeafIndex;

        /// <summary>初始化实例</summary>
        public LeafColumnInfo(ColumnPlan column)
        {
            Column = column;
        }
    }

    /// <summary>单个 Sheet 的导出计划</summary>
    internal sealed class SheetPlan
    {
        /// <summary>请求的 Sheet 名称（最终名由名称注册表确定）</summary>
        public string? RequestedName;

        /// <summary>行元素类型</summary>
        public Type RowType = typeof(object);

        /// <summary>根列计划</summary>
        public List<ColumnPlan> Columns = new List<ColumnPlan>();

        /// <summary>行数据（非泛型 IEnumerable）</summary>
        public System.Collections.IEnumerable Rows = Array.Empty<object>();

        /// <summary>是否为集合子 Sheet</summary>
        public bool IsChildSheet;

        /// <summary>子 Sheet 父关联列标题</summary>
        public string? ParentColumnName;

        /// <summary>固定位置图片（Logo / 水印 / 说明图），行数据写完后再统一插入</summary>
        public List<PicturePlan> Pictures = new List<PicturePlan>();

        /// <summary>表头之上的自由排版行（标题、元信息键值表等）</summary>
        public List<ReportRow> Preamble = new List<ReportRow>();

        /// <summary>数据区之下的自由排版行（汇总、备注、签名栏等）</summary>
        public List<ReportRow> Postamble = new List<ReportRow>();

        /// <summary>是否存在位于数据表之外的布局行</summary>
        public bool HasLayoutRows => Preamble.Count > 0 || Postamble.Count > 0;
    }

    /// <summary>
    /// 一张待插入图片的计划：来源（文件路径或字节）+ 锚定单元格 + 插入选项。
    /// </summary>
    /// <remarks>
    /// 由两种途径产生：
    /// <list type="number">
    /// <item>数据列：实体属性标注 <c>[ExcelImage]</c> 或 Fluent <c>AsImage()</c>，引擎按行逐条生成；</item>
    /// <item>固定图片：通过 <c>ExcelMapperBuilder.WithPicture(...)</c> 显式添加。</item>
    /// </list>
    /// </remarks>
    internal sealed class PicturePlan
    {
        /// <summary>图片文件全路径（相对路径会结合 BaseDirectory 解析）</summary>
        public string? FilePath;

        /// <summary>图片字节内容；不为 null 时优先于 FilePath</summary>
        public byte[]? Bytes;

        /// <summary>锚定行号（Excel 行号，1 起）</summary>
        public int Row = 1;

        /// <summary>锚定列号（Excel 列号，1 起）</summary>
        public int Column = 1;

        /// <summary>插入选项</summary>
        public ExcelImageOptions Options = new ExcelImageOptions();

        /// <summary>图片名称（同一 Sheet 内需唯一）；为空时由引擎自动生成</summary>
        public string? Name;

        /// <summary>
        /// 解析出图片字节内容。优先级：Bytes &gt; FilePath。
        /// 文件不存在、路径非法、字节为空时返回 null（表示"缺失"），由调用方按 OnMissing 策略处理。
        /// </summary>
        public byte[]? ResolveBytes()
        {
            if (Bytes != null && Bytes.Length > 0)
                return Bytes;

            if (string.IsNullOrWhiteSpace(FilePath))
                return null;

            try
            {
                string path = ResolvePath(FilePath!, Options?.BaseDirectory);
                if (!File.Exists(path))
                    return null;

                byte[] data = File.ReadAllBytes(path);
                return data.Length == 0 ? null : data;
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException ||
                ex is System.Security.SecurityException)
            {
                // 路径非法 / 无权限 / 设备未就绪等：统一按"缺失"处理，避免单次异常中断整个导出
                return null;
            }
        }

        /// <summary>
        /// 将可能是相对路径的图片地址解析为全路径。
        /// 基准目录顺序：BaseDirectory（显式指定） → 程序所在目录（AppContext.BaseDirectory）。
        /// </summary>
        private static string ResolvePath(string path, string? baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            if (Path.IsPathRooted(path))
                return path;

            // 说明：netstandard2.0 / net472 的参考程序集未给 string.IsNullOrWhiteSpace 标注
            // [NotNullWhen(false)]，编译器无法自动收窄空值，这里显式合并空值以消除告警。
            string baseDir = baseDirectory ?? string.Empty;
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                // AppContext.BaseDirectory 在 netstandard2.0 上可用；
                // 用它而非 Environment.CurrentDirectory，可避免工作目录被 OpenFileDialog 等改动后找不到图片。
                baseDir = AppContext.BaseDirectory ?? string.Empty;
            }

            return string.IsNullOrEmpty(baseDir) ? path : Path.Combine(baseDir, path);
        }
    }

    /// <summary>文档中的一个 Sheet 部件（计划 + 行级规则）</summary>
    internal sealed class DocumentPart
    {
        /// <summary>Sheet 计划</summary>
        public SheetPlan Sheet = new SheetPlan();

        /// <summary>作用于该 Sheet 的行级条件规则</summary>
        public List<ConditionalRule> RowRules = new List<ConditionalRule>();
    }

    /// <summary>一次导出的完整文档计划</summary>
    internal sealed class ExportDocument
    {
        /// <summary>主 Sheet 部件（子 Sheet 在引擎写入过程中动态生成）</summary>
        public DocumentPart Primary = new DocumentPart();

        /// <summary>工作簿级追加 Sheet（多数据集导出）</summary>
        public List<DocumentPart> AdditionalParts = new List<DocumentPart>();
    }
}
