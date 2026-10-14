// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VassasCo.Utility
{
    /// <summary>
    /// 崩溃捕获工具 — 记录崩溃前的 FirstChance 异常链 + 生成 Windows MiniDump 文件。
    /// <para>
    /// 典型用法：在 <c>Main</c> 中最先调用 <see cref="Initialize(CrashDumpOptions)"/>，
    /// 再在 <c>UnhandledException</c> 中调用 <see cref="FlushAndWriteCrashLog(Exception, string)"/>。
    /// </para>
    /// <para>
    /// MiniDump 依赖 Windows <c>dbghelp.dll</c>，仅 Windows 平台可用；
    /// 其他平台仍然会正常写出崩溃日志。
    /// </para>
    /// </summary>
    public static class CrashDumpHelper
    {
        #region 常量

        /// <summary>
        /// FirstChance 异常环形缓冲的容量。缓冲条数与容量上限共用同一个常量，避免两者不一致。
        /// </summary>
        public const int FirstChanceMaxCount = 50;

        private const string DefaultDirectoryName = "CrashLogs";
        private const string LogFilePrefix = "Crash_";
        private const string DumpFilePrefix = "CrashDump_";

        #endregion

        #region 原生转储类型（与 dbghelp MINIDUMP_TYPE 一致）

        [Flags]
        private enum MINIDUMP_TYPE : uint
        {
            MiniDumpNormal = 0x00000000,
            MiniDumpWithDataSegs = 0x00000001,
            MiniDumpWithFullMemory = 0x00000002,
            MiniDumpWithHandleData = 0x00000004,
            MiniDumpFilterMemory = 0x00000008,
            MiniDumpScanMemory = 0x00000010,
            MiniDumpWithUnloadedModules = 0x00000020,
            MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
            MiniDumpFilterModulePaths = 0x00000080,
            MiniDumpWithProcessThreadData = 0x00000100,
            MiniDumpWithPrivateReadWriteMemory = 0x00000200,
            MiniDumpWithoutOptionalData = 0x00000400,
            MiniDumpWithFullMemoryInfo = 0x00000800,
            MiniDumpWithThreadInfo = 0x00001000,
            MiniDumpWithCodeSegs = 0x00002000,
            MiniDumpWithoutAuxiliaryState = 0x00004000,
            MiniDumpWithFullAuxiliaryState = 0x00008000,
            MiniDumpWithPrivateWriteCopyMemory = 0x00010000,
            MiniDumpIgnoreInaccessibleMemory = 0x00020000,
            MiniDumpWithTokenInformation = 0x00040000
        }

        [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess, uint processId, SafeHandle hFile, MINIDUMP_TYPE dumpType,
            IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

        #endregion

        #region 状态

        private static readonly object _crashLock = new object();

        /// <summary>保护环形缓冲自身的读写指针。</summary>
        private static readonly object _firstChanceLock = new object();

        /// <summary>
        /// 固定长度环形缓冲。用「数组 + 写指针」而非 <c>List.RemoveAt(0)</c>，
        /// 后者淘汰最旧记录时会触发 O(n) 元素搬移。
        /// </summary>
        private static readonly string?[] _firstChanceRing = new string?[FirstChanceMaxCount];
        private static int _ringHead;                     // 下一个写入位置
        private static int _ringCount;                    // 当前有效条数

        /// <summary>
        /// 初始化重入守卫。必须保证 handler 只注册一次：
        /// 重复 <c>+=</c> 会让每条 FirstChance 异常被记录 N 次，
        /// 缓冲被瞬间冲掉，真正崩溃前的线索反而丢失。
        /// </summary>
        private static int _initialized;

        private static CrashDumpOptions _options = new CrashDumpOptions();

        /// <summary>
        /// 保存 handler 委托引用，保证 <see cref="Deinitialize"/> 能精确 <c>-=</c> 掉同一个委托。
        /// </summary>
        private static readonly EventHandler<FirstChanceExceptionEventArgs> _firstChanceHandler =
            OnFirstChanceException;

        private static readonly UnhandledExceptionEventHandler _unhandledHandler =
            OnUnhandledException;

        private static readonly EventHandler<UnobservedTaskExceptionEventArgs> _unobservedTaskHandler =
            OnUnobservedTaskException;

        /// <summary>
        /// 采集/落盘进行中置 1。用于阻断「处理过程抛出异常 → 再次进入采集 → 再次处理」
        /// 的无限递归 —— 该递归会直接把进程搞成栈溢出，比漏记几条异常严重得多。
        /// </summary>
        private static int _processing;

        #endregion

        /// <summary>崩溃监听是否已初始化。</summary>
        public static bool IsInitialized => Volatile.Read(ref _initialized) == 1;

        /// <summary>当前生效的配置（只读用途）。</summary>
        public static CrashDumpOptions Options => _options;

        #region 初始化 / 注销

        /// <summary>
        /// 使用默认配置初始化崩溃捕获：注册 <c>FirstChanceException</c> 监听。
        /// 应在程序 <c>Main</c> 中最先调用。重复调用安全（会被忽略并输出诊断提示）。
        /// </summary>
        public static void Initialize() => Initialize(null);

        /// <summary>
        /// 使用指定配置初始化崩溃捕获。
        /// </summary>
        /// <param name="options">配置；为 null 时保留默认配置。重复调用时该配置不生效。</param>
        public static void Initialize(CrashDumpOptions? options)
        {
            // 用 CAS 保证 handler 只注册一次
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            {
                ReportDiagnostic(
                    "[CrashDumpHelper] Initialize() 被重复调用，已忽略 —— " +
                    "监听只允许注册一次，重复注册会导致异常被记录多次。");
                return;
            }

            // 配置只在真正接管时生效，与上面「重复调用已忽略」的语义保持一致。
            if (options != null)
                _options = options;

            AppDomain.CurrentDomain.FirstChanceException += _firstChanceHandler;

            if (_options.HookUnhandledException)
                AppDomain.CurrentDomain.UnhandledException += _unhandledHandler;

            if (_options.HookUnobservedTaskException)
                TaskScheduler.UnobservedTaskException += _unobservedTaskHandler;
        }

        /// <summary>
        /// 注销崩溃捕获监听（移除 <c>FirstChanceException</c> handler），
        /// 并把状态完整复位：清空 FirstChance 环形缓冲、配置回落到默认值。
        /// 适用于插件卸载、热重载、单元测试隔离，或在长时间运行的进程中临时关闭采集。
        /// 幂等，可安全重复调用。复位后再次调用 <see cref="Initialize()"/> 即为默认配置。
        /// </summary>
        public static void Deinitialize()
        {
            bool wasInitialized = Interlocked.CompareExchange(ref _initialized, 0, 1) == 1;
            if (wasInitialized)
            {
                AppDomain.CurrentDomain.FirstChanceException -= _firstChanceHandler;
                AppDomain.CurrentDomain.UnhandledException -= _unhandledHandler;
                TaskScheduler.UnobservedTaskException -= _unobservedTaskHandler;
            }

            // 无论此前是否已初始化，都完整复位，避免上一次配置/历史泄漏到下一次使用。
            _options = new CrashDumpOptions();
            Volatile.Write(ref _processing, 0);

            lock (_firstChanceLock)
            {
                Array.Clear(_firstChanceRing, 0, _firstChanceRing.Length);
                _ringHead = 0;
                _ringCount = 0;
            }
        }

        #endregion

        #region FirstChance 采集

        private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
        {
            // 采集与落盘过程中抛出的异常会再次触发本事件，不阻断就会无限递归至栈溢出。
            if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
                return;

            // 崩溃路径上绝不能抛异常 —— 任何失败都必须被吞掉
            try
            {
                var ex = e.Exception;

                var ignore = _options.ShouldIgnore;
                if (ignore != null)
                {
                    bool ignored;
                    try { ignored = ignore(ex); }
                    catch { ignored = false; }   // 过滤器自身出错时按「不忽略」处理
                    if (ignored) return;
                }

                var msg = FormatExceptionLine(ex);

                bool full;
                lock (_firstChanceLock)
                {
                    _firstChanceRing[_ringHead] = msg;
                    _ringHead = (_ringHead + 1) % FirstChanceMaxCount;
                    if (_ringCount < FirstChanceMaxCount)
                        _ringCount++;
                    full = _ringCount >= FirstChanceMaxCount;
                }

                if (full && _options.AutoFlushWhenFull)
                    FlushFirstChanceLog("RingBufferFull");
            }
            catch
            {
                // 采集失败不影响业务
            }
            finally
            {
                Volatile.Write(ref _processing, 0);
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.ExceptionObject as Exception
                    ?? new Exception("未处理异常对象不是 Exception: " +
                                     (e.ExceptionObject?.ToString() ?? "(null)"));

                FlushAndWriteCrashLog(ex, "AppDomain.UnhandledException");
            }
            catch (Exception ex)
            {
                ReportDiagnostic(
                    $"[CrashDumpHelper] 处理未处理异常失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                // .NET Framework 4.5 起未观察的 Task 异常默认不再终止进程，只会静默丢弃，
                // 订阅本事件是捕获这类异常的唯一途径，因此这里必须落盘而不是只留在缓冲里。
                FlushAndWriteCrashLog(e.Exception, "TaskScheduler.UnobservedTaskException");
            }
            catch (Exception ex)
            {
                ReportDiagnostic(
                    $"[CrashDumpHelper] 处理未观察 Task 异常失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 把当前环形缓冲中的异常记录写入崩溃日志目录下的当日日志文件，并清空缓冲。
        /// <para>
        /// 与 <see cref="WriteCrashLog(Exception, string)"/> 的区别：本方法只落盘异常链，
        /// 不生成 MiniDump —— 缓冲满是预警信号，不是崩溃，生成 dump 既慢又没有意义。
        /// </para>
        /// </summary>
        /// <param name="source">落盘来源描述，便于在日志中区分自动落盘与崩溃落盘。</param>
        /// <returns>写入的异常记录条数。</returns>
        public static int FlushFirstChanceLog(string source = "RingBufferFull")
        {
            var entries = TakeFirstChanceSnapshotAndClear();
            if (entries.Count == 0)
                return 0;

            var options = _options;
            try
            {
                var dir = ResolveLogDirectory(options);
                Directory.CreateDirectory(dir);

                var fileName = Path.Combine(dir,
                    LogFilePrefix + DateTime.Now.ToString("yyyyMMdd") + ".log");

                var sb = new StringBuilder();
                sb.AppendLine("========================================");
                sb.AppendLine($"落盘时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"来源: {source}");
                sb.AppendLine("说明: FirstChance 环形缓冲已满，以下为采集到的最近异常记录");
                sb.AppendLine("      （自动落盘，非崩溃，未生成 MiniDump）");
                sb.AppendLine("========================================");
                sb.AppendLine();
                foreach (var entry in entries)
                    sb.AppendLine(entry);
                sb.AppendLine();

                File.AppendAllText(fileName, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                ReportDiagnostic(
                    $"[CrashDumpHelper] 落盘 FirstChance 缓冲失败（来源: {source}）: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            return entries.Count;
        }

        /// <summary>
        /// 取出当前 FirstChance 环形缓冲的快照（按时间从旧到新）。
        /// </summary>
        public static List<string> GetFirstChanceSnapshot()
        {
            var result = new List<string>();
            lock (_firstChanceLock)
            {
                result.Capacity = _ringCount;
                int start = (_ringHead - _ringCount + FirstChanceMaxCount) % FirstChanceMaxCount;
                for (int i = 0; i < _ringCount; i++)
                {
                    var entry = _firstChanceRing[(start + i) % FirstChanceMaxCount];
                    if (entry != null)
                        result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>
        /// 取出环形缓冲快照并清空缓冲。落盘与状态复位共用同一段逻辑，
        /// 保证「读到的就是要清掉的」，避免出现重复落盘或记录丢失。
        /// </summary>
        private static List<string> TakeFirstChanceSnapshotAndClear()
        {
            var result = new List<string>();
            lock (_firstChanceLock)
            {
                result.Capacity = _ringCount;
                int start = (_ringHead - _ringCount + FirstChanceMaxCount) % FirstChanceMaxCount;
                for (int i = 0; i < _ringCount; i++)
                {
                    var entry = _firstChanceRing[(start + i) % FirstChanceMaxCount];
                    if (entry != null)
                        result.Add(entry);
                }

                Array.Clear(_firstChanceRing, 0, _firstChanceRing.Length);
                _ringHead = 0;
                _ringCount = 0;
            }
            return result;
        }

        private static string FormatExceptionLine(Exception ex)
        {
            var msg = $"[{DateTime.Now:HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}";

            try
            {
                var frame = new StackTrace(ex, true).GetFrame(0);
                if (frame != null)
                {
                    var fileName = frame.GetFileName();
                    var line = frame.GetFileLineNumber();
                    if (!string.IsNullOrEmpty(fileName))
                        msg += $"  ← {Path.GetFileName(fileName)}:第{line}行";
                }
            }
            catch
            {
                // 获取源文件信息失败时忽略，保留基本信息即可
            }

            return msg;
        }

        #endregion

        #region 写崩溃日志

        /// <summary>记录崩溃日志（传入 Exception 对象）。</summary>
        /// <param name="ex">未处理异常。</param>
        /// <param name="source">来源描述，例如 <c>"AppDomain.UnhandledException"</c>。</param>
        public static void WriteCrashLog(Exception ex, string source)
        {
            var message = source + " 未处理异常\r\n\r\n" + FlattenException(ex);
            WriteCrashLog(message, source);
        }

        /// <summary>记录崩溃日志（传入自定义消息字符串）。</summary>
        /// <param name="message">崩溃详情。</param>
        /// <param name="source">来源描述。</param>
        public static void WriteCrashLog(string message, string source)
        {
            lock (_crashLock)
            {
                var options = _options;
                try
                {
                    var dir = ResolveLogDirectory(options);
                    Directory.CreateDirectory(dir);

                    var fileName = Path.Combine(dir, LogFilePrefix + DateTime.Now.ToString("yyyyMMdd") + ".log");

                    var sb = new StringBuilder();
                    sb.AppendLine("========================================");
                    sb.AppendLine($"崩溃时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                    sb.AppendLine($"来源: {source}");
                    sb.AppendLine($"进程ID: {Process.GetCurrentProcess().Id}");
                    sb.AppendLine($"进程名: {Process.GetCurrentProcess().ProcessName}");
                    sb.AppendLine($"运行时长: {Environment.TickCount / 1000} 秒");
                    sb.AppendLine($"内存使用: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MB");
                    sb.AppendLine($"线程数: {Process.GetCurrentProcess().Threads.Count}");
                    sb.AppendLine("========================================");
                    sb.AppendLine();

                    var firstChance = GetFirstChanceSnapshot();
                    if (firstChance.Count > 0)
                    {
                        sb.AppendLine("【崩溃前最近异常记录 (FirstChance)】");
                        sb.AppendLine("----------------------------------------");
                        foreach (var entry in firstChance)
                            sb.AppendLine(entry);
                        sb.AppendLine("----------------------------------------");
                        sb.AppendLine();
                    }

                    sb.AppendLine("【崩溃异常详情】");
                    sb.AppendLine("----------------------------------------");
                    sb.AppendLine(message);
                    sb.AppendLine("----------------------------------------");
                    sb.AppendLine();

                    try
                    {
                        sb.AppendLine("【崩溃处理时的调用堆栈】");
                        sb.AppendLine("----------------------------------------");
                        sb.AppendLine(new StackTrace(true).ToString());
                    }
                    catch
                    {
                        sb.AppendLine("(无法获取堆栈)");
                    }

                    sb.AppendLine("========================================");
                    sb.AppendLine();

                    File.AppendAllText(fileName, sb.ToString(), new UTF8Encoding(false));

                    GenerateMiniDump(dir, options);
                    CleanupExpiredFiles(dir, options);
                }
                catch (Exception ex)
                {
                    // 崩溃处理路径上再出异常也必须留下线索，转交诊断通道而不是空吞。
                    ReportDiagnostic(
                        $"[CrashDumpHelper] 写崩溃日志失败（来源: {source}）: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>崩溃时先尝试通知日志系统 flush 队列，再记录崩溃日志。</summary>
        /// <param name="ex">未处理异常。</param>
        /// <param name="source">来源描述。</param>
        public static void FlushAndWriteCrashLog(Exception ex, string source)
        {
            TryFlushLogger();
            WriteCrashLog(ex, source);
        }

        /// <summary>
        /// 写崩溃日志前先 flush LogHelper 的缓冲队列，避免进程终止时丢失最后的日志。
        /// 使用编译期项目引用而非反射字符串调用，可获得编译期检查与重构安全。
        /// </summary>
        private static void TryFlushLogger()
        {
            var options = _options;
            if (!options.FlushLogger)
                return;

            try
            {
                // LogManager.Current 在日志系统未初始化时会抛异常，先判 IsInitialized
                if (!LogManager.IsInitialized)
                    return;

                LogManager.Current.Flush();
            }
            catch (Exception ex)
            {
                ReportDiagnostic(
                    $"[CrashDumpHelper] flush 日志系统失败（已继续写崩溃日志）: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析日志输出目录：未配置时落到 <c>{程序目录}/CrashLogs</c>；
        /// 配置为相对路径时基于程序目录解析（而非当前工作目录）。
        /// </summary>
        private static string ResolveLogDirectory(CrashDumpOptions options)
        {
            var configured = options.LogDirectory;
            if (string.IsNullOrWhiteSpace(configured))
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultDirectoryName);

            // 说明：netstandard2.0 / net472 的参考程序集未对 string.IsNullOrWhiteSpace 标注
            // [NotNullWhen(false)]，编译器的空值流分析无法自动收窄，这里显式用 ! 消除告警。
            return Path.IsPathRooted(configured!)
                ? configured!
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured!);
        }

        #endregion

        #region MiniDump

        private static void GenerateMiniDump(string directory, CrashDumpOptions options)
        {
            if (!options.GenerateDump)
                return;

            // 显式判断平台，而不是靠捕获 DllNotFoundException 来推断 —— 后者会掩盖真实错误。
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AppendNote(directory, "[CrashDumpHelper] 当前平台非 Windows，已跳过 MiniDump 生成。");
                return;
            }

            string dumpFileName = string.Empty;
            try
            {
                var process = Process.GetCurrentProcess();
                dumpFileName = Path.Combine(directory,
                    DumpFilePrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + process.Id + ".dmp");

                bool ok;
                int win32Error = 0;

                using (var fs = new FileStream(dumpFileName, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    ok = MiniDumpWriteDump(process.Handle, (uint)process.Id, fs.SafeFileHandle,
                        (MINIDUMP_TYPE)options.DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (!ok)
                        win32Error = Marshal.GetLastWin32Error();   // 必须在其它托管调用前取
                }

                // 无论成功还是失败都留下记录，排查者才能区分「没生成」与「生成失败」。
                AppendNote(directory, ok
                    ? $"MiniDump 已生成: {dumpFileName}"
                    : $"MiniDump 生成失败: {dumpFileName}（Win32 错误码 {win32Error}）");
            }
            catch (Exception ex)
            {
                ReportDiagnostic(
                    $"[CrashDumpHelper] 生成 MiniDump 异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AppendNote(string directory, string note)
        {
            try
            {
                var path = Path.Combine(directory,
                    LogFilePrefix + DateTime.Now.ToString("yyyyMMdd") + ".log");
                File.AppendAllText(path, note + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                ReportDiagnostic($"[CrashDumpHelper] 追加日志备注失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        #endregion

        #region 过期清理

        /// <summary>
        /// 按保留天数清理崩溃目录下由本库生成的文件
        /// （<c>Crash_*.log</c> 与 <c>CrashDump_*.dmp</c>）。
        /// 只匹配本库前缀，避免误删同目录下的其他文件。
        /// </summary>
        private static void CleanupExpiredFiles(string directory, CrashDumpOptions options)
        {
            int days = options.RetentionDays;
            if (days <= 0)
                return;

            try
            {
                var cutoff = DateTime.Now.AddDays(-days);

                var patterns = new[] { LogFilePrefix + "*.log", DumpFilePrefix + "*.dmp" };
                foreach (var pattern in patterns)
                {
                    string[] files;
                    try { files = Directory.GetFiles(directory, pattern); }
                    catch { continue; }

                    for (int i = 0; i < files.Length; i++)
                    {
                        try
                        {
                            if (File.GetLastWriteTime(files[i]) < cutoff)
                                File.Delete(files[i]);
                        }
                        catch
                        {
                            // 单文件被占用/无权限时跳过，不影响其它文件
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ReportDiagnostic(
                    $"[CrashDumpHelper] 清理过期崩溃文件失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>清理超过保留天数的崩溃文件（公开入口）。</summary>
        /// <returns>被删除的文件数。</returns>
        public static int CleanupExpiredFiles()
        {
            var options = _options;
            var dir = ResolveLogDirectory(options);
            if (!Directory.Exists(dir))
                return 0;

            int days = options.RetentionDays;
            if (days <= 0)
                return 0;

            var cutoff = DateTime.Now.AddDays(-days);
            int deleted = 0;

            var patterns = new[] { LogFilePrefix + "*.log", DumpFilePrefix + "*.dmp" };
            foreach (var pattern in patterns)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, pattern); }
                catch { continue; }

                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (File.GetLastWriteTime(files[i]) >= cutoff)
                            continue;
                        File.Delete(files[i]);
                        deleted++;
                    }
                    catch
                    {
                        // 忽略被占用/无权限的单个文件
                    }
                }
            }

            return deleted;
        }

        #endregion

        #region 异常展开

        private static string FlattenException(Exception? ex)
        {
            if (ex == null) return "(null)";
            var sb = new StringBuilder();
            FlattenExceptionRecursive(ex, sb, 0);
            return sb.ToString();
        }

        private static void FlattenExceptionRecursive(Exception ex, StringBuilder sb, int depth)
        {
            var indent = new string(' ', depth * 2);

            sb.AppendLine($"{indent}[第{depth + 1}层异常]");
            sb.AppendLine($"{indent}异常类型: {ex.GetType().FullName}");
            sb.AppendLine($"{indent}异常消息: {ex.Message}");
            sb.AppendLine($"{indent}异常来源: {ex.Source}");

            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.AppendLine($"{indent}堆栈跟踪:");
                foreach (var line in ex.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    sb.AppendLine($"{indent}  {line.Trim()}");
            }
            else
            {
                try
                {
                    var st = new StackTrace(ex, true);
                    sb.AppendLine($"{indent}堆栈跟踪(通过StackTrace获取):");
                    for (var i = 0; i < st.FrameCount; i++)
                    {
                        var frame = st.GetFrame(i);
                        var method = frame?.GetMethod();
                        if (method == null) continue;
                        var fileName = frame?.GetFileName();
                        var lineNum = frame?.GetFileLineNumber() ?? 0;
                        var location = !string.IsNullOrEmpty(fileName)
                            ? $"  ← {Path.GetFileName(fileName)}:第{lineNum}行" : "";
                        sb.AppendLine($"{indent}  at {method.DeclaringType?.FullName}.{method.Name}{location}");
                    }
                }
                catch
                {
                    sb.AppendLine($"{indent}  (无法获取堆栈)");
                }
            }

            sb.AppendLine();

            if (ex.InnerException != null) FlattenExceptionRecursive(ex.InnerException, sb, depth + 1);
            if (ex is AggregateException aggEx)
                foreach (var inner in aggEx.InnerExceptions) FlattenExceptionRecursive(inner, sb, depth + 1);
        }

        #endregion

        #region 诊断

        private static void ReportDiagnostic(string message)
        {
            var sink = _options.Diagnostics;
            if (sink == null) return;
            try { sink(message); }
            catch { /* 诊断回调自身的异常绝不能影响崩溃处理流程 */ }
        }

        #endregion
    }
}
