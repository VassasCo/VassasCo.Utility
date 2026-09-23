// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VassasCo.Utility;
using Xunit;

namespace VassasCo.Utility.Tests
{
    public sealed class LogHelperTests : IDisposable
    {
        private readonly string _dir;

        public LogHelperTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "LogHelperTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); }
            catch { /* 忽略清理失败 */ }
        }

        private string? FindLogFile(string fileName)
        {
            if (!Directory.Exists(_dir))
                return null;
            foreach (string f in Directory.EnumerateFiles(_dir, fileName, SearchOption.AllDirectories))
                return f;
            return null;
        }

        [Fact]
        public void Write_And_Dispose_WritesToFile()
        {
            using (var logger = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start())
            {
                logger.AddLog(LogCategories.Database, LogLevel.Info, "查询完成");
            }

            string? file = FindLogFile("Database.log");
            Assert.NotNull(file);
            string content = File.ReadAllText(file!);
            Assert.Contains("查询完成", content);
            Assert.Contains("[Info]", content);
        }

        [Fact]
        public void Categories_WriteToSeparateFiles()
        {
            using (var logger = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start())
            {
                logger.AddLog(LogCategories.Api, LogLevel.Error, "api 错误");
                logger.AddLog(LogCategories.Database, LogLevel.Error, "db 错误");
            }

            Assert.NotNull(FindLogFile("Api.log"));
            Assert.NotNull(FindLogFile("Database.log"));
            Assert.Contains("api 错误", File.ReadAllText(FindLogFile("Api.log")!));
            Assert.Contains("db 错误", File.ReadAllText(FindLogFile("Database.log")!));
        }

        [Fact]
        public void MinLevel_FiltersLowerLevels()
        {
            using (var logger = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Warning)
                .Start())
            {
                logger.AddLog(LogCategories.General, LogLevel.Info, "不应记录");
                logger.AddLog(LogCategories.General, LogLevel.Error, "应记录");
            }

            string? file = FindLogFile("General.log");
            Assert.NotNull(file);
            string content = File.ReadAllText(file!);
            Assert.Contains("应记录", content);
            Assert.DoesNotContain("不应记录", content);
        }

        [Fact]
        public void NullCategory_FallsBackToGeneral()
        {
            using (var logger = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start())
            {
                logger.AddLog(null!, LogLevel.Info, "无分类消息");
            }

            Assert.NotNull(FindLogFile("General.log"));
        }

        [Fact]
        public void Module_IsWrittenToContent()
        {
            using (var logger = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start())
            {
                logger.AddLog(LogCategories.Api, LogLevel.Error, "接口超时", "订单服务");
            }

            string content = File.ReadAllText(FindLogFile("Api.log")!);
            Assert.Contains("[订单服务]", content);
        }

        [Fact]
        public void Category_WithPathSeparator_IsSanitized()
        {
            using (var logger = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start())
            {
                logger.AddLog("bad/name", LogLevel.Info, "安全");
            }

            // 分类中的路径分隔符被替换为下划线，不会产生子目录/路径穿越
            Assert.NotNull(FindLogFile("bad_name.log"));
        }

        [Fact]
        public void OnError_Invoked_OnWriteFailure()
        {
            int errorCount = 0;
            string fileAsDirectory = Path.Combine(_dir, "conflict.log");
            File.WriteAllText(fileAsDirectory, string.Empty);

            using (var logger = LogHelper.Build()
                .SetLogPath(fileAsDirectory)
                .SetMinLevel(LogLevel.Trace)
                .EnableDailyCleanup(false)
                .OnError(_ => Interlocked.Increment(ref errorCount))
                .Start())
            {
                logger.AddLog(LogCategories.General, LogLevel.Info, "触发写入失败");
            }

            Assert.True(errorCount > 0);
        }

        [Fact]
        public void Dispose_FlushesAllQueuedLogs()
        {
            using (var logger = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start())
            {
                for (int i = 0; i < 100; i++)
                    logger.AddLog(LogCategories.General, LogLevel.Info, "消息 " + i);
            }

            string[] lines = File.ReadAllLines(FindLogFile("General.log")!);
            Assert.Equal(100, lines.Length);
        }

        [Fact]
        public void Concurrent_AddLog_IsThreadSafe()
        {
            using (var logger = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start())
            {
                Parallel.For(0, 1000, i => logger.AddLog(LogCategories.General, LogLevel.Info, "消息 " + i));
            }

            string[] lines = File.ReadAllLines(FindLogFile("General.log")!);
            Assert.Equal(1000, lines.Length);
        }

        [Fact]
        public void LogApi_Extension_WritesApiCategory()
        {
            LogManager.Current = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start();
            try
            {
                "接口调用失败".LogApi(LogLevel.Error);
            }
            finally
            {
                LogManager.Shutdown();
            }

            string content = File.ReadAllText(FindLogFile("Api.log")!);
            Assert.Contains("接口调用失败", content);
            Assert.Contains("[Error]", content);
        }

        [Fact]
        public void LogError_Exception_IncludesInnerException()
        {
            var inner = new InvalidOperationException("内部错误");
            var outer = new ApplicationException("外层错误", inner);

            LogManager.Current = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start();
            try
            {
                outer.LogError(LogCategories.Database);
            }
            finally
            {
                LogManager.Shutdown();
            }

            string content = File.ReadAllText(FindLogFile("Database.log")!);
            Assert.Contains("外层错误", content);
            Assert.Contains("内部错误", content);
        }

        [Fact]
        public void LogManager_GlobalManagement()
        {
            Assert.False(LogManager.IsInitialized);

            LogManager.Current = LogHelper.Build()
                .SetLogPath(_dir)
                .SetMinLevel(LogLevel.Trace)
                .Start();
            try
            {
                Assert.True(LogManager.IsInitialized);
                "测试日志".LogSystem();
            }
            finally
            {
                LogManager.Shutdown();
            }

            Assert.False(LogManager.IsInitialized);
            Assert.NotNull(FindLogFile("System.log"));
        }
    }
}
