// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VassasCo.Utility.Internals;

namespace VassasCo.Utility
{
    /// <summary>
    /// VassasCo.Utility.ExcelMapper 统一门面：
    /// Excel/CSV 的导出（侵入式特性 + 非侵入式 Fluent 两套并行配置方式）、
    /// 动态字典导出、Excel/CSV 安全导入。面向桌面端提供异步/取消/进度支持。
    /// </summary>
    public static class ExcelMapper
    {
        #region Excel Export

        /// <summary>创建 Excel 导出构建器（Fluent 配置入口）</summary>
        public static ExcelMapperBuilder<T> Build<T>(IEnumerable<T> data)
        {
            return new ExcelMapperBuilder<T>(data);
        }

        /// <summary>快速导出 Excel 到文件（配置全部来自实体特性/约定），返回导出诊断信息</summary>
        public static ExportResult SaveToFile<T>(IEnumerable<T> data, string filePath)
        {
            return Build(data).ToFile(filePath);
        }

        /// <summary>快速导出 Excel 到流，返回导出诊断信息</summary>
        public static ExportResult SaveToStream<T>(IEnumerable<T> data, Stream stream)
        {
            return Build(data).ToStream(stream);
        }

        /// <summary>异步导出 Excel 到文件（支持取消与进度报告），返回导出诊断信息</summary>
        public static Task<ExportResult> SaveToFileAsync<T>(
            IEnumerable<T> data,
            string filePath,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            return Build(data).ToFileAsync(filePath, cancellationToken, progress);
        }

        /// <summary>异步导出 Excel 到流，返回导出诊断信息</summary>
        public static Task<ExportResult> SaveToStreamAsync<T>(
            IEnumerable<T> data,
            Stream stream,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            return Build(data).ToStreamAsync(stream, cancellationToken, progress);
        }

        #endregion

        #region CSV Export

        /// <summary>创建 CSV 导出构建器（与 Excel 共用特性/Fluent 列配置）</summary>
        public static CsvExportBuilder<T> BuildCsv<T>(IEnumerable<T> data)
        {
            return new CsvExportBuilder<T>(data);
        }

        /// <summary>快速导出 CSV 到文件（UTF-8 BOM、公式注入防护默认开启）</summary>
        public static void WriteCsv<T>(IEnumerable<T> data, string filePath)
        {
            BuildCsv(data).ToFile(filePath);
        }

        /// <summary>快速导出 CSV 到流</summary>
        public static void WriteCsvToStream<T>(IEnumerable<T> data, Stream stream)
        {
            BuildCsv(data).ToStream(stream);
        }

        /// <summary>异步导出 CSV 到文件</summary>
        public static Task WriteCsvAsync<T>(
            IEnumerable<T> data,
            string filePath,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            return BuildCsv(data).ToFileAsync(filePath, cancellationToken, progress);
        }

        #endregion

        #region Dictionary Export

        /// <summary>动态字典导出到文件（键少宽表，键多竖表）</summary>
        public static void DictionaryToFile(IDictionary dictionary, string filePath, string sheetName = "Dictionary")
        {
            string fullPath = Guard.EnsureValidFilePath(filePath);
            Guard.EnsureDirectoryForFile(fullPath, autoCreate: true);
            DictionaryWriter.Write(dictionary, fullPath, null, sheetName, new ExcelStyle(), new ExcelExportOptions());
        }

        /// <summary>动态字典导出到流</summary>
        public static void DictionaryToStream(IDictionary dictionary, Stream stream, string sheetName = "Dictionary")
        {
            DictionaryWriter.Write(dictionary, null, stream, sheetName, new ExcelStyle(), new ExcelExportOptions());
        }

        /// <summary>异步导出动态字典到文件</summary>
        public static Task DictionaryToFileAsync(
            IDictionary dictionary,
            string filePath,
            string sheetName = "Dictionary",
            CancellationToken cancellationToken = default)
        {
            string fullPath = Guard.EnsureValidFilePath(filePath);
            Guard.EnsureDirectoryForFile(fullPath, autoCreate: true);
            return DictionaryWriter.WriteAsync(dictionary, fullPath, null, sheetName,
                new ExcelStyle(), new ExcelExportOptions(), cancellationToken);
        }

        #endregion

        #region Import

        /// <summary>
        /// 按文件扩展名自动选择导入器：.csv 走 CSV 解析，其余按 Excel 工作簿处理。
        /// </summary>
        public static ImportResult<T> Import<T>(
            string filePath,
            EntityMap<T>? map = null,
            CancellationToken cancellationToken = default)
        {
            string fullPath = Guard.EnsureValidFilePath(filePath);
            string extension = Path.GetExtension(fullPath);
            if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
                return ImportFromCsv(fullPath, map, cancellationToken: cancellationToken);
            return ImportFromExcel(fullPath, map: map, cancellationToken: cancellationToken);
        }

        /// <summary>从 Excel 文件导入（默认第一个工作表）</summary>
        public static ImportResult<T> ImportFromExcel<T>(
            string filePath,
            string? sheetName = null,
            int? sheetIndex = null,
            EntityMap<T>? map = null,
            CancellationToken cancellationToken = default)
        {
            return ExcelImporter.ImportFile(filePath, sheetName, sheetIndex, map, cancellationToken);
        }

        /// <summary>从 Excel 流导入</summary>
        public static ImportResult<T> ImportFromExcel<T>(
            Stream stream,
            string? sheetName = null,
            int? sheetIndex = null,
            EntityMap<T>? map = null,
            CancellationToken cancellationToken = default)
        {
            return ExcelImporter.ImportStream(stream, sheetName, sheetIndex, map, cancellationToken);
        }

        /// <summary>从 CSV 文件导入</summary>
        public static ImportResult<T> ImportFromCsv<T>(
            string filePath,
            EntityMap<T>? map = null,
            char delimiter = ',',
            CancellationToken cancellationToken = default)
        {
            return CsvImporter.ImportFile(filePath, map, delimiter, cancellationToken);
        }

        /// <summary>从 CSV 流导入</summary>
        public static ImportResult<T> ImportFromCsv<T>(
            Stream stream,
            EntityMap<T>? map = null,
            char delimiter = ',',
            CancellationToken cancellationToken = default)
        {
            return CsvImporter.ImportStream(stream, map, delimiter, cancellationToken);
        }

        #endregion
    }
}
