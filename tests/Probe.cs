using System;
using System.Text;
using NetSwitcher;

namespace NetSwitcherProbe
{
    internal static class Probe
    {
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            int failures = 0;

            Console.WriteLine("=== 1. 枚举网卡（只读）===");
            var adapters = NetworkManager.GetAdapters();
            Console.WriteLine("网卡数量: " + adapters.Count);
            foreach (AdapterInfo a in adapters)
            {
                Console.WriteLine(string.Format("  - {0} | {1} | {2} | IP={3} | 掩码={4} | 网关={5} | DNS={6} | DHCP={7} | {8}",
                    a.Name, a.Type, a.Status,
                    a.State.Ip, a.State.Mask, a.State.Gateway, a.State.DnsText, a.State.DhcpEnabled, a.Description));
            }
            if (adapters.Count == 0) { Console.WriteLine("  [FAIL] 未枚举到任何网卡"); failures++; }

            Console.WriteLine();
            Console.WriteLine("=== 2. netsh 读取 + 解析（只读）===");
            foreach (AdapterInfo a in adapters)
            {
                if (a.Type == "回环") continue;
                AdapterState st = NetworkManager.ReadStateByNetsh(a.Name);
                Console.WriteLine("  " + a.Name + " -> IP=" + st.Ip + " 掩码=" + st.Mask
                    + " 网关=" + st.Gateway + " DNS=" + st.DnsText + " DHCP=" + st.DhcpEnabled);
                if (st.Ip != a.State.Ip)
                {
                    Console.WriteLine("  [WARN] netsh 解析结果与 API 结果不一致（API=" + a.State.Ip + "）");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== 3. 掩码换算 ===");
            Check(ref failures, "PrefixToMask(24)", NetworkManager.PrefixToMask(24), "255.255.255.0");
            Check(ref failures, "PrefixToMask(16)", NetworkManager.PrefixToMask(16), "255.255.0.0");
            Check(ref failures, "PrefixToMask(30)", NetworkManager.PrefixToMask(30), "255.255.255.252");
            Check(ref failures, "PrefixToMask(8)", NetworkManager.PrefixToMask(8), "255.0.0.0");
            Check(ref failures, "MaskToPrefix(255.255.255.0)", NetworkManager.MaskToPrefix("255.255.255.0").ToString(), "24");

            Console.WriteLine();
            Console.WriteLine("=== 4. IP / DNS 校验 ===");
            Check(ref failures, "IsIpv4(192.168.1.10)", NetworkManager.IsIpv4("192.168.1.10").ToString(), "True");
            Check(ref failures, "IsIpv4(192.168.1.999)", NetworkManager.IsIpv4("192.168.1.999").ToString(), "False");
            Check(ref failures, "IsIpv4(空)", NetworkManager.IsIpv4("").ToString(), "False");
            var dns = NetworkManager.ParseDnsList("8.8.8.8, 114.114.114.114；1.1.1.1 坏值");
            Console.WriteLine("  解析 DNS -> " + string.Join(" | ", dns.ToArray()));
            Check(ref failures, "ParseDnsList 数量", dns.Count.ToString(), "3");

            Console.WriteLine();
            Console.WriteLine("=== 5. 解析固定样例（中/英文 netsh 输出）===");
            string zh = "\r\n接口 \"net1\" 的配置\r\n    DHCP 已启用:                          否\r\n"
                + "    IP 地址:                           192.168.3.217\r\n"
                + "    子网前缀:                        192.168.3.0/24 (掩码 255.255.255.0)\r\n"
                + "    默认网关:                         192.168.3.1\r\n"
                + "    网关跃点数:                       256\r\n"
                + "    InterfaceMetric:                      20\r\n"
                + "    静态配置的 DNS 服务器:            192.168.3.6\r\n"
                + "    用哪个前缀注册:                   只是主要\r\n"
                + "    静态配置的 WINS 服务器:           无\r\n";
            AdapterState z = NetworkManager.ParseNetshConfig(zh);
            Check(ref failures, "中文样例 IP", z.Ip, "192.168.3.217");
            Check(ref failures, "中文样例 掩码", z.Mask, "255.255.255.0");
            Check(ref failures, "中文样例 网关", z.Gateway, "192.168.3.1");
            Check(ref failures, "中文样例 DNS", string.Join(",", z.Dns.ToArray()), "192.168.3.6");
            Check(ref failures, "中文样例 DHCP", z.DhcpEnabled.ToString(), "False");

            string en = "\r\nConfiguration for interface \"Ethernet\"\r\n"
                + "    DHCP enabled:                         Yes\r\n"
                + "    IP Address:                           10.0.0.8\r\n"
                + "    Subnet Prefix:                        10.0.0.0/8 (mask 255.0.0.0)\r\n"
                + "    Default Gateway:                      10.0.0.1\r\n"
                + "    Gateway Metric:                       0\r\n"
                + "    InterfaceMetric:                      25\r\n"
                + "    DNS servers configured through DHCP:  8.8.8.8\r\n"
                + "                                          1.1.1.1\r\n"
                + "    Register with which suffix:           Primary only\r\n";
            AdapterState e = NetworkManager.ParseNetshConfig(en);
            Check(ref failures, "英文样例 IP", e.Ip, "10.0.0.8");
            Check(ref failures, "英文样例 掩码", e.Mask, "255.0.0.0");
            Check(ref failures, "英文样例 网关", e.Gateway, "10.0.0.1");
            Check(ref failures, "英文样例 DNS", string.Join(",", e.Dns.ToArray()), "8.8.8.8,1.1.1.1");
            Check(ref failures, "英文样例 DHCP", e.DhcpEnabled.ToString(), "True");

            string none = "\r\nConfiguration for interface \"WLAN\"\r\n"
                + "    DHCP enabled:                         Yes\r\n"
                + "    IP Address:                           169.254.1.1\r\n"
                + "    Subnet Prefix:                        169.254.0.0/16 (mask 255.255.0.0)\r\n"
                + "    Default Gateway:                      None\r\n"
                + "    Gateway Metric:                       0\r\n";
            AdapterState n = NetworkManager.ParseNetshConfig(none);
            Check(ref failures, "无网关样例 IP", n.Ip, "169.254.1.1");
            Check(ref failures, "无网关样例 网关", n.Gateway, "");
            Check(ref failures, "无网关样例 掩码", n.Mask, "255.255.0.0");

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "全部通过 ✔" : ("失败项: " + failures));
            return failures == 0 ? 0 : 1;
        }

        private static void Check(ref int failures, string label, string actual, string expected)
        {
            bool ok = actual == expected;
            if (!ok) failures++;
            Console.WriteLine((ok ? "  [OK]   " : "  [FAIL] ") + label + " = " + actual + (ok ? "" : " (期望 " + expected + ")"));
        }
    }
}
