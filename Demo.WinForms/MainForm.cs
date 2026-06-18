using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VassasCo.Utility;

namespace WinFormsDemo
{
    public partial class MainForm : Form
    {
        private readonly string _configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
        private readonly string _exportDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exports");
        private readonly string _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        private CancellationTokenSource? _scheduleCts;
        private LogHelper? _logHelper;

        public MainForm()
        {
            if (!Directory.Exists(_configDir)) Directory.CreateDirectory(_configDir);
            if (!Directory.Exists(_exportDir)) Directory.CreateDirectory(_exportDir);
            if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);

            InitializeComponent();
            WireUpEvents();
            LoadConfig();
        }

        private void WireUpEvents()
        {
            // ConfigHelper
            _btnCfgLoad.Click += (s, e) => LoadConfig();
            _btnCfgSave.Click += (s, e) => SaveConfig();
            _btnCfgReload.Click += (s, e) => LoadConfig();
            _btnCfgShowFile.Click += (s, e) => ShowConfigFile();
            _btnCfgReset.Click += (s, e) => ResetConfig();

            // LogHelper
            _btnLogWrite.Click += (s, e) => WriteLog();
            _btnLogWriteBatch.Click += (s, e) => WriteLogBatch();
            _btnLogViewFile.Click += (s, e) => ViewLogFile();
            _btnLogClear.Click += (s, e) => _lstLog.Items.Clear();

            // ExcelMapper
            _btnXlExportList.Click += (s, e) => ExportSimpleList();
            _btnXlExportNested.Click += (s, e) => ExportNestedData();
            _btnXlExportDict.Click += (s, e) => ExportDictionary();
            _btnXlExportLargeDict.Click += (s, e) => ExportLargeDictionary();
            _btnXlOpenFolder.Click += (s, e) => Process.Start("explorer", _exportDir);

            // RetryHelper
            _btnRetrySync.Click += (s, e) => RunSyncRetry();
            _btnRetryAsync.Click += async (s, e) => await RunAsyncRetry();
            _btnRetryFallback.Click += (s, e) => RunFallbackRetry();
            _btnRetryCircuit.Click += (s, e) => RunCircuitBreakerDemo();

            // SnowflakeIdHelper
            _btnSfGenerate.Click += (s, e) => GenerateSnowflakeId();
            _btnSfGenerateBatch.Click += (s, e) => GenerateSnowflakeIdBatch();
            _btnSfDeconstruct.Click += (s, e) => DeconstructSnowflakeId();

            // CrashDumpHelper
            _btnCrashInit.Click += (s, e) => InitCrashDump();
            _btnCrashInfo.Click += (s, e) => ShowCrashInfo();

            // ScheduleHelper
            _btnSchAdd.Click += (s, e) => AddScheduleTask();
            _btnSchRemove.Click += (s, e) => RemoveScheduleTask();
            _btnSchStart.Click += (s, e) => StartScheduler();
            _btnSchStop.Click += (s, e) => StopScheduler();
        }

        #region ConfigHelper
        private string ConfigPath => Path.Combine(_configDir, "demo_app.json");

        private void LoadConfig()
        {
            try
            {
                DemoAppConfig cfg;
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    cfg = System.Text.Json.JsonSerializer.Deserialize<DemoAppConfig>(json) ?? new DemoAppConfig();
                }
                else { cfg = new DemoAppConfig(); }

                _txtAppName.Text = cfg.AppName;
                _txtVersion.Text = cfg.Version;
                _numMaxConn.Value = cfg.MaxConnections;
                _numTimeout.Value = (decimal)cfg.TimeoutSeconds;
                _chkEnableLog.Checked = cfg.EnableLogging;
                _txtDesc.Text = cfg.Description;
                _txtApiKey.Text = cfg.ApiKey;
                _cmbLogLevel.Text = cfg.LogLevel;
                _lblCfgStatus.Text = "Loaded OK";
                _lblCfgStatus.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                _lblCfgStatus.Text = "Error: " + ex.Message;
                _lblCfgStatus.ForeColor = Color.Red;
            }
        }

        private void SaveConfig()
        {
            try
            {
                var cfg = new DemoAppConfig
                {
                    AppName = _txtAppName.Text,
                    Version = _txtVersion.Text,
                    MaxConnections = (int)_numMaxConn.Value,
                    TimeoutSeconds = (double)_numTimeout.Value,
                    EnableLogging = _chkEnableLog.Checked,
                    Description = _txtDesc.Text,
                    ApiKey = _txtApiKey.Text,
                    LogLevel = _cmbLogLevel.Text
                };
                var json = System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                _lblCfgStatus.Text = "Saved OK -> " + ConfigPath;
                _lblCfgStatus.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                _lblCfgStatus.Text = "Save Error: " + ex.Message;
                _lblCfgStatus.ForeColor = Color.Red;
            }
        }

        private void ShowConfigFile()
        {
            if (File.Exists(ConfigPath)) Process.Start("notepad", ConfigPath);
        }

        private void ResetConfig()
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            LoadConfig();
        }
        #endregion

        #region LogHelper
        private void EnsureLogHelper()
        {
            if (_logHelper == null)
            {
                _logHelper = LogHelper.Build()
                    .SetLogPath(_logDir)
                    .AddLogTypes("Debug", "Info", "Warning", "Error", "Performance", "Security", "Business", "Audit", "Operation", "System")
                    .SetRetentionMonths(1)
                    .Start();
            }
        }

        private void WriteLog()
        {
            EnsureLogHelper();
            var t = _cmbLogType.Text;
            var m = string.IsNullOrWhiteSpace(_txtLogMessage.Text) ? $"Test {t} log" : _txtLogMessage.Text;
            _logHelper!.AddLog(t, m, "Demo");
            _lstLog.Items.Insert(0, new LogDemoEntry { Time = DateTime.Now, Type = t, Message = m });
        }

        private void WriteLogBatch()
        {
            EnsureLogHelper();
            var types = new[] { "Debug", "Info", "Warning", "Error" };
            for (int i = 0; i < 10; i++)
            {
                var t = types[i % 4];
                var m = $"Batch log #{i + 1}";
                _logHelper!.AddLog(t, m, "Demo");
                _lstLog.Items.Insert(0, new LogDemoEntry { Time = DateTime.Now, Type = t, Message = m });
            }
        }

        private void ViewLogFile()
        {
            var files = Directory.GetFiles(_logDir, "*.log", SearchOption.AllDirectories);
            if (files.Length > 0)
                Process.Start("notepad", files.OrderByDescending(f => f).First());
            else
                MessageBox.Show("No log files found.", "LogHelper");
        }
        #endregion

        #region ExcelMapper
        private void ExportSimpleList()
        {
            try
            {
                var orders = new List<DemoOrder>
                {
                    new() { Id = SnowflakeIdHelper.Next(), Customer = "Alice", Amount = 150.50m, OrderDate = DateTime.Now },
                    new() { Id = SnowflakeIdHelper.Next(), Customer = "Bob", Amount = 299.99m, OrderDate = DateTime.Now.AddDays(-1) },
                    new() { Id = SnowflakeIdHelper.Next(), Customer = "Charlie", Amount = 89.00m, OrderDate = DateTime.Now.AddDays(-2) }
                };
                var path = Path.Combine(_exportDir, "SimpleList.xlsx");
                ExcelMapper.ToFile(orders, path, "Orders");
                AppendOutput($"Exported: {path} (3 orders)");
                AppendOutput("  Headers use [ExcelDisplay]: 订单编号, 客户名称, 订单金额, 下单日期");
            }
            catch (Exception ex) { AppendOutput($"Error: {ex.Message}"); }
        }

        private void ExportNestedData()
        {
            try
            {
                var orders = new List<DemoOrder>
                {
                    new()
                    {
                        Id = SnowflakeIdHelper.Next(), Customer = "Alice", Amount = 150.50m, OrderDate = DateTime.Now,
                        CustomerInfo = new DemoCustomer { Name = "Alice", City = "Beijing", Phone = "13800001111" },
                        Items = { new DemoOrderItem { ProductName = "Mouse", Quantity = 2, UnitPrice = 75.25m } }
                    },
                    new()
                    {
                        Id = SnowflakeIdHelper.Next(), Customer = "Bob", Amount = 520m, OrderDate = DateTime.Now.AddDays(-1),
                        CustomerInfo = new DemoCustomer { Name = "Bob", City = "Shanghai", Phone = "13800002222" },
                        Items =
                        {
                            new DemoOrderItem { ProductName = "Keyboard", Quantity = 1, UnitPrice = 320m },
                            new DemoOrderItem { ProductName = "Monitor", Quantity = 1, UnitPrice = 200m }
                        }
                    }
                };
                var path = Path.Combine(_exportDir, "NestedData.xlsx");
                ExcelMapper.ToFile(orders, path, "Orders");
                AppendOutput($"Exported: {path}");
                AppendOutput("  [ExcelDisplay] columns: 订单编号, 客户名称, 订单金额, 下单日期, 客户信息, 订单明细");
                AppendOutput("  Child sheet: 'Orders.订单明细', parent column: '订单编号'");
            }
            catch (Exception ex) { AppendOutput($"Error: {ex.Message}"); }
        }

        private void ExportDictionary()
        {
            try
            {
                var d = new Dictionary<string, object>
                {
                    ["Server"] = "192.168.1.1",
                    ["Port"] = 8080,
                    ["Database"] = "MyDB",
                    ["User"] = "admin",
                    ["ConnectionTimeout"] = 30
                };
                var path = Path.Combine(_exportDir, "Dictionary.xlsx");
                ExcelMapper.DictionaryToFile(d, path, "Config");
                AppendOutput($"Exported: {path}");
            }
            catch (Exception ex) { AppendOutput($"Error: {ex.Message}"); }
        }

        private void ExportLargeDictionary()
        {
            try
            {
                var d = new Dictionary<string, string>();
                for (int i = 1; i <= 20; i++)
                    d[$"Key_{i:D2}"] = $"Value_{i:D2}";
                var path = Path.Combine(_exportDir, "LargeDict.xlsx");
                ExcelMapper.DictionaryToFile(d, path, "LargeConfig");
                AppendOutput($"Exported: {path}");
            }
            catch (Exception ex) { AppendOutput($"Error: {ex.Message}"); }
        }

        private void AppendOutput(string text) { _txtXlOutput.AppendText(text + Environment.NewLine); }
        #endregion

        #region RetryHelper
        private int _retryFailCounter;

        private RetryPolicy BuildRetryPolicy()
        {
            var b = RetryPolicy.Build().WithMaxRetries((int)_numRetryCount.Value);
            switch (_cmbRetryPolicy.Text)
            {
                case "FixedBackoff": b.WithFixedBackoff(TimeSpan.FromMilliseconds(200)); break;
                case "LinearBackoff": b.WithLinearBackoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2)); break;
                case "Exponential+Jitter": b.WithExponentialBackoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3)).WithJitter(JitterType.Full); break;
            }
            b.OnRetry(ctx => this.Invoke(() => _lstRetry.Items.Insert(0, $"Retry #{ctx.Attempt}: wait={ctx.WaitDuration.TotalMilliseconds:F0}ms, ex={ctx.Exception?.GetType().Name}")));
            return b.Build();
        }

        private void RunSyncRetry()
        {
            _retryFailCounter = 0; _lstRetry.Items.Clear();
            var sw = Stopwatch.StartNew();
            try
            {
                BuildRetryPolicy().Execute(() =>
                {
                    _retryFailCounter++;
                    if (new Random().Next(100) < (int)_numFailRate.Value)
                        throw new InvalidOperationException($"Fail #{_retryFailCounter}");
                    return true;
                });
                sw.Stop();
                _lblRetryStatus.Text = $"Sync: success in {sw.ElapsedMilliseconds}ms";
                _lblRetryStatus.ForeColor = Color.Green;
            }
            catch (RetryExhaustedException)
            {
                sw.Stop();
                _lblRetryStatus.Text = $"Sync: exhausted after {sw.ElapsedMilliseconds}ms";
                _lblRetryStatus.ForeColor = Color.Red;
            }
        }

        private async Task RunAsyncRetry()
        {
            _retryFailCounter = 0; _lstRetry.Items.Clear();
            var sw = Stopwatch.StartNew();
            try
            {
                await BuildRetryPolicy().ExecuteAsync(async ct =>
                {
                    await Task.Delay(50, ct);
                    _retryFailCounter++;
                    if (_retryFailCounter % 3 == 0)
                        throw new TimeoutException($"Fail #{_retryFailCounter}");
                    return "OK";
                });
                sw.Stop();
                _lblRetryStatus.Text = $"Async: success in {sw.ElapsedMilliseconds}ms";
                _lblRetryStatus.ForeColor = Color.Green;
            }
            catch (RetryExhaustedException)
            {
                sw.Stop();
                _lblRetryStatus.Text = $"Async: exhausted after {sw.ElapsedMilliseconds}ms";
                _lblRetryStatus.ForeColor = Color.Red;
            }
        }

        private void RunFallbackRetry()
        {
            _lstRetry.Items.Clear();
            var p = RetryPolicy.Build().WithMaxRetries(2).WithFixedBackoff(TimeSpan.FromMilliseconds(100)).Build();
            var r = p.ExecuteWithFallback(
                () => throw new InvalidOperationException("Primary failed"),
                report => $"Fallback (attempts: {report.TotalAttempts})");
            _lblRetryStatus.Text = $"Fallback: {r}";
            _lblRetryStatus.ForeColor = Color.Orange;
        }

        private void RunCircuitBreakerDemo()
        {
            _lstRetry.Items.Clear();
            var p = RetryPolicy.Build().WithCircuitBreaker(3, TimeSpan.FromSeconds(5)).WithMaxRetries(1).Build();
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    p.Execute(() =>
                    {
                        _lstRetry.Items.Insert(0, $"Call #{i + 1}");
                        throw new InvalidOperationException("Force fail");
                    });
                }
                catch (Exception ex)
                {
                    _lstRetry.Items.Insert(0, $"#{i + 1}: {ex.Message}");
                }
            }
            _lblRetryStatus.Text = "CircuitBreaker demo done";
            _lblRetryStatus.ForeColor = Color.Blue;
        }
        #endregion

        #region SnowflakeIdHelper
        private void GenerateSnowflakeId()
        {
            try
            {
                var id = SnowflakeIdHelper.Next();
                var info = SnowflakeIdHelper.Parse(id);
                _lstSfIds.Items.Insert(0, $"ID: {id} | Time: {info.Timestamp:HH:mm:ss.fff} | DC:{info.DataCenterId} | W:{info.WorkerId} | Seq:{info.Sequence}");
            }
            catch (Exception ex) { _lstSfIds.Items.Insert(0, $"Error: {ex.Message}"); }
        }

        private void GenerateSnowflakeIdBatch()
        {
            try
            {
                _lstSfIds.BeginUpdate();
                _lstSfIds.Items.Clear();
                for (int i = 0; i < 100; i++)
                {
                    var id = SnowflakeIdHelper.Next();
                    var info = SnowflakeIdHelper.Parse(id);
                    _lstSfIds.Items.Add($"{id} | {info.Timestamp:HH:mm:ss.fff} | DC:{info.DataCenterId} | W:{info.WorkerId} | S:{info.Sequence}");
                }
                _lstSfIds.EndUpdate();
            }
            catch (Exception ex) { _lstSfIds.Items.Insert(0, $"Error: {ex.Message}"); }
        }

        private void DeconstructSnowflakeId()
        {
            try
            {
                if (long.TryParse(_txtSfId.Text.Trim(), out var id))
                {
                    var info = SnowflakeIdHelper.Parse(id);
                    _txtSfDeconstruct.Text =
                        $"Timestamp: {info.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\r\n" +
                        $"DataCenter: {info.DataCenterId}\r\n" +
                        $"WorkerId: {info.WorkerId}\r\n" +
                        $"Sequence: {info.Sequence}\r\n" +
                        $"RawId: {info.RawId}";
                }
                else { _txtSfDeconstruct.Text = "Invalid ID"; }
            }
            catch (Exception ex) { _txtSfDeconstruct.Text = $"Error: {ex.Message}"; }
        }
        #endregion

        #region CrashDumpHelper
        private void InitCrashDump()
        {
            try
            {
                CrashDumpHelper.Initialize();
                _txtCrashInfo.AppendText("CrashDumpHelper initialized.\r\nFirstChance tracking active.\r\n");
            }
            catch (Exception ex) { _txtCrashInfo.AppendText($"Error: {ex.Message}\r\n"); }
        }

        private void ShowCrashInfo()
        {
            _txtCrashInfo.AppendText("\r\n=== CrashDumpHelper Status ===\r\n" +
                "Passive monitoring tool.\r\n" +
                "Records FirstChance exceptions.\r\n" +
                "On crash: writes crash log + MiniDump (Windows only).\r\n");
        }
        #endregion

        #region ScheduleHelper
        private void AddScheduleTask()
        {
            _lstSch.Items.Add(new ScheduleTaskInfo
            {
                Name = $"Task_{DateTime.Now:HHmmss}",
                CronExpression = "*/10 * * * * *",
                NextRun = DateTime.Now.AddSeconds(10),
                IsRunning = false
            });
        }

        private void RemoveScheduleTask()
        {
            if (_lstSch.SelectedItem is ScheduleTaskInfo info)
                _lstSch.Items.Remove(info);
        }

        private void StartScheduler()
        {
            _scheduleCts = new CancellationTokenSource();
            Task.Run(async () =>
            {
                while (!_scheduleCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(3000, _scheduleCts.Token);
                        this.Invoke(() =>
                        {
                            _lstSchLog.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Tick: {_lstSch.Items.Count} tasks");
                            if (_lstSchLog.Items.Count > 50)
                                _lstSchLog.Items.RemoveAt(_lstSchLog.Items.Count - 1);
                        });
                    }
                    catch (OperationCanceledException) { break; }
                }
            }, _scheduleCts.Token);
        }

        private void StopScheduler() { _scheduleCts?.Cancel(); }
        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _scheduleCts?.Cancel();
                _scheduleCts?.Dispose();
                _logHelper?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
