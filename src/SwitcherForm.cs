using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NetSwitcher
{
    /// <summary>
    /// 悬浮快捷切换面板：始终置顶的小方块，列出所有方案，单击即切换。
    /// 位置可拖动并记忆，适合放在任务栏上方当"任务栏快捷入口"用。
    /// 切换逻辑走 Applier（与主界面、托盘菜单完全一致）。
    /// </summary>
    internal sealed class SwitcherForm : Form
    {
        private readonly Panel _titleBar;
        private readonly Label _lblTitle;
        private readonly Label _lblCurrent;
        private readonly Panel _profileHost;
        private readonly Panel _bottomBar;
        private readonly Button _btnDhcp;
        private readonly Label _lblStatus;
        private readonly ToolTip _tips = new ToolTip();

        private List<NetProfile> _profiles = new List<NetProfile>();
        private readonly List<AdapterInfo> _adapters = new List<AdapterInfo>();

        private bool _dragging;
        private Point _dragOrigin;
        private bool _savingPosition;

        internal event Action MainWindowRequested;
        internal event Action ClosedByUser;

        internal SwitcherForm()
        {
            Text = "NetSwitcher 快捷面板";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(232, 160);
            BackColor = Color.FromArgb(45, 48, 55);
            Font = new Font("Microsoft YaHei UI", 9f);
            KeyPreview = true;

            // ---- 标题栏（可拖动）----
            _titleBar = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(32, 35, 40) };
            _lblTitle = new Label
            {
                Text = "网络快捷切换",
                ForeColor = Color.White,
                AutoSize = false,
                Location = new Point(8, 7),
                Size = new Size(170, 18),
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };
            var btnPin = MakeTitleButton("—", 176, "最小化到托盘（随时可从托盘图标再打开）");
            btnPin.Click += (s, e) => Hide();
            var btnClose = MakeTitleButton("×", 202, "关闭快捷面板");
            btnClose.Click += (s, e) => ClosePanel();
            _titleBar.Controls.AddRange(new Control[] { _lblTitle, btnPin, btnClose });
            _titleBar.MouseDown += TitleMouseDown;
            _titleBar.MouseMove += TitleMouseMove;
            _titleBar.MouseUp += (s, e) => _dragging = false;
            _lblTitle.MouseDown += TitleMouseDown;
            _lblTitle.MouseMove += TitleMouseMove;
            _lblTitle.MouseUp += (s, e) => _dragging = false;
            _lblTitle.DoubleClick += (s, e) => RaiseMainWindow();

            // ---- 当前网卡一行 ----
            _lblCurrent = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Color.FromArgb(190, 196, 205),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 4, 0),
                Text = "读取中…"
            };

            // ---- 方案按钮区 ----
            _profileHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0) };

            // ---- 底部 ----
            _bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(38, 41, 47) };
            _btnDhcp = new Button
            {
                Text = "恢复自动获取 (DHCP)",
                Location = new Point(8, 6),
                Size = new Size(216, 24),
                FlatStyle = FlatStyle.Flat
            };
            _btnDhcp.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 106);
            _btnDhcp.ForeColor = Color.White;
            _btnDhcp.BackColor = Color.FromArgb(58, 63, 72);
            _btnDhcp.Click += (s, e) => RestoreDhcp();
            _lblStatus = new Label
            {
                Location = new Point(8, 32),
                Size = new Size(216, 16),
                ForeColor = Color.FromArgb(170, 176, 185),
                Text = "单击方案即可切换",
                AutoEllipsis = true
            };
            _bottomBar.Controls.AddRange(new Control[] { _btnDhcp, _lblStatus });

            Controls.Add(_profileHost);
            Controls.Add(_lblCurrent);
            Controls.Add(_bottomBar);
            Controls.Add(_titleBar);

            Deactivate += (s, e) => { /* 面板要保持可见，失焦不做任何事 */ };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
            FormClosing += (s, e) =>
            {
                // 关掉只隐藏；真正退出跟随主程序
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
        }

        private Button MakeTitleButton(string text, int x, string tip)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, 3),
                Size = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(210, 214, 220),
                BackColor = Color.FromArgb(32, 35, 40),
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;
            _tips.SetToolTip(b, tip);
            return b;
        }

        #region 拖动与位置记忆

        private void TitleMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOrigin = new Point(e.X, e.Y);
        }

        private void TitleMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point screen = ((Control)sender).PointToScreen(new Point(e.X, e.Y));
            Location = new Point(screen.X - _dragOrigin.X, screen.Y - _dragOrigin.Y);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (_savingPosition) return;
            SavePlacement();
        }

        private void SavePlacement()
        {
            try
            {
                _savingPosition = true;
                JsonSettings s = SettingsStore.Load();
                s.PanelVisible = true;
                s.PanelX = Left;
                s.PanelY = Top;
                SettingsStore.Save(s);
            }
            catch { }
            finally { _savingPosition = false; }
        }

        /// <summary>放到上次的位置；第一次打开则贴在做下角、任务栏上方。</summary>
        internal void ApplyPlacement()
        {
            JsonSettings s = SettingsStore.Load();
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;

            var saved = new Point(s.PanelX, s.PanelY);
            bool plausible = s.PanelX != 0 || s.PanelY != 0;
            Rectangle bounds = new Rectangle(saved, Size);
            bool visible = false;
            foreach (Screen sc in Screen.AllScreens)
            {
                if (sc.WorkingArea.IntersectsWith(bounds)) { visible = true; break; }
            }

            if (plausible && visible)
            {
                Location = saved;
            }
            else
            {
                Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            }
        }

        private void ClosePanel()
        {
            try
            {
                JsonSettings s = SettingsStore.Load();
                s.PanelVisible = false;
                s.PanelX = Left;
                s.PanelY = Top;
                SettingsStore.Save(s);
            }
            catch { }
            Hide();
            if (ClosedByUser != null) ClosedByUser();
        }

        private void RaiseMainWindow()
        {
            if (MainWindowRequested != null) MainWindowRequested();
        }

        #endregion

        #region 内容刷新与切换

        /// <summary>重新读取网卡与方案，重建按钮。</summary>
        internal void Reload()
        {
            _adapters.Clear();
            _adapters.AddRange(NetworkManager.GetAdapters());
            _profiles = ProfileStore.Load();
            Rebuild();
        }

        private AdapterInfo CurrentAdapter()
        {
            AdapterInfo a = _adapters.Find(x => x.Type != "回环" && x.Type != "隧道"
                && x.Status == "已连接" && x.State.HasIp);
            if (a != null) return a;
            return _adapters.Find(x => x.Type != "回环");
        }

        private void Rebuild()
        {
            AdapterInfo current = CurrentAdapter();
            _lblCurrent.Text = current == null
                ? "未找到可用网卡"
                : current.Name + "：" + (current.State.HasIp ? current.State.Ip : current.Status);
            _tips.SetToolTip(_lblCurrent, current == null ? "" : current.Name + " / " + current.Description);

            _profileHost.SuspendLayout();
            foreach (Control c in new List<Control>(GetProfileControls())) c.Dispose();
            _profileHost.Controls.Clear();

            int top = 4;
            if (_profiles.Count == 0)
            {
                var empty = new Label
                {
                    Location = new Point(8, top),
                    Size = new Size(216, 34),
                    ForeColor = Color.FromArgb(165, 170, 178),
                    Text = "还没有方案。\r\n请在主窗口里保存一个。"
                };
                _profileHost.Controls.Add(empty);
                top += 40;
            }
            else
            {
                foreach (NetProfile p in _profiles)
                {
                    NetProfile captured = p;
                    var btn = new Button
                    {
                        Text = (p.Dhcp ? "自动获取" : p.Name + "   " + p.Ip),
                        Location = new Point(6, top),
                        Size = new Size(220, 26),
                        FlatStyle = FlatStyle.Flat,
                        ForeColor = Color.White,
                        BackColor = Color.FromArgb(62, 68, 78),
                        TextAlign = ContentAlignment.MiddleLeft,
                        Padding = new Padding(6, 0, 0, 0),
                        TabStop = false
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(96, 103, 114);
                    _tips.SetToolTip(btn, p.Name + Environment.NewLine + p.Summary + Environment.NewLine + "单击立即切换");
                    btn.Click += (s, e) => SwitchTo(captured);
                    _profileHost.Controls.Add(btn);
                    top += 30;
                }
            }
            _profileHost.ResumeLayout();

            // 面板高度随方案数量自适应（至少能显示标题+状态）
            int wanted = 30 + 22 + top + 4 + 52 + 2;
            Height = Math.Max(150, Math.Min(460, wanted));
        }

        private IEnumerable<Control> GetProfileControls()
        {
            var list = new List<Control>();
            foreach (Control c in _profileHost.Controls) list.Add(c);
            return list;
        }

        private void SwitchTo(NetProfile profile)
        {
            AdapterInfo target = null;
            if (!string.IsNullOrEmpty(profile.Adapter))
                target = _adapters.Find(a => string.Equals(a.Name, profile.Adapter, StringComparison.OrdinalIgnoreCase));
            if (target == null) target = CurrentAdapter();
            if (target == null) { SetStatus("没有可用的网卡"); return; }

            // 面板上不弹确认框：单击就切，结果用状态行 + 屏幕提示告知
            _lblStatus.Text = "切换中… " + profile.Name;
            _lblStatus.ForeColor = Color.FromArgb(220, 200, 130);
            Application.DoEvents();

            Applier.Switch(profile, target, SwitchOrigin.Panel, (ok, title, detail) =>
            {
                SetStatus((ok ? "✔ " : "✘ ") + title);
                Reload();
            });
        }

        private void RestoreDhcp()
        {
            AdapterInfo target = CurrentAdapter();
            if (target == null) { SetStatus("没有可用的网卡"); return; }

            _lblStatus.Text = "恢复自动获取中…";
            _lblStatus.ForeColor = Color.FromArgb(220, 200, 130);
            Application.DoEvents();

            NetResult r;
            try { r = NetworkManager.UseDhcp(target.Name); }
            catch (Exception ex) { r = new NetResult { Ok = false, Title = "执行出错", Detail = ex.Message }; }

            TrayPopup.Show(r.Ok, r.Title, r.Detail);
            SetStatus((r.Ok ? "✔ " : "✘ ") + r.Title);
            Reload();
        }

        private void SetStatus(string text)
        {
            _lblStatus.Text = text;
            _lblStatus.ForeColor = Color.FromArgb(170, 176, 185);
        }

        #endregion

        protected override bool ShowWithoutActivation
        {
            get { return false; }
        }
    }
}
