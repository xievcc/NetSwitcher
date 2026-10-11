using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NetSwitcher
{
    /// <summary>进程级共享信息。</summary>
    internal static class Core
    {
        /// <summary>
        /// 任务栏跳转列表与本程序绑定用的 ApplicationId。
        /// 用固定字符串，这样 exe 换目录也不会丢任务栏固定项。
        /// </summary>
        internal const string ApplicationId = "NetSwitcher.NetworkSwitcher";

        /// <summary>单实例互斥体名。</summary>
        internal const string MutexName = "NetSwitcher_SingleInstance_Mutex";

        /// <summary>跨进程传递指令的命名管道名。</summary>
        internal const string PipeName = "NetSwitcher_Command_Pipe";
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            CommandLine cl = CommandLine.Parse(args);

            bool createdNew;
            using (var mutex = new Mutex(true, Core.MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例在运行：把指令交给它，自己立刻退出
                    string ipc = cl.ToIpc();
                    bool delivered = ipc.Length > 0 && IpcClient.Send(ipc);
                    if (!delivered) IpcClient.Send("SHOW");

                    if (!delivered && ipc.Length == 0)
                    {
                        MessageBox.Show("NetSwitcher 已经在运行了，请查看系统任务栏或托盘图标。", "NetSwitcher",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }

                // 让任务栏跳转列表稳定归属本程序
                try { ApplicationIdSetter.TrySet(Core.ApplicationId); }
                catch { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var form = new MainForm();

                if (cl.HasAction)
                {
                    // 从任务栏菜单/快捷方式启动：静默应用后直接退出，不闪窗口
                    form.HandleCreated += (s, e) => form.BeginInvoke(new Action(() => form.RunStartupCommand(cl)));
                    form.Shown += (s, e) => { form.Hide(); form.ShowInTaskbar = false; };
                }

                form.StartIpcListener();
                Application.Run(form);
            }
        }
    }

    /// <summary>设置 Windows 的 Application User Model ID（反射，缺类型时忽略）。</summary>
    internal static class ApplicationIdSetter
    {
        internal static void TrySet(string appId)
        {
            Type t = typeof(Form).Assembly.GetType("System.Windows.Forms.Application", false);
            if (t == null) return;
            var p = t.GetProperty("ApplicationUserModelID",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (p != null && p.CanWrite) p.SetValue(null, appId, null);
        }
    }

    /// <summary>第二个实例把指令送给正在运行的那个实例。</summary>
    internal static class IpcClient
    {
        internal static bool Send(string message)
        {
            return Send(Core.PipeName, message, 1500);
        }

        internal static bool Send(string pipeName, string message, int timeoutMs)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                {
                    pipe.Connect(timeoutMs);
                    byte[] data = Encoding.UTF8.GetBytes(message ?? "");
                    pipe.Write(data, 0, data.Length);
                    pipe.Flush();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>命名管道接收端：把收到的指令回调出来（抽出来便于自检）。</summary>
    internal static class IpcServer
    {
        /// <summary>在后台线程持续监听；handler 在管道线程上被调用。</summary>
        internal static void Start(string pipeName, Action<string> handler)
        {
            var thread = new Thread(() => Loop(pipeName, handler));
            thread.IsBackground = true;
            thread.Name = "NetSwitcher-IPC-" + pipeName;
            thread.Start();
        }

        private static void Loop(string pipeName, Action<string> handler)
        {
            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        var buffer = new byte[1024];
                        int read = server.Read(buffer, 0, buffer.Length);
                        string message = Encoding.UTF8.GetString(buffer, 0, read);
                        if (handler != null) handler(message);
                    }
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
        }
    }
}
