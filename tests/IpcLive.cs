using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NetSwitcher;

/// <summary>
/// 真实进程级的单实例通信自检：
///   IpcLive.exe --listen            作为"已经运行的实例"监听生产管道名
///   IpcLive.exe --send "APPLY|X|"   作为"第二个实例"把指令发给它
/// 父进程用 Process.Start 拉起子进程（继承标准输出），能直接看到结果。
/// </summary>
internal static class IpcLive
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool listen = false;
        string toSend = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--listen") listen = true;
            if (args[i] == "--send" && i + 1 < args.Length) { toSend = args[i + 1]; i++; }
        }

        if (toSend != null)
        {
            bool ok = IpcClient.Send(toSend);
            Console.WriteLine("[发送方] 目标管道=" + Core.PipeName + "  负载=[" + toSend + "]  结果=" + (ok ? "已送达" : "失败"));
            return ok ? 0 : 1;
        }

        if (!listen)
        {
            Console.WriteLine("用法: --listen | --send \"消息\"");
            return 2;
        }

        // ---- 作为"正在运行的实例"：真的建一个主窗口并启动管道监听 ----
        MainForm.DryRun = true;                 // 不碰真实网络
        var form = new MainForm();
        form.ShowInTaskbar = false;
        form.WindowState = FormWindowState.Minimized;
        form.StartIpcListener();

        string got = null;
        form.IpcReceived += msg =>
        {
            got = msg;
            Console.WriteLine("[监听方] 回调触发，收到 [" + msg + "]");
        };
        Console.WriteLine("[监听方] 已启动，管道=" + Core.PipeName + "  pid=" + Process.GetCurrentProcess().Id
            + "  句柄已建=" + form.IsHandleCreated);

        // 由父进程在另一个进程里发送指令；这里等最多 12 秒
        DateTime deadline = DateTime.Now.AddSeconds(12);
        while (got == null && DateTime.Now < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }

        Console.WriteLine("[监听方] 收到的指令 = [" + (got ?? "（超时，没收到）") + "]");
        try { form.TrayIcon.Visible = false; } catch { }
        form.Dispose();
        return got == null ? 1 : 0;
    }
}
