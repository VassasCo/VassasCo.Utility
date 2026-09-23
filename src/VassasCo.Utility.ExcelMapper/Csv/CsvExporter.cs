// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VassasCo.Utility.Internals;

namespace VassasCo.Utility
{
    /// <summary>
    /// CSV 导出构建器：复用侵入式特性 / 非侵入式 <see cref="EntityMap{T}"/> 的列配置，
    /// 输出 RFC 4180 安全 CSV。集合属性无法在 CSV 中表达，对应列输出空字符串。
    /// </summary>
    public sealed class CsvExportBuilder<T>
    {
        private readonly IEnumerable<T> _data;
        private EntityMap<T>? _map;
        private CsvExportOptions _csvOptions = new CsvExportOptions();
        private ExcelStyle _style = new ExcelStyle();
        private readonly ExcelExportOptions _excelOptions = new ExcelExportOptions();

        internal CsvExportBuilder(IEnumerable<T> data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>使用非侵入式实体映射</summary>
        public CsvExportBuilder<T> WithMap(EntityMap<T> map)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            return this;
        }

        /// <summary>用 Fluent 方式就地声明非侵入式映射</summary>
        public CsvExportBuilder<T> WithMap(Action<EntityMap<T>> configure)
        {
            if (configure is null) throw new ArgumentNullException(nameof(configure));
            _map = new EntityMap<T>();
            configure(_map);
            return this;
        }

        /// <summary>调整 CSV 选项</summary>
        public CsvExportBuilder<T> Configure(Action<CsvExportOptions> configure)
        {
            if (configure is null) throw new ArgumentNullException(nameof(configure));
            configure(_csvOptions);
            return this;
        }

        /// <summary>设置视觉/文本样式（布尔文本、枚举描述等）</summary>
        public CsvExportBuilder<T> WithStyle(ExcelStyle style)
        {
            _style = style ?? throw new ArgumentNullException(nameof(style));
            return this;
        }

        /// <summary>同步导出到文件</summary>
        public void ToFile(string filePath, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
        {
            string fullPath = Guard.EnsureValidFilePath(filePath);
            Guard.EnsureDirectoryForFile(fullPath, _csvOptions.AutoCreateDirectory);

            using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                WriteToStream(stream, cancellationToken, progress);
            }
        }

        /// <summary>同步导出到流</summary>
        public void ToStream(Stream stream, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite) throw new ArgumentException("输出流不可写。", nameof(stream));
            WriteToStream(stream, cancellationToken, progress);
        }

        /// <summary>异步导出到文件</summary>
        public Task ToFileAsync(string filePath, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
        {
            string fullPath = Guard.EnsureValidFilePath(filePath);
            Guard.EnsureDirectoryForFile(fullPath, _csvOptions.AutoCreateDirectory);

            return Task.Run(async () =>
            {
                using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None,
                           bufferSize: 4096, useAsync: true))
                {
                    await WriteToStreamAsync(stream, cancellationToken, progress).ConfigureAwait(false);
                }
            }, cancellationToken);
        }

        /// <summary>异步导出到流</summary>
        public Task ToStreamAsync(Stream stream, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite) throw new ArgumentException("输出流不可写。", nameof(stream));
            return WriteToStreamAsync(stream, cancellationToken, progress);
        }

        #region Writing

        private void WriteToStream(Stream stream, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            var document = PlanBuilder.BuildDocument(_data, null, _map, _excelOptions);
            var leaves = ColumnPlan.CollectAll(document.Primary.Sheet.Columns);

            var encoding = new UTF8Encoding(_csvOptions.UseUtf8Bom);
            using (var writer = new StreamWriter(stream, encoding, 1024, leaveOpen: true))
            {
                WriteTo(writer, leaves, cancellationToken, progress);
                writer.Flush();
            }
        }

        private async Task WriteToStreamAsync(Stream stream, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            var document = PlanBuilder.BuildDocument(_data, null, _map, _excelOptions);
            var leaves = ColumnPlan.CollectAll(document.Primary.Sheet.Columns);

            var encoding = new UTF8Encoding(_csvOptions.UseUtf8Bom);
            using (var writer = new StreamWriter(stream, encoding, 1024, leaveOpen: true))
            {
                await WriteToAsync(writer, leaves, cancellationToken, progress).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        private void WriteTo(
            StreamWriter writer,
            List<LeafColumnInfo> leaves,
            CancellationToken cancellationToken,
            IProgress<int>? progress)
        {
            if (_csvOptions.IncludeHeader)
                WriteLine(writer, BuildHeader(leaves));

            int rowsDone = 0;
            foreach (var row in _data)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteLine(writer, BuildRow(row, leaves));
                rowsDone++;
                if ((rowsDone & 0xFF) == 0)
                    progress?.Report(rowsDone);
            }
            progress?.Report(rowsDone);
        }

        private async Task WriteToAsync(
            StreamWriter writer,
            List<LeafColumnInfo> leaves,
            CancellationToken cancellationToken,
            IProgress<int>? progress)
        {
            if (_csvOptions.IncludeHeader)
                await writer.WriteLineAsync(BuildHeader(leaves)).ConfigureAwait(false);

            int rowsDone = 0;
            foreach (var row in _data)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(BuildRow(row, leaves)).ConfigureAwait(false);
                rowsDone++;
                if ((rowsDone & 0xFF) == 0)
                    progress?.Report(rowsDone);
            }
            progress?.Report(rowsDone);
        }

        private string BuildHeader(List<LeafColumnInfo> leaves)
        {
            var fields = new string[leaves.Count];
            for (int i = 0; i < leaves.Count; i++)
                fields[i] = leaves[i].Column.DisplayName;
            return JoinLine(fields);
        }

        private string BuildRow(T row, List<LeafColumnInfo> leaves)
        {
            var fields = new string[leaves.Count];
            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i].Column;
                string text;
                if (leaf.IsCollection)
                {
                    text = "";
                }
                else
                {
                    object? raw = ValueServices.Resolve(row, leaf);
                    object? converted = ValueServices.ApplyConverter(raw, leaf);
                    text = ValueServices.ToText(converted, leaf, _style, _excelOptions);
                    text = ApplyInjectionGuard(text);
                }
                fields[i] = Escape(text);
            }
            return JoinLine(fields);
        }

        private string JoinLine(string[] escapedFields)
        {
            if (escapedFields.Length == 0)
                return "";
            return string.Join(_csvOptions.Delimiter.ToString(), escapedFields);
        }

        private void WriteLine(StreamWriter writer, string line)
        {
            writer.Write(line);
            writer.Write("\r\n");
        }

        /// <summary>RFC 4180 转义：含分隔符/引号/换行/Tab 时加引号，引号双写</summary>
        private string Escape(string field)
        {
            bool needQuote = _csvOptions.QuoteAllFields
                || field.IndexOf('"') >= 0
                || field.IndexOf(_csvOptions.Delimiter) >= 0
                || field.IndexOf('\n') >= 0
                || field.IndexOf('\r') >= 0
                || field.IndexOf('\t') >= 0;

            if (!needQuote)
                return field;

            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>公式注入防护：危险首字符前置单引号</summary>
        private string ApplyInjectionGuard(string field)
        {
            if (!_csvOptions.FormulaInjectionGuard || field.Length == 0)
                return field;

            char first = field[0];
            if (first == '=' || first == '+' || first == '-' || first == '@'
                || first == '\t' || first == '\r')
            {
                return "'" + field;
            }
            return field;
        }

        #endregion
    }
}
