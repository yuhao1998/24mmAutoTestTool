using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SocOtaUpgrade
{
    internal sealed class UiLogSink : ILogSink
    {
        private readonly Control _host;
        private readonly RichTextBox _logBox;

        public UiLogSink(Control host, RichTextBox logBox)
        {
            _host = host;
            _logBox = logBox;
        }

        public void Step(string message)
        {
            Append("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message, Color.Black);
        }

        public void Error(string message)
        {
            Append("[ERROR] " + message, Color.DarkRed);
        }

        private void Append(string text, Color color)
        {
            if (_host.InvokeRequired)
            {
                _host.Invoke(new Action<string, Color>(Append), text, color);
                return;
            }
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.SelectionColor = color;
            _logBox.AppendText(text + Environment.NewLine);
            _logBox.SelectionColor = _logBox.ForeColor;
            _logBox.ScrollToCaret();
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly string _baseDir;
        private AppConfig _cfg;

        private TextBox _txtPackage;
        private TextBox _txtCom;
        private NumericUpDown _numBaud;
        private TextBox _txtUsbCmd;
        private TextBox _txtRemoteDir;
        private NumericUpDown _numBootTimeout;
        private NumericUpDown _numUpdateTimeout;
        private TextBox _txtLogDir;
        private TextBox _txtEmailImapHost;
        private TextBox _txtEmailUsername;
        private TextBox _txtEmailPassword;
        private TextBox _txtEmailMailbox;
        private TextBox _txtEmailFrom;
        private TextBox _txtEmailSubjectKeyword;
        private TextBox _txtEmailPackageSaveDir;
        private Button _btnBrowseEmailSaveDir;
        private RichTextBox _logBox;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnSave;
        private Button _btnBrowse;
        private Button _btnOpenLogs;
        private Button _btnHalStatus;
        private Button _btnSerialReboot;
        private Button _btnSerialDev;
        private Button _btnAdbRoot;
        private Button _btnAdbRemount;
        private Button _btnEmailListenStart;
        private Button _btnEmailListenStop;
        private Button _btnEmailSimulate;
        private CheckBox _chkEmailSimulateFullUpgrade;
        private CheckBox _chkVersionVerify;
        private CheckBox _chkHalStatus;
        private DataGridView _dgvHalModules;
        private Button _btnHalModulesAdd;
        private Button _btnHalModulesDelete;
        private Button _btnHalModulesReset;
        private Button _btnHalScriptBrowse;
        private ListView _lvResults;
        private SplitContainer _splitMain;
        private TabControl _tabsConfig;
        private TabControl _tabsOutput;
        private ProgressBar _progress;
        private Label _lblStatus;
        private EmailListenRunner _emailRunner;

        private Thread _worker;
        private volatile bool _running;
        private volatile bool _toolBusy;
        private UpgradeService _activeService;

        private readonly string _autoStartPackage;
        private readonly bool _reportMode;
        public int ExitCode { get; private set; }

        public MainForm(string baseDir, string autoStartPackage = null, bool reportMode = false)
        {
            _baseDir = baseDir;
            _autoStartPackage = string.IsNullOrWhiteSpace(autoStartPackage) ? null : autoStartPackage;
            _reportMode = reportMode;
            _cfg = AppConfig.Load(baseDir, null);
            EmailListenConfigHelper.ApplyFromT2Config(baseDir, _cfg);
            InitializeUi();
            AppIconHelper.TryApply(this, baseDir);
            LoadConfigToUi();
            FormClosed += OnFormClosed;
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            if (_activeService != null)
            {
                _activeService.RequestStop();
            }
            if (_emailRunner != null)
            {
                _emailRunner.Dispose();
                _emailRunner = null;
            }
            AdbProcessTracker.CleanupAll(true);
        }

        private void InitializeUi()
        {
            Text = AppBranding.ProductName;
            Size = new Size(1100, 820);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            MinimumSize = new Size(900, 640);

            var panelActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                Padding = new Padding(12, 6, 12, 0),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            _btnSave = new Button { Text = "保存配置", Width = 100, Height = 30 };
            _btnStart = new Button { Text = "开始升级", Width = 100, Height = 30 };
            _btnStop = new Button { Text = "结束升级", Width = 100, Height = 30, Enabled = false };
            _btnOpenLogs = new Button { Text = "打开日志目录", Width = 110, Height = 30 };
            _btnSave.Click += OnSaveConfig;
            _btnStart.Click += OnStartUpgrade;
            _btnStop.Click += OnStopUpgrade;
            _btnOpenLogs.Click += OnOpenLogs;
            panelActions.Controls.Add(_btnSave);
            panelActions.Controls.Add(_btnStart);
            panelActions.Controls.Add(_btnStop);
            panelActions.Controls.Add(_btnOpenLogs);

            _lblStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Padding = new Padding(12, 4, 0, 0),
                Text = "就绪"
            };
            _progress = new ProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 16,
                Style = ProgressBarStyle.Marquee,
                Visible = false,
                MarqueeAnimationSpeed = 30
            };

            _splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                Panel1MinSize = 180,
                Panel2MinSize = 120,
                Padding = new Padding(8, 8, 8, 4)
            };

            _tabsConfig = new TabControl { Dock = DockStyle.Fill };
            _tabsConfig.TabPages.Add(BuildUpgradeTab());
            _tabsConfig.TabPages.Add(BuildDeviceTab());
            _tabsConfig.TabPages.Add(BuildEmailTab());
            _tabsConfig.TabPages.Add(BuildVerifyTab());
            _splitMain.Panel1.Controls.Add(_tabsConfig);

            _tabsOutput = new TabControl { Dock = DockStyle.Fill };

            var pageResults = new TabPage("检查结果");
            _lvResults = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Microsoft YaHei UI", 9F),
                Scrollable = true
            };
            _lvResults.Columns.Add("分类", 80);
            _lvResults.Columns.Add("检查项", 160);
            _lvResults.Columns.Add("结果", 60);
            _lvResults.Columns.Add("说明", 100);
            _lvResults.Resize += (s, e) => FitListViewColumns(_lvResults);
            pageResults.Controls.Add(_lvResults);

            var pageLog = new TabPage("运行日志");
            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.White,
                Font = new Font("Consolas", 9F),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both
            };
            pageLog.Controls.Add(_logBox);

            _tabsOutput.TabPages.Add(pageResults);
            _tabsOutput.TabPages.Add(pageLog);
            _splitMain.Panel2.Controls.Add(_tabsOutput);

            Controls.Add(_splitMain);
            Controls.Add(_progress);
            Controls.Add(_lblStatus);
            Controls.Add(panelActions);

            Load += OnMainFormLoad;
            Resize += (s, e) => FitListViewColumns(_lvResults);

            AppendInfo("程序目录: " + _baseDir);
            AppendInfo("配置文件: " + AppConfig.ConfigPath(_baseDir));
            AppendInfo("adb 工具: " + Path.Combine(_baseDir, "tools", "adb.exe"));
        }

        private static void FitListViewColumns(ListView lv)
        {
            if (lv == null || lv.Columns.Count == 0 || lv.ClientSize.Width <= 0) return;
            int fixedW = 0;
            for (int i = 0; i < lv.Columns.Count - 1; i++)
                fixedW += lv.Columns[i].Width;
            int last = lv.ClientSize.Width - fixedW - 4;
            if (SystemInformation.VerticalScrollBarWidth > 0 && lv.Items.Count > 0)
            {
                // 预留竖向滚动条宽度，避免出现横向滚动条
                last -= SystemInformation.VerticalScrollBarWidth;
            }
            if (last < 80) last = 80;
            lv.Columns[lv.Columns.Count - 1].Width = last;
        }

        private static Panel CreatePathRow(string label, out TextBox textBox, out Button browseButton, EventHandler browseClick)
        {
            var row = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 2, 0, 2) };
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Left,
                Width = 108,
                TextAlign = ContentAlignment.MiddleLeft
            };
            browseButton = new Button { Text = "浏览...", Dock = DockStyle.Right, Width = 80, Height = 28 };
            textBox = new TextBox { Dock = DockStyle.Fill };
            if (browseClick != null) browseButton.Click += browseClick;
            // 后添加的先 Dock：Left / Right 占边，Fill 居中
            row.Controls.Add(textBox);
            row.Controls.Add(browseButton);
            row.Controls.Add(lbl);
            return row;
        }

        private static Panel CreateLabeledFieldRow(string label, Control field, int labelWidth = 108, int rowHeight = 36)
        {
            var row = new Panel { Dock = DockStyle.Top, Height = rowHeight, Padding = new Padding(0, 2, 0, 2) };
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Left,
                Width = labelWidth,
                TextAlign = ContentAlignment.MiddleLeft
            };
            field.Dock = DockStyle.Fill;
            row.Controls.Add(field);
            row.Controls.Add(lbl);
            return row;
        }

        private TabPage BuildUpgradeTab()
        {
            var page = new TabPage("升级任务");
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };

            // 自下而上 Dock.Top
            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "本页配置升级包与超时。开始/结束升级请使用窗口底部按钮。",
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var rowTimeouts = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 2, 0, 2) };
            var lblRemote = new Label { Text = "车机 OTA 目录:", Dock = DockStyle.Left, Width = 108, TextAlign = ContentAlignment.MiddleLeft };
            _txtRemoteDir = new TextBox { Dock = DockStyle.Left, Width = 200 };
            var lblBoot = new Label { Text = "启动超时(s):", Dock = DockStyle.Left, Width = 90, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };
            _numBootTimeout = new NumericUpDown
            {
                Dock = DockStyle.Left, Width = 90,
                Minimum = 60, Maximum = 3600, Value = 600
            };
            var lblUpd = new Label { Text = "升级超时(s):", Dock = DockStyle.Left, Width = 90, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };
            _numUpdateTimeout = new NumericUpDown
            {
                Dock = DockStyle.Left, Width = 90,
                Minimum = 300, Maximum = 7200, Value = 3600
            };
            rowTimeouts.Controls.Add(_numUpdateTimeout);
            rowTimeouts.Controls.Add(lblUpd);
            rowTimeouts.Controls.Add(_numBootTimeout);
            rowTimeouts.Controls.Add(lblBoot);
            rowTimeouts.Controls.Add(_txtRemoteDir);
            rowTimeouts.Controls.Add(lblRemote);

            var rowLog = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 2, 0, 2) };
            var lblLog = new Label
            {
                Text = "日志目录:",
                Dock = DockStyle.Left,
                Width = 108,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var lblLogHint = new Label
            {
                Text = "（空=程序目录/logs）",
                Dock = DockStyle.Right,
                Width = 140,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray
            };
            _txtLogDir = new TextBox { Dock = DockStyle.Fill };
            rowLog.Controls.Add(_txtLogDir);
            rowLog.Controls.Add(lblLogHint);
            rowLog.Controls.Add(lblLog);

            Panel rowPkg = CreatePathRow("OTA 升级包:", out _txtPackage, out _btnBrowse, OnBrowsePackage);

            // Dock.Top 后添加的在上方 → 倒序添加
            body.Controls.Add(hint);
            body.Controls.Add(rowTimeouts);
            body.Controls.Add(rowLog);
            body.Controls.Add(rowPkg);
            page.Controls.Add(body);
            return page;
        }

        private TabPage BuildDeviceTab()
        {
            var page = new TabPage("设备连接");
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };

            var grpTools = new GroupBox
            {
                Text = "常用操作",
                Dock = DockStyle.Top,
                Height = 72,
                Padding = new Padding(8)
            };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            _btnSerialReboot = new Button { Text = "串口一键重启", Width = 120, Height = 30, Margin = new Padding(4) };
            _btnSerialDev = new Button { Text = "串口切 dev", Width = 110, Height = 30, Margin = new Padding(4) };
            _btnAdbRoot = new Button { Text = "adb root", Width = 100, Height = 30, Margin = new Padding(4) };
            _btnAdbRemount = new Button { Text = "adb remount", Width = 110, Height = 30, Margin = new Padding(4) };
            _btnSerialReboot.Click += OnSerialReboot;
            _btnSerialDev.Click += OnSerialDev;
            _btnAdbRoot.Click += OnAdbRoot;
            _btnAdbRemount.Click += OnAdbRemount;
            flow.Controls.Add(_btnSerialReboot);
            flow.Controls.Add(_btnSerialDev);
            flow.Controls.Add(_btnAdbRoot);
            flow.Controls.Add(_btnAdbRemount);
            grpTools.Controls.Add(flow);

            _txtUsbCmd = new TextBox();
            Panel rowUsb = CreateLabeledFieldRow("USB 切 dev:", _txtUsbCmd);

            var rowCom = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 2, 0, 2) };
            var lblCom = new Label { Text = "串口 COM:", Dock = DockStyle.Left, Width = 108, TextAlign = ContentAlignment.MiddleLeft };
            _txtCom = new TextBox { Dock = DockStyle.Left, Width = 100 };
            var lblBaud = new Label { Text = "波特率:", Dock = DockStyle.Left, Width = 60, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0) };
            _numBaud = new NumericUpDown
            {
                Dock = DockStyle.Left, Width = 110,
                Minimum = 9600, Maximum = 921600, Value = 115200
            };
            rowCom.Controls.Add(_numBaud);
            rowCom.Controls.Add(lblBaud);
            rowCom.Controls.Add(_txtCom);
            rowCom.Controls.Add(lblCom);

            body.Controls.Add(grpTools);
            body.Controls.Add(rowUsb);
            body.Controls.Add(rowCom);
            page.Controls.Add(body);
            return page;
        }

        private TabPage BuildEmailTab()
        {
            var page = new TabPage("邮件监听");
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };

            var grpListen = new GroupBox
            {
                Text = "监听控制",
                Dock = DockStyle.Top,
                Height = 72,
                Padding = new Padding(8)
            };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            _btnEmailListenStart = new Button { Text = "启动邮箱监听", Width = 120, Height = 30, Margin = new Padding(4) };
            _btnEmailListenStop = new Button { Text = "停止邮箱监听", Width = 120, Height = 30, Margin = new Padding(4), Enabled = false };
            _btnEmailSimulate = new Button { Text = "模拟邮件测试", Width = 120, Height = 30, Margin = new Padding(4) };
            _chkEmailSimulateFullUpgrade = new CheckBox
            {
                Text = "模拟走完整升级",
                AutoSize = true,
                Checked = false,
                Margin = new Padding(12, 8, 4, 4)
            };
            _btnEmailListenStart.Click += OnEmailListenStart;
            _btnEmailListenStop.Click += OnEmailListenStop;
            _btnEmailSimulate.Click += OnEmailSimulate;
            flow.Controls.Add(_btnEmailListenStart);
            flow.Controls.Add(_btnEmailListenStop);
            flow.Controls.Add(_btnEmailSimulate);
            flow.Controls.Add(_chkEmailSimulateFullUpgrade);
            grpListen.Controls.Add(flow);

            Panel rowSave = CreatePathRow("升级包保存:", out _txtEmailPackageSaveDir, out _btnBrowseEmailSaveDir, OnBrowseEmailPackageSaveDir);

            var rowFilter = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 2, 0, 2) };
            var flowFilter = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            flowFilter.Controls.Add(new Label { Text = "收件箱:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _txtEmailMailbox = new TextBox { Width = 100, Margin = new Padding(0, 2, 12, 0) };
            flowFilter.Controls.Add(_txtEmailMailbox);
            flowFilter.Controls.Add(new Label { Text = "发件人:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _txtEmailFrom = new TextBox { Width = 160, Margin = new Padding(0, 2, 12, 0) };
            flowFilter.Controls.Add(_txtEmailFrom);
            flowFilter.Controls.Add(new Label { Text = "提示词:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _txtEmailSubjectKeyword = new TextBox { Width = 140, Margin = new Padding(0, 2, 8, 0) };
            flowFilter.Controls.Add(_txtEmailSubjectKeyword);
            flowFilter.Controls.Add(new Label { Text = "（主题/正文）", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 6, 0, 0) });
            rowFilter.Controls.Add(flowFilter);

            var rowAcct = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 2, 0, 2) };
            var flowAcct = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            flowAcct.Controls.Add(new Label { Text = "邮件主机:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _txtEmailImapHost = new TextBox { Width = 200, Margin = new Padding(0, 2, 12, 0) };
            flowAcct.Controls.Add(_txtEmailImapHost);
            flowAcct.Controls.Add(new Label { Text = "账号:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _txtEmailUsername = new TextBox { Width = 200, Margin = new Padding(0, 2, 12, 0) };
            flowAcct.Controls.Add(_txtEmailUsername);
            flowAcct.Controls.Add(new Label { Text = "密码:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _txtEmailPassword = new TextBox { Width = 160, Margin = new Padding(0, 2, 0, 0), UseSystemPasswordChar = true };
            flowAcct.Controls.Add(_txtEmailPassword);
            rowAcct.Controls.Add(flowAcct);

            body.Controls.Add(grpListen);
            body.Controls.Add(rowSave);
            body.Controls.Add(rowFilter);
            body.Controls.Add(rowAcct);
            page.Controls.Add(body);
            return page;
        }

        private TabPage BuildVerifyTab()
        {
            var page = new TabPage("升级后检查");
            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };

            // 底部操作条，避免挤在表格右侧超出可视区
            var panelBtns = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            var flowBtns = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };
            _btnHalModulesAdd = new Button { Text = "添加", Width = 88, Height = 28, Margin = new Padding(0, 0, 8, 0) };
            _btnHalModulesDelete = new Button { Text = "删除", Width = 88, Height = 28, Margin = new Padding(0, 0, 8, 0) };
            _btnHalModulesReset = new Button { Text = "恢复默认", Width = 88, Height = 28, Margin = new Padding(0, 0, 8, 0) };
            _btnHalScriptBrowse = new Button { Text = "浏览脚本…", Width = 100, Height = 28, Margin = new Padding(0, 0, 8, 0) };
            _btnHalModulesAdd.Click += OnAddHalModule;
            _btnHalModulesDelete.Click += OnDeleteHalModules;
            _btnHalModulesReset.Click += OnResetHalModules;
            _btnHalScriptBrowse.Click += OnBrowseHalScriptForSelected;
            flowBtns.Controls.Add(_btnHalModulesAdd);
            flowBtns.Controls.Add(_btnHalModulesDelete);
            flowBtns.Controls.Add(_btnHalModulesReset);
            flowBtns.Controls.Add(_btnHalScriptBrowse);
            panelBtns.Controls.Add(flowBtns);

            var panelTop = new Panel { Dock = DockStyle.Top, Height = 118 };
            var flowOpts = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            _chkVersionVerify = new CheckBox
            {
                Text = "版本校验（含 A/B 槽位）",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 6, 16, 0)
            };
            _chkHalStatus = new CheckBox
            {
                Text = "升级后：L0 状态 + 已配置脚本池（L1/L2）",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 6, 16, 0)
            };
            _btnHalStatus = new Button
            {
                Text = "执行检查（L0+脚本池）",
                Width = 180,
                Height = 28,
                Margin = new Padding(0, 2, 0, 0)
            };
            _btnHalStatus.Click += OnHalStatusCheck;
            flowOpts.Controls.Add(_chkVersionVerify);
            flowOpts.Controls.Add(_chkHalStatus);
            flowOpts.Controls.Add(_btnHalStatus);
            var lblHint = new Label
            {
                Dock = DockStyle.Fill,
                Text =
                    "「自检脚本」= 本机脚本池入口 run.sh。填写后点「执行检查」会：① L0 进程/系统态；② 同步路径到 t2_script_pool.json；" + Environment.NewLine +
                    "③ 将该模块目录 push 到车机 /data/local/tmp/t2_selfcheck/<模块>/；④ 自动执行 run.sh 并拉取 module_result.json / detect 报告。" + Environment.NewLine +
                    "留空 = 只做 L0，不 push。相对 exe：../hal_selfcheck/<模块>/run.sh；示例：../hal_selfcheck/vehicleconfig/run.sh",
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.TopLeft,
                AutoEllipsis = false
            };
            panelTop.Controls.Add(lblHint);
            panelTop.Controls.Add(flowOpts);

            _dgvHalModules = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F),
                EditMode = DataGridViewEditMode.EditOnEnter,
                ScrollBars = ScrollBars.Vertical,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
            };
            _dgvHalModules.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colPackage",
                HeaderText = "HAL 包名 / 服务",
                FillWeight = 58,
                MinimumWidth = 120
            });
            _dgvHalModules.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colScript",
                HeaderText = "自检脚本路径 (.sh)",
                FillWeight = 42,
                MinimumWidth = 100
            });

            // 添加顺序：Fill → Bottom → Top
            root.Controls.Add(_dgvHalModules);
            root.Controls.Add(panelBtns);
            root.Controls.Add(panelTop);
            page.Controls.Add(root);
            return page;
        }

        private void OnMainFormLoad(object sender, EventArgs e)
        {
            if (_splitMain != null && _splitMain.Height > 0)
            {
                int distance = Math.Max(
                    _splitMain.Panel1MinSize,
                    (int)(_splitMain.Height * 0.50));
                int maxDistance = _splitMain.Height - _splitMain.Panel2MinSize - _splitMain.SplitterWidth;
                if (maxDistance >= _splitMain.Panel1MinSize)
                {
                    _splitMain.SplitterDistance = Math.Min(distance, maxDistance);
                }
            }

            FitListViewColumns(_lvResults);

            // 脚本驱动：-gui -package <path> 启动时，自动填入包路径并触发升级
            if (_autoStartPackage != null)
            {
                _txtPackage.Text = _autoStartPackage;
                AppendInfo("自动启动升级（脚本触发）: " + _autoStartPackage);
                BeginInvoke(new Action(() => OnStartUpgrade(null, EventArgs.Empty)));
            }
        }

        private void LoadConfigToUi()
        {
            _txtCom.Text = _cfg.SerialPort;
            _numBaud.Value = Math.Max(_numBaud.Minimum, Math.Min(_numBaud.Maximum, _cfg.BaudRate));
            _txtUsbCmd.Text = _cfg.UsbModeCmd;
            _txtRemoteDir.Text = _cfg.RemoteOtaDir;
            _numBootTimeout.Value = Math.Max(_numBootTimeout.Minimum, Math.Min(_numBootTimeout.Maximum, _cfg.BootTimeoutSec));
            _numUpdateTimeout.Value = Math.Max(_numUpdateTimeout.Minimum, Math.Min(_numUpdateTimeout.Maximum, _cfg.UpdateTimeoutSec));
            _txtLogDir.Text = _cfg.LogDir ?? "";
            _txtEmailImapHost.Text = string.IsNullOrWhiteSpace(_cfg.EmailImapHost)
                ? "webmail.hangsheng.com.cn" : _cfg.EmailImapHost;
            _txtEmailUsername.Text = _cfg.EmailUsername ?? "";
            _txtEmailPassword.Text = _cfg.EmailPassword ?? "";
            _txtEmailMailbox.Text = string.IsNullOrWhiteSpace(_cfg.EmailMailbox) ? "INBOX" : _cfg.EmailMailbox;
            _txtEmailFrom.Text = _cfg.EmailFromKeyword ?? "";
            _txtEmailSubjectKeyword.Text = string.IsNullOrWhiteSpace(_cfg.EmailSubjectKeyword) ? "T2_OTA" : _cfg.EmailSubjectKeyword;
            _txtEmailPackageSaveDir.Text = _cfg.EmailPackageSaveDir ?? "";
            _chkVersionVerify.Checked = !_cfg.SkipVersionVerify;
            _chkHalStatus.Checked = !_cfg.SkipHalStatusCheck;
            LoadHalModulesToGrid(GetHalModuleFilterText(_cfg));
        }

        private AppConfig ReadConfigFromUi()
        {
            return new AppConfig
            {
                SerialPort = _txtCom.Text.Trim(),
                BaudRate = (int)_numBaud.Value,
                UsbModeCmd = _txtUsbCmd.Text.Trim(),
                RemoteOtaDir = _txtRemoteDir.Text.Trim(),
                BootTimeoutSec = (int)_numBootTimeout.Value,
                UpdateTimeoutSec = (int)_numUpdateTimeout.Value,
                LogDir = _txtLogDir.Text.Trim(),
                EmailImapHost = string.IsNullOrWhiteSpace(_txtEmailImapHost.Text)
                    ? "webmail.hangsheng.com.cn" : _txtEmailImapHost.Text.Trim(),
                EmailUsername = _txtEmailUsername.Text.Trim(),
                EmailPassword = _txtEmailPassword.Text,
                EmailMailbox = string.IsNullOrWhiteSpace(_txtEmailMailbox.Text) ? "INBOX" : _txtEmailMailbox.Text.Trim(),
                EmailFromKeyword = _txtEmailFrom.Text.Trim(),
                EmailSubjectKeyword = string.IsNullOrWhiteSpace(_txtEmailSubjectKeyword.Text) ? "T2_OTA" : _txtEmailSubjectKeyword.Text.Trim(),
                EmailPackageSaveDir = _txtEmailPackageSaveDir.Text.Trim(),
                SkipVersionVerify = !_chkVersionVerify.Checked,
                SkipHalStatusCheck = !_chkHalStatus.Checked,
                HalModuleFilter = GetHalModuleFilterLinesFromGrid()
            };
        }

        private string GetHalModuleFilterText(AppConfig cfg)
        {
            if (cfg != null && cfg.HalModuleFilter != null && cfg.HalModuleFilter.Count > 0)
            {
                return HalModulesManifestHelper.JoinFilterLines(cfg.HalModuleFilter);
            }
            return HalModulesManifestHelper.JoinFilterLines(GetDefaultHalModuleLinesWithScripts());
        }

        private List<string> GetDefaultHalModulePackages()
        {
            try
            {
                return HalModulesManifestHelper.GetDefaultPackageNames(_baseDir, _cfg);
            }
            catch
            {
                return new List<string>();
            }
        }

        private List<string> GetDefaultHalModuleLinesWithScripts()
        {
            var lines = new List<string>();
            foreach (string pkg in GetDefaultHalModulePackages())
            {
                string script = HalModulesManifestHelper.GuessDefaultSelfCheckScript(pkg);
                lines.Add(string.IsNullOrEmpty(script) ? pkg : (pkg + " | " + script));
            }
            return lines;
        }

        private void OnResetHalModules(object sender, EventArgs e)
        {
            LoadHalModulesToGrid(HalModulesManifestHelper.JoinFilterLines(GetDefaultHalModuleLinesWithScripts()));
        }

        private void LoadHalModulesToGrid(string text)
        {
            if (_dgvHalModules == null) return;
            try { _dgvHalModules.EndEdit(); } catch { }
            _dgvHalModules.Rows.Clear();
            foreach (var b in HalModulesManifestHelper.ParseScriptBindings(text ?? ""))
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Package)) continue;
                _dgvHalModules.Rows.Add(b.Package.Trim(), b.ScriptPath ?? "");
            }
        }

        private List<string> GetHalModuleFilterLinesFromGrid()
        {
            var lines = new List<string>();
            if (_dgvHalModules == null) return lines;
            try { _dgvHalModules.EndEdit(); } catch { }
            foreach (DataGridViewRow row in _dgvHalModules.Rows)
            {
                if (row.IsNewRow) continue;
                string pkg = (Convert.ToString(row.Cells[0].Value) ?? "").Trim();
                string script = (Convert.ToString(row.Cells[1].Value) ?? "").Trim();
                if (string.IsNullOrWhiteSpace(pkg)) continue;
                lines.Add(string.IsNullOrEmpty(script) ? pkg : (pkg + " | " + script));
            }
            return lines;
        }

        private void OnAddHalModule(object sender, EventArgs e)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "添加 HAL 自检";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(520, 160);
                dlg.ShowInTaskbar = false;

                var lblPkg = new Label { Text = "HAL 包名:", Left = 12, Top = 16, AutoSize = true };
                var txtPkg = new TextBox { Left = 100, Top = 12, Width = 400 };
                var lblScript = new Label { Text = "脚本路径:", Left = 12, Top = 48, AutoSize = true };
                var txtScript = new TextBox { Left = 100, Top = 44, Width = 320 };
                var lblScriptHint = new Label
                {
                    Text = "模板(相对exe)：../hal_selfcheck/<模块名>/run.sh",
                    Left = 100,
                    Top = 72,
                    AutoSize = true,
                    ForeColor = Color.DimGray
                };
                var btnBrowse = new Button { Text = "浏览…", Left = 430, Top = 42, Width = 70, Height = 26 };
                btnBrowse.Click += (s, ev) =>
                {
                    using (var ofd = new OpenFileDialog())
                    {
                        ofd.Filter = "Shell 脚本 (*.sh)|*.sh|所有文件|*.*";
                        ofd.Title = "选择脚本池 run.sh";
                        string scriptsRoot = Path.GetDirectoryName(_baseDir);
                        if (!string.IsNullOrEmpty(scriptsRoot) && Directory.Exists(Path.Combine(scriptsRoot, "hal_selfcheck")))
                            ofd.InitialDirectory = Path.Combine(scriptsRoot, "hal_selfcheck");
                        if (ofd.ShowDialog(dlg) == DialogResult.OK)
                        {
                            txtScript.Text = ToScriptsRelativePath(ofd.FileName);
                            if (string.IsNullOrWhiteSpace(txtPkg.Text))
                            {
                                // 尝试从路径推断模块名
                                string norm = ofd.FileName.Replace("\\", "/");
                                int idxHal = norm.ToLowerInvariant().IndexOf("hal_selfcheck/");
                                if (idxHal >= 0)
                                {
                                    string rest = norm.Substring(idxHal + "hal_selfcheck/".Length);
                                    string mid = rest.Split('/')[0];
                                    if (!string.IsNullOrEmpty(mid))
                                        txtPkg.Text = mid;
                                }
                            }
                        }
                    }
                };
                var btnOk = new Button { Text = "确定", Left = 320, Top = 110, Width = 80, DialogResult = DialogResult.OK };
                var btnCancel = new Button { Text = "取消", Left = 410, Top = 110, Width = 80, DialogResult = DialogResult.Cancel };
                dlg.Controls.Add(lblPkg);
                dlg.Controls.Add(txtPkg);
                dlg.Controls.Add(lblScript);
                dlg.Controls.Add(txtScript);
                dlg.Controls.Add(lblScriptHint);
                dlg.Controls.Add(btnBrowse);
                dlg.Controls.Add(btnOk);
                dlg.Controls.Add(btnCancel);
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string pkg = txtPkg.Text.Trim();
                string script = txtScript.Text.Trim();
                if (string.IsNullOrWhiteSpace(pkg))
                {
                    MessageBox.Show(this, "请填写 HAL 包名或模块 ID。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                // 去重：同包名则更新脚本
                foreach (DataGridViewRow row in _dgvHalModules.Rows)
                {
                    if (row.IsNewRow) continue;
                    string existing = (Convert.ToString(row.Cells[0].Value) ?? "").Trim();
                    if (string.Equals(existing, pkg, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Cells[1].Value = script;
                        return;
                    }
                }
                _dgvHalModules.Rows.Add(pkg, script);
            }
        }

        private void OnDeleteHalModules(object sender, EventArgs e)
        {
            if (_dgvHalModules == null) return;
            try { _dgvHalModules.EndEdit(); } catch { }
            var toRemove = new List<DataGridViewRow>();
            foreach (DataGridViewRow row in _dgvHalModules.SelectedRows)
            {
                if (!row.IsNewRow) toRemove.Add(row);
            }
            if (toRemove.Count == 0 && _dgvHalModules.CurrentRow != null && !_dgvHalModules.CurrentRow.IsNewRow)
                toRemove.Add(_dgvHalModules.CurrentRow);
            if (toRemove.Count == 0)
            {
                MessageBox.Show(this, "请先选中要删除的 HAL 行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (var row in toRemove)
                _dgvHalModules.Rows.Remove(row);
        }

        private void OnBrowseHalScriptForSelected(object sender, EventArgs e)
        {
            if (_dgvHalModules == null) return;
            try { _dgvHalModules.EndEdit(); } catch { }
            DataGridViewRow target = null;
            if (_dgvHalModules.SelectedRows.Count > 0)
                target = _dgvHalModules.SelectedRows[0];
            else if (_dgvHalModules.CurrentCell != null)
                target = _dgvHalModules.Rows[_dgvHalModules.CurrentCell.RowIndex];
            if (target == null || target.IsNewRow)
            {
                MessageBox.Show(this, "请先选中一行；也可直接单击「自检脚本」列输入路径。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "Shell 脚本 (*.sh)|*.sh|所有文件|*.*";
                ofd.Title = "为选中 HAL 选择自检脚本";
                string scriptsRoot = Path.GetDirectoryName(_baseDir);
                if (!string.IsNullOrEmpty(scriptsRoot) && Directory.Exists(Path.Combine(scriptsRoot, "hal_selfcheck")))
                    ofd.InitialDirectory = Path.Combine(scriptsRoot, "hal_selfcheck");
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                target.Cells[1].Value = ToScriptsRelativePath(ofd.FileName);
            }
        }

        private string ToScriptsRelativePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return "";
            try
            {
                // 优先写成相对 exe 所在目录，便于对照界面说明中的模板
                string full = Path.GetFullPath(fullPath);
                string exeRoot = Path.GetFullPath(_baseDir);
                if (full.StartsWith(exeRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = full.Substring(exeRoot.Length).TrimStart('\\', '/');
                    return rel.Replace("\\", "/");
                }
                string scriptsRoot = Path.GetDirectoryName(_baseDir);
                if (!string.IsNullOrEmpty(scriptsRoot))
                {
                    string root = Path.GetFullPath(scriptsRoot);
                    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        string underScripts = full.Substring(root.Length).TrimStart('\\', '/').Replace("\\", "/");
                        return ("../" + underScripts).Replace("\\", "/");
                    }
                }
            }
            catch { }
            return fullPath.Replace("\\", "/");
        }


        private void RefreshVerificationResults(string sessionDir)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(RefreshVerificationResults), sessionDir);
                return;
            }
            VerificationResultsUiHelper.BindListView(
                _lvResults, VerificationResultsUiHelper.LoadFromSession(sessionDir));
            FitListViewColumns(_lvResults);
            if (_tabsOutput != null && _tabsOutput.TabPages.Count > 0)
                _tabsOutput.SelectedIndex = 0;
        }

        private void OnHalStatusCheck(object sender, EventArgs e)
        {
            if (_running || _toolBusy) return;

            _cfg = ReadConfigFromUi();
            AppConfig.Save(_baseDir, _cfg);
            SyncT2AtfAndScriptPool();

            SetRunning(true);
            _lblStatus.Text = "升级后检查中（L0 + 脚本池）...";
            var sink = new UiLogSink(this, _logBox);

            _worker = new Thread(() =>
            {
                string sessionDir = null;
                SessionFileLogSink sessionLog = null;
                try
                {
                    string logRoot = string.IsNullOrWhiteSpace(_cfg.LogDir)
                        ? Path.Combine(_baseDir, "logs")
                        : _cfg.LogDir;
                    sessionDir = Path.Combine(logRoot, "hal_status_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(sessionDir);
                    sessionLog = new SessionFileLogSink(Path.Combine(sessionDir, "session.log"), sink);
                    Log.SetSink(sessionLog);

                    var adb = new AdbHelper(_baseDir);
                    // 切 dev / kill-server 后可能有短暂枚举抖动，勿用单次 DeviceReady 直接判死
                    if (!adb.WaitDeviceReady(20))
                    {
                        string devices = adb.Run(new[] { "devices", "-l" }, true);
                        Log.Step("当前 adb devices:\n" + devices);
                        throw new InvalidOperationException(
                            "adb 未连接或设备未就绪，请先「串口切 dev」。\n" +
                            "若刚刚已切成功仍报错，多为 adb 枚举抖动或 USB 掉线，请重试一次检查。\n" +
                            "devices 输出: " + (string.IsNullOrWhiteSpace(devices) ? "(空)" : devices.Trim()));
                    }
                    HalStatusChecker.VerifyOrThrow(adb, _cfg, _baseDir, sessionDir);
                    HalScriptPoolResult poolResult = HalScriptPoolRunner.Run(
                        adb, _baseDir, sessionDir, throwOnFail: false);
                    string reportHtml = null;
                    try
                    {
                        string pkgPath = "";
                        try { pkgPath = (_txtPackage != null ? _txtPackage.Text : "") ?? ""; }
                        catch { pkgPath = ""; }
                        reportHtml = SessionDetectReportHelper.Generate(adb, _baseDir, sessionDir, pkgPath);
                    }
                    catch (Exception rex)
                    {
                        Log.Step("生成 detect_result.html 失败: " + rex.Message);
                    }
                    if (poolResult.Ran && !poolResult.Passed)
                    {
                        throw new HalScriptPoolException(
                            "脚本池执行未通过: " + poolResult.Summary +
                            (string.IsNullOrEmpty(reportHtml) ? "" : ("\n报告: " + reportHtml)),
                            poolResult);
                    }
                    string okMsg = poolResult.Ran
                        ? ("L0 通过；脚本池 " + poolResult.Summary)
                        : ("L0 通过；" + poolResult.Summary);
                    if (!string.IsNullOrEmpty(reportHtml))
                    {
                        okMsg = okMsg + "\n报告: " + reportHtml;
                    }
                    BeginInvoke(new Action(() =>
                    {
                        RefreshVerificationResults(sessionDir);
                        _lblStatus.Text = poolResult.Ran ? "L0+脚本池 Pass" : "L0 Pass（未跑脚本池）";
                        MessageBox.Show(this, okMsg, "检查完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch (HalStatusException ex)
                {
                    sink.Error(ex.Summary);
                    try
                    {
                        if (!string.IsNullOrEmpty(sessionDir))
                        {
                            var adb2 = new AdbHelper(_baseDir);
                            string pkgPath = "";
                            try { pkgPath = (_txtPackage != null ? _txtPackage.Text : "") ?? ""; } catch { }
                            SessionDetectReportHelper.Generate(adb2, _baseDir, sessionDir, pkgPath);
                        }
                    }
                    catch { }
                    BeginInvoke(new Action(() =>
                    {
                        if (!string.IsNullOrEmpty(sessionDir))
                        {
                            RefreshVerificationResults(sessionDir);
                        }
                        _lblStatus.Text = "L0 检查失败";
                        MessageBox.Show(this, ex.Summary, "HAL 基本状态检查失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                catch (HalScriptPoolException ex)
                {
                    sink.Error(ex.Message);
                    try
                    {
                        if (!string.IsNullOrEmpty(sessionDir))
                        {
                            var adb2 = new AdbHelper(_baseDir);
                            string pkgPath = "";
                            try { pkgPath = (_txtPackage != null ? _txtPackage.Text : "") ?? ""; } catch { }
                            string html = SessionDetectReportHelper.Generate(adb2, _baseDir, sessionDir, pkgPath);
                            if (!string.IsNullOrEmpty(html))
                            {
                                sink.Step("报告: " + html);
                            }
                        }
                    }
                    catch { }
                    BeginInvoke(new Action(() =>
                    {
                        if (!string.IsNullOrEmpty(sessionDir))
                        {
                            RefreshVerificationResults(sessionDir);
                        }
                        _lblStatus.Text = "脚本池未通过";
                        MessageBox.Show(this, ex.Message, "脚本池执行失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                catch (Exception ex)
                {
                    sink.Error(ex.Message);
                    BeginInvoke(new Action(() =>
                    {
                        if (!string.IsNullOrEmpty(sessionDir))
                        {
                            RefreshVerificationResults(sessionDir);
                        }
                        _lblStatus.Text = "检查失败";
                        MessageBox.Show(this, ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    if (sessionLog != null)
                    {
                        sessionLog.Dispose();
                    }
                    Log.SetSink(null);
                    BeginInvoke(new Action(() => SetRunning(false)));
                }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void OnSerialReboot(object sender, EventArgs e)
        {
            RunMaintenanceTool("串口重启", "串口重启命令已发送", (cfg, adb) =>
            {
                DeviceMaintenanceHelper.RebootViaSerial(cfg);
            }, requireAdb: false);
        }

        private void OnSerialDev(object sender, EventArgs e)
        {
            RunMaintenanceTool("串口切 dev", "串口切 dev 完成", (cfg, adb) =>
            {
                DeviceMaintenanceHelper.SwitchToDevMode(cfg, adb);
            }, requireAdb: false);
        }

        private void OnAdbRoot(object sender, EventArgs e)
        {
            RunMaintenanceTool("adb root", "adb root 完成", (cfg, adb) =>
            {
                DeviceMaintenanceHelper.AdbRoot(adb);
            });
        }

        private void OnAdbRemount(object sender, EventArgs e)
        {
            RunMaintenanceTool("adb remount", "adb remount 完成", (cfg, adb) =>
            {
                DeviceMaintenanceHelper.AdbRemount(adb);
            });
        }

        private void RunMaintenanceTool(
            string actionName,
            string successStatus,
            Action<AppConfig, AdbHelper> work,
            bool requireAdb = true)
        {
            if (_running || _toolBusy) return;

            _cfg = ReadConfigFromUi();
            AppConfig.Save(_baseDir, _cfg);
            SyncT2AtfAndScriptPool();

            SetToolBusy(true);
            _lblStatus.Text = actionName + " 执行中...";
            var sink = new UiLogSink(this, _logBox);

            _worker = new Thread(() =>
            {
                try
                {
                    Log.SetSink(sink);
                    Log.Step("=== " + actionName + " ===");
                    var adb = new AdbHelper(_baseDir);
                    if (requireAdb && !adb.WaitDeviceReady(15))
                    {
                        string devices = adb.Run(new[] { "devices", "-l" }, true);
                        Log.Step("当前 adb devices:\n" + devices);
                        throw new InvalidOperationException(
                            "adb 未连接，请先「串口切 dev」或检查 USB。\ndevices: " +
                            (string.IsNullOrWhiteSpace(devices) ? "(空)" : devices.Trim()));
                    }
                    work(_cfg, adb);
                    BeginInvoke(new Action(() =>
                    {
                        _lblStatus.Text = successStatus;
                        MessageBox.Show(this, successStatus, actionName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch (Exception ex)
                {
                    sink.Error(ex.Message);
                    BeginInvoke(new Action(() =>
                    {
                        _lblStatus.Text = actionName + " 失败";
                        MessageBox.Show(this, ex.Message, actionName + " 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    Log.SetSink(null);
                    BeginInvoke(new Action(() => SetToolBusy(false)));
                }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void OnBrowsePackage(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "OTA 包 (*.zip)|*.zip|所有文件|*.*";
                dlg.Title = "选择 OTA 升级包";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _txtPackage.Text = dlg.FileName;
                }
            }
        }

        private void OnBrowseEmailPackageSaveDir(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择邮箱触发时升级包的本地保存目录";
                if (!string.IsNullOrWhiteSpace(_txtEmailPackageSaveDir.Text)
                    && Directory.Exists(_txtEmailPackageSaveDir.Text.Trim()))
                {
                    dlg.SelectedPath = _txtEmailPackageSaveDir.Text.Trim();
                }
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _txtEmailPackageSaveDir.Text = dlg.SelectedPath;
                }
            }
        }

        private EmailListenRunner EnsureEmailRunner()
        {
            if (_emailRunner == null)
            {
                _emailRunner = new EmailListenRunner(_baseDir, AppendInfoSafe);
            }
            return _emailRunner;
        }

        private void AppendInfoSafe(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(AppendInfoSafe), text); } catch { }
                return;
            }
            AppendInfo(text);
            UpdateEmailListenButtons();
        }

        private void SyncT2AtfAndScriptPool()
        {
            string syncMsg;
            EmailListenConfigHelper.SyncToT2Config(_baseDir, _cfg, out syncMsg);
            if (!string.IsNullOrEmpty(syncMsg)) AppendInfo(syncMsg);
        }

        private void PersistEmailConfigFromUi()
        {
            _cfg = ReadConfigFromUi();
            AppConfig.Save(_baseDir, _cfg);
            SyncT2AtfAndScriptPool();
        }

        private void OnEmailListenStart(object sender, EventArgs e)
        {
            if (_running || _toolBusy) return;
            PersistEmailConfigFromUi();

            if (string.IsNullOrWhiteSpace(_cfg.EmailUsername))
            {
                MessageBox.Show(this, "请先填写邮箱账号。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(_cfg.EmailPassword))
            {
                MessageBox.Show(this,
                    "请先填写邮箱密码。\n\n说明：已使用与 OWA 同源的 EWS。\n账号请填短名（如 yuhaohs）。\n关键字 T2_OTA 可写在主题或正文；升级包支持 .7z UNC 路径。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                EnsureEmailRunner().StartListen();
                _lblStatus.Text = "邮箱监听运行中...";
                UpdateEmailListenButtons();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "启动邮箱监听失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnEmailListenStop(object sender, EventArgs e)
        {
            if (_emailRunner == null) return;
            _emailRunner.StopListen();
            _lblStatus.Text = "邮箱监听已停止";
            UpdateEmailListenButtons();
        }

        private void OnEmailSimulate(object sender, EventArgs e)
        {
            if (_running || _toolBusy) return;

            PersistEmailConfigFromUi();

            string source = _txtPackage.Text.Trim();
            if (string.IsNullOrWhiteSpace(source))
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Filter = "OTA 包 (*.zip;*.7z)|*.zip;*.7z|所有文件|*.*";
                    dlg.Title = "选择用于模拟邮件的升级包（或填 URL/UNC）";
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    source = dlg.FileName;
                    _txtPackage.Text = source;
                }
            }

            bool fullUpgrade = _chkEmailSimulateFullUpgrade != null && _chkEmailSimulateFullUpgrade.Checked;
            SetToolBusy(true);
            _lblStatus.Text = fullUpgrade ? "模拟邮件完整升级中..." : "模拟邮件拉取中...";
            string capturedSource = source;
            _worker = new Thread(() =>
            {
                try
                {
                    int code;
                    string local = EnsureEmailRunner().RunSimulate(capturedSource, fullUpgrade, out code);
                    BeginInvoke(new Action(() =>
                    {
                        if (fullUpgrade)
                        {
                            if (!string.IsNullOrWhiteSpace(local))
                            {
                                _txtPackage.Text = local;
                            }
                            if (code == 0)
                            {
                                AppendInfo("[邮箱] 模拟完整升级成功" + (string.IsNullOrWhiteSpace(local) ? "" : (": " + local)));
                                MessageBox.Show(this,
                                    "模拟邮件完整升级成功。\n\n退出码: 0" +
                                    (string.IsNullOrWhiteSpace(local) ? "" : ("\n本地包: " + local)),
                                    "模拟邮件测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            else
                            {
                                MessageBox.Show(this,
                                    "模拟完整升级结束（exit=" + code + "），请查看日志。",
                                    "模拟邮件测试", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            }
                            SetToolBusy(false);
                            _lblStatus.Text = "就绪";
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(local))
                        {
                            _txtPackage.Text = local;
                            AppendInfo("[邮箱] 模拟完成，已填入升级包: " + local);
                            var ask = MessageBox.Show(this,
                                "模拟邮件拉取成功。\n\n本地包: " + local + "\n\n是否立即开始升级？",
                                "模拟邮件测试", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                            if (ask == DialogResult.Yes)
                            {
                                SetToolBusy(false);
                                OnStartUpgrade(null, EventArgs.Empty);
                                return;
                            }
                        }
                        else
                        {
                            MessageBox.Show(this,
                                "模拟拉取失败（exit=" + code + "），请查看日志。",
                                "模拟邮件测试", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        SetToolBusy(false);
                        _lblStatus.Text = "就绪";
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show(this, ex.Message, "模拟邮件测试失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        SetToolBusy(false);
                        _lblStatus.Text = "就绪";
                    }));
                }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void UpdateEmailListenButtons()
        {
            bool listening = _emailRunner != null && _emailRunner.IsRunning;
            bool busy = _running || _toolBusy;
            if (_btnEmailListenStart != null)
            {
                _btnEmailListenStart.Enabled = !listening && !busy;
            }
            if (_btnEmailListenStop != null)
            {
                _btnEmailListenStop.Enabled = listening;
            }
            if (_btnEmailSimulate != null)
            {
                _btnEmailSimulate.Enabled = !busy;
            }
            if (_chkEmailSimulateFullUpgrade != null)
            {
                _chkEmailSimulateFullUpgrade.Enabled = !busy;
            }
        }

        private void OnSaveConfig(object sender, EventArgs e)
        {
            _cfg = ReadConfigFromUi();
            AppConfig.Save(_baseDir, _cfg);
            SyncT2AtfAndScriptPool();
            AppendInfo("配置已保存: " + AppConfig.ConfigPath(_baseDir));

            string syncMsg;
            if (EmailListenConfigHelper.SyncToT2Config(_baseDir, _cfg, out syncMsg))
            {
                AppendInfo(syncMsg);
            }
            else if (!string.IsNullOrEmpty(syncMsg))
            {
                AppendInfo(syncMsg);
            }

            _lblStatus.Text = "配置已保存";
        }

        private void OnOpenLogs(object sender, EventArgs e)
        {
            string dir = string.IsNullOrWhiteSpace(_cfg.LogDir)
                ? Path.Combine(_baseDir, "logs")
                : _cfg.LogDir;
            Directory.CreateDirectory(dir);
            ProcessHelper.OpenFolder(dir);
        }

        private void OnStartUpgrade(object sender, EventArgs e)
        {
            if (_running || _toolBusy) return;

            _cfg = ReadConfigFromUi();
            AppConfig.Save(_baseDir, _cfg);
            SyncT2AtfAndScriptPool();
            string syncMsg;
            EmailListenConfigHelper.SyncToT2Config(_baseDir, _cfg, out syncMsg);

            string package = _txtPackage.Text.Trim();
            if (string.IsNullOrWhiteSpace(package))
            {
                MessageBox.Show(this, "请先选择 OTA 升级包路径。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(package) && !Directory.Exists(package))
            {
                MessageBox.Show(this, "升级包路径不存在。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SetRunning(true);
            _logBox.Clear();
            _lvResults.Items.Clear();
            var sink = new UiLogSink(this, _logBox);

            _worker = new Thread(() =>
            {
                try
                {
                    _activeService = new UpgradeService(_baseDir, sink);
                    _activeService.Run(_cfg, package);
                    string sessionDir = _activeService.LastSessionDir;
                    ExitCode = 0;
                    BeginInvoke(new Action(() =>
                    {
                        RefreshVerificationResults(sessionDir);
                        _lblStatus.Text = "升级成功";
                        if (_reportMode)
                        {
                            MessageBox.Show(this, AppBranding.ProductName + " 升级+验证已完成（监听触发，将上报 ATF）。", "升级完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            Close();
                        }
                        else if (_autoStartPackage != null)
                        {
                            // 脚本触发：成功后静默关闭，让调用方拿到退出码 0
                            Close();
                        }
                        else
                        {
                            MessageBox.Show(this, AppBranding.ProductName + " 流程已完成。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }));
                }
                catch (OperationCanceledException ex)
                {
                    sink.Error(ex.Message);
                    ExitCode = 2;
                    BeginInvoke(new Action(() =>
                    {
                        _lblStatus.Text = "已终止";
                        if (_reportMode) { MessageBox.Show(this, "升级已终止（监听触发）。", "结束升级", MessageBoxButtons.OK, MessageBoxIcon.Warning); Close(); }
                        else if (_autoStartPackage != null) Close();
                        else MessageBox.Show(this, "升级已终止。", "结束升级", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
                catch (RecoveryBootException ex)
                {
                    sink.Error(ex.ReasonSummary);
                    ExitCode = 3;
                    BeginInvoke(new Action(() =>
                    {
                        _lblStatus.Text = "进入 Recovery";
                        if (_reportMode)
                        {
                            string detail = ex.ReasonSummary;
                            if (!string.IsNullOrEmpty(ex.FullReport))
                            {
                                int idx = ex.FullReport.IndexOf("【可能原因】", StringComparison.Ordinal);
                                if (idx >= 0)
                                {
                                    string tail = ex.FullReport.Substring(idx);
                                    if (tail.Length > 600) tail = tail.Substring(0, 600) + "...";
                                    detail += Environment.NewLine + Environment.NewLine + tail;
                                }
                            }
                            MessageBox.Show(this, detail, "OTA 写入成功 · 进入 Recovery", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            Close();
                            return;
                        }
                        if (_autoStartPackage != null)
                        {
                            Close();
                            return;
                        }
                        string detail2 = ex.ReasonSummary;
                        if (!string.IsNullOrEmpty(ex.FullReport))
                        {
                            int idx = ex.FullReport.IndexOf("【可能原因】", StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                string tail = ex.FullReport.Substring(idx);
                                if (tail.Length > 600) tail = tail.Substring(0, 600) + "...";
                                detail2 += Environment.NewLine + Environment.NewLine + tail;
                            }
                        }
                        MessageBox.Show(
                            this,
                            detail2,
                            ex.ReasonSummary.IndexOf("反复重启", StringComparison.Ordinal) >= 0
                                ? "OTA 写入成功 · Recovery 反复重启"
                                : "OTA 写入成功 · 进入 Recovery",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }));
                }
                catch (VersionVerifyException ex)
                {
                    sink.Error(ex.Summary);
                    ExitCode = 4;
                    BeginInvoke(new Action(() =>
                    {
                        if (_activeService != null)
                        {
                            RefreshVerificationResults(_activeService.LastSessionDir);
                        }
                        _lblStatus.Text = "版本校验失败";
                        string detail = ex.Summary;
                        if (!string.IsNullOrEmpty(ex.FullReport))
                        {
                            detail += Environment.NewLine + Environment.NewLine + ex.FullReport;
                            if (detail.Length > 1200)
                            {
                                detail = detail.Substring(0, 1200) + "...";
                            }
                        }
                        if (_reportMode) { MessageBox.Show(this, detail, "版本校验失败", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); return; }
                        if (_autoStartPackage != null) { Close(); return; }
                        MessageBox.Show(this, detail, "版本校验失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                catch (HalStatusException ex)
                {
                    sink.Error(ex.Summary);
                    ExitCode = 5;
                    BeginInvoke(new Action(() =>
                    {
                        if (_activeService != null)
                        {
                            RefreshVerificationResults(_activeService.LastSessionDir);
                        }
                        _lblStatus.Text = "HAL 状态检查失败";
                        string detail = ex.Summary;
                        if (!string.IsNullOrEmpty(ex.FullReport))
                        {
                            detail += Environment.NewLine + Environment.NewLine + ex.FullReport;
                            if (detail.Length > 1200)
                            {
                                detail = detail.Substring(0, 1200) + "...";
                            }
                        }
                        if (_reportMode) { MessageBox.Show(this, detail, "HAL 基本状态检查失败", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); return; }
                        if (_autoStartPackage != null) { Close(); return; }
                        MessageBox.Show(this, detail, "HAL 基本状态检查失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                catch (Exception ex)
                {
                    sink.Error(ex.Message);
                    ExitCode = 1;
                    BeginInvoke(new Action(() =>
                    {
                        _lblStatus.Text = "升级失败";
                        if (_reportMode) { MessageBox.Show(this, ex.Message, "升级失败", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); }
                        else if (_autoStartPackage != null) Close();
                        else MessageBox.Show(this, ex.Message, "升级失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    BeginInvoke(new Action(() =>
                    {
                        _activeService = null;
                        SetRunning(false);
                    }));
                }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void OnStopUpgrade(object sender, EventArgs e)
        {
            if (!_running) return;
            if (_activeService != null)
            {
                _activeService.RequestStop();
                _lblStatus.Text = "正在结束升级...";
                return;
            }
            if (UpgradeService.StopActiveUpgrade())
            {
                _lblStatus.Text = "正在结束升级...";
            }
            else
            {
                MessageBox.Show(this, "当前没有进行中的升级任务。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void SetRunning(bool running)
        {
            _running = running;
            ApplyBusyState();
        }

        private void SetToolBusy(bool busy)
        {
            _toolBusy = busy;
            ApplyBusyState();
        }

        private void ApplyBusyState()
        {
            bool busy = _running || _toolBusy;
            _btnStart.Enabled = !busy;
            _btnStop.Enabled = _running;
            _btnSave.Enabled = !busy;
            _btnBrowse.Enabled = !busy;
            _btnHalStatus.Enabled = !busy;
            _btnSerialReboot.Enabled = !busy;
            _btnSerialDev.Enabled = !busy;
            _btnAdbRoot.Enabled = !busy;
            _btnAdbRemount.Enabled = !busy;
            _chkVersionVerify.Enabled = !busy;
            _chkHalStatus.Enabled = !busy;
            if (_dgvHalModules != null) _dgvHalModules.Enabled = !busy;
            if (_btnHalModulesAdd != null) _btnHalModulesAdd.Enabled = !busy;
            if (_btnHalModulesDelete != null) _btnHalModulesDelete.Enabled = !busy;
            if (_btnHalModulesReset != null) _btnHalModulesReset.Enabled = !busy;
            if (_btnHalScriptBrowse != null) _btnHalScriptBrowse.Enabled = !busy;
            if (_btnBrowseEmailSaveDir != null) _btnBrowseEmailSaveDir.Enabled = !busy;
            UpdateEmailListenButtons();
            _progress.Visible = _running;
            if (!_running && !_toolBusy)
            {
                bool listening = _emailRunner != null && _emailRunner.IsRunning;
                _lblStatus.Text = listening ? "邮箱监听运行中..." : "就绪";
            }
            else if (_running)
            {
                _lblStatus.Text = "升级进行中...";
            }
        }

        private void AppendInfo(string text)
        {
            _logBox.AppendText(text + Environment.NewLine);
        }

        private static Label AddLabel(Control parent, string text, int x, int y)
        {
            var lbl = new Label { Text = text, Left = x, Top = y + 4, AutoSize = true };
            parent.Controls.Add(lbl);
            return lbl;
        }

        private static TextBox AddTextBox(Control parent, int x, int y, int width)
        {
            var tb = new TextBox { Left = x, Top = y - 2, Width = width };
            parent.Controls.Add(tb);
            return tb;
        }

        private static Button AddButton(Control parent, string text, int x, int y, int width)
        {
            var btn = new Button { Text = text, Left = x, Top = y, Width = width, Height = 28 };
            parent.Controls.Add(btn);
            return btn;
        }
    }

    internal static class ProcessHelper
    {
        public static void OpenFolder(string path)
        {
            System.Diagnostics.Process.Start("explorer.exe", path);
        }
    }
}
