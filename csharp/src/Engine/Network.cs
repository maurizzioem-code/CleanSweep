using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CleanSweep.Engine
{
    public class NetAdapter
    {
        public string Name, Description, Id; public int Index; public bool Up, Wireless; public long Speed;
        public NetworkInterface Nic;
        public string[] Dns => Nic?.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray() ?? new string[0];
        public string Gateway => Nic?.GetIPProperties().GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))?.ToString();
        public UnicastIPAddressInformation IPv4 => Nic?.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
    }
    public class NetTest
    {
        public int? Ping; public int Jitter, Loss; public int? Dns; public string Mode = "ping";
        public override string ToString() =>
            Ping == null ? "No internet response (100% packet loss)." :
            (Mode == "tcp" ? "(ping is blocked on this network - measured with TCP) " : "") + $"Ping {Ping} ms, jitter {Jitter} ms, packet loss {Loss}%, DNS lookup " + (Dns != null ? Dns + " ms" : "failed");
    }
    public class NetOption { public string Key, Name, What; public bool Default; }
    public class DiagRow { public string Area, Result, Status, Tip; }
    public class WifiNet { public string Ssid, Signal, Band; public int Channel; }

    /// <summary>
    /// Network Optimizer. Measures first, explains every option, and only resets things to Windows' defaults -
    /// no "gaming" TCP tweaks, no registry hacks. Ethernet diagnostics read counters and the event log; they change nothing.
    /// </summary>
    public static class Network
    {
        static readonly Regex WirelessName = new Regex(@"Wi-?Fi|Wireless|802\.11|WLAN|Bluetooth", RegexOptions.IgnoreCase);
        static readonly Regex Virtual = new Regex(@"VPN|TAP-|Hyper-V Virtual|vEthernet|VirtualBox|VMware|Loopback|Teredo|isatap|WAN Miniport|Npcap|Tailscale|ZeroTier|WireGuard", RegexOptions.IgnoreCase);

        public static List<NetAdapter> Adapters()
        {
            var list = new List<NetAdapter>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                int idx = -1; try { idx = n.GetIPProperties().GetIPv4Properties()?.Index ?? -1; } catch { }
                list.Add(new NetAdapter
                {
                    Name = n.Name, Description = n.Description, Id = n.Id, Index = idx, Up = n.OperationalStatus == OperationalStatus.Up, Nic = n,
                    Wireless = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || WirelessName.IsMatch(n.Description), Speed = n.Speed,
                });
            }
            return list;
        }
        static bool HasInternetRoute(NetAdapter a) => a.Up && a.Gateway != null;

        public static NetAdapter Wifi() => Adapters().Where(a => a.Wireless && !a.Description.Contains("Bluetooth") && !Virtual.IsMatch(a.Description)).OrderByDescending(a => a.Up).FirstOrDefault();
        /// <summary>The wired adapter carrying internet traffic, else any connected wired one (docks, USB adapters), else any physical wired one.</summary>
        public static NetAdapter Ethernet()
        {
            var wired = Adapters().Where(a => !a.Wireless && a.Nic.NetworkInterfaceType != NetworkInterfaceType.Ppp).ToList();
            return wired.Where(a => !Virtual.IsMatch(a.Description) && !Virtual.IsMatch(a.Name)).OrderByDescending(HasInternetRoute).ThenByDescending(a => a.Up).FirstOrDefault()
                ?? wired.Where(HasInternetRoute).FirstOrDefault(a => !a.Description.Contains("VPN"));
        }
        /// <summary>"Ethernet" if Windows sends internet traffic over the cable (or there's no Wi-Fi card at all), else "Wi-Fi".</summary>
        public static string ActiveKind()
        {
            var w = Wifi(); var e = Ethernet();
            if (e != null && HasInternetRoute(e) && (w == null || !HasInternetRoute(w))) return "Ethernet";
            if (e != null && w == null) return "Ethernet";
            if (e != null && HasInternetRoute(e) && w != null && HasInternetRoute(w)) return Metric(e) <= Metric(w) ? "Ethernet" : "Wi-Fi";
            return "Wi-Fi";
        }
        static int Metric(NetAdapter a)
        {
            var o = Wmi.TryQuery($"SELECT InterfaceMetric FROM MSFT_NetIPInterface WHERE InterfaceIndex={a.Index} AND AddressFamily=2", @"root\StandardCimv2").FirstOrDefault();
            return o != null ? (int)Wmi.Long(o, "InterfaceMetric", 9999) : 9999;
        }
        public static NetAdapter Target(string kind) => kind == "Ethernet" ? Ethernet() : Wifi();

        // ---------------------------------------------------------------- Wi-Fi details (netsh)
        public const string LocationHelp = "Windows needs Location turned on to show Wi-Fi details.\nOpen Settings > Privacy & security > Location, turn on Location services and 'Let desktop apps access your location'.";
        public static bool NeedsLocation(string netshOut) => Regex.IsMatch(netshOut ?? "", "location permission|location services", RegexOptions.IgnoreCase);
        public static Dictionary<string, string> WifiInfo(out bool needsLocation)
        {
            var (_, o) = Cmd.Run("netsh.exe", "wlan show interfaces", 15000);
            needsLocation = NeedsLocation(o);
            return ParseColon(o);
        }
        public static Dictionary<string, string> ParseColon(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in (text ?? "").Split('\n'))
            {
                var m = Regex.Match(l, @"^\s*([^:]+?)\s*:\s*(.+?)\s*$");
                if (m.Success && !d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return d;
        }
        public static string Describe(string kind)
        {
            var a = Target(kind);
            if (kind == "Ethernet")
            {
                if (a == null) return "No Ethernet port was found on this PC.";
                if (!a.Up) return $"Ethernet adapter: {a.Description}\nNo cable connected (or the cable/router port isn't working).";
                long mbps = a.Speed / 1000000;
                string hint = mbps > 0 && mbps <= 100 ? "  - Only 100 Mbps: the cable or router port may be limiting you. Try a Cat5e/Cat6 cable or another port." : "";
                return $"Ethernet adapter: {a.Description}\nLink speed: {SpeedText(a.Speed)}{hint}\nAdapter name: {a.Name}     DNS: {string.Join(", ", a.Dns)}";
            }
            if (a == null) return "No Wi-Fi adapter was found on this PC.";
            var i = WifiInfo(out bool loc);
            if (loc) return LocationHelp;
            if (!a.Up || !i.ContainsKey("SSID")) return $"Wi-Fi adapter: {a.Description}\nNot connected to a Wi-Fi network.";
            string sig = i.TryGetValue("Signal", out var s) ? s : "?"; int.TryParse(Regex.Replace(sig, @"[^\d]", ""), out int pct);
            string q = pct >= 80 ? "Excellent" : pct >= 60 ? "Good" : pct >= 40 ? "Fair - moving closer to the router will help" : "Weak - move closer to the router";
            string G(string k) => i.TryGetValue(k, out var v) ? v : "-";
            return $"Network: {G("SSID")}     Signal: {sig} ({q})\nBand: {G("Band")}     Channel: {G("Channel")}     Type: {G("Radio type")}\n" +
                   $"Speed: {G("Receive rate (Mbps)")} Mbps down / {G("Transmit rate (Mbps)")} Mbps up (link speed)     DNS: {string.Join(", ", a.Dns)}";
        }
        public static string SpeedText(long bps) => bps >= 1000000000 ? $"{bps / 1e9:0.#} Gbps" : bps > 0 ? $"{bps / 1000000} Mbps" : "unknown";

        public static List<WifiNet> Nearby(out bool needsLocation)
        {
            var (_, o) = Cmd.Run("netsh.exe", "wlan show networks mode=bssid", 15000);
            needsLocation = NeedsLocation(o);
            return ParseNearby(o);
        }
        public static List<WifiNet> ParseNearby(string text)
        {
            var nets = new List<WifiNet>(); string ssid = ""; WifiNet cur = null;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r'); Match m;
                if ((m = Regex.Match(line, @"^SSID \d+ : (.*)$")).Success) { ssid = m.Groups[1].Value.Trim(); if (ssid.Length == 0) ssid = "(hidden)"; }
                else if (Regex.IsMatch(line, @"^\s+BSSID \d+")) { cur = new WifiNet { Ssid = ssid, Signal = "", Band = "" }; nets.Add(cur); }
                else if (cur != null && (m = Regex.Match(line, @"^\s+Signal\s*:\s*(.+)$")).Success) cur.Signal = m.Groups[1].Value.Trim();
                else if (cur != null && (m = Regex.Match(line, @"^\s+Channel\s*:\s*(\d+)")).Success) cur.Channel = int.Parse(m.Groups[1].Value);
                else if (cur != null && (m = Regex.Match(line, @"^\s+Band\s*:\s*(.+)$")).Success) cur.Band = m.Groups[1].Value.Trim();
            }
            return nets;
        }

        // ---------------------------------------------------------------- connection test
        public static int? PingMs(string host, bool tcp = false, int timeout = 1000)
        {
            if (tcp)
            {
                using (var c = new TcpClient())
                {
                    var sw = Stopwatch.StartNew();
                    try { var t = c.ConnectAsync(host, 443); if (t.Wait(1500) && c.Connected) return (int)sw.ElapsedMilliseconds; } catch { }
                    return null;
                }
            }
            try { using (var p = new Ping()) { var r = p.Send(host, timeout); if (r.Status == IPStatus.Success) return (int)r.RoundtripTime; } } catch { }
            return null;
        }
        public static NetTest Test(CancellationToken ct, string host = "1.1.1.1", int count = 10)
        {
            var t = new NetTest(); var times = new List<int>(); int lost = 0;
            for (int n = 0; n < count; n++) { ct.ThrowIfCancellationRequested(); var ms = PingMs(host); if (ms != null) times.Add(ms.Value); else lost++; }
            if (times.Count == 0)   // ping blocked? measure with TCP instead
            {
                t.Mode = "tcp"; lost = 0;
                for (int n = 0; n < count; n++) { ct.ThrowIfCancellationRequested(); var ms = PingMs(host, true); if (ms != null) times.Add(ms.Value); else lost++; }
            }
            t.Dns = DnsMs();
            if (times.Count > 0) t.Ping = (int)times.Average();
            t.Jitter = times.Count > 1 ? (int)Enumerable.Range(1, times.Count - 1).Average(i => Math.Abs(times[i] - times[i - 1])) : 0;
            t.Loss = lost * 100 / count;
            return t;
        }
        public static int? DnsMs(string host = "www.microsoft.com")
        {
            try { Cmd.Run("ipconfig.exe", "/flushdns", 10000); var sw = Stopwatch.StartNew(); var t = Dns.GetHostAddressesAsync(host); if (!t.Wait(5000) || t.Result.Length == 0) return null; return (int)sw.ElapsedMilliseconds; }
            catch { return null; }
        }

        // ---------------------------------------------------------------- optimizations
        public static List<NetOption> Options(string kind)
        {
            var l = new List<NetOption>
            {
                new NetOption { Key = "dns-flush", Name = "Flush DNS cache", Default = true, What = "Clears stored website addresses so stale entries can't slow or break loading." },
                new NetOption { Key = "arp", Name = "Clear ARP cache", Default = true, What = "Clears the local device address table, which fixes some router connection glitches." },
                new NetOption { Key = "renew", Name = "Renew IP address", Default = true, What = "Asks your router for a fresh IP address. The connection drops for a few seconds." },
            };
            if (kind == "Ethernet") l.Add(new NetOption { Key = "eth-power", Name = "Ethernet power saving off", What = "Optional. Only if the cable connection drops out: turns off Energy Efficient / Green Ethernet. Reconnects briefly." });
            else l.Add(new NetOption { Key = "wifi-power", Name = "Wi-Fi power: max performance", What = "Optional. Only if you get lag spikes or drop-outs: stops Windows power-saving the Wi-Fi card when plugged in." });
            l.Add(new NetOption { Key = "autotune", Name = "Reset TCP auto-tuning to normal", Default = true, What = "Restores the Windows default only if another tool changed it. No other TCP tweaks are applied." });
            l.Add(new NetOption { Key = "stack", Name = "Reset network stack (Winsock/IP)", What = "Deep repair for broken connections. Needs a restart. Only use if your internet is misbehaving." });
            return l;
        }
        public static readonly string[] DnsChoices = { "Keep current", "Cloudflare (1.1.1.1)", "Google (8.8.8.8)", "Automatic (from router)" };
        /// <summary>Self-test: record the commands instead of running them (they would drop the connection).</summary>
        public static List<string> DryRun;

        static (int, string) Do(string exe, string args) { if (DryRun != null) { DryRun.Add(exe + " " + args); return (0, ""); } return Cmd.Run(exe, args); }
        static (int, string) Ps(string script) { if (DryRun != null) { DryRun.Add("powershell: " + script); return (0, ""); } return Cmd.PowerShell(script); }

        /// <summary>Applies the chosen options in a safe order; returns what was done. needsRestart is set for the stack reset.</summary>
        public static List<string> Apply(string kind, IEnumerable<string> keys, int dnsChoice, Action<string> progress, out bool needsRestart)
        {
            var done = new List<string>(); var k = new HashSet<string>(keys); needsRestart = false;
            var a = Target(kind); string name = a?.Name ?? "";
            void Step(string text, Func<bool> act) { progress?.Invoke(text + "..."); try { if (act()) done.Add(text); else done.Add(text + " (Windows reported a problem)"); } catch (Exception e) { done.Add($"{text} (failed: {e.Message})"); } }
            if (k.Contains("dns-flush")) Step("Flushed DNS cache", () => Do("ipconfig.exe", "/flushdns").Item1 == 0);
            if (k.Contains("arp")) Step("Cleared ARP cache", () => Do("netsh.exe", "interface ip delete arpcache").Item1 == 0);
            if (k.Contains("wifi-power")) Step("Wi-Fi power set to max performance when plugged in", () =>
            {
                const string sub = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", set = "12bbebe6-58d6-4636-95bb-3217ef867c1a";
                Do("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT {sub} {set} 0"); Do("powercfg.exe", $"/setdcvalueindex SCHEME_CURRENT {sub} {set} 1");
                return Do("powercfg.exe", "/setactive SCHEME_CURRENT").Item1 == 0;
            });
            if (k.Contains("eth-power") && a != null) Step("Turned off Ethernet power saving", () =>
            {
                // Driver setting names differ between Intel, Realtek, Killer etc., so match the common ones
                var (code, o) = Ps($"$n='{name.Replace("'", "''")}'; $c=$false; Get-NetAdapterAdvancedProperty -Name $n -EA Ignore | ? {{ $_.DisplayName -match 'Energy.?Efficient|Green Ethernet|Power Saving Mode|Advanced EEE|Gigabit Lite|Ultra Low Power|System Idle Power Saver' }} | % {{ $off = $_.ValidDisplayValues | ? {{ $_ -match '^(Disabled|Off)$' }} | select -First 1; if ($off -and $_.DisplayValue -ne $off) {{ Set-NetAdapterAdvancedProperty -Name $n -DisplayName $_.DisplayName -DisplayValue $off -NoRestart -EA Ignore; $c=$true }} }}; if ($c) {{ Restart-NetAdapter -Name $n -Confirm:$false -EA Ignore }}");
                return code == 0;
            });
            if (k.Contains("autotune")) Step("TCP auto-tuning set to the Windows default (normal)", () => Do("netsh.exe", "int tcp set global autotuninglevel=normal").Item1 == 0);
            if (a != null && a.Index > 0)
            {
                if (dnsChoice == 1) Step("DNS switched to Cloudflare (1.1.1.1)", () => Ps($"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses ('1.1.1.1','1.0.0.1') -EA Stop").Item1 == 0);
                if (dnsChoice == 2) Step("DNS switched to Google (8.8.8.8)", () => Ps($"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses ('8.8.8.8','8.8.4.4') -EA Stop").Item1 == 0);
                if (dnsChoice == 3) Step("DNS set back to automatic (from the router)", () => Ps($"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ResetServerAddresses -EA Stop").Item1 == 0);
            }
            if (k.Contains("renew") && a != null) Step("Renewed IP address", () => { Do("ipconfig.exe", $"/release \"{name}\""); if (DryRun == null) Thread.Sleep(1000); return Do("ipconfig.exe", $"/renew \"{name}\"").Item1 == 0; });
            if (k.Contains("stack")) { Step("Reset network stack (restart needed)", () => { Do("netsh.exe", "winsock reset"); return Do("netsh.exe", "int ip reset").Item1 == 0; }); needsRestart = true; }
            return done;
        }
        /// <summary>Waits up to ~15 s for the internet to answer again after an optimization.</summary>
        public static void WaitForInternet(CancellationToken ct)
        {
            for (int n = 0; n < 20 && !ct.IsCancellationRequested; n++) { if (PingMs("1.1.1.1") != null || PingMs("1.1.1.1", true) != null) return; Thread.Sleep(700); }
        }

        // ---------------------------------------------------------------- Ethernet diagnostics (read-only)
        public static List<DiagRow> EthernetDiagnostics(Action<string> progress, CancellationToken ct)
        {
            var rows = new List<DiagRow>();
            void Row(string area, string result, string status, string tip = "") => rows.Add(new DiagRow { Area = area, Result = result, Status = status, Tip = tip });
            void P(string t) { progress?.Invoke($"Diagnosing: {t}..."); ct.ThrowIfCancellationRequested(); }
            var a = Ethernet();
            if (a == null) { Row("Ethernet adapter", "No Ethernet port found", "Problem", "This PC has no wired network adapter, or its driver isn't installed. A USB Ethernet adapter can add one."); return rows; }

            // 1. Adapter and driver
            P("adapter and driver");
            var drv = Wmi.TryQuery($"SELECT DriverVersion, DriverDate, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceClass='NET' AND Description='{a.Description.Replace("'", "\\'")}'").FirstOrDefault();
            DateTime? date = drv != null ? Wmi.Date(drv, "DriverDate") : null; string ver = drv != null ? Wmi.Str(drv, "DriverVersion") : "";
            string prov = drv != null ? Wmi.Str(drv, "DriverProviderName") : "";
            string drvText = $"{a.Description}" + (ver.Length > 0 ? $", driver {ver}" : "") + (date != null ? $" ({date:MMM yyyy})" : "");
            // Windows' built-in drivers are always stamped June 2006, so their date says nothing about age
            bool inbox = prov.StartsWith("Microsoft") || (date?.Year == 2006 && date?.Month == 6);
            int? age = date != null ? (int?)((DateTime.Now - date.Value).TotalDays / 365) : null;
            if (inbox) Row("Adapter driver", drvText + " - built into Windows, updated by Windows Update", "OK");
            else if (age >= 3) Row("Adapter driver", drvText, "Warning", $"The driver is about {age} years old. Check your laptop maker's support site or Windows Update > Advanced options > Optional updates for a newer network driver.");
            else Row("Adapter driver", drvText, "OK");

            // 2. Cable / link
            P("cable and link");
            if (!a.Up)
            {
                Row("Cable connection", "No link detected", "Problem", "Check the cable is clicked in at both ends, try a different cable and a different router port. The port lights should blink when connected.");
                return rows;
            }
            Row("Cable connection", "Connected", "OK");
            long mbps = a.Speed / 1000000;
            var adv = AdvancedProps(a.Name);
            var sd = adv.FirstOrDefault(p => Regex.IsMatch(p.Name, "Speed.*Duplex|Link Speed|Connection Type", RegexOptions.IgnoreCase));
            int? max = null;
            if (sd != null)
            {
                var speeds = sd.Valid.Select(v => Regex.Match(v, @"(\d+(?:\.\d+)?)\s*(G|M)bps", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * (m.Groups[2].Value.ToUpper() == "G" ? 1000 : 1)).ToList();
                if (speeds.Count > 0) max = (int)speeds.Max();
            }
            string sp = SpeedText(a.Speed) + (max != null ? $" (adapter supports up to {(max >= 1000 ? max / 1000.0 + " Gbps" : max + " Mbps")})" : "");
            if (max != null && mbps < max && mbps <= 100) Row("Link speed", sp, "Problem", "Your connection negotiated far below what the adapter supports. Usually a damaged or old (Cat5) cable, a bent connector pin, or a 100 Mbps router/switch port. Try a Cat5e or Cat6 cable and another port.");
            else if (max != null && mbps < max) Row("Link speed", sp, "Warning", "Running below the adapter's maximum. This is normal if your router or switch port is slower; otherwise try another cable or port.");
            else Row("Link speed", sp, "OK");
            if (sd != null)
            {
                if (Regex.IsMatch(sd.Value, "Auto", RegexOptions.IgnoreCase)) Row("Speed & Duplex setting", sd.Value, "OK");
                else Row("Speed & Duplex setting", $"Forced to '{sd.Value}'", "Warning", "A forced speed can mismatch the router. Set it back to Auto Negotiation in Device Manager > Network adapters > your adapter > Advanced.");
            }

            // 3. Error counters (bad cables and interference show up here)
            P("packet errors");
            try
            {
                var st = a.Nic.GetIPStatistics();
                double pk = st.UnicastPacketsReceived + st.NonUnicastPacketsReceived + st.UnicastPacketsSent + st.NonUnicastPacketsSent;
                double err = st.IncomingPacketsWithErrors + st.OutgoingPacketsWithErrors, disc = st.IncomingPacketsDiscarded + st.OutgoingPacketsDiscarded;
                double rate = pk > 0 ? err / pk * 100 : 0;
                string txt = $"{pk:N0} packets, {err:N0} errors ({rate:N3}%), {disc:N0} discarded since the adapter started";
                if (rate >= 0.1) Row("Packet errors", txt, "Problem", "A high error rate almost always means a faulty cable, connector or port. Replace the cable first.");
                else if (err > 0) Row("Packet errors", txt, "Warning", "A few errors is normal-ish; if the number keeps climbing, try another cable.");
                else Row("Packet errors", txt, "OK");
            }
            catch { }

            // 4. Link drops in the last 7 days (driver event log entries)
            P("link drops");
            int drops = 0;
            try
            {
                var since = DateTime.Now.AddDays(-7);
                using (var log = new EventLog("System"))
                {
                    var entries = log.Entries; int n = entries.Count;
                    for (int i = n - 1, seen = 0; i >= 0 && seen < 5000; i--, seen++)
                    {
                        var e = entries[i]; if (e.TimeGenerated < since) break;
                        if (Regex.IsMatch(e.Source, @"e1\w*express|e1i|e2f|rt\w*64|rtcx|Netwtw|killer|igc|b57|bxvbd|l1c|athr|mlx", RegexOptions.IgnoreCase) &&
                            Regex.IsMatch(e.Message ?? "", @"link.*(down|disconnect)|disconnected|lost", RegexOptions.IgnoreCase)) drops++;
                    }
                }
            }
            catch { }
            if (drops >= 5) Row("Link drops (7 days)", $"{drops} disconnects", "Problem", "The cable connection keeps dropping. Try another cable/port, and turn off Ethernet power saving with Optimize.");
            else if (drops > 0) Row("Link drops (7 days)", $"{drops} disconnects", "Warning", "Occasional drops can be sleep/unplugging. If you didn't cause them, check the cable.");
            else Row("Link drops (7 days)", "None recorded", "OK");

            // 5. IP configuration
            P("IP address");
            var ip = a.IPv4; string gw = a.Gateway;
            if (ip == null) Row("IP address", "None", "Problem", "Windows has no IPv4 address. Try Renew IP address in Optimize, or restart the router.");
            else if (ip.Address.ToString().StartsWith("169.254.")) Row("IP address", ip.Address + " (self-assigned)", "Problem", "The router didn't give this PC an address (DHCP failed). Restart the router, then use Renew IP address.");
            else
            {
                bool dhcp = false; try { dhcp = a.Nic.GetIPProperties().GetIPv4Properties().IsDhcpEnabled; } catch { }
                Row("IP address", $"{ip.Address}/{ip.PrefixLength} via {(dhcp ? "DHCP (automatic)" : "manual setting")}", "OK");
            }
            bool v6 = a.Nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal && !IPAddress.IsLoopback(u.Address));
            Row("IPv6", v6 ? "Available" : "Not available (IPv4 only)", "Info");

            // 6. Router latency - on a cable this should be about 1 ms
            if (gw != null)
            {
                P("router latency");
                var t = new List<int>(); int lost = 0;
                for (int n = 0; n < 10; n++) { ct.ThrowIfCancellationRequested(); var ms = PingMs(gw); if (ms != null) t.Add(ms.Value); else lost++; }
                if (t.Count == 0) Row($"Router ({gw})", "No reply", "Warning", "Some routers ignore pings. If websites also fail, restart the router.");
                else
                {
                    int avg = (int)t.Average(), mx = t.Max(); string txt = $"Average {avg} ms, worst {mx} ms, loss {lost * 10}%";
                    if (lost > 0 || avg > 5) Row($"Router ({gw})", txt, "Problem", "A wired link to the router should be about 1 ms with no loss. Suspect the cable, a switch in between, or an overloaded router.");
                    else if (mx > 10) Row($"Router ({gw})", txt, "Warning", "Occasional spikes. Could be router load or a background download.");
                    else Row($"Router ({gw})", txt, "OK");
                }
            }
            else Row("Router", "No default gateway", "Problem", "Windows doesn't know where the router is. Renew IP address or restart the router.");

            // 7. DNS
            P("DNS");
            var dms = DnsMs(); string dnsList = string.Join(", ", a.Dns);
            if (dms == null) Row("DNS lookup", "Failed using " + dnsList, "Problem", "Websites can't be found. Switch DNS to Cloudflare or Automatic and Optimize.");
            else if (dms > 150) Row("DNS lookup", $"{dms} ms using {dnsList}", "Warning", "Slow DNS makes every new website feel slow. Try Cloudflare in the DNS server box, then Optimize.");
            else Row("DNS lookup", $"{dms} ms using {dnsList}", "OK");

            // 8. Internet
            P("internet");
            var it = Test(ct); string how = it.Mode == "tcp" ? "TCP connect (ping blocked)" : "Ping";
            if (it.Ping != null)
            {
                string txt = $"{how} {it.Ping} ms, jitter {it.Jitter} ms, loss {it.Loss}%";
                if (it.Loss >= 20 || it.Ping > 100) Row("Internet", txt, "Problem", "If the router checks above are OK, this usually points to your internet provider or modem.");
                else if (it.Loss > 0 || it.Jitter > 20 || it.Ping > 50) Row("Internet", txt, "Warning", "Some lag or jitter. Can be busy network, provider congestion, or other devices downloading.");
                else Row("Internet", txt, "OK");
            }
            else Row("Internet", "No reply from 1.1.1.1", "Problem", "The internet isn't reachable. Restart the modem/router; if it continues, contact your provider.");
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://www.msftconnecttest.com/connecttest.txt"); req.Timeout = 6000;
                using (var resp = req.GetResponse()) using (var rd = new System.IO.StreamReader(resp.GetResponseStream()))
                    if (rd.ReadToEnd().Contains("Microsoft Connect Test")) Row("Web access", "Working", "OK");
                    else Row("Web access", "Redirected", "Warning", "Something is intercepting web traffic (a sign-in page, filter or proxy).");
            }
            catch { Row("Web access", "Failed", "Problem", "Web pages can't load. Check proxy/VPN settings or firewall software."); }

            // 9. MTU - largest packet that passes without being split
            P("packet size (MTU)");
            int? best = null; int lo = 1200, hi = 1472;
            bool Fits(int size) { try { using (var p = new Ping()) return p.Send("1.1.1.1", 1500, new byte[size], new PingOptions(64, true)).Status == IPStatus.Success; } catch { return false; } }
            if (Fits(lo)) while (lo <= hi) { ct.ThrowIfCancellationRequested(); int mid = (lo + hi) / 2; if (Fits(mid)) { best = mid; lo = mid + 1; } else hi = mid - 1; }
            if (best != null) { int mtu = best.Value + 28; if (mtu >= 1500) Row("MTU", "1500 (standard)", "OK"); else Row("MTU", mtu.ToString(), "Info", "Your connection carries slightly smaller packets than standard (common with DSL/PPPoE or VPNs). Windows usually handles this automatically."); }
            else Row("MTU", "Could not measure", "Info");

            // 10. Is Windows actually using the cable?
            P("route preference");
            if (ActiveKind() == "Ethernet") Row("Internet traffic uses", "Ethernet", "OK");
            else { var w = Wifi(); Row("Internet traffic uses", $"{w?.Name ?? "another adapter"} (not Ethernet)", "Warning", "Windows is sending traffic over Wi-Fi instead of the cable. Turn off Wi-Fi or VPN while wired, or lower the Ethernet interface metric."); }

            // 11. Power saving still enabled?
            var ps = adv.Where(p => Regex.IsMatch(p.Name, "Energy.?Efficient|Green Ethernet|Power Saving Mode|Advanced EEE", RegexOptions.IgnoreCase) && !Regex.IsMatch(p.Value, "^(Disabled|Off)$", RegexOptions.IgnoreCase)).ToList();
            if (ps.Count > 0) Row("Ethernet power saving", "On: " + string.Join(", ", ps.Select(p => p.Name)), "Warning", "Can add small delays or drops. Tick 'Ethernet power saving off' and click Optimize.");
            else Row("Ethernet power saving", "Off", "OK");
            return rows;
        }

        public class AdvProp { public string Name, Value; public string[] Valid = new string[0]; }
        /// <summary>Driver "Advanced" settings (Speed & Duplex, Energy Efficient Ethernet...) via the NetAdapter WMI class.</summary>
        public static List<AdvProp> AdvancedProps(string adapterName)
        {
            var l = new List<AdvProp>();
            foreach (var o in Wmi.TryQuery($"SELECT DisplayName, DisplayValue, ValidDisplayValues FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE Name='{adapterName.Replace("'", "\\'")}'", @"root\StandardCimv2"))
                l.Add(new AdvProp { Name = Wmi.Str(o, "DisplayName"), Value = Wmi.Str(o, "DisplayValue"), Valid = Wmi.Prop(o, "ValidDisplayValues") as string[] ?? new string[0] });
            return l;
        }
    }
}
