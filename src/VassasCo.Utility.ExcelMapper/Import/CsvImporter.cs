// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// CSV → 对象导入：RFC 4180 解析、首行表头映射、逐字段安全转换与错误收集。
    /// 支持 UTF-8（自动识别 BOM）与自定义分隔符；空行跳过。
    /// </summary>
    internal static class CsvImporter
    {
        public static ImportResult<T> ImportFile<T>(
            string filePath,
            EntityMap<T>? map,
            char delimiter,
            CancellationToken cancellationToken)
        {
            string fullPath = Guard.EnsureValidFilePath(filePath);
            if (!File.Exists(fullPath))
                throw new ExcelFileAccessException($"文件不存在：{fullPath}");

            using (var reader = new StreamReader(fullPath, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
            {
                return ImportReader(reader, map, delimiter, cancellationToken);
            }
        }

        public static ImportResult<T> ImportStream<T>(
            Stream stream,
            EntityMap<T>? map,
            char delimiter,
            CancellationToken cancellationToken)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("输入流不可读。", nameof(stream));

            using (var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true,
                       bufferSize: 1024, leaveOpen: true))
            {
                return ImportReader(reader, map, delimiter, cancellationToken);
            }
        }

        private static ImportResult<T> ImportReader<T>(
            TextReader reader,
            EntityMap<T>? map,
            char delimiter,
            CancellationToken cancellationToken)
        {
            var options = new ExcelExportOptions();
            var accessor = ImportAccessor<T>.Build(map, options);
            var parser = new CsvLineParser(reader, delimiter);

            var result = new ImportResult<T>();
            foreach (var warning in accessor.HeaderWarnings)
                result.Errors.Add(new ImportError(1, null, null, warning));

            List<string>? headers = parser.ReadRecord();
            if (headers is null)
                return result; // 空文件：返回空结果

            var bindings = BuildBindings(headers, accessor, result);

            int rowNo = 1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var record = parser.ReadRecord();
                if (record is null)
                    break;

                rowNo++;
                if (IsAllEmpty(record))
                    continue;

                T item;
                try
                {
                    item = (T)Activator.CreateInstance(typeof(T))!;
                }
                catch (Exception ex) when (ex is MissingMethodException or MemberAccessException)
                {
                    throw new ExcelImportException($"类型 {typeof(T).Name} 缺少公共无参构造函数，无法创建实例。", ex);
                }

                for (int i = 0; i < record.Count; i++)
                {
                    var binding = bindings[i];
                    string rawText = record[i];
                    if (binding is null)
                    {
                        // 未识别表头：仅当有值时记录提示，不阻断
                        if (rawText.Length > 0)
                        {
                            result.Errors.Add(new ImportError(rowNo, headers[i], rawText, "未识别的列，已忽略"));
                        }
                        continue;
                    }

                    var leaf = binding;
                    Type targetType = leaf.ValueType;
                    if (!ImportValueConverter.TryConvert(rawText, targetType, out object? converted, out string reason))
                    {
                        result.Errors.Add(new ImportError(rowNo, leaf.DisplayName, rawText, reason));
                        continue;
                    }

                    if (!accessor.TrySetValue(item, leaf, converted, out reason))
                    {
                        result.Errors.Add(new ImportError(rowNo, leaf.DisplayName, rawText, reason));
                    }
                }

                result.Items.Add(item);
            }

            return result;
        }

        private static ColumnPlan?[] BuildBindings<T>(
            List<string> headers,
            ImportAccessor<T> accessor,
            ImportResult<T> result)
        {
            var bindings = new ColumnPlan?[headers.Count];
            for (int i = 0; i < headers.Count; i++)
                bindings[i] = accessor.FindColumn(headers[i]);
            return bindings;
        }

        private static bool IsAllEmpty(List<string> record)
        {
            foreach (var field in record)
            {
                if (field.Length > 0)
                    return false;
            }
            return true;
        }
    }
}
