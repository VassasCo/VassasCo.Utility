// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VassasCo.Utility.Internals;

namespace VassasCo.Utility
{
    /// <summary>
    /// Excel 导出构建器（非侵入式 Fluent 入口）：配置 Sheet 名、样式、选项、实体映射，
    /// 支持多数据集多 Sheet、同步/异步导出、取消令牌、进度报告与多文件分页。
    /// 侵入式用法无需本配置 —— 直接在实体上贴特性后调用 <see cref="ExcelMapper.SaveToFile{T}"/> 即可。
    /// </summary>
    public sealed class ExcelMapperBuilder<T>
    {
        private readonly IEnumerable<T> _data;
        private string? _sheetName;
        private ExcelStyle _style = new ExcelStyle();
        private ExcelExportOptions _options = new ExcelExportOptions();
        private EntityMap<T>? _map;
        private readonly List<AdditionalSheet> _additional = new List<AdditionalSheet>();

        internal ExcelMapperBuilder(IEnumerable<T> data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>设置主 Sheet 名称（null/空白回退类型名或 [ExcelSheet(Name=...)]）</summary>
        public ExcelMapperBuilder<T> WithSheetName(string? sheetName)
        {
            _sheetName = sheetName;
            return this;
        }

        /// <summary>替换整体样式配置</summary>
        public ExcelMapperBuilder<T> WithStyle(ExcelStyle style)
        {
            _style = style ?? throw new ArgumentNullException(nameof(style));
            return this;
        }

        /// <summary>就地调整样式</summary>
        public ExcelMapperBuilder<T> WithStyle(Action<ExcelStyle> configure)
        {
            if (configure is null) throw new ArgumentNullException(nameof(configure));
            configure(_style);
            return this;
        }

        /// <summary>替换整体导出选项</summary>
        public ExcelMapperBuilder<T> WithOptions(ExcelExportOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            return this;
        }

        /// <summary>就地调整导出选项</summary>
        public ExcelMapperBuilder<T> WithOptions(Action<ExcelExportOptions> configure)
        {
            if (configure is null) throw new ArgumentNullException(nameof(configure));
            configure(_options);
            return this;
        }

        /// <summary>使用非侵入式实体映射（不在业务类上贴特性）</summary>
        public ExcelMapperBuilder<T> WithMap(EntityMap<T> map)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            return this;
        }

        /// <summary>用 Fluent 方式就地声明非侵入式实体映射</summary>
        public ExcelMapperBuilder<T> WithMap(Action<EntityMap<T>> configure)
        {
            if (configure is null) throw new ArgumentNullException(nameof(configure));
            _map = new EntityMap<T>();
            configure(_map);
            return this;
        }

        /// <summary>
        /// 追加一个数据集作为独立 Sheet（多数据集工作簿）；
        /// 该数据集同样支持侵入式特性或自带非侵入式映射。
        /// </summary>
        public ExcelMapperBuilder<T> AddSheet<TOther>(
            IEnumerable<TOther> data,
            string? sheetName = null,
            EntityMap<TOther>? map = null)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            _additional.Add(new AdditionalSheet
            {
                Rows = data,
                Name = sheetName,
                Map = map,
                BuildPart = CreatePartBuilder<TOther>(),
                CreateList = CreateListFactory<TOther>()
            });
            return this;
        }

        #region Sync Output

        /// <summary>导出到文件（自动校验路径、按需创建目录），返回导出诊断信息</summary>
        public ExportResult ToFile(string filePath, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
        {
            string fullPath = Guard.EnsureValidFilePath(filePath);
            Guard.EnsureDirectoryForFile(fullPath, _options.AutoCreateDirectory);

            var document = BuildDocument();
            PlanValidator.Validate(document, _options);

            if (_options.PageMode == PageMode.MultipleFiles && _options.PageSize is int ps && ps > 0)
            {
                return WriteMultipleFiles(document, fullPath, ps, cancellationToken, progress);
            }

            var sw = Stopwatch.StartNew();
            IExcelExportEngine engine = EngineFactory.Create(_options, EngineFactory.TryGetCount(document));
            var stats = engine.Export(document, fullPath, null, _style, _options, cancellationToken, progress);
            sw.Stop();

            return new ExportResult(stats.RowsWritten, stats.SheetsWritten, engine.Kind, sw.Elapsed, new[] { fullPath });
        }

        /// <summary>导出到流（流必须可写，导出后保持开启，由调用方负责释放），返回导出诊断信息</summary>
        public ExportResult ToStream(Stream stream, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite) throw new ArgumentException("输出流不可写。", nameof(stream));

            if (_options.PageMode == PageMode.MultipleFiles && _options.PageSize is int ps && ps > 0)
            {
                throw new ExcelMappingException("多文件分页模式无法写入单个流，请使用 ToFile 导出，或改用 PageMode.MultipleSheets。");
            }

            var document = BuildDocument();
            PlanValidator.Validate(document, _options);

            var sw = Stopwatch.StartNew();
            IExcelExportEngine engine = EngineFactory.Create(_options, EngineFactory.TryGetCount(document));
            var stats = engine.Export(document, null, stream, _style, _options, cancellationToken, progress);
            sw.Stop();

            return new ExportResult(stats.RowsWritten, stats.SheetsWritten, engine.Kind, sw.Elapsed, Array.Empty<string>());
        }

        #endregion

        #region Async Output

        /// <summary>异步导出到文件（桌面端不卡 UI；取消/进度同步支持），返回导出诊断信息</summary>
        public Task<ExportResult> ToFileAsync(string filePath, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
        {
            return Task.Run(() => ToFile(filePath, cancellationToken, progress), cancellationToken);
        }

        /// <summary>异步导出到流，返回导出诊断信息</summary>
        public Task<ExportResult> ToStreamAsync(Stream stream, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
        {
            return Task.Run(() => ToStream(stream, cancellationToken, progress), cancellationToken);
        }

        #endregion

        #region Internals

        private ExportDocument BuildDocument()
        {
            var document = PlanBuilder.BuildDocument(_data, _sheetName, _map, _options);
            foreach (var extra in _additional)
            {
                var part = extra.BuildPart(extra.Rows, extra.Name, extra.Map, _options);
                document.AdditionalParts.Add(part);
            }
            return document;
        }

        private delegate DocumentPart BuildPartDelegate(IEnumerable rows, string? name, object? map, ExcelExportOptions options);

        private static BuildPartDelegate CreatePartBuilder<TOther>()
        {
            return (rows, name, map, options) =>
                PlanBuilder.BuildPart(
                    (IEnumerable<TOther>)rows,
                    name,
                    (EntityMap<TOther>?)map,
                    options);
        }

        private static Func<IList> CreateListFactory<TOther>()
        {
            return () => new List<TOther>();
        }

        /// <summary>安全释放枚举器（非泛型 IEnumerator 不保证实现 IDisposable）</summary>
        private static void DisposeEnumerator(IEnumerator enumerator)
        {
            if (enumerator is IDisposable disposable)
                disposable.Dispose();
        }

        /// <summary>
        /// 多文件分页：每个页码一个工作簿文件，各数据集按相同页码对齐。
        /// 文件名：report.xlsx → report_1.xlsx、report_2.xlsx …
        /// </summary>
        private ExportResult WriteMultipleFiles(
            ExportDocument document,
            string fullPath,
            int pageSize,
            CancellationToken cancellationToken,
            IProgress<int>? progress)
        {
            var sw = Stopwatch.StartNew();

            string? dir = Path.GetDirectoryName(fullPath);
            string baseFileName = Path.GetFileNameWithoutExtension(fullPath);
            string ext = Path.GetExtension(fullPath);
            if (string.IsNullOrEmpty(ext)) ext = ".xlsx";

            string primaryBase = ResolveBaseName(document.Primary);
            var primaryPages = RowPaginator.Paginate(document.Primary.Sheet.Rows, pageSize).GetEnumerator();

            // 追加数据集使用惰性枚举器与主页对齐
            var extraEnumerators = document.AdditionalParts
                .Select(p => new AlignedPart
                {
                    Part = p,
                    Enumerator = p.Sheet.Rows.GetEnumerator(),
                    Additional = _additional.First(a => ReferenceEquals(a.Rows, p.Sheet.Rows))
                })
                .ToList();

            int totalRows = 0;
            int totalSheets = 0;
            int pageNo = 0;
            var files = new List<string>();
            ExportEngineKind engineKind = _options.Engine;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!primaryPages.MoveNext())
                        break;

                    pageNo++;
                    var primaryPage = primaryPages.Current;

                    var pageDoc = new ExportDocument();
                    string primaryName = string.Format(
                        _options.SheetPageNameFormat, primaryBase, pageNo);

                    // 复用已构建的列计划与行规则（引擎只读，不修改计划树）
                    var primaryPart = new DocumentPart();
                    primaryPart.Sheet.RequestedName = primaryName;
                    primaryPart.Sheet.RowType = typeof(T);
                    primaryPart.Sheet.Rows = primaryPage;
                    primaryPart.Sheet.Columns = document.Primary.Sheet.Columns;
                    foreach (var rowRule in document.Primary.RowRules)
                        primaryPart.RowRules.Add(rowRule);
                    pageDoc.Primary = primaryPart;

                    foreach (var aligned in extraEnumerators)
                    {
                        var extraPage = TakePage(aligned.Enumerator, pageSize, aligned.Additional.CreateList);
                        if (extraPage != null)
                        {
                            string extraBase = ResolveBaseName(aligned.Part);
                            string extraName = string.Format(_options.SheetPageNameFormat, extraBase, pageNo);

                            pageDoc.AdditionalParts.Add(
                                aligned.Additional.BuildPart(extraPage, extraName, aligned.Additional.Map, _options));
                        }
                    }

                    PlanValidator.Validate(pageDoc, _options);

                    IExcelExportEngine engine = EngineFactory.Create(_options, knownRows: null);
                    engineKind = engine.Kind;
                    string pageFile = Path.Combine(dir ?? string.Empty, baseFileName + "_" + pageNo + ext);
                    var stats = engine.Export(pageDoc, pageFile, null, _style, _options, cancellationToken, null);

                    totalRows += stats.RowsWritten;
                    totalSheets += stats.SheetsWritten;
                    files.Add(pageFile);
                    progress?.Report(totalRows);
                }
            }
            finally
            {
                DisposeEnumerator(primaryPages);
                foreach (var aligned in extraEnumerators)
                    DisposeEnumerator(aligned.Enumerator);
            }

            if (pageNo == 0)
            {
                // 空数据：仍输出一个仅含表头的文件，保持与单文件模式一致的"有文件可用"语义
                var emptyDoc = BuildDocument();
                IExcelExportEngine engine = EngineFactory.Create(_options, 0);
                engineKind = engine.Kind;
                var stats = engine.Export(emptyDoc, fullPath, null, _style, _options, cancellationToken, progress);
                totalRows += stats.RowsWritten;
                totalSheets += stats.SheetsWritten;
                files.Add(fullPath);
            }

            sw.Stop();
            return new ExportResult(totalRows, totalSheets, engineKind, sw.Elapsed, files);
        }

        private static IList? TakePage(IEnumerator enumerator, int pageSize, Func<IList> createList)
        {
            IList page = createList();
            while (page.Count < pageSize && enumerator.MoveNext())
                page.Add(enumerator.Current);
            return page.Count == 0 ? null : page;
        }

        private static string ResolveBaseName(DocumentPart part)
        {
            var attr = part.Sheet.RowType.GetTypeInfo()
                .GetCustomAttribute(typeof(ExcelSheetAttribute)) as ExcelSheetAttribute;
            if (!string.IsNullOrEmpty(attr?.Name))
                return attr!.Name!;
            return part.Sheet.RequestedName ?? part.Sheet.RowType.Name;
        }

        #endregion

        private sealed class AdditionalSheet
        {
            public IEnumerable Rows = null!;
            public string? Name;
            public object? Map;
            public BuildPartDelegate BuildPart = null!;
            public Func<IList> CreateList = null!;
        }

        private sealed class AlignedPart
        {
            public DocumentPart Part = null!;
            public IEnumerator Enumerator = null!;
            public AdditionalSheet Additional = null!;
        }
    }
}
