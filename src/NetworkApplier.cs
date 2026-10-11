using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NetSwitcher
{
    /// <summary>切换动作的发起方，用于决定结果提示方式。</summary>
    internal enum SwitchOrigin
    {
        MainWindow,
        TrayMenu,
        Panel,
        ExternalRequest
    }

    /// <summary>
    /// 全局切换核心：主界面、托盘菜单、任务栏跳转列表都走这里，
    /// 保证"确认 → 执行 → 校验 → 提示 → 刷新界面"的行为完全一致。
    /// 所有方法都必须从 UI 线程调用（内部有 Application.DoEvents 与界面刷新）。
    /// </summary>
    internal static class Applier
    {
        /// <summary>正在执行切换，避免连点造成 netsh 打架。</summary>
        private static bool _busy;

        /// <summary>方案被套用到哪张网卡（日志/提示用）。</summary>
        internal static string LastAdapter = "";

        /// <summary>执行切换后的回调：(成功, 提示标题, 提示正文)。</summary>
        internal static void Switch(NetProfile profile, AdapterInfo adapter, SwitchOrigin origin, Action<bool, string, string> callback)
        {
            if (_busy)
            {
                if (callback != null) callback(false, "正在切换中", "上一次切换还没结束，请稍等。");
                return;
            }

            if (profile == null)
            {
                if (callback != null) callback(false, "没有可用的方案", "请先在主界面里保存一个网络配置方案。");
                return;
            }

            AdapterInfo target = adapter ?? ResolveAdapter(profile);
            if (target == null)
            {
                if (callback != null)
                    callback(false, "找不到网卡",
                        string.IsNullOrEmpty(profile.Adapter)
                            ? "本机没有可用的 IPv4 网卡。"
                            : "方案绑定的网卡「" + profile.Adapter + "」当前不存在，可能已被禁用或改名。");
                return;
            }

            if (MainForm.DryRun)
            {
                LastAdapter = target.Name;
                if (callback != null)
                    callback(true, "自检模式", "[自检模式] 已跳过实际网络操作：" + profile.Name + " → " + target.Name);
                return;
            }

            _busy = true;
            NetResult result;
            try
            {
                result = UseProfile(profile, target);
                LastAdapter = target.Name;
            }
            catch (Exception ex)
            {
                result = new NetResult { Ok = false, Title = "执行出错", Detail = ex.Message };
            }
            finally
            {
                _busy = false;
            }

            string detail = result.Detail ?? "";

            // 主窗口自己会弹结果框；托盘/任务栏/外部请求改成气泡 + 屏幕上的一小块提示，不打断操作
            // 主窗口自己弹结果框；托盘/面板/外部请求用气泡或屏幕浮层提示，不打断操作
            if (origin != SwitchOrigin.MainWindow)
            {
                TrayPopup.Show(result.Ok, result.Title, detail);
            }

            if (callback != null) callback(result.Ok, result.Title, detail);
        }

        /// <summary>按方案本身的网卡名找网卡；找不到返回 null。</summary>
        internal static AdapterInfo ResolveAdapter(NetProfile profile)
        {
            var adapters = NetworkManager.GetAdapters();
            if (adapters.Count == 0) return null;

            if (profile != null && !string.IsNullOrEmpty(profile.Adapter))
            {
                AdapterInfo hit = adapters.Find(a => string.Equals(a.Name, profile.Adapter, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }

            // 方案没绑定网卡：挑正在联网的那张
            AdapterInfo best = adapters.Find(a => a.Type != "回环" && a.Type != "隧道" && a.Status == "已连接" && a.State.HasIp);
            if (best != null) return best;
            return adapters.Find(a => a.Type != "回环");
        }

        /// <summary>把方案落到网卡上（DHCP 或静态）。</summary>
        internal static NetResult UseProfile(NetProfile profile, AdapterInfo adapter)
        {
            if (profile.Dhcp) return NetworkManager.UseDhcp(adapter.Name);
            return NetworkManager.ApplyStatic(adapter.Name, profile.Ip, profile.Mask, profile.Gateway, profile.Dns);
        }

        /// <summary>按方案名查找方案。</summary>
        internal static NetProfile FindProfile(List<NetProfile> profiles, string name)
        {
            if (profiles == null || string.IsNullOrEmpty(name)) return null;
            return profiles.Find(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        }
    }

    /// <summary>结果提示：托盘气泡 + 屏幕右下角一小块浮层，几秒后自动消失（不抢焦点）。</summary>
    internal static class TrayPopup
    {
        private static NotifyIcon _icon;
        private static ToastForm _toast;

        internal static void Attach(NotifyIcon icon)
        {
            _icon = icon;
        }

        internal static void Show(bool ok, string title, string detail)
        {
            if (_icon != null)
            {
                try
                {
                    _icon.BalloonTipTitle = (ok ? "✔ " : "✘ ") + title;
                    string text = detail ?? "";
                    _icon.BalloonTipText = text.Length > 220 ? text.Substring(0, 220) : text;
                    _icon.ShowBalloonTip(4000);
                }
                catch { }
            }

            try
            {
                if (_toast != null) { _toast.Close(); _toast.Dispose(); _toast = null; }
                _toast = new ToastForm(ok, title, detail);
                _toast.Show();
            }
            catch { }
        }
    }

    /// <summary>无边框、不抢焦点的小提示窗口。</summary>
    internal sealed class ToastForm : Form
    {
        private readonly Timer _timer;

        internal ToastForm(bool ok, string title, string detail)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = ok ? Color.FromArgb(38, 96, 52) : Color.FromArgb(132, 48, 44);
            Size = new Size(430, 96);
            Padding = new Padding(12, 8, 12, 8);

            var lblTitle = new Label
            {
                Text = (ok ? "✔ " : "✘ ") + title,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                AutoSize = false,
                Location = new Point(12, 8),
                Size = new Size(406, 22)
            };
            string text = detail ?? "";
            if (text.Length > 120) text = text.Substring(0, 120) + " …";
            var lblDetail = new Label
            {
                Text = text,
                ForeColor = Color.FromArgb(238, 238, 238),
                AutoSize = false,
                Location = new Point(12, 32),
                Size = new Size(406, 56)
            };
            Controls.Add(lblTitle);
            Controls.Add(lblDetail);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);

            _timer = new Timer { Interval = 3200 };
            _timer.Tick += (s, e) => { _timer.Stop(); Close(); };
            _timer.Start();
            Click += (s, e) => Close();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _timer.Stop();
        }
    }
}
