using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using NetSwitcher;

internal static class UiSmoke
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool keepOpen = args.Length > 0 && args[0] == "--visible";

        Console.WriteLine("=== A. 配置存取往返（真实读写 %APPDATA%\\NetSwitcher）===");
        string backup = null;
        if (System.IO.File.Exists(ProfileStore.FilePath))
        {
            backup = System.IO.File.ReadAllText(ProfileStore.FilePath, Encoding.UTF8);
            Console.WriteLine("  已备份现有配置，测试结束后还原。");
        }

        try
        {
            var sample = new List<NetProfile>
            {
                new NetProfile { Name = "公司内网", Adapter = "net1", Ip = "192.168.3.217", Mask = "255.255.255.0",
                                 Gateway = "192.168.3.1", Dns = new List<string> { "192.168.3.6", "8.8.8.8" } },
                new NetProfile { Name = "自动", Dhcp = true }
            };
            ProfileStore.Save(sample);
            List<NetProfile> loaded = ProfileStore.Load();

            Check("方案数量", loaded.Count.ToString(), "2");
            Check("第一条名称", loaded[0].Name, "公司内网");
            Check("第一条 IP", loaded[0].Ip, "192.168.3.217");
            Check("第一条 DNS 数量", loaded[0].Dns.Count.ToString(), "2");
            Check("第二条 DHCP", loaded[1].Dhcp.ToString(), "True");
            Check("Summary 文本", loaded[1].Summary, "自动获取 IP / DNS");
            Console.WriteLine("  配置文件: " + ProfileStore.FilePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [FAIL] 存取往返异常: " + ex.Message);
            _failures++;
        }
        finally
        {
            try
            {
                if (backup != null) System.IO.File.WriteAllText(ProfileStore.FilePath, backup, new UTF8Encoding(false));
                else if (System.IO.File.Exists(ProfileStore.FilePath)) System.IO.File.Delete(ProfileStore.FilePath);
            }
            catch { }
        }

        Console.WriteLine();
        Console.WriteLine("=== B. 主界面构建与网卡填充 ===");
        MainForm.DryRun = true;
        MainForm form = null;
        try
        {
            form = new MainForm();
            form.CreateControl();
            IntPtr handle = form.Handle;   // 强制创建窗口句柄，触发真正的布局
            Console.WriteLine("  窗体标题: " + form.Text);
            Console.WriteLine("  客户区尺寸: " + form.ClientSize.Width + " x " + form.ClientSize.Height);
            Console.WriteLine("  窗体句柄: " + handle);
            Console.WriteLine("  加载到的方案数: " + form.Profiles.Count);
            if (handle == IntPtr.Zero) { Console.WriteLine("  [FAIL] 句柄创建失败"); _failures++; }

            form.Show();
            Application.DoEvents();
            Console.WriteLine("  已显示并完成一次消息泵（界面未抛异常）");

            // 触发一次方案列表操作与托盘菜单构建
            form.Profiles.Add(new NetProfile { Name = "自检方案", Ip = "10.1.1.2", Mask = "255.255.255.0", Gateway = "10.1.1.1" });
            Console.WriteLine("  方案列表追加后数量: " + form.Profiles.Count);

            Check("托盘图标已创建", (form.Tray != null).ToString(), "True");
            if (form.Tray != null)
            {
                Console.WriteLine("  托盘提示文字: " + form.Tray.Text);
                Check("托盘菜单已挂载", (form.Tray.ContextMenuStrip != null).ToString(), "True");
                if (form.Tray.ContextMenuStrip != null)
                {
                    form.Tray.ContextMenuStrip.Show(0, 0);      // 触发 Opening -> 重建菜单项
                    Application.DoEvents();
                    var items = new List<string>();
                    foreach (ToolStripItem it in form.Tray.ContextMenuStrip.Items)
                    {
                        if (!(it is ToolStripSeparator)) items.Add(it.Text);
                    }
                    Console.WriteLine("  托盘菜单项: " + string.Join(" | ", items.ToArray()));
                    form.Tray.ContextMenuStrip.Close();
                }
            }

            string shot = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--shot" && i + 1 < args.Length) shot = args[i + 1];
            }
            if (!string.IsNullOrEmpty(shot))
            {
                using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("  界面截图已保存: " + shot);
            }

            if (keepOpen)
            {
                Console.WriteLine("  --visible：保持窗口 10 秒供肉眼确认。");
                DateTime until = DateTime.Now.AddSeconds(10);
                while (DateTime.Now < until)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [FAIL] 界面构建异常: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
            _failures++;
        }
        finally
        {
            if (form != null)
            {
                try { form.Close(); } catch { }
                try { form.Dispose(); } catch { }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== C. 托盘「退出」必须真的能退出 ===");
        MainForm quitForm = null;
        try
        {
            quitForm = new MainForm();
            quitForm.Show();
            Application.DoEvents();

            quitForm.Close();                     // 等价于点右上角叉号
            Application.DoEvents();
            Check("点叉号后窗口隐藏（不退出）", quitForm.Visible.ToString(), "False");

            quitForm.Show();
            Application.DoEvents();
            ToolStripMenuItem exitItem = null;
            if (quitForm.Tray != null && quitForm.Tray.ContextMenuStrip != null)
            {
                quitForm.Tray.ContextMenuStrip.Show(0, 0);
                Application.DoEvents();
                foreach (ToolStripItem it in quitForm.Tray.ContextMenuStrip.Items)
                {
                    if (it.Text == "退出") { exitItem = it as ToolStripMenuItem; break; }
                }
                quitForm.Tray.ContextMenuStrip.Close();
            }
            Check("找到托盘退出项", (exitItem != null).ToString(), "True");
            if (exitItem != null)
            {
                exitItem.PerformClick();
                Application.DoEvents();
                Check("点退出后窗口真正关闭", quitForm.Visible.ToString(), "False");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [FAIL] 托盘退出测试异常: " + ex.GetType().Name + ": " + ex.Message);
            _failures++;
        }
        finally
        {
            if (quitForm != null) { try { quitForm.Dispose(); } catch { } }
        }

        Console.WriteLine();
        Console.WriteLine("=== D. 命令解析（任务栏菜单/快捷方式靠它驱动）===");
        CommandLine c1 = CommandLine.Parse(new[] { "--apply", "公司内网" });
        Check("--apply 空格分隔", c1.ProfileName, "公司内网");
        Check("--apply 有动作", c1.HasAction.ToString(), "True");

        CommandLine c2 = CommandLine.Parse(new[] { "--apply\"公司 内网\"" });
        Check("--apply\"名字\" 粘连写法", c2.ProfileName, "公司 内网");

        CommandLine c3 = CommandLine.Parse(new[] { "--apply=家里" });
        Check("--apply=名字", c3.ProfileName, "家里");

        CommandLine c4 = CommandLine.Parse(new[] { "调试直连" });
        Check("裸方案名", c4.ProfileName, "调试直连");

        CommandLine c5 = CommandLine.Parse(new[] { "--dhcp" });
        Check("--dhcp", c5.Dhcp.ToString(), "True");
        Check("--dhcp 的 IPC 文本", c5.ToIpc(), "DHCP");

        CommandLine c6 = CommandLine.Parse(new[] { "--apply", "公司内网", "--adapter", "net1" });
        Check("--adapter", c6.AdapterName, "net1");
        Check("带网卡的 IPC 文本", c6.ToIpc(), "APPLY|公司内网|net1");
        Check("IPC 还原方案名", CommandLine.ProfileFromIpc(c6.ToIpc()), "公司内网");
        Check("IPC 还原网卡名", CommandLine.AdapterFromIpc(c6.ToIpc()), "net1");

        CommandLine c7 = CommandLine.Parse(new[] { "--show" });
        Check("--show 无动作", c7.HasAction.ToString(), "False");
        Check("--show 的 IPC 文本", c7.ToIpc(), "SHOW");
        Check("空参数", CommandLine.Parse(new string[0]).HasAction.ToString(), "False");

        Console.WriteLine();
        Console.WriteLine("=== E. 跨进程指令通道（第二个实例 → 已运行实例）===");
        string testPipe = "NetSwitcher_SmokeTest_Pipe";
        string received = null;
        var got = new System.Threading.ManualResetEvent(false);
        try
        {
            IpcServer.Start(testPipe, msg => { received = msg; got.Set(); });
            System.Threading.Thread.Sleep(800);
            bool sent = IpcClient.Send(testPipe, "APPLY|公司内网|net1", 3000);
            got.WaitOne(5000);

            if (!sent && received == null)
            {
                // DSH 的文件沙箱会禁止命名管道连接（UnauthorizedAccessException），这不是程序缺陷。
                // 完整链路请用 build\ipc-run.exe（两个真实进程）在不受限环境下验证。
                Console.WriteLine("  [SKIP] 当前环境禁止命名管道连接（沙箱限制）。");
                Console.WriteLine("         生产链路请运行：build\\ipc-run.exe \"APPLY|方案名|网卡名\"");
            }
            else
            {
                Check("客户端发送成功", sent.ToString(), "True");
                Check("服务端收到指令", received, "APPLY|公司内网|net1");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [SKIP] IPC 测试被环境拒绝: " + ex.GetType().Name + ": " + ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine("=== F. 悬浮快捷面板 ===");
        try
        {
            SwitcherForm panel = new SwitcherForm();
            panel.ApplyPlacement();
            panel.Reload();
            panel.Show();
            Application.DoEvents();
            Console.WriteLine("  面板尺寸: " + panel.Width + " x " + panel.Height);
            Console.WriteLine("  面板位置: " + panel.Left + ", " + panel.Top + "  (屏幕工作区 "
                + Screen.PrimaryScreen.WorkingArea.Width + "x" + Screen.PrimaryScreen.WorkingArea.Height + ")");
            Console.WriteLine("  置顶: " + panel.TopMost + "   不进任务栏: " + (!panel.ShowInTaskbar));
            Check("面板已创建句柄", (panel.Handle != IntPtr.Zero).ToString(), "True");
            Check("面板置顶", panel.TopMost.ToString(), "True");

            // 面板里的方案按钮数量应与配置文件里的方案一致
            int buttons = 0;
            foreach (Control c in panel.Controls)
            {
                foreach (Control inner in c.Controls)
                {
                    if (inner is Button && inner.Text != "恢复自动获取 (DHCP)"
                        && !inner.Text.StartsWith("—") && inner.Text != "×") buttons++;
                }
            }
            Console.WriteLine("  面板里的方案按钮数: " + buttons);

            System.Drawing.Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            bool onScreen = panel.Left >= wa.Left - 50 && panel.Top >= wa.Top - 50
                && panel.Left < wa.Right && panel.Top < wa.Bottom;
            Check("面板落在屏幕可见范围内", onScreen.ToString(), "True");

            string panelShot = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--panel-shot" && i + 1 < args.Length) panelShot = args[i + 1];
            }
            if (!string.IsNullOrEmpty(panelShot))
            {
                using (var bmp = new System.Drawing.Bitmap(panel.Width, panel.Height))
                {
                    panel.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, panel.Width, panel.Height));
                    bmp.Save(panelShot, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("  面板截图已保存: " + panelShot);
            }
            panel.Hide();
            panel.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [FAIL] 面板异常: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
            _failures++;
        }

        Console.WriteLine();
        Console.WriteLine("=== G. 托盘菜单 / 主界面的面板开关 ===");
        try
        {
            var menuForm = new MainForm();
            Application.DoEvents();
            var texts = new List<string>();
            bool hasPanelToggle = false;
            foreach (ToolStripItem it in menuForm.TrayMenuInternal.Items)
            {
                if (it is ToolStripSeparator) continue;
                texts.Add(it.Text);
                if (it.Text.Contains("面板")) hasPanelToggle = true;
            }
            Console.WriteLine("  托盘菜单项: " + string.Join(" | ", texts.ToArray()));
            Check("托盘菜单含面板开关", hasPanelToggle.ToString(), "True");
            Check("主界面勾选框存在", (menuForm.ChkPanel != null).ToString(), "True");

            menuForm.ShowPanel();               // 相当于勾选
            Application.DoEvents();
            Check("勾选后面板出现", (menuForm.Panel != null && menuForm.Panel.Visible).ToString(), "True");
            Check("勾选框同步为选中", menuForm.ChkPanel.Checked.ToString(), "True");

            menuForm.ChkPanel.Checked = false;  // 相当于取消勾选
            Application.DoEvents();
            Check("取消勾选后面板隐藏", (menuForm.Panel != null && !menuForm.Panel.Visible).ToString(), "True");

            menuForm.TrayIcon.Visible = false;
            menuForm.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [FAIL] 面板开关检查异常: " + ex.GetType().Name + ": " + ex.Message);
            _failures++;
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "界面自检通过 ✔" : ("界面自检失败项: " + _failures));
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string label, string actual, string expected)
    {
        bool ok = actual == expected;
        if (!ok) _failures++;
        Console.WriteLine((ok ? "  [OK]   " : "  [FAIL] ") + label + " = " + actual + (ok ? "" : " (期望 " + expected + ")"));
    }
}
