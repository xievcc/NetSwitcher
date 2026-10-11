using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

/// <summary>
/// 拉起两个真实进程验证单实例通信：
///   1) 子进程 IpcLive.exe --listen 作为"已运行实例"监听管道
///   2) 本进程用 Process.Start 再起一个 IpcLive.exe --send "..." 作为"第二个实例"
/// 两个子进程都继承本进程的标准输出，结果直接打印在下面。
/// </summary>
internal static class IpcLiveRunner
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string exe = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ipc-live.exe");
        if (!System.IO.File.Exists(exe))
        {
            Console.WriteLine("找不到 " + exe);
            return 2;
        }

        string payload = args.Length > 0 ? args[0] : "APPLY|公司内网|net1";

        Console.WriteLine("=== 启动监听方（模拟「已经运行的实例」）===");
        var listen = new Process();
        listen.StartInfo.FileName = exe;
        listen.StartInfo.Arguments = "--listen";
        listen.StartInfo.UseShellExecute = false;      // 继承标准输出，便于观察
        listen.Start();
        Thread.Sleep(2500);                            // 等它把管道建好

        Console.WriteLine();
        Console.WriteLine("=== 启动第二个实例，把指令发给它 ===");
        var send = new Process();
        send.StartInfo.FileName = exe;
        send.StartInfo.Arguments = "--send \"" + payload + "\"";
        send.StartInfo.UseShellExecute = false;
        send.Start();
        send.WaitForExit(20000);
        Console.WriteLine("[第二个实例] 退出码 = " + send.ExitCode);

        listen.WaitForExit(20000);
        Console.WriteLine("[监听方] 退出码 = " + listen.ExitCode);

        bool ok = send.ExitCode == 0 && listen.ExitCode == 0;
        Console.WriteLine();
        Console.WriteLine(ok ? "单实例通信链路验证通过 ✔" : "单实例通信链路验证失败 ✘");
        return ok ? 0 : 1;
    }
}
