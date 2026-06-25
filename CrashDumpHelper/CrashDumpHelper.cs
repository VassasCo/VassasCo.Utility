using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VassasCo.Utility
{
    /// <summary>
    /// 崩溃捕获工具 — 记录崩溃前 FirstChance 异常 + 生成 Windows MiniDump 文件。
    /// 使用方式：程序启动时 CrashDumpHelper.Initialize()，然后在 UnhandledException 中调用 WriteCrashLog。
    /// MiniDump 依赖 Windows dbghelp.dll，仅 Windows 可用。
    /// </summary>
    public static class CrashDumpHelper
    {
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

        private static readonly object _crashLock = new();
        private static readonly object _firstChanceLock = new();
        private static readonly List<string> _firstChanceLog = new(64);
        private const int FirstChanceMaxCount = 50;

        /// <summary>初始化崩溃捕获：注册 FirstChanceException 事件监听。应在程序 Main 中最先调用。</summary>
        public static void Initialize()
        {
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        }

        private static void OnFirstChanceException(object? sender,
            System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            lock (_firstChanceLock)
            {
                if (_firstChanceLog.Count >= FirstChanceMaxCount)
                    _firstChanceLog.RemoveAt(0);

                var msg = $"[{DateTime.Now:HH:mm:ss.fff}] {e.Exception.GetType().Name}: {e.Exception.Message}";

                try
                {
                    var frame = new StackTrace(e.Exception, true).GetFrame(0);
                    if (frame != null)
                    {
                        var fileName = frame.GetFileName();
                        var line = frame.GetFileLineNumber();
                        if (!string.IsNullOrEmpty(fileName))
                            msg += $"  ← {Path.GetFileName(fileName)}:第{line}行";
                    }
                }
                catch { }

                _firstChanceLog.Add(msg);
            }
        }

        /// <summary>记录崩溃日志（传入 Exception 对象）</summary>
        public static void WriteCrashLog(Exception ex, string source)
        {
            var message = source + " 未处理异常\r\n\r\n" + FlattenException(ex);
            WriteCrashLog(message, source);
        }

        /// <summary>记录崩溃日志（传入自定义消息字符串）</summary>
        public static void WriteCrashLog(string message, string source)
        {
            lock (_crashLock)
            {
                try
                {
                    var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CrashLogs");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    var fileName = Path.Combine(dir, $"Crash_{DateTime.Now:yyyyMMdd}.log");

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

                    lock (_firstChanceLock)
                    {
                        if (_firstChanceLog.Count > 0)
                        {
                            sb.AppendLine("【崩溃前最近异常记录 (FirstChance)】");
                            sb.AppendLine("----------------------------------------");
                            foreach (var entry in _firstChanceLog)
                                sb.AppendLine(entry);
                            sb.AppendLine("----------------------------------------");
                            sb.AppendLine();
                        }
                    }

                    sb.AppendLine("【崩溃异常详情】");
                    sb.AppendLine("----------------------------------------");
                    sb.AppendLine(message);
                    sb.AppendLine("----------------------------------------");
                    sb.AppendLine();

                    try
                    {
                        var stackTrace = new StackTrace(true);
                        sb.AppendLine("【崩溃处理时的调用堆栈】");
                        sb.AppendLine("----------------------------------------");
                        sb.AppendLine(stackTrace.ToString());
                    }
                    catch { }

                    sb.AppendLine("========================================");
                    sb.AppendLine();

                    File.AppendAllText(fileName, sb.ToString(), Encoding.UTF8);
                    GenerateMiniDump(dir);
                }
                catch { }
            }
        }

        /// <summary>崩溃时先尝试通知日志系统 flush 队列，再记录崩溃日志</summary>
        public static void FlushAndWriteCrashLog(Exception ex, string source)
        {
            try { TryFlushLogger(); }
            catch { }
            WriteCrashLog(ex, source);
        }

        /// <summary>通过反射调用 LogHelper 停止服务（避免硬依赖）</summary>
        private static void TryFlushLogger()
        {
            try
            {
                var logManagerType = Type.GetType("VassasCo.Utility.LogManager, VassasCo.Utility");
                if (logManagerType == null) return;

                var currentProp = logManagerType.GetProperty("Current");
                if (currentProp == null) return;

                var current = currentProp.GetValue(null);
                if (current == null) return;

                var stopMethod = current.GetType().GetMethod("StopLogService");
                stopMethod?.Invoke(current, null);
            }
            catch { }
        }

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
                catch { sb.AppendLine($"{indent}  (无法获取堆栈)"); }
            }

            sb.AppendLine();

            if (ex.InnerException != null) FlattenExceptionRecursive(ex.InnerException, sb, depth + 1);
            if (ex is AggregateException aggEx)
                foreach (var inner in aggEx.InnerExceptions) FlattenExceptionRecursive(inner, sb, depth + 1);
        }

        private static void GenerateMiniDump(string directory)
        {
            try
            {
                var dumpFileName = Path.Combine(directory,
                    $"CrashDump_{DateTime.Now:yyyyMMdd_HHmmss}_{Process.GetCurrentProcess().Id}.dmp");

                using (var fs = new FileStream(dumpFileName, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var process = Process.GetCurrentProcess();
                    MiniDumpWriteDump(process.Handle, (uint)process.Id, fs.SafeFileHandle,
                        MINIDUMP_TYPE.MiniDumpNormal
                        | MINIDUMP_TYPE.MiniDumpWithDataSegs
                        | MINIDUMP_TYPE.MiniDumpWithIndirectlyReferencedMemory
                        | MINIDUMP_TYPE.MiniDumpWithThreadInfo
                        | MINIDUMP_TYPE.MiniDumpWithUnloadedModules
                        | MINIDUMP_TYPE.MiniDumpWithProcessThreadData
                        | MINIDUMP_TYPE.MiniDumpWithCodeSegs
                        | MINIDUMP_TYPE.MiniDumpIgnoreInaccessibleMemory,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }

                File.AppendAllText(Path.Combine(directory, $"Crash_{DateTime.Now:yyyyMMdd}.log"),
                    $"MiniDump已生成: {dumpFileName}\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }
}
