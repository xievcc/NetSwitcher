using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace NetSwitcher
{
    /// <summary>一个可保存/一键套用的网络配置方案。</summary>
    public sealed class NetProfile
    {
        public string Name { get; set; }
        /// <summary>绑定的网卡名称；为空表示"套用到当前选中的网卡"。</summary>
        public string Adapter { get; set; }
        /// <summary>true = 该方案是"恢复自动获取 (DHCP)"。</summary>
        public bool Dhcp { get; set; }
        public string Ip { get; set; }
        public string Mask { get; set; }
        public string Gateway { get; set; }
        public List<string> Dns { get; set; }

        public NetProfile()
        {
            Name = "";
            Adapter = "";
            Ip = "";
            Mask = "";
            Gateway = "";
            Dns = new List<string>();
        }

        public string ModeText { get { return Dhcp ? "自动 (DHCP)" : "静态"; } }

        public string Summary
        {
            get
            {
                if (Dhcp) return "自动获取 IP / DNS";
                string dns = (Dns == null || Dns.Count == 0) ? "自动" : string.Join(", ", Dns.ToArray());
                return Ip + " / " + Mask
                    + (string.IsNullOrEmpty(Gateway) ? "" : "  网关 " + Gateway)
                    + "  DNS " + dns;
            }
        }

        public NetProfile Clone()
        {
            return new NetProfile
            {
                Name = Name,
                Adapter = Adapter,
                Dhcp = Dhcp,
                Ip = Ip,
                Mask = Mask,
                Gateway = Gateway,
                Dns = new List<string>(Dns ?? new List<string>())
            };
        }
    }

    /// <summary>方案的 JSON 持久化（默认 %APPDATA%\NetSwitcher\profiles.json）。</summary>
    public static class ProfileStore
    {
        public static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSwitcher");

        /// <summary>
        /// 配置文件路径。可用环境变量 NETSWITCHER_PROFILE_FILE 指定（做绿色版/多份配置时有用）。
        /// FilePath 表示配置文件本身，DataDirectory 始终是其所在目录。
        /// </summary>
        public static readonly string FilePath = ResolveFilePath();

        private static string ResolveFilePath()
        {
            try
            {
                string custom = Environment.GetEnvironmentVariable("NETSWITCHER_PROFILE_FILE");
                if (!string.IsNullOrEmpty(custom)) return Path.GetFullPath(custom);
            }
            catch { }
            return Path.Combine(DataDirectory, "profiles.json");
        }

        /// <summary>配置文件所在目录（随 FilePath 变化）。</summary>
        public static string ProfileDirectory
        {
            get
            {
                string dir = Path.GetDirectoryName(FilePath);
                return string.IsNullOrEmpty(dir) ? DataDirectory : dir;
            }
        }

        public static List<NetProfile> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<NetProfile>();
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new List<NetProfile>();

                var serializer = new JavaScriptSerializer();
                var list = serializer.Deserialize<List<NetProfile>>(json);
                if (list == null) return new List<NetProfile>();

                // 兼容手工编辑过的文件：补齐可能为 null 的字段
                foreach (NetProfile p in list)
                {
                    if (p == null) continue;
                    if (p.Name == null) p.Name = "";
                    if (p.Adapter == null) p.Adapter = "";
                    if (p.Ip == null) p.Ip = "";
                    if (p.Mask == null) p.Mask = "";
                    if (p.Gateway == null) p.Gateway = "";
                    if (p.Dns == null) p.Dns = new List<string>();
                }
                return list.Where(p => p != null).ToList();
            }
            catch
            {
                // 配置损坏时不让程序起不来
                return new List<NetProfile>();
            }
        }

        public static void Save(List<NetProfile> profiles)
        {
            try
            {
                Directory.CreateDirectory(ProfileDirectory);
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(profiles ?? new List<NetProfile>());
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                throw new IOException("保存配置文件失败：" + FilePath + Environment.NewLine + ex.Message, ex);
            }
        }
    }
}
