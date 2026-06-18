using System;
using System.Drawing;
using System.Windows.Forms;

namespace WinFormsDemo
{
    partial class MainForm
    {
        #region Control Fields
        private TabControl _tabs = null!;

        private TableLayoutPanel _cfgLayout = null!;
        private TextBox _txtAppName = null!, _txtVersion = null!, _txtDesc = null!, _txtApiKey = null!;
        private NumericUpDown _numMaxConn = null!, _numTimeout = null!;
        private CheckBox _chkEnableLog = null!;
        private ComboBox _cmbLogLevel = null!;
        private Button _btnCfgLoad = null!, _btnCfgSave = null!, _btnCfgReload = null!, _btnCfgShowFile = null!, _btnCfgReset = null!;
        private Label _lblCfgStatus = null!;

        private TableLayoutPanel _logLayout = null!;
        private ComboBox _cmbLogType = null!;
        private TextBox _txtLogMessage = null!;
        private Button _btnLogWrite = null!, _btnLogWriteBatch = null!, _btnLogViewFile = null!, _btnLogClear = null!;
        private ListBox _lstLog = null!;

        private TableLayoutPanel _xlLayout = null!;
        private Button _btnXlExportList = null!, _btnXlExportNested = null!, _btnXlExportDict = null!, _btnXlExportLargeDict = null!, _btnXlOpenFolder = null!;
        private TextBox _txtXlOutput = null!;
        private Label _lblXlStatus = null!;

        private TableLayoutPanel _retryLayout = null!;
        private ComboBox _cmbRetryPolicy = null!;
        private NumericUpDown _numRetryCount = null!, _numFailRate = null!;
        private Button _btnRetrySync = null!, _btnRetryAsync = null!, _btnRetryFallback = null!, _btnRetryCircuit = null!;
        private ListBox _lstRetry = null!;
        private Label _lblRetryStatus = null!;

        private TableLayoutPanel _sfLayout = null!;
        private Button _btnSfGenerate = null!, _btnSfGenerateBatch = null!, _btnSfDeconstruct = null!;
        private TextBox _txtSfId = null!, _txtSfDeconstruct = null!;
        private ListBox _lstSfIds = null!;

        private TableLayoutPanel _crashLayout = null!;
        private Button _btnCrashInit = null!, _btnCrashInfo = null!;
        private TextBox _txtCrashInfo = null!;

        private TableLayoutPanel _schLayout = null!;
        private Button _btnSchAdd = null!, _btnSchRemove = null!, _btnSchStart = null!, _btnSchStop = null!;
        private ListBox _lstSch = null!, _lstSchLog = null!;
        #endregion

        private void InitializeComponent()
        {
            Text = "VassasCo.Utility WinForms Demo";
            Size = new Size(950, 720);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 10F);

            _tabs = new TabControl { Dock = DockStyle.Fill };
            Controls.Add(_tabs);

            _tabs.TabPages.Add(CreateConfigHelperTab());
            _tabs.TabPages.Add(CreateLogHelperTab());
            _tabs.TabPages.Add(CreateExcelMapperTab());
            _tabs.TabPages.Add(CreateRetryHelperTab());
            _tabs.TabPages.Add(CreateSnowflakeTab());
            _tabs.TabPages.Add(CreateCrashDumpTab());
            _tabs.TabPages.Add(CreateScheduleTab());
        }

        #region Helper Methods
        private static Label CreateLabel(string text) => new() { Text = text, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Right };
        private static TextBox CreateTextBox(int width = 200) => new() { Width = width };
        private static NumericUpDown CreateNumeric(decimal min = 0, decimal max = 99999, int decimals = 0) => new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Width = 100 };
        private static Button CreateButton(string text, int width = 100) => new() { Text = text, Width = width, Height = 30 };
        private static ComboBox CreateCombo(string[] items, int width = 150) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, DataSource = items };
        private static ListBox CreateListBox() => new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };

        private TextBox AddRow(TableLayoutPanel panel, int row, string label)
        {
            panel.Controls.Add(CreateLabel(label), 0, row);
            var txt = CreateTextBox();
            panel.Controls.Add(txt, 1, row);
            return txt;
        }

        private NumericUpDown AddRowNumeric(TableLayoutPanel panel, int row, string label, decimal min = 0, decimal max = 99999, int decimals = 0)
        {
            panel.Controls.Add(CreateLabel(label), 0, row);
            var num = CreateNumeric(min, max, decimals);
            panel.Controls.Add(num, 1, row);
            return num;
        }

        private CheckBox AddRowCheck(TableLayoutPanel panel, int row, string label)
        {
            panel.Controls.Add(CreateLabel(label), 0, row);
            var chk = new CheckBox { AutoSize = true };
            panel.Controls.Add(chk, 1, row);
            return chk;
        }

        private ComboBox AddRowCombo(TableLayoutPanel panel, int row, string label, string[] items)
        {
            panel.Controls.Add(CreateLabel(label), 0, row);
            var cmb = CreateCombo(items);
            panel.Controls.Add(cmb, 1, row);
            return cmb;
        }
        #endregion

        #region Tab Creation
        private TabPage CreateConfigHelperTab()
        {
            var page = new TabPage("ConfigHelper");
            _cfgLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 12, Padding = new Padding(10) };
            _cfgLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _cfgLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _cfgLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _cfgLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            _txtAppName = AddRow(_cfgLayout, 0, "AppName:");
            _txtVersion = AddRow(_cfgLayout, 1, "Version:");
            _numMaxConn = AddRowNumeric(_cfgLayout, 2, "MaxConnections:");
            _numTimeout = AddRowNumeric(_cfgLayout, 3, "Timeout(s):", decimals: 1);
            _chkEnableLog = AddRowCheck(_cfgLayout, 4, "EnableLogging:");
            _txtDesc = AddRow(_cfgLayout, 5, "Description:");
            _txtApiKey = AddRow(_cfgLayout, 6, "ApiKey:");
            _cmbLogLevel = AddRowCombo(_cfgLayout, 7, "LogLevel:", new[] { "Info", "Debug", "Warning", "Error" });
            _lblCfgStatus = new Label { Text = "Ready", AutoSize = true, ForeColor = Color.Gray };
            _cfgLayout.Controls.Add(_lblCfgStatus, 0, 9);
            _cfgLayout.SetColumnSpan(_lblCfgStatus, 4);

            var btnRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            _btnCfgLoad = CreateButton("Load", 90);
            _btnCfgSave = CreateButton("Save", 90);
            _btnCfgReload = CreateButton("Reload", 90);
            _btnCfgShowFile = CreateButton("ShowFile", 90);
            _btnCfgReset = CreateButton("Reset", 90);
            btnRow.Controls.AddRange(new Control[] { _btnCfgLoad, _btnCfgSave, _btnCfgReload, _btnCfgShowFile, _btnCfgReset });
            _cfgLayout.Controls.Add(btnRow, 0, 10);
            _cfgLayout.SetColumnSpan(btnRow, 4);

            page.Controls.Add(_cfgLayout);
            return page;
        }

        private TabPage CreateLogHelperTab()
        {
            var page = new TabPage("LogHelper");
            _logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            _logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            _logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var topRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 0, 0, 8) };
            topRow.Controls.Add(new Label { Text = "Type:", AutoSize = true, Margin = new Padding(0, 5, 5, 0) });
            _cmbLogType = CreateCombo(new[] { "Debug", "Info", "Warning", "Error", "Performance", "Security", "Business", "Audit", "Operation", "System" });
            topRow.Controls.Add(_cmbLogType);
            topRow.Controls.Add(new Label { Text = "  Message:", AutoSize = true, Margin = new Padding(10, 5, 5, 0) });
            _txtLogMessage = new TextBox { Width = 250 };
            topRow.Controls.Add(_txtLogMessage);
            _logLayout.Controls.Add(topRow, 0, 0);

            _lstLog = CreateListBox();
            _logLayout.Controls.Add(_lstLog, 0, 1);

            var btnRow = new FlowLayoutPanel { AutoSize = true };
            _btnLogWrite = CreateButton("WriteLog", 100);
            _btnLogWriteBatch = CreateButton("Batch(10)", 100);
            _btnLogViewFile = CreateButton("ViewLogFile", 100);
            _btnLogClear = CreateButton("Clear", 80);
            btnRow.Controls.AddRange(new Control[] { _btnLogWrite, _btnLogWriteBatch, _btnLogViewFile, _btnLogClear });
            _logLayout.Controls.Add(btnRow, 0, 2);

            page.Controls.Add(_logLayout);
            return page;
        }

        private TabPage CreateExcelMapperTab()
        {
            var page = new TabPage("ExcelMapper");
            _xlLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            _xlLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _xlLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _xlLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var btnRow = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
            _btnXlExportList = CreateButton("Export List", 110);
            _btnXlExportNested = CreateButton("Export Nested", 110);
            _btnXlExportDict = CreateButton("Export Dict", 110);
            _btnXlExportLargeDict = CreateButton("Large Dict", 110);
            _btnXlOpenFolder = CreateButton("Open Folder", 110);
            btnRow.Controls.AddRange(new Control[] { _btnXlExportList, _btnXlExportNested, _btnXlExportDict, _btnXlExportLargeDict, _btnXlOpenFolder });
            _xlLayout.Controls.Add(btnRow, 0, 0);

            _lblXlStatus = new Label { Text = "Output Dir: exports/", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 0, 0, 8) };
            _xlLayout.Controls.Add(_lblXlStatus, 0, 1);

            _txtXlOutput = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, BackColor = Color.White };
            _xlLayout.Controls.Add(_txtXlOutput, 0, 2);

            page.Controls.Add(_xlLayout);
            return page;
        }

        private TabPage CreateRetryHelperTab()
        {
            var page = new TabPage("RetryHelper");
            _retryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            _retryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _retryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            _retryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var topRow = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
            topRow.Controls.Add(new Label { Text = "Policy:", AutoSize = true, Margin = new Padding(0, 5, 5, 0) });
            _cmbRetryPolicy = CreateCombo(new[] { "Default", "FixedBackoff", "LinearBackoff", "Exponential+Jitter" });
            topRow.Controls.Add(_cmbRetryPolicy);
            topRow.Controls.Add(new Label { Text = "  FailRate%:", AutoSize = true, Margin = new Padding(10, 5, 5, 0) });
            _numFailRate = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 50, Width = 60 };
            topRow.Controls.Add(_numFailRate);
            topRow.Controls.Add(new Label { Text = "  MaxRetries:", AutoSize = true, Margin = new Padding(10, 5, 5, 0) });
            _numRetryCount = new NumericUpDown { Minimum = 1, Maximum = 10, Value = 4, Width = 60 };
            topRow.Controls.Add(_numRetryCount);
            _retryLayout.Controls.Add(topRow, 0, 0);

            _lstRetry = CreateListBox();
            _retryLayout.Controls.Add(_lstRetry, 0, 1);

            var btnRow = new FlowLayoutPanel { AutoSize = true };
            _btnRetrySync = CreateButton("Sync Retry", 100);
            _btnRetryAsync = CreateButton("Async Retry", 100);
            _btnRetryFallback = CreateButton("Fallback", 100);
            _btnRetryCircuit = CreateButton("CircuitBreak", 100);
            _lblRetryStatus = new Label { Text = "Ready", AutoSize = true, ForeColor = Color.Gray };
            btnRow.Controls.AddRange(new Control[] { _btnRetrySync, _btnRetryAsync, _btnRetryFallback, _btnRetryCircuit, _lblRetryStatus });
            _retryLayout.Controls.Add(btnRow, 0, 2);

            page.Controls.Add(_retryLayout);
            return page;
        }

        private TabPage CreateSnowflakeTab()
        {
            var page = new TabPage("SnowflakeIdHelper");
            _sfLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            _sfLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _sfLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            _sfLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var btnRow = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
            _btnSfGenerate = CreateButton("Generate", 100);
            _btnSfGenerateBatch = CreateButton("Batch(100)", 100);
            _btnSfDeconstruct = CreateButton("Deconstruct", 110);
            _txtSfId = new TextBox { Width = 200, PlaceholderText = "Paste ID here" };
            btnRow.Controls.AddRange(new Control[] { _btnSfGenerate, _btnSfGenerateBatch, _txtSfId, _btnSfDeconstruct });
            _sfLayout.Controls.Add(btnRow, 0, 0);

            _lstSfIds = CreateListBox();
            _sfLayout.Controls.Add(_lstSfIds, 0, 1);

            _txtSfDeconstruct = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Height = 60, BackColor = Color.White };
            _sfLayout.Controls.Add(_txtSfDeconstruct, 0, 2);

            page.Controls.Add(_sfLayout);
            return page;
        }

        private TabPage CreateCrashDumpTab()
        {
            var page = new TabPage("CrashDumpHelper");
            _crashLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
            _crashLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _crashLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var btnRow = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
            _btnCrashInit = CreateButton("Initialize", 110);
            _btnCrashInfo = CreateButton("Show Status", 110);
            btnRow.Controls.AddRange(new Control[] { _btnCrashInit, _btnCrashInfo });
            _crashLayout.Controls.Add(btnRow, 0, 0);

            _txtCrashInfo = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, BackColor = Color.White };
            _crashLayout.Controls.Add(_txtCrashInfo, 0, 1);

            page.Controls.Add(_crashLayout);
            return page;
        }

        private TabPage CreateScheduleTab()
        {
            var page = new TabPage("ScheduleHelper");
            _schLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
            _schLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _schLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _schLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _schLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _schLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _schLayout.Controls.Add(new Label { Text = "Tasks:", AutoSize = true }, 0, 0);
            _schLayout.Controls.Add(new Label { Text = "Log:", AutoSize = true }, 1, 0);

            _lstSch = CreateListBox();
            _schLayout.Controls.Add(_lstSch, 0, 1);

            _lstSchLog = CreateListBox();
            _schLayout.Controls.Add(_lstSchLog, 1, 1);

            var btnRow = new FlowLayoutPanel { AutoSize = true };
            _btnSchAdd = CreateButton("Add Task", 100);
            _btnSchRemove = CreateButton("Remove", 80);
            _btnSchStart = CreateButton("Start All", 100);
            _btnSchStop = CreateButton("Stop All", 100);
            btnRow.Controls.AddRange(new Control[] { _btnSchAdd, _btnSchRemove, _btnSchStart, _btnSchStop });
            _schLayout.Controls.Add(btnRow, 0, 2);
            _schLayout.SetColumnSpan(btnRow, 2);

            page.Controls.Add(_schLayout);
            return page;
        }
        #endregion
    }
}
