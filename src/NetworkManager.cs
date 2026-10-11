using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace NetSwitcher
{
    /// <summary>网卡当前 IPv4 状态快照。</summary>
    public sealed class AdapterState
    {
        public string Ip = "";
        public string Mask = "";
        public string Gateway = "";
        public List<string> Dns = new List<string>();
        public bool DhcpEnabled;
        public string DnsMode = "";

        public bool HasIp { get { return !string.IsNullOrEmpty(Ip) && Ip != "0.0.0.0"; } }

        public string DnsText
        {
            get
            {
                if (Dns.Count == 0) return "无";
                return DhcpEnabled ? string.Join(", ", Dns.ToArray()) + "  (自动)" : string.Join(", ", Dns.ToArray());
            }
        }
    }

    /// <summary>一张网卡的基本信息 + 当前配置。</summary>
    public sealed class AdapterInfo
    {
        public string Name = "";
        public string Description = "";
        public string Type = "";
        public string Status = "";
        public AdapterState State = new AdapterState();

        public override string ToString() { return Name; }

        public string Display
        {
            get
            {
                if (State.HasIp) return Name + "   [" + State.Ip + "]   " + Type;
                return Name + "   [" + Status + "]   " + Type;
            }
        }
    }

    /// <summary>一次配置动作的结果。</summary>
    public sealed class NetResult
    {
        public bool Ok;
        public string Title = "";
        public string Detail = "";
        public string Raw = "";
    }

    /// <summary>
    /// 网络配置读写层：读取用 System.Net.NetworkInformation，
    /// 修改用 netsh（Windows 自带、稳定、对中英文系统都适用）。
    /// </summary>
    public static class NetworkManager
    {
        private static readonly Regex Ipv4Re = new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);

        /// <summary>
        /// netsh 输出使用系统 ANSI 代码页（中文系统为 936），
        /// 不能用 Encoding.Default —— 在 .NET Core 下它返回 UTF-8，会把中文解成乱码。
        /// </summary>
        private static readonly Encoding ConsoleEncoding = GetConsoleEncoding();

        private static Encoding GetConsoleEncoding()
        {
            try
            {
                int cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                if (cp <= 0) cp = 936;
                return Encoding.GetEncoding(cp);
            }
            catch
            {
                return Encoding.Default;
            }
        }

        #region 读取

        public static List<AdapterInfo> GetAdapters()
        {
            var list = new List<AdapterInfo>();
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return list; }

            foreach (NetworkInterface nic in nics)
            {
                var info = new AdapterInfo();
                info.Name = nic.Name;
                info.Description = nic.Description;
                info.Type = TypeText(nic.NetworkInterfaceType);
                info.Status = StatusText(nic.OperationalStatus);
                info.State = ReadState(nic);
                list.Add(info);
            }
            return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static AdapterInfo FindAdapter(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return GetAdapters().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static AdapterState ReadState(NetworkInterface nic)
        {
            var st = new AdapterState();
            if (nic == null) return st;

            try
            {
                IPInterfaceProperties props = nic.GetIPProperties();

                UnicastIPAddressInformation uni = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (uni != null)
                {
                    st.Ip = uni.Address.ToString();
                    if (uni.IPv4Mask != null) st.Mask = uni.IPv4Mask.ToString();
                    else st.Mask = PrefixToMask(uni.PrefixLength);
                }

                GatewayIPAddressInformation gw = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (gw != null) st.Gateway = gw.Address.ToString();

                foreach (System.Net.IPAddress d in props.DnsAddresses)
                {
                    if (d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        st.Dns.Add(d.ToString());
                }

                IPv4InterfaceProperties v4 = props.GetIPv4Properties();
                st.DhcpEnabled = v4 != null && v4.IsDhcpEnabled;
                st.DnsMode = st.DhcpEnabled ? "自动 (DHCP)" : "手动";
            }
            catch
            {
                // 网卡刚断开/正在重配时可能抛异常，保留已取到的部分即可
            }
            return st;
        }

        /// <summary>用 netsh 读取同一张网卡的配置（作为 WMI/API 的兜底来源）。</summary>
        public static AdapterState ReadStateByNetsh(string adapterName)
        {
            string output = RunNetsh("interface ip show config name=\"" + adapterName + "\"", 8000);
            return ParseNetshConfig(output);
        }

        /// <summary>标签与值之间的分隔符：英文冒号或中文全角冒号。</summary>
        private static readonly Regex LabelValueRe =
            new Regex("^(?<label>[^:：]+)[:：](?<value>.*)$", RegexOptions.Compiled);

        private static readonly Regex SubnetMaskRe =
            new Regex(@"\(\s*(?:mask|\u63A9\u7801)\s+((?:\d{1,3}\.){3}\d{1,3})\s*\)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TokenRe =
            new Regex(@"^\s*((?:\d{1,3}\.){3}\d{1,3})\s*$", RegexOptions.Compiled);

        /// <summary>
        /// 解析 netsh 输出。所有行都带缩进，因此不能靠缩进区分；
        /// 改为「标签: 值」逐行识别，并通过标签关键词 + 取值形态判断每一行的含义，
        /// 中英文系统都能用（中文输出的冒号是全角）。
        /// </summary>
        public static AdapterState ParseNetshConfig(string output)
        {
            var st = new AdapterState();
            if (string.IsNullOrEmpty(output)) return st;

            string currentKey = "";
            bool dhcpSeen = false;
            string dhcpValue = "";

            // 同一含义的多行写法，避免硬编码中文字符串在个别编码下失真
            string kIp = "\u5730\u5740";     // 地址
            string kMask = "\u63A9\u7801";   // 掩码
            string kWins = "WINS";
            string[] skipKeys = new[] { "\u8DC3\u70B9", "\u6CE8\u518C", "metric", "Metric", "Register", "suffix" };

            foreach (string raw in output.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Trim().Length == 0) continue;

                Match lv = LabelValueRe.Match(raw);
                int indent = raw.Length - raw.TrimStart().Length;

                if (!lv.Success)
                {
                    // 没有冒号：只可能是续行（DNS 的第二条起，或 "Default Gateway: None" 后的行）
                    Match token = TokenRe.Match(raw);
                    if (!token.Success) continue;

                    if (indent > 0)
                    {
                        if (IsGatewayKey(currentKey)) st.Gateway = token.Groups[1].Value;
                        else if (IsSubnetKey(currentKey, kMask)) st.Mask = token.Groups[1].Value;
                        else if (currentKey.Length > 0) st.Dns.Add(token.Groups[1].Value);
                    }
                    continue;
                }

                string label = lv.Groups["label"].Value.Trim();
                string value = lv.Groups["value"].Value.Trim();
                currentKey = label;

                if (label.ToLowerInvariant().Contains("dhcp") && !label.ToUpperInvariant().Contains("DNS"))
                {
                    dhcpSeen = true;
                    dhcpValue = value;
                    continue;
                }

                if (value.Length == 0) continue;

                // 掩码优先：同行的 (mask x.x.x.x) 比网段 192.168.3.0/24 更可靠
                Match mk = SubnetMaskRe.Match(value);
                if (mk.Success) { st.Mask = mk.Groups[1].Value; continue; }

                string[] tokens = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;
                if (!NetworkManager.IsIpv4(tokens[0])) continue;   // 跳过 "None"、DNS 后缀等非地址行

                if (IsGatewayKey(label))
                {
                    st.Gateway = tokens[0];
                    continue;
                }
                if (IsSubnetKey(label, kMask))
                {
                    st.Mask = tokens[0];
                    continue;
                }
                if (label.ToUpperInvariant().Contains(kWins)) continue;

                // 第 5 条 DNS 服务器会缩进排在下一行，这里先收下
                if (indent > 16 && st.Ip.Length > 0) { st.Dns.Add(tokens[0]); continue; }

                // "IP Address" 行：第一个 token 是地址；同行无掩码时用第二个 token 兜底
                if (label.Contains(kIp))
                {
                    st.Ip = tokens[0];
                    if (tokens.Length > 1 && NetworkManager.IsIpv4(tokens[1])) st.Mask = tokens[1];
                    continue;
                }

                // 标签里带 DNS（中英文均含 "DNS"）
                if (label.ToUpperInvariant().Contains("DNS"))
                {
                    foreach (string t in tokens) if (NetworkManager.IsIpv4(t)) st.Dns.Add(t);
                    continue;
                }

                // 兜底：第一处出现的地址视作本机 IP，其余忽略（如 WINS 之类已在上方排除）
                if (st.Ip.Length == 0 && !skipKeys.Any(s => label.Contains(s))) st.Ip = tokens[0];
            }

            bool dhcpOn = dhcpValue.Length > 0
                && (dhcpValue.Contains("\u542F\u7528")
                    || dhcpValue.StartsWith("Y", StringComparison.OrdinalIgnoreCase)
                    || dhcpValue.StartsWith("E", StringComparison.OrdinalIgnoreCase));
            st.DhcpEnabled = dhcpSeen && dhcpOn;
            st.DnsMode = st.DhcpEnabled ? "自动 (DHCP)" : "手动";
            st.Dns = st.Dns.Distinct().ToList();
            return st;
        }

        private static bool IsGatewayKey(string label)
        {
            return label.ToLowerInvariant().Contains("gateway") || label.Contains("\u7F51\u5173");
        }

        private static bool IsSubnetKey(string label, string maskKey)
        {
            return label.ToLowerInvariant().Contains("subnet") || label.Contains(maskKey);
        }

        #endregion

        #region 写入

        /// <summary>当前状态：优先用系统 API（net1 等），取不到再退回 netsh 解析。</summary>
        public static AdapterState ReadStateSmart(string adapterName)
        {
            try
            {
                AdapterInfo live = FindAdapter(adapterName);
                if (live != null && live.State.HasIp) return live.State;
            }
            catch { }

            AdapterState fallback = ReadStateByNetsh(adapterName);
            if (!fallback.HasIp)
            {
                try
                {
                    AdapterInfo live = FindAdapter(adapterName);
                    if (live != null) return live.State;
                }
                catch { }
            }
            return fallback;
        }

        /// <summary>恢复为自动获取（DHCP）：清掉静态 IP/网关，并打开 DHCP 与自动 DNS。</summary>
        public static NetResult UseDhcp(string adapterName)
        {
            if (string.IsNullOrEmpty(adapterName)) return Fail("未选择网卡");

            var sb = new StringBuilder();
            bool ok = true;
            string q = " name=\"" + adapterName + "\"";

            ok &= RunStep(sb, "ip set address" + q + " source=dhcp");
            ok &= RunStep(sb, "ip set dns" + q + " source=dhcp");

            var st = ReadStateSmart(adapterName);
            bool verified = ok && st.DhcpEnabled;

            return new NetResult
            {
                Ok = verified,
                Title = verified ? "已恢复自动获取 (DHCP)" : "恢复 DHCP 失败",
                Detail = verified
                    ? BuildStateDetail(st)
                    : "系统未确认已切换为自动获取，请检查网卡权限或驱动。" + Environment.NewLine
                      + "当前：" + BuildStateDetail(st) + Environment.NewLine + sb.ToString(),
                Raw = sb.ToString()
            };
        }

        /// <summary>设置为静态 IP / 掩码 / 网关（网关可留空），并按需设置 DNS。</summary>
        public static NetResult ApplyStatic(string adapterName, string ip, string mask, string gateway, IEnumerable<string> dns)
        {
            if (string.IsNullOrEmpty(adapterName)) return Fail("未选择网卡");
            if (!IsIpv4(ip)) return Fail("IP 地址格式不正确：" + ip);
            if (!IsIpv4(mask)) return Fail("子网掩码格式不正确：" + mask);
            if (!string.IsNullOrEmpty(gateway) && !IsIpv4(gateway)) return Fail("网关格式不正确：" + gateway);

            var sb = new StringBuilder();
            bool ok = true;
            string q = " name=\"" + adapterName + "\"";

            string cmd = "ip set address" + q + " static " + ip + " " + mask;
            cmd += string.IsNullOrEmpty(gateway) ? " gateway=none" : " " + gateway;
            ok &= RunStep(sb, cmd);

            var dnsList = (dns ?? Enumerable.Empty<string>())
                .Where(d => IsIpv4(d)).Distinct().ToList();

            if (dnsList.Count > 0)
            {
                ok &= RunStep(sb, "ip set dns" + q + " static " + dnsList[0] + " primary");
                for (int i = 1; i < dnsList.Count; i++)
                    ok &= RunStep(sb, "ip add dns" + q + " " + dnsList[i] + " index=" + (i + 1));
            }
            else
            {
                // 不指定 DNS 就交给 DHCP，避免残留上一次的静态 DNS
                RunStep(sb, "ip set dns" + q + " source=dhcp");
            }

            var after = ReadStateSmart(adapterName);
            bool verified = ok
                && string.Equals(after.Ip, ip, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(gateway) || string.Equals(after.Gateway, gateway, StringComparison.OrdinalIgnoreCase));

            string dnsNote = dnsList.Count == 0
                ? Environment.NewLine + "（未填写 DNS，已设为自动获取）"
                : "";

            return new NetResult
            {
                Ok = verified,
                Title = verified ? "切换完成" : "切换未生效",
                Detail = verified
                    ? BuildStateDetail(after) + dnsNote
                    : ("目标：" + ip + " / " + mask + (string.IsNullOrEmpty(gateway) ? "" : " / " + gateway)
                       + Environment.NewLine + "当前：" + BuildStateDetail(after)
                       + Environment.NewLine + Environment.NewLine + sb.ToString()),
                Raw = sb.ToString()
            };
        }

        /// <summary>只改 DNS。</summary>
        public static NetResult SetDns(string adapterName, IEnumerable<string> dns)
        {
            if (string.IsNullOrEmpty(adapterName)) return Fail("未选择网卡");

            var dnsList = (dns ?? Enumerable.Empty<string>())
                .Where(d => IsIpv4(d)).Distinct().ToList();
            var sb = new StringBuilder();
            bool ok;

            if (dnsList.Count == 0)
            {
                ok = RunStep(sb, "ip set dns name=\"" + adapterName + "\" source=dhcp");
            }
            else
            {
                ok = RunStep(sb, "ip set dns name=\"" + adapterName + "\" static " + dnsList[0] + " primary");
                for (int i = 1; i < dnsList.Count; i++)
                    ok &= RunStep(sb, "ip add dns name=\"" + adapterName + "\" " + dnsList[i] + " index=" + (i + 1));
            }

            return new NetResult
            {
                Ok = ok,
                Title = ok ? "DNS 已更新" : "DNS 更新失败",
                Detail = ok
                    ? ("DNS 服务器：" + (dnsList.Count == 0 ? "自动获取" : string.Join(", ", dnsList.ToArray())))
                    : ("设置 DNS 失败。" + Environment.NewLine + sb.ToString()),
                Raw = sb.ToString()
            };
        }

        #endregion

        #region 内部：执行与校验

        private static bool RunStep(StringBuilder sb, string arguments)
        {
            string output = RunNetsh("interface " + arguments, 20000);
            sb.Append("> netsh interface ").Append(arguments).AppendLine();
            if (!string.IsNullOrEmpty(output))
            {
                sb.AppendLine(output.Trim());
            }
            sb.AppendLine();

            string low = (output ?? "").ToLowerInvariant();
            bool bad = low.Contains("failed")
                    || low.Contains("not accepted")
                    || low.Contains("error")
                    || low.Contains("拒绝访问")
                    || low.Contains("失败")
                    || low.Contains("错误")
                    || low.Contains("找不到")
                    || low.Contains("cannot");
            return !bad;
        }

        private static string RunNetsh(string arguments, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    // 直接读字节再判断编码：不同宿主（控制台/PowerShell/.NET 版本）对
                    // 控制台输出代码页的认定不一致，用固定编码会把中文解成乱码。
                    byte[] outBytes = ReadAllBytes(p.StandardOutput.BaseStream);
                    byte[] errBytes = ReadAllBytes(p.StandardError.BaseStream);

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "命令超时：" + arguments;
                    }

                    string stdout = Decode(outBytes);
                    string stderr = Decode(errBytes);
                    string all = (stdout + Environment.NewLine + stderr).Trim();
                    if (p.ExitCode != 0 && all.Length == 0) all = "netsh 退出码 " + p.ExitCode;
                    return all;
                }
            }
            catch (Exception ex)
            {
                return "无法执行 netsh：" + ex.Message;
            }
        }

        private static byte[] ReadAllBytes(System.IO.Stream stream)
        {
            var buffer = new System.IO.MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        /// <summary>先按 UTF-8 解码，出现替换字符（非法序列）再退回系统 ANSI 代码页。</summary>
        private static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";

            var strictUtf8 = new UTF8Encoding(false, true);
            try
            {
                return strictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                try { return ConsoleEncoding.GetString(bytes); }
                catch { return Encoding.Default.GetString(bytes); }
            }
        }

        private static NetResult Fail(string message)
        {
            return new NetResult { Ok = false, Title = "参数有误", Detail = message };
        }

        private static string BuildStateDetail(AdapterState st)
        {
            if (st == null || !st.HasIp) return "未取到有效 IPv4 地址。";
            var sb = new StringBuilder();
            sb.Append("IP 地址   : ").Append(st.Ip).AppendLine();
            sb.Append("子网掩码 : ").Append(string.IsNullOrEmpty(st.Mask) ? "-" : st.Mask).AppendLine();
            sb.Append("默认网关 : ").Append(string.IsNullOrEmpty(st.Gateway) ? "(无)" : st.Gateway).AppendLine();
            sb.Append("DNS      : ").Append(st.DnsText).AppendLine();
            sb.Append("获取方式 : ").Append(st.DhcpEnabled ? "自动 (DHCP)" : "手动 (静态)");
            return sb.ToString();
        }

        public static bool IsIpv4(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            System.Net.IPAddress addr;
            if (!System.Net.IPAddress.TryParse(text.Trim(), out addr)) return false;
            return addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && addr.ToString() == text.Trim();
        }

        public static List<string> ParseDnsList(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<string>();
            return text.Split(new[] { ',', ';', ' ', '\t', '，', '；', '、' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(IsIpv4)
                .Distinct()
                .ToList();
        }

        public static string PrefixToMask(int prefix)
        {
            if (prefix <= 0) return "0.0.0.0";
            if (prefix >= 32) return "255.255.255.255";
            uint value = uint.MaxValue << (32 - prefix);
            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}",
                (value >> 24) & 0xFF, (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
        }

        public static int MaskToPrefix(string mask)
        {
            System.Net.IPAddress addr;
            if (!System.Net.IPAddress.TryParse(mask, out addr)) return -1;
            byte[] b = addr.GetAddressBytes();
            if (b.Length != 4) return -1;
            uint value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            int count = 0;
            for (int i = 31; i >= 0; i--)
            {
                if ((value & (1u << i)) != 0) count++;
                else break;
            }
            return count;
        }

        private static string TypeText(NetworkInterfaceType t)
        {
            switch (t)
            {
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.GigabitEthernet:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.FastEthernetFx:
                    return "有线";
                case NetworkInterfaceType.Wireless80211: return "无线";
                case NetworkInterfaceType.Loopback: return "回环";
                case NetworkInterfaceType.Tunnel: return "隧道";
                default: return t.ToString();
            }
        }

        private static string StatusText(OperationalStatus s)
        {
            switch (s)
            {
                case OperationalStatus.Up: return "已连接";
                case OperationalStatus.Down: return "未连接";
                case OperationalStatus.Dormant: return "休眠";
                case OperationalStatus.NotPresent: return "不存在";
                case OperationalStatus.LowerLayerDown: return "下层断开";
                default: return s.ToString();
            }
        }

        #endregion
    }
}
