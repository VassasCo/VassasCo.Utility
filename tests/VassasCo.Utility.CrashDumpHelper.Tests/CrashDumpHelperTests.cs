// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VassasCo.Utility.Tests
{
    /// <summary>
    /// 说明：CrashDumpHelper 是静态类、监听的是进程级 FirstChanceException，
    /// 因此每个用例开头都先 Deinitialize() 复位状态（清空环形缓冲 / 注销监听）。
    /// </summary>
    public class CrashDumpHelperTests : IDisposable
    {
        private readonly List<string> _tempDirs = new List<string>();

        public void Dispose()
        {
            CrashDumpHelper.Deinitialize();

            for (int i = 0; i < _tempDirs.Count; i++)
            {
                try { Directory.Delete(_tempDirs[i], recursive: true); }
                catch { /* 清理临时目录失败不影响测试结果 */ }
            }
        }

        private string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "VassasCo.CrashTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        private static CrashDumpOptions OptionsFor(string dir) => new CrashDumpOptions
        {
            LogDirectory = dir,
            GenerateDump = false,     // 单元测试里不生成 dump（文件大且依赖 dbghelp）
            RetentionDays = 0         // 默认不做清理，避免干扰其它断言
        };

        #region 初始化重入保护

        /// <summary>
        /// 重复 Initialize() 不得重复注册 handler，
        /// 否则同一条 FirstChance 异常会被记录多次（日志体积爆炸、缓冲被冲掉）。
        /// </summary>
        [Fact]
        public void Initialize_CalledTwice_RecordsExceptionOnlyOnce()
        {
            var dir = CreateTempDir();
            var options = OptionsFor(dir);

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(options);
            CrashDumpHelper.Initialize(options);      // 重复调用

            try { throw new InvalidOperationException("probe"); }
            catch { /* FirstChance 已在抛出时被采集 */ }

            var snapshot = CrashDumpHelper.GetFirstChanceSnapshot();
            Assert.Equal(1, snapshot.Count(s => s.Contains("probe")));
        }

        [Fact]
        public void Initialize_RepeatedCall_ReportsDiagnostics()
        {
            var messages = new List<string>();
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(new CrashDumpOptions
            {
                LogDirectory = dir,
                GenerateDump = false,
                RetentionDays = 0,
                Diagnostics = messages.Add
            });
            CrashDumpHelper.Initialize(OptionsFor(dir));      // 重复调用

            Assert.Contains(messages, m => m.Contains("重复调用"));
        }

        [Fact]
        public void Initialize_RepeatedCall_KeepsOriginalOptions()
        {
            var dirA = CreateTempDir();
            var dirB = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dirA));
            CrashDumpHelper.Initialize(OptionsFor(dirB));    // 重复调用，应被忽略

            // 已忽略的调用不该偷偷换掉生效中的配置
            Assert.Equal(dirA, CrashDumpHelper.Options.LogDirectory);
        }

        [Fact]
        public void IsInitialized_ReflectsState()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            Assert.False(CrashDumpHelper.IsInitialized);

            CrashDumpHelper.Initialize(OptionsFor(dir));
            Assert.True(CrashDumpHelper.IsInitialized);

            CrashDumpHelper.Deinitialize();
            Assert.False(CrashDumpHelper.IsInitialized);
        }

        #endregion

        #region 注销

        [Fact]
        public void Deinitialize_StopsCollecting()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            try { throw new InvalidOperationException("before"); } catch { }
            Assert.Equal(1, CrashDumpHelper.GetFirstChanceSnapshot().Count(s => s.Contains("before")));

            CrashDumpHelper.Deinitialize();

            try { throw new InvalidOperationException("after"); } catch { }
            Assert.DoesNotContain("after", string.Join("|", CrashDumpHelper.GetFirstChanceSnapshot()));
        }

        [Fact]
        public void Deinitialize_IsIdempotent()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));
            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Deinitialize();

            Assert.False(CrashDumpHelper.IsInitialized);
        }

        #endregion

        #region FirstChance 环形缓冲

        [Fact]
        public void FirstChanceRing_KeepsOnlyMostRecentEntries()
        {
            var dir = CreateTempDir();

            // 关闭自动落盘，才能观察覆盖式缓冲本身的淘汰行为
            var options = OptionsFor(dir);
            options.AutoFlushWhenFull = false;

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(options);

            for (int i = 0; i < CrashDumpHelper.FirstChanceMaxCount + 10; i++)
            {
                try { throw new InvalidOperationException("marker" + i); }
                catch { }
            }

            var snapshot = CrashDumpHelper.GetFirstChanceSnapshot();

            Assert.Equal(CrashDumpHelper.FirstChanceMaxCount, snapshot.Count);
            Assert.Contains("marker" + (CrashDumpHelper.FirstChanceMaxCount + 9), snapshot[snapshot.Count - 1]);
            Assert.DoesNotContain("marker0", string.Join("|", snapshot));
        }

        [Fact]
        public void FirstChanceSnapshot_IsInChronologicalOrder()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            for (int i = 0; i < 5; i++)
            {
                try { throw new InvalidOperationException("seq" + i); } catch { }
            }

            var snapshot = CrashDumpHelper.GetFirstChanceSnapshot();
            Assert.Equal(5, snapshot.Count);
            Assert.Contains("seq0", snapshot[0]);
            Assert.Contains("seq4", snapshot[4]);
        }

        #endregion

        #region 写崩溃日志

        [Fact]
        public void WriteCrashLog_CreatesFileWithExpectedName()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            CrashDumpHelper.WriteCrashLog(new InvalidOperationException("inner boom"), "UnitTest.Source");

            var expected = Path.Combine(dir, "Crash_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            Assert.True(File.Exists(expected));
        }

        [Fact]
        public void WriteCrashLog_ContainsSourceAndExceptionChain()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            var inner = new IOException("disk gone");
            var outer = new InvalidOperationException("outer failed", inner);
            CrashDumpHelper.WriteCrashLog(outer, "UnitTest.Source");

            var path = Path.Combine(dir, "Crash_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            var content = File.ReadAllText(path);

            Assert.Contains("UnitTest.Source", content);
            Assert.Contains("【崩溃异常详情】", content);
            Assert.Contains("outer failed", content);
            Assert.Contains("disk gone", content);
            Assert.Contains("第1层异常", content);
            Assert.Contains("第2层异常", content);
        }

        [Fact]
        public void WriteCrashLog_IncludesFirstChanceHistory()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            try { throw new ArgumentException("pre-crash warning"); } catch { }

            CrashDumpHelper.WriteCrashLog(new Exception("final boom"), "UnitTest");

            var path = Path.Combine(dir, "Crash_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            var content = File.ReadAllText(path);

            Assert.Contains("【崩溃前最近异常记录 (FirstChance)】", content);
            Assert.Contains("pre-crash warning", content);
            Assert.Contains("final boom", content);
        }

        [Fact]
        public void FlushAndWriteCrashLog_WithoutLogHelper_DoesNotThrow()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            // LogManager 未初始化时应安全跳过 flush，且整体不抛异常
            var ex = Record.Exception(() =>
                CrashDumpHelper.FlushAndWriteCrashLog(new Exception("boom"), "UnitTest"));

            Assert.Null(ex);
            Assert.True(File.Exists(Path.Combine(dir, "Crash_" + DateTime.Now.ToString("yyyyMMdd") + ".log")));
        }

        [Fact]
        public void WriteCrashLog_UsesConfiguredLogDirectory()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            CrashDumpHelper.WriteCrashLog("custom message", "UnitTest");

            var files = Directory.GetFiles(dir, "Crash_*.log");
            Assert.Single(files);
        }

        #endregion

        #region 过期清理

        [Fact]
        public void CleanupExpiredFiles_RemovesOldFiles_KeepsFreshAndUnrelated()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(new CrashDumpOptions
            {
                LogDirectory = dir,
                GenerateDump = false,
                RetentionDays = 7
            });

            var old = Path.Combine(dir, "Crash_20200101.log");
            var fresh = Path.Combine(dir, "Crash_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            var unrelated = Path.Combine(dir, "important.txt");

            File.WriteAllText(old, "old");
            File.WriteAllText(fresh, "fresh");
            File.WriteAllText(unrelated, "keep me");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));

            int deleted = CrashDumpHelper.CleanupExpiredFiles();

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(unrelated));      // 只清理本库前缀的文件
        }

        [Fact]
        public void CleanupExpiredFiles_WithNonPositiveRetention_DoesNothing()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));   // RetentionDays = 0

            var old = Path.Combine(dir, "Crash_20200101.log");
            File.WriteAllText(old, "old");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-300));

            Assert.Equal(0, CrashDumpHelper.CleanupExpiredFiles());
            Assert.True(File.Exists(old));
        }

        [Fact]
        public void CleanupExpiredFiles_OnMissingDirectory_ReturnsZero()
        {
            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(new CrashDumpOptions
            {
                LogDirectory = Path.Combine(Path.GetTempPath(), "VassasCo.Definitely.Not.Exists." + Guid.NewGuid().ToString("N")),
                GenerateDump = false,
                RetentionDays = 1
            });

            Assert.Equal(0, CrashDumpHelper.CleanupExpiredFiles());
        }

        #endregion

        #region 异常过滤器

        [Fact]
        public void ShouldIgnore_FiltersOutMatchingExceptions()
        {
            var dir = CreateTempDir();
            var options = OptionsFor(dir);
            options.ShouldIgnore = ex => ex is ArgumentException;

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(options);

            try { throw new ArgumentException("ignored one"); } catch { }
            try { throw new InvalidOperationException("kept one"); } catch { }

            var snapshot = CrashDumpHelper.GetFirstChanceSnapshot();
            Assert.DoesNotContain("ignored one", string.Join("|", snapshot));
            Assert.Contains(snapshot, s => s.Contains("kept one"));
        }

        [Fact]
        public void ShouldIgnore_ThrowingCallback_TreatedAsNotIgnored()
        {
            var dir = CreateTempDir();
            var options = OptionsFor(dir);
            options.ShouldIgnore = _ => throw new Exception("filter blew up");

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(options);

            try { throw new InvalidOperationException("still recorded"); } catch { }

            Assert.Contains(CrashDumpHelper.GetFirstChanceSnapshot(),
                s => s.Contains("still recorded"));
        }

        #endregion

        #region 缓冲满自动落盘

        [Fact]
        public void RingBuffer_Full_AutoFlushesAndClears()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            for (int i = 0; i < CrashDumpHelper.FirstChanceMaxCount; i++)
            {
                try { throw new InvalidOperationException("fill" + i); } catch { }
            }

            Assert.Empty(CrashDumpHelper.GetFirstChanceSnapshot());
            Assert.Single(Directory.GetFiles(dir, "Crash_*.log"));
        }

        [Fact]
        public void AutoFlushWhenFull_Disabled_KeepsBuffer()
        {
            var dir = CreateTempDir();
            var options = OptionsFor(dir);
            options.AutoFlushWhenFull = false;

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(options);

            for (int i = 0; i < CrashDumpHelper.FirstChanceMaxCount; i++)
            {
                try { throw new InvalidOperationException("fill" + i); } catch { }
            }

            Assert.Equal(CrashDumpHelper.FirstChanceMaxCount,
                CrashDumpHelper.GetFirstChanceSnapshot().Count);
            Assert.Empty(Directory.GetFiles(dir, "Crash_*.log"));
        }

        [Fact]
        public void FlushFirstChanceLog_EmptyBuffer_ReturnsZero()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            Assert.Equal(0, CrashDumpHelper.FlushFirstChanceLog("UnitTest"));
            Assert.Empty(Directory.GetFiles(dir, "Crash_*.log"));
        }

        [Fact]
        public void FlushFirstChanceLog_WritesEntriesAndClears()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            try { throw new InvalidOperationException("first-record"); } catch { }
            try { throw new InvalidOperationException("second-record"); } catch { }

            Assert.Equal(2, CrashDumpHelper.FlushFirstChanceLog("UnitTest.Manual"));
            Assert.Empty(CrashDumpHelper.GetFirstChanceSnapshot());

            var path = Path.Combine(dir, "Crash_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            var content = File.ReadAllText(path);
            Assert.Contains("first-record", content);
            Assert.Contains("second-record", content);
            Assert.Contains("UnitTest.Manual", content);
        }

        [Fact]
        public void FlushFirstChanceLog_DoesNotGenerateDump()
        {
            var dir = CreateTempDir();

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(OptionsFor(dir));

            try { throw new InvalidOperationException("no-dump"); } catch { }

            CrashDumpHelper.FlushFirstChanceLog("UnitTest");

            Assert.Empty(Directory.GetFiles(dir, "CrashDump_*.dmp"));
        }

        #endregion

        #region 未观察 Task 异常

        [Fact]
        public void UnobservedTaskException_IsWrittenToLog()
        {
            var dir = CreateTempDir();
            var messages = new List<string>();
            var options = OptionsFor(dir);
            options.Diagnostics = messages.Add;

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(options);

            RunAndForgetFaultedTask();

            // 未观察异常要等 Task 被 GC 回收时才会触发，这里轮询等待
            var file = Path.Combine(dir, "Crash_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(file) && DateTime.UtcNow < deadline)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }

            Assert.True(File.Exists(file),
                "未观察的 Task 异常应被落盘。诊断信息: " + string.Join(" | ", messages));
            Assert.Contains("TaskScheduler.UnobservedTaskException", File.ReadAllText(file));
        }

        [Fact]
        public void HookUnobservedTaskException_Disabled_LeavesBufferUntouched()
        {
            var dir = CreateTempDir();
            var options = OptionsFor(dir);
            options.HookUnobservedTaskException = false;

            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(options);

            try { throw new InvalidOperationException("manual-probe"); } catch { }
            Assert.Single(CrashDumpHelper.GetFirstChanceSnapshot());

            RunAndForgetFaultedTask();

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // 关闭接管后，未观察 Task 异常不应触发写文件
            Assert.Empty(Directory.GetFiles(dir, "Crash_*.log"));
        }

        /// <summary>启动一个必定失败的后台 Task 且不观察其结果，用于触发 UnobservedTaskException。</summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RunAndForgetFaultedTask()
        {
            _ = Task.Run(() => throw new InvalidOperationException("unobserved task boom"));
        }

        #endregion

        #region 配置

        [Fact]
        public void Initialize_NullOptions_KeepsDefaults()
        {
            CrashDumpHelper.Deinitialize();
            CrashDumpHelper.Initialize(null);

            Assert.True(CrashDumpHelper.IsInitialized);
            Assert.True(CrashDumpHelper.Options.GenerateDump);
            Assert.Equal(30, CrashDumpHelper.Options.RetentionDays);
            Assert.Equal(MiniDumpType.Default, CrashDumpHelper.Options.DumpType);
        }

        #endregion
    }
}
