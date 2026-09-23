// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
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
