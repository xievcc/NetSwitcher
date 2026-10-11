using System;
using System.Collections.Generic;

namespace NetSwitcher
{
    /// <summary>
    /// 命令行参数。任务栏跳转列表（Jump List）、快捷方式、脚本都通过它驱动本程序。
    /// 特点：不弹窗、不开窗口，适合"点一下就走"的场景。
    /// </summary>
    internal sealed class CommandLine
    {
        /// <summary>要套用的方案名（--apply"名字" 或 --apply=名字）。空表示没指定。</summary>
        public string ProfileName = "";

        /// <summary>把界面切到某个网卡再套用方案（--adapter"网卡名"）。</summary>
        public string AdapterName = "";

        /// <summary>只恢复自动获取 (DHCP)。</summary>
        public bool Dhcp;

        /// <summary>强制显示主窗口（即使已有实例在运行）。</summary>
        public bool Show;

        /// <summary>是否带任何"动作"参数（有动作就不弹主窗口）。</summary>
        public bool HasAction
        {
            get { return ProfileName.Length > 0 || Dhcp || AdapterName.Length > 0; }
        }

        private static readonly char[] TrimChars = { ' ', '\t', '"', '\u3000', '\u201C', '\u201D' };

        /// <summary>
        /// 解析参数。支持多种写法（任务栏菜单、快捷方式、脚本都会用到）：
        ///   --apply"公司内网"      ← 任务栏/快捷方式生成的形式，整串粘在一起
        ///   --apply "公司内网"     ← 正常命令行形式
        ///   --apply=公司内网
        ///   --apply 公司内网       ← 整个命令行被当成单个字符串传进来时
        ///   --adapter"net1" / --adapter "net1"
        ///   --dhcp   --show   裸方案名（直接 `NetSwitcher.exe 公司内网`）
        /// </summary>
        public static CommandLine Parse(string[] args)
        {
            var cl = new CommandLine();
            if (args == null) return cl;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim(TrimChars);
                if (a.Length == 0) continue;

                string name = a;
                string inline = "";

                // "--apply公司内网" / "--apply"公司内网"" / "--apply=公司内网"：开关和值粘在一起
                foreach (char splitOn in new[] { '"', '=', '\u201C', '\u201D' })
                {
                    int pos = name.IndexOf(splitOn);
                    if (pos > 0)
                    {
                        string tail = name.Substring(pos);
                        if (splitOn == '"' || splitOn == '\u201C' || splitOn == '\u201D')
                        {
                            inline = tail.Trim(TrimChars);          // 引号由 TrimChars 去掉
                        }
                        else
                        {
                            inline = tail.TrimStart('=').Trim(TrimChars);   // "=" 本身要去掉
                        }
                        name = name.Substring(0, pos);
                        break;
                    }
                }
                if (inline.Length == 0 && name.StartsWith("--"))
                {
                    int sep = name.IndexOfAny(new[] { ' ', '\t', '\u3000' });
                    if (sep > 0)
                    {
                        inline = name.Substring(sep).Trim(TrimChars);
                        name = name.Substring(0, sep);
                    }
                }

                string lower = name.ToLowerInvariant();
                if (lower == "--dhcp") { cl.Dhcp = true; continue; }
                if (lower == "--show") { cl.Show = true; continue; }
                if (lower == "--apply")
                {
                    cl.ProfileName = inline.Length > 0 ? inline : NextValue(args, ref i);
                    continue;
                }
                if (lower == "--adapter")
                {
                    cl.AdapterName = inline.Length > 0 ? inline : NextValue(args, ref i);
                    continue;
                }
                if (!name.StartsWith("-") && cl.ProfileName.Length == 0)
                {
                    // 裸方案名，方便做快捷方式
                    cl.ProfileName = a;
                }
            }
            return cl;
        }

        private static string NextValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length) return "";
            string next = (args[index + 1] ?? "").Trim(TrimChars);
            index++;
            return next;
        }

        /// <summary>把参数拼成传给已有实例的 IPC 文本。</summary>
        public string ToIpc()
        {
            if (Dhcp) return "DHCP";
            if (ProfileName.Length > 0) return "APPLY|" + ProfileName + "|" + AdapterName;
            if (Show) return "SHOW";
            return "";
        }

        public static string ProfileFromIpc(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            string[] parts = message.Split('|');
            return parts.Length >= 2 ? parts[1] : "";
        }

        public static string AdapterFromIpc(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            string[] parts = message.Split('|');
            return parts.Length >= 3 ? parts[2] : "";
        }
    }
}
