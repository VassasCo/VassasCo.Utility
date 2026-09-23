// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace VassasCo.Utility
{
    /// <summary>ExcelMapper 配置/映射类异常的基类</summary>
    public class ExcelMappingException : Exception
    {
        /// <summary>初始化实例</summary>
        public ExcelMappingException(string message) : base(message) { }

        /// <summary>初始化实例</summary>
        public ExcelMappingException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>导出计划校验异常</summary>
    public sealed class ExcelPlanValidationException : ExcelMappingException
    {
        /// <summary>校验出的问题列表</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>初始化实例</summary>
        public ExcelPlanValidationException(IReadOnlyList<string> errors)
            : base("导出计划校验失败：" + string.Join("；", errors))
        {
            Errors = errors;
        }
    }

    /// <summary>文件/路径安全校验异常</summary>
    public sealed class ExcelFileAccessException : ExcelMappingException
    {
        /// <summary>初始化实例</summary>
        public ExcelFileAccessException(string message) : base(message) { }

        /// <summary>初始化实例</summary>
        public ExcelFileAccessException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>读取导入异常（文件格式无法识别等致命问题）</summary>
    public sealed class ExcelImportException : Exception
    {
        /// <summary>初始化实例</summary>
        public ExcelImportException(string message) : base(message) { }

        /// <summary>初始化实例</summary>
        public ExcelImportException(string message, Exception innerException) : base(message, innerException) { }
    }
}
