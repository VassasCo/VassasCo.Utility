// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace VassasCo.Utility.Internals
{
    /// <summary>参数与文件路径安全校验</summary>
    internal static class Guard
    {
        /// <summary>非空校验</summary>
        public static T NotNull<T>(T? value, string paramName) where T : class
        {
            if (value is null)
                throw new ArgumentNullException(paramName);
            return value;
        }

        /// <summary>非空/空白字符串校验</summary>
        public static string NotNullOrWhiteSpace(string? value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("参数不能为 null 或空白字符串。", paramName);
            return value!;
        }

        /// <summary>
        /// 校验并规范化文件路径：非空、不含路径非法字符；返回绝对路径。
        /// 不校验文件是否存在（覆盖导出场景）。
        /// </summary>
        public static string EnsureValidFilePath(string? filePath)
        {
            NotNullOrWhiteSpace(filePath, nameof(filePath));

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath!);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ExcelFileAccessException($"文件路径不合法：{filePath}", ex);
            }

            string fileName = Path.GetFileName(fullPath);
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ExcelFileAccessException($"文件名包含非法字符：{fileName}");

            // 目录段中残留的非法字符（GetFullPath 不检查全部非法字符，如 双引号）
            var invalidPathChars = Path.GetInvalidPathChars();
            foreach (char c in fullPath)
            {
                if (Array.IndexOf(invalidPathChars, c) >= 0)
                    throw new ExcelFileAccessException($"文件路径包含非法字符：{filePath}");
            }

            return fullPath;
        }

        /// <summary>确保目标文件所在目录存在；不存在且 autoCreate=true 时创建</summary>
        public static void EnsureDirectoryForFile(string fullFilePath, bool autoCreate)
        {
            string? dir = Path.GetDirectoryName(fullFilePath);
            if (string.IsNullOrEmpty(dir))
                return;

            if (!Directory.Exists(dir))
            {
                if (!autoCreate)
                    throw new ExcelFileAccessException($"目标目录不存在：{dir}");
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ExcelFileAccessException($"无法创建目录：{dir}", ex);
                }
            }
        }

        /// <summary>校验目录路径并返回绝对路径</summary>
        public static string EnsureValidDirectory(string? directory)
        {
            NotNullOrWhiteSpace(directory, nameof(directory));
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(directory!);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ExcelFileAccessException($"目录路径不合法：{directory}", ex);
            }

            if (!Directory.Exists(fullPath))
                throw new ExcelFileAccessException($"目录不存在：{fullPath}");

            return fullPath;
        }
    }
}
