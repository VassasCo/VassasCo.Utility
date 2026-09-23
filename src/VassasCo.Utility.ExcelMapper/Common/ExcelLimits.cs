// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace VassasCo.Utility.Internals
{
    /// <summary>Excel 规范常量</summary>
    internal static class ExcelLimits
    {
        /// <summary>单 Sheet 最大行数（含表头）</summary>
        public const int MaxRows = 1_048_576;

        /// <summary>单 Sheet 最大列数</summary>
        public const int MaxColumns = 16_384;

        /// <summary>Sheet 名称最大长度</summary>
        public const int MaxSheetNameLength = 31;

        /// <summary>单元格文本最大字符数</summary>
        public const int MaxCellTextLength = 32_767;

        /// <summary>列树递归最大深度（防止循环引用栈溢出）</summary>
        public const int MaxColumnDepth = 16;
    }

    /// <summary>Sheet 名称清洗与注册（保证工作簿内唯一、符合 Sheet 命名规范）</summary>
    internal sealed class SheetNameRegistry
    {
        private readonly HashSet<string> _usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly char[] InvalidChars = { '\\', '/', '*', '?', ':', '[', ']' };

        /// <summary>清洗 Sheet 名称：去非法字符、截断 31 字符</summary>
        public static string Sanitize(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return "Sheet1";

            foreach (char ch in InvalidChars)
                name = name!.Replace(ch.ToString(), string.Empty);

            name = name!.Trim('\'').Trim();

            if (name.Length > ExcelLimits.MaxSheetNameLength)
                name = name.Substring(0, ExcelLimits.MaxSheetNameLength);

            return name.Length == 0 ? "Sheet1" : name;
        }

        /// <summary>注册一个 Sheet 名称并保证唯一（重名自动追加 (n)）</summary>
        public string Register(string? name)
        {
            string baseName = Sanitize(name);
            string candidate = baseName;
            int suffix = 1;

            while (_usedNames.Contains(candidate))
            {
                string suffixStr = $"({suffix})";
                string truncated = baseName.Length + suffixStr.Length > ExcelLimits.MaxSheetNameLength
                    ? baseName.Substring(0, ExcelLimits.MaxSheetNameLength - suffixStr.Length)
                    : baseName;
                candidate = truncated + suffixStr;
                suffix++;
            }

            _usedNames.Add(candidate);
            return candidate;
        }

        /// <summary>名称是否已被注册</summary>
        public bool Contains(string name) => _usedNames.Contains(name);
    }
}
