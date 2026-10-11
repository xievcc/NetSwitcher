using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NetSwitcher
{
    public sealed class MainForm : Form
    {
        private readonly List<AdapterInfo> _adapters = new List<AdapterInfo>();
        internal List<NetProfile> Profiles = new List<NetProfile>();
        private readonly bool _isAdmin;
        private bool _busy;

        /// <summary>
        /// 自检模式：只构建界面与读取状态，绝不执行任何修改网络的命令。
        /// 供 UI 冒烟测试使用，正常运行时为 false。
        /// </summary>
        internal static bool DryRun { get; set; }

        private ComboBox _cboAdapter;
        private Label _lblCurrent;
        private TextBox _txtIp, _txtMask, _txtGateway, _txtDns, _txtName;
        private ListView _lstProfiles;
        private Button _btnApply, _btnSave, _btnDelete, _btnEdit, _btnApplyProfile, _btnOpenFolder;
        internal Label StatusLabel;
        internal NotifyIcon TrayIcon;
        internal ContextMenuStrip TrayMenu;
        internal CheckBox ChkPanel;
        internal SwitcherForm Panel { get { return _panel; } }
        private SwitcherForm _panel;
        private ToolStripMenuItem _trayProfilesHeader, _trayPanelToggle;
        private ToolStripSeparator _trayProfilesSeparator;
        private readonly List<ToolStripItem> _trayProfileItems = new List<ToolStripItem>();
        private JsonSettings _settings = new JsonSettings();
        /// <summary>真正退出（托盘菜单"退出"或系统关机），否则关窗只是收进托盘。</summary>
        private bool _reallyExit;

        public MainForm()
        {
            _isAdmin = IsAdministrator();
            _settings = SettingsStore.Load();
            BuildUi();          // 内部会建托盘菜单（此时 _trayProfilesSeparator 才能用）
            LoadProfiles();     // 载入方案，并同步托盘菜单
            RefreshAdapters();
            if (_settings.PanelVisible) ShowPanel();   // 上次开着就自动恢复
            if (!_isAdmin) SetStatus("当前不是管理员：连接建立后才能修改网络配置。");
        }

        private static bool IsAdministrator()
        {
            try
            {
                var principal = new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent());
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        #region 界面搭建

        internal void BuildUi()
        {
            Text = "NetSwitcher - 网络快速切换";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(940, 610);
            MinimumSize = new Size(820, 560);
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;

            // ---------- 顶部：网卡选择 ----------
            var top = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(14, 12, 14, 8) };
            var lblAdapter = new Label { Text = "网卡：", AutoSize = true, Location = new Point(14, 18) };
            _cboAdapter = new ComboBox
            {
                Location = new Point(66, 14),
                Width = 620,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboAdapter.SelectedIndexChanged += (s, e) => OnAdapterChanged();
            var btnRefresh = new Button { Text = "刷新", Location = new Point(700, 13), Size = new Size(84, 27) };
            btnRefresh.Click += (s, e) => RefreshAdapters();
            var btnDhcpTop = new Button { Text = "恢复自动 (DHCP)", Location = new Point(792, 13), Size = new Size(132, 27) };
            btnDhcpTop.Click += (s, e) => RestoreDhcp();

            _lblCurrent = new Label
            {
                AutoSize = false,
                Location = new Point(16, 50),
                Size = new Size(908, 20),
                ForeColor = Color.FromArgb(70, 70, 70),
                Text = "当前配置：读取中…"
            };
            top.Controls.AddRange(new Control[] { lblAdapter, _cboAdapter, btnRefresh, btnDhcpTop, _lblCurrent });

            // ---------- 左侧：静态配置输入 ----------
            var left = new GroupBox
            {
                Text = " 静态配置 ",
                Location = new Point(14, 104),
                Size = new Size(452, 300)
            };
            var lblIp = new Label { Text = "IP 地址", AutoSize = true, Location = new Point(20, 36) };
            _txtIp = new TextBox { Location = new Point(120, 32), Width = 300 };
            var lblMask = new Label { Text = "子网掩码", AutoSize = true, Location = new Point(20, 72) };
            _txtMask = new TextBox { Location = new Point(120, 68), Width = 300 };
            var lblGw = new Label { Text = "默认网关", AutoSize = true, Location = new Point(20, 108) };
            _txtGateway = new TextBox { Location = new Point(120, 104), Width = 300 };
            var lblDns = new Label { Text = "DNS 服务器", AutoSize = true, Location = new Point(20, 144) };
            _txtDns = new TextBox { Location = new Point(120, 140), Width = 300 };
            var lblDnsHint = new Label
            {
                Text = "多个 DNS 用逗号分隔，留空表示自动获取",
                AutoSize = true,
                Location = new Point(120, 164),
                ForeColor = Color.Gray
            };
            var lblName = new Label { Text = "方案名称", AutoSize = true, Location = new Point(20, 200) };
            _txtName = new TextBox { Location = new Point(120, 196), Width = 300 };

            _btnApply = new Button { Text = "立即应用（不保存）", Location = new Point(120, 236), Size = new Size(146, 32) };
            _btnApply.Click += (s, e) => ApplyManual(false);
            _btnSave = new Button { Text = "保存为方案", Location = new Point(274, 236), Size = new Size(146, 32) };
            _btnSave.Click += (s, e) => SaveProfileFromForm();

            left.Controls.AddRange(new Control[] {
                lblIp, _txtIp, lblMask, _txtMask, lblGw, _txtGateway, lblDns, _txtDns, lblDnsHint, lblName, _txtName,
                _btnApply, _btnSave
            });

            // ---------- 右侧：方案列表 ----------
            var right = new GroupBox
            {
                Text = " 配置方案（双击即可切换） ",
                Location = new Point(476, 104),
                Size = new Size(450, 300)
            };
            _lstProfiles = new ListView
            {
                Location = new Point(14, 24),
                Size = new Size(422, 216),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false
            };
            _lstProfiles.Columns.Add("方案名称", 120);
            _lstProfiles.Columns.Add("模式", 78);
            _lstProfiles.Columns.Add("IP / 网关", 218);
            _lstProfiles.SelectedIndexChanged += (s, e) => UpdateButtons();
            _lstProfiles.DoubleClick += (s, e) => ApplySelectedProfile();

            _btnApplyProfile = new Button { Text = "切换到此方案", Location = new Point(14, 250), Size = new Size(126, 32) };
            _btnApplyProfile.Click += (s, e) => ApplySelectedProfile();
            _btnEdit = new Button { Text = "载入到左侧", Location = new Point(146, 250), Size = new Size(96, 32) };
            _btnEdit.Click += (s, e) => LoadSelectedProfileToForm();
            _btnDelete = new Button { Text = "删除", Location = new Point(248, 250), Size = new Size(76, 32) };
            _btnDelete.Click += (s, e) => DeleteSelectedProfile();
            _btnOpenFolder = new Button { Text = "配置文件", Location = new Point(330, 250), Size = new Size(106, 32) };
            _btnOpenFolder.Click += (s, e) => OpenDataFolder();

            right.Controls.AddRange(new Control[] { _lstProfiles, _btnApplyProfile, _btnEdit, _btnDelete, _btnOpenFolder });

            // ---------- 底部：状态栏 ----------
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 158, Padding = new Padding(14, 6, 14, 10) };
            var btnApplyBottom = new Button { Text = "立即应用左侧配置", Location = new Point(14, 8), Size = new Size(150, 32) };
            btnApplyBottom.Click += (s, e) => ApplyManual(false);
            var btnDns = new Button { Text = "仅应用 DNS", Location = new Point(172, 8), Size = new Size(110, 32) };
            btnDns.Click += (s, e) => ApplyDnsOnly();
            var btnTray = new Button { Text = "最小化到托盘", Location = new Point(290, 8), Size = new Size(120, 32) };
            btnTray.Click += (s, e) => HideToTray();
            ChkPanel = new CheckBox
            {
                Text = "显示悬浮快捷面板（置顶小窗，单击即切换）",
                Location = new Point(424, 13),
                Size = new Size(330, 24),
                Checked = _settings.PanelVisible
            };
            ChkPanel.CheckedChanged += (s, e) =>
            {
                _settings.PanelVisible = ChkPanel.Checked;
                SettingsStore.Save(_settings);
                if (ChkPanel.Checked) ShowPanel();
                else HidePanel();
            };

            StatusLabel = new Label
            {
                AutoSize = false,
                Location = new Point(16, 52),
                Size = new Size(908, 96),
                ForeColor = Color.FromArgb(40, 40, 40),
                Text = "就绪。勾选上面的「悬浮快捷面板」后，屏幕上会出现一个置顶小窗，列出所有方案，单击即切换。"
            };
            bottom.Controls.AddRange(new Control[] { btnApplyBottom, btnDns, btnTray, ChkPanel, StatusLabel });

            Controls.Add(left);
            Controls.Add(right);
            Controls.Add(top);
            Controls.Add(bottom);

            BuildTray();
            AcceptButton = null;
            UpdateButtons();
        }

        private void BuildTray()
        {
            TrayMenu = new ContextMenuStrip();

            // 结构搭一次，方案列表变化时只重建中间那段
            _trayProfilesHeader = new ToolStripMenuItem("未选择网卡") { Enabled = false };
            _trayProfilesSeparator = new ToolStripSeparator();
            TrayMenu.Items.Add(_trayProfilesHeader);
            TrayMenu.Items.Add(_trayProfilesSeparator);
            TrayMenu.Items.Add(new ToolStripMenuItem("恢复自动获取 (DHCP)", null, (s, e) => RestoreDhcp()));
            TrayMenu.Items.Add(new ToolStripMenuItem("打开主窗口", null, (s, e) => RestoreFromTray()));

            _trayPanelToggle = new ToolStripMenuItem("显示悬浮快捷面板", null, (s, e) => TogglePanel());
            _trayPanelToggle.CheckOnClick = false;
            TrayMenu.Items.Add(_trayPanelToggle);

            TrayMenu.Items.Add(new ToolStripSeparator());
            TrayMenu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) =>
            {
                _reallyExit = true;
                if (TrayIcon != null) TrayIcon.Visible = false;
                Close();
            }));

            TrayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "NetSwitcher - 网络快速切换",
                ContextMenuStrip = TrayMenu,
                Visible = true
            };
            TrayIcon.DoubleClick += (s, e) => RestoreFromTray();
            TrayIcon.BalloonTipClicked += (s, e) => RestoreFromTray();
            TrayPopup.Attach(TrayIcon);

            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized) HideToTray();
            };
            FormClosing += OnFormClosing;

            BuildTrayProfileItems();
        }

        internal NotifyIcon Tray { get { return TrayIcon; } }
        internal ContextMenuStrip TrayMenuInternal { get { return TrayMenu; } }

        /// <summary>只重建托盘菜单里的方案列表部分。</summary>
        private void BuildTrayProfileItems()
        {
            foreach (ToolStripItem item in _trayProfileItems) TrayMenu.Items.Remove(item);
            _trayProfileItems.Clear();

            AdapterInfo current = SelectedAdapter();
            _trayProfilesHeader.Text = current == null
                ? "未选择网卡"
                : current.Name + "：" + (current.State.HasIp ? current.State.Ip : current.Status);

            int insertAt = TrayMenu.Items.IndexOf(_trayProfilesSeparator) + 1;
            var newItems = new List<ToolStripItem>();

            if (Profiles.Count == 0)
            {
                newItems.Add(new ToolStripMenuItem("（还没有保存的方案）") { Enabled = false });
            }
            else
            {
                foreach (NetProfile p in Profiles)
                {
                    NetProfile captured = p;
                    string text = p.Name + "  —  " + (p.Dhcp ? "DHCP" : p.Ip);
                    var item = new ToolStripMenuItem(text);
                    item.Click += (s, e) => ApplyProfile(captured, SelectedAdapter(), SwitchOrigin.TrayMenu);
                    newItems.Add(item);
                }
            }

            for (int i = 0; i < newItems.Count; i++) TrayMenu.Items.Insert(insertAt + i, newItems[i]);
            _trayProfileItems.AddRange(newItems);

            if (_trayPanelToggle != null)
            {
                _trayPanelToggle.Checked = _panel != null && _panel.Visible;
                _trayPanelToggle.Text = _trayPanelToggle.Checked ? "隐藏悬浮快捷面板" : "显示悬浮快捷面板";
            }
        }

        #endregion

        #region 悬浮快捷面板

        /// <summary>打开（或重新激活）悬浮快捷面板。</summary>
        internal void ShowPanel()
        {
            if (_panel == null || _panel.IsDisposed)
            {
                _panel = new SwitcherForm();
                _panel.MainWindowRequested += () => { RestoreFromTray(); _panel.Hide(); };
                _panel.ClosedByUser += () =>
                {
                    _settings.PanelVisible = false;
                    SettingsStore.Save(_settings);
                    if (ChkPanel != null) ChkPanel.Checked = false;
                    BuildTrayProfileItems();
                };
                _panel.ApplyPlacement();
            }

            _panel.Reload();
            if (!_panel.Visible) _panel.Show();
            _panel.BringToFront();
            _panel.TopMost = true;

            _settings.PanelVisible = true;
            SettingsStore.Save(_settings);
            if (ChkPanel != null && !ChkPanel.Checked) ChkPanel.Checked = true;
            BuildTrayProfileItems();
        }

        private void HidePanel()
        {
            if (_panel != null && !_panel.IsDisposed) _panel.Hide();
            _settings.PanelVisible = false;
            SettingsStore.Save(_settings);
            if (ChkPanel != null && ChkPanel.Checked) ChkPanel.Checked = false;
            BuildTrayProfileItems();
        }

        private void TogglePanel()
        {
            if (_panel != null && _panel.Visible) HidePanel();
            else ShowPanel();
        }

        /// <summary>方案变化后刷新面板里的按钮。</summary>
        internal void RefreshPanelIfOpen()
        {
            if (_panel != null && !_panel.IsDisposed && _panel.Visible)
            {
                try { _panel.Reload(); } catch { }
            }
        }

        #endregion

        #region 网卡与界面同步

        private void RefreshAdapters()
        {
            string keep = _cboAdapter.SelectedItem is AdapterInfo ? ((AdapterInfo)_cboAdapter.SelectedItem).Name : null;

            _adapters.Clear();
            _adapters.AddRange(NetworkManager.GetAdapters());

            _cboAdapter.BeginUpdate();
            _cboAdapter.Items.Clear();
            foreach (AdapterInfo a in _adapters) _cboAdapter.Items.Add(a);

            int index = -1;
            if (keep != null)
            {
                for (int i = 0; i < _adapters.Count; i++)
                {
                    if (string.Equals(_adapters[i].Name, keep, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                }
            }
            // 首次进入（或原网卡消失）时，默认选中"正在联网的那张网卡"，而不是回环/虚拟网卡
            if (index < 0) index = PreferredAdapterIndex();
            if (_cboAdapter.Items.Count > 0) _cboAdapter.SelectedIndex = Math.Max(0, index);
            _cboAdapter.EndUpdate();

            OnAdapterChanged();
        }

        /// <summary>优先挑已连接、有 IP、非回环/隧道的网卡。</summary>
        private int PreferredAdapterIndex()
        {
            for (int i = 0; i < _adapters.Count; i++)
            {
                AdapterInfo a = _adapters[i];
                if (a.Type == "回环" || a.Type == "隧道") continue;
                if (a.Status == "已连接" && a.State.HasIp) return i;
            }
            for (int i = 0; i < _adapters.Count; i++)
            {
                if (_adapters[i].State.HasIp && _adapters[i].Type != "回环") return i;
            }
            return -1;
        }

        private AdapterInfo SelectedAdapter()
        {
            return _cboAdapter.SelectedItem as AdapterInfo;
        }

        private void OnAdapterChanged()
        {
            AdapterInfo a = SelectedAdapter();
            if (a == null)
            {
                _lblCurrent.Text = "当前配置：未发现可用网卡。";
                return;
            }

            var st = a.State;
            _lblCurrent.Text = "当前配置：" + (st.HasIp ? st.Ip : "无 IP")
                + "   掩码 " + (string.IsNullOrEmpty(st.Mask) ? "-" : st.Mask)
                + "   网关 " + (string.IsNullOrEmpty(st.Gateway) ? "(无)" : st.Gateway)
                + "   DNS " + st.DnsText
                + "   " + (st.DhcpEnabled ? "[自动 DHCP]" : "[静态]")
                + "   " + a.Status;

            // 预填输入框：以网卡现状为起点，方便微调
            _txtIp.Text = st.HasIp ? st.Ip : "";
            _txtMask.Text = st.HasIp && !string.IsNullOrEmpty(st.Mask) ? st.Mask : "255.255.255.0";
            _txtGateway.Text = st.Gateway ?? "";
            _txtDns.Text = string.Join(", ", st.Dns.ToArray());

            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool hasAdapter = SelectedAdapter() != null;
            _btnApply.Enabled = hasAdapter && !_busy;
            _btnSave.Enabled = hasAdapter && !_busy;
            _btnApplyProfile.Enabled = hasAdapter && _lstProfiles.SelectedItems.Count > 0 && !_busy;
            _btnEdit.Enabled = _lstProfiles.SelectedItems.Count > 0 && !_busy;
            _btnDelete.Enabled = _lstProfiles.SelectedItems.Count > 0 && !_busy;
        }

        private void SetStatus(string text)
        {
            StatusLabel.Text = text;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            UseWaitCursor = busy;
            UpdateButtons();
        }

        #endregion

        #region 方案列表

        private void LoadProfiles()
        {
            Profiles = ProfileStore.Load();
            RefreshProfileList();
        }

        private void RefreshProfileList()
        {
            _lstProfiles.BeginUpdate();
            _lstProfiles.Items.Clear();
            foreach (NetProfile p in Profiles)
            {
                var item = new ListViewItem(p.Name);
                item.SubItems.Add(p.ModeText);
                item.SubItems.Add(p.Dhcp ? "-" : (p.Ip + (string.IsNullOrEmpty(p.Gateway) ? "" : "  →  " + p.Gateway)));
                item.Tag = p;
                _lstProfiles.Items.Add(item);
            }
            _lstProfiles.EndUpdate();
            UpdateButtons();

            // 方案列表一变，托盘菜单与悬浮面板都要跟着更新
            BuildTrayProfileItems();
            RefreshPanelIfOpen();
        }

        private NetProfile SelectedProfile()
        {
            if (_lstProfiles.SelectedItems.Count == 0) return null;
            return _lstProfiles.SelectedItems[0].Tag as NetProfile;
        }

        private void SaveProfiles()
        {
            try
            {
                ProfileStore.Save(Profiles);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SaveProfileFromForm()
        {
            AdapterInfo a = SelectedAdapter();
            if (a == null) { Warn("请先选择网卡。"); return; }

            string name = (_txtName.Text ?? "").Trim();
            if (name.Length == 0) { Warn("请填写方案名称，例如：公司内网 / 家里 / 调试直连。"); _txtName.Focus(); return; }

            string ip = (_txtIp.Text ?? "").Trim();
            string mask = NormalizeMask((_txtMask.Text ?? "").Trim());
            string gateway = (_txtGateway.Text ?? "").Trim();
            var dns = NetworkManager.ParseDnsList(_txtDns.Text);

            if (!NetworkManager.IsIpv4(ip)) { Warn("IP 地址格式不正确。"); _txtIp.Focus(); return; }
            if (!NetworkManager.IsIpv4(mask)) { Warn("子网掩码格式不正确，可填 255.255.255.0 或 /24。"); _txtMask.Focus(); return; }
            if (gateway.Length > 0 && !NetworkManager.IsIpv4(gateway)) { Warn("默认网关格式不正确，留空表示不设置网关。"); _txtGateway.Focus(); return; }

            var profile = new NetProfile
            {
                Name = name,
                Adapter = a.Name,
                Dhcp = false,
                Ip = ip,
                Mask = mask,
                Gateway = gateway,
                Dns = dns
            };

            NetProfile exists = Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (exists != null)
            {
                if (MessageBox.Show(this, "已存在同名方案「" + name + "」，覆盖它吗？", "确认覆盖",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Profiles[Profiles.IndexOf(exists)] = profile;
            }
            else
            {
                Profiles.Add(profile);
            }

            SaveProfiles();
            RefreshProfileList();
            SetStatus("已保存方案「" + name + "」。");
        }

        private void DeleteSelectedProfile()
        {
            NetProfile p = SelectedProfile();
            if (p == null) return;
            if (MessageBox.Show(this, "删除方案「" + p.Name + "」？", "确认删除",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Profiles.Remove(p);
            SaveProfiles();
            RefreshProfileList();
            SetStatus("已删除方案「" + p.Name + "」。");
        }

        private void LoadSelectedProfileToForm()
        {
            NetProfile p = SelectedProfile();
            if (p == null) return;

            _txtName.Text = p.Name;
            if (!p.Dhcp)
            {
                _txtIp.Text = p.Ip;
                _txtMask.Text = p.Mask;
                _txtGateway.Text = p.Gateway;
                _txtDns.Text = string.Join(", ", (p.Dns ?? new List<string>()).ToArray());
            }
            SetStatus("已把方案「" + p.Name + "」载入左侧，可修改后再次保存。");
        }

        private void OpenDataFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(ProfileStore.ProfileDirectory);
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + ProfileStore.FilePath + "\"");
            }
            catch (Exception ex)
            {
                Warn("无法打开配置目录：" + ex.Message);
            }
        }

        #endregion

        #region 执行切换

        private void ApplySelectedProfile()
        {
            NetProfile p = SelectedProfile();
            if (p == null) return;
            ApplyProfile(p, SelectedAdapter(), SwitchOrigin.MainWindow);
        }

        /// <summary>套用一个方案：方案指定了网卡就用它，否则用当前选中的网卡。</summary>
        internal void ApplyProfile(NetProfile profile, AdapterInfo fallback, SwitchOrigin origin)
        {
            if (profile == null) return;

            AdapterInfo target = null;
            if (!string.IsNullOrEmpty(profile.Adapter) && _adapters.Count > 0)
            {
                target = _adapters.Find(a => string.Equals(a.Name, profile.Adapter, StringComparison.OrdinalIgnoreCase));
            }
            if (target == null) target = fallback;
            if (target == null) target = Applier.ResolveAdapter(profile);
            if (target == null)
            {
                Warn("没有可用的网卡，无法切换。");
                return;
            }

            if (origin == SwitchOrigin.MainWindow)
            {
                string confirm = "即将把网卡「" + target.Name + "」切换为：" + Environment.NewLine + Environment.NewLine
                    + "方案：" + profile.Name + Environment.NewLine
                    + profile.Summary + Environment.NewLine + Environment.NewLine
                    + "切换过程中网络会短暂中断。继续吗？";
                if (MessageBox.Show(this, confirm, "确认切换", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return;
                RunNetworkAction("正在切换「" + target.Name + "」→ " + profile.Name + " …",
                    () => Applier.UseProfile(profile, target));
            }
            else
            {
                // 托盘/任务栏入口：不弹确认框，切换完用气泡与浮层提示
                SetStatus("正在切换「" + target.Name + "」→ " + profile.Name + " …");
                Application.DoEvents();
                Applier.Switch(profile, target, origin, (ok, title, detail) =>
                {
                    SetStatus((ok ? "✔ " : "✘ ") + title + Environment.NewLine + detail
                        + Environment.NewLine + "（来自" + OriginText(origin) + "）");
                    RefreshAdapters();
                });
            }
        }

        private static string OriginText(SwitchOrigin origin)
        {
            switch (origin)
            {
                case SwitchOrigin.TrayMenu: return "托盘菜单";
                case SwitchOrigin.Panel: return "悬浮快捷面板";
                case SwitchOrigin.ExternalRequest: return "快捷方式/命令行";
                default: return "主界面";
            }
        }

        private void ApplyManual(bool silent)
        {
            AdapterInfo a = SelectedAdapter();
            if (a == null) { Warn("请先选择网卡。"); return; }

            string ip = (_txtIp.Text ?? "").Trim();
            string mask = NormalizeMask((_txtMask.Text ?? "").Trim());
            string gateway = (_txtGateway.Text ?? "").Trim();
            var dns = NetworkManager.ParseDnsList(_txtDns.Text);

            if (!NetworkManager.IsIpv4(ip)) { Warn("IP 地址格式不正确。"); return; }
            if (!NetworkManager.IsIpv4(mask)) { Warn("子网掩码格式不正确，可填 255.255.255.0 或 /24。"); return; }
            if (gateway.Length > 0 && !NetworkManager.IsIpv4(gateway)) { Warn("默认网关格式不正确，留空表示不设置网关。"); return; }

            if (!silent)
            {
                string confirm = "即将把网卡「" + a.Name + "」设置为：" + Environment.NewLine + Environment.NewLine
                    + "IP " + ip + " / " + mask + Environment.NewLine
                    + "网关 " + (gateway.Length == 0 ? "(不设置)" : gateway) + Environment.NewLine
                    + "DNS " + (dns.Count == 0 ? "自动获取" : string.Join(", ", dns.ToArray())) + Environment.NewLine + Environment.NewLine
                    + "切换过程中网络会短暂中断。继续吗？";
                if (MessageBox.Show(this, confirm, "确认应用", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return;
            }

            RunNetworkAction("正在应用配置到「" + a.Name + "」…", () =>
                NetworkManager.ApplyStatic(a.Name, ip, mask, gateway, dns));
        }

        private void ApplyDnsOnly()
        {
            AdapterInfo a = SelectedAdapter();
            if (a == null) { Warn("请先选择网卡。"); return; }
            var dns = NetworkManager.ParseDnsList(_txtDns.Text);
            RunNetworkAction("正在更新「" + a.Name + "」的 DNS …", () => NetworkManager.SetDns(a.Name, dns));
        }

        private void RestoreDhcp()
        {
            AdapterInfo a = SelectedAdapter();
            if (a == null)
            {
                if (_adapters.Count > 0) { a = _adapters[0]; _cboAdapter.SelectedItem = a; }
                else { Warn("没有可用的网卡。"); return; }
            }

            if (MessageBox.Show(this, "把网卡「" + a.Name + "」恢复为自动获取 IP 和 DNS？", "确认",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            RunNetworkAction("正在恢复「" + a.Name + "」为自动获取 …", () => NetworkManager.UseDhcp(a.Name));
        }

        /// <summary>统一的执行外壳（主界面直接发起）：忙碌状态、结果提示、刷新界面。</summary>
        private void RunNetworkAction(string progressText, Func<NetResult> action)
        {
            if (_busy) return;
            if (DryRun)
            {
                // 自检模式：只报告，不真的改网络
                SetStatus("[自检模式] 已跳过实际网络操作：" + progressText);
                return;
            }
            SetBusy(true);
            SetStatus(progressText);
            Application.DoEvents();

            NetResult result;
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                result = new NetResult { Ok = false, Title = "执行出错", Detail = ex.Message };
            }

            SetBusy(false);
            FinishNetworkAction(result);
        }

        /// <summary>执行结束：更新状态栏、必要时气泡提示、弹结果框、刷新网卡。</summary>
        private void FinishNetworkAction(NetResult result)
        {
            string body = result.Detail ?? "";
            SetStatus((result.Ok ? "✔ " : "✘ ") + result.Title + Environment.NewLine + body);

            if ((!Visible || _settings.ShowToast) && result.Ok)
            {
                TrayPopup.Show(true, result.Title, body);
            }

            MessageBox.Show(this, body, result.Title,
                MessageBoxButtons.OK, result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

            RefreshAdapters();
        }

        #endregion

        #region 命令行 / 进程间通信

        /// <summary>
        /// 从任务栏右键菜单、快捷方式或命令行启动时的静默执行入口。
        /// 执行完提示结果并退出进程（不留下第二个托盘图标）。
        /// </summary>
        internal void RunStartupCommand(CommandLine cl)
        {
            try
            {
                if (cl.Dhcp)
                {
                    AdapterInfo target = null;
                    if (cl.AdapterName.Length > 0)
                    {
                        target = _adapters.Find(a => string.Equals(a.Name, cl.AdapterName, StringComparison.OrdinalIgnoreCase));
                    }
                    if (target == null) target = PreferredAdapter();
                    if (target == null)
                    {
                        ShowResult(false, "找不到网卡", "本机没有可用的 IPv4 网卡。");
                        return;
                    }

                    SetStatus("正在把「" + target.Name + "」恢复为自动获取 …");
                    NetResult r = NetworkManager.UseDhcp(target.Name);
                    ShowResult(r.Ok, r.Title, r.Detail);
                    return;
                }

                NetProfile profile = Applier.FindProfile(Profiles, cl.ProfileName);
                if (profile == null)
                {
                    ShowResult(false, "找不到方案",
                        "任务栏菜单里没有「" + cl.ProfileName + "」这个方案。"
                        + Environment.NewLine + "可能已被重命名或删除，请重新打开主界面把它加回任务栏菜单。");
                    return;
                }

                AdapterInfo adapter = null;
                if (cl.AdapterName.Length > 0)
                    adapter = _adapters.Find(a => string.Equals(a.Name, cl.AdapterName, StringComparison.OrdinalIgnoreCase));
                if (adapter == null) adapter = Applier.ResolveAdapter(profile);

                SetStatus("正在切换「" + profile.Name + "」…");
                NetResult result = adapter == null
                    ? new NetResult { Ok = false, Title = "找不到网卡", Detail = "本机没有可用的 IPv4 网卡。" }
                    : Applier.UseProfile(profile, adapter);
                ShowResult(result.Ok, result.Title, result.Detail);
            }
            catch (Exception ex)
            {
                ShowResult(false, "执行出错", ex.Message);
            }
            finally
            {
                _reallyExit = true;
                Close();
            }
        }

        private AdapterInfo PreferredAdapter()
        {
            AdapterInfo a = _adapters.Find(x => x.Type != "回环" && x.Type != "隧道" && x.Status == "已连接" && x.State.HasIp);
            if (a != null) return a;
            return _adapters.Find(x => x.Type != "回环");
        }

        private void ShowResult(bool ok, string title, string detail)
        {
            if (MainForm.DryRun) return;
            MessageBox.Show((detail ?? "") + Environment.NewLine + Environment.NewLine + "网卡：" + (Applier.LastAdapter ?? "-"),
                (ok ? "✔ " : "✘ ") + title, MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        /// <summary>监听命名管道：程序已在后台时，任务栏菜单/快捷方式发来的指令由本实例执行。</summary>
        internal void StartIpcListener()
        {
            // 必须先有窗口句柄，否则第一条指令会因为 BeginInvoke 失败而永久丢失
            if (!IsHandleCreated)
            {
                try { CreateControl(); } catch { }
            }

            IpcServer.Start(Core.PipeName, message =>
            {
                if (IpcReceived != null) { try { IpcReceived(message); } catch { } }

                // 句柄还没好就稍等重试，不要把指令丢掉
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    try
                    {
                        if (IsHandleCreated)
                        {
                            BeginInvoke(new Action(() => HandleIpcMessage(message)));
                            return;
                        }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(50);
                }
            });
        }

        /// <summary>收到跨进程指令时触发（自检用，观察单实例通信是否真的送达）。</summary>
        internal event Action<string> IpcReceived;

        private void HandleIpcMessage(string message)
        {
            if (IpcReceived != null) { try { IpcReceived(message); } catch { } }

            if (string.IsNullOrEmpty(message)) { RestoreFromTray(); return; }

            if (message == "SHOW") { RestoreFromTray(); return; }

            if (message == "DHCP")
            {
                AdapterInfo target = SelectedAdapter() ?? PreferredAdapter();
                if (target == null) return;
                SetStatus("正在把「" + target.Name + "」恢复为自动获取 …");
                Application.DoEvents();
                NetResult r;
                try { r = NetworkManager.UseDhcp(target.Name); }
                catch (Exception ex) { r = new NetResult { Ok = false, Title = "执行出错", Detail = ex.Message }; }
                SetStatus((r.Ok ? "✔ " : "✘ ") + r.Title + Environment.NewLine + r.Detail);
                TrayPopup.Show(r.Ok, r.Title, r.Detail);
                RefreshAdapters();
                return;
            }

            if (message.StartsWith("APPLY|"))
            {
                string name = CommandLine.ProfileFromIpc(message);
                string adapterName = CommandLine.AdapterFromIpc(message);
                NetProfile profile = Applier.FindProfile(Profiles, name);
                if (profile == null)
                {
                    TrayPopup.Show(false, "找不到方案", "「" + name + "」已不存在，请重新打开主界面同步任务栏菜单。");
                    return;
                }

                AdapterInfo adapter = null;
                if (adapterName.Length > 0)
                    adapter = _adapters.Find(a => string.Equals(a.Name, adapterName, StringComparison.OrdinalIgnoreCase));
                if (adapter == null) adapter = SelectedAdapter();

                ApplyProfile(profile, adapter, SwitchOrigin.ExternalRequest);
            }
        }

        #endregion

        #region 托盘与生命周期

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            TrayIcon.BalloonTipTitle = "NetSwitcher 仍在运行";
            TrayIcon.BalloonTipText = "双击托盘图标可重新打开窗口，右键可快速切换方案。";
            TrayIcon.ShowBalloonTip(2500);
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // 点叉号 = 收进托盘；托盘菜单的"退出"/系统注销才真正结束进程
            if (e.CloseReason == CloseReason.UserClosing && !_reallyExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            _reallyExit = true;
            if (TrayIcon != null) TrayIcon.Visible = false;
        }

        private static string NormalizeMask(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Trim();
            if (text.StartsWith("/")) text = text.Substring(1);
            int prefix;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix)
                && prefix >= 0 && prefix <= 32 && text.Length <= 2)
            {
                return NetworkManager.PrefixToMask(prefix);
            }
            return text;
        }

        private void Warn(string message)
        {
            MessageBox.Show(this, message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        #endregion
    }
}
