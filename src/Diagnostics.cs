using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BrowserProxyDoctor {
    public sealed class Finding {
        public string Level { get; set; } public string Item { get; set; } public string Detail { get; set; } public string Suggestion { get; set; }
        public Finding(string level,string item,string detail,string suggestion) { Level=level; Item=item; Detail=detail; Suggestion=suggestion; }
    }
    public sealed class PolicyRow {
        public string Browser { get; set; } public string Source { get; set; } public string Name { get; set; } public string Value { get; set; }
    }
    public sealed class ExtensionRow {
        public string Browser { get; set; } public string Profile { get; set; } public string Name { get; set; }
        public string State { get; set; } public string Proxy { get; set; } public string Id { get; set; }
    }
    public sealed class BrowserProcess {
        public string Browser { get; set; } public int Pid { get; set; } public string ProxySwitches { get; set; }
    }
    public sealed class Snapshot {
        public string Generated { get; set; } public string ExpectedEndpoint { get; set; } public string PortState { get; set; }
        public ProxySettings Actual { get; set; } public string RegistryEnabled { get; set; } public string RegistryServer { get; set; }
        public List<Finding> Findings { get; set; } public List<PolicyRow> Policies { get; set; }
        public List<ExtensionRow> Extensions { get; set; } public List<BrowserProcess> Processes { get; set; }
        public Dictionary<string,int> ProxyConnections { get; set; }
        public List<string> ReadErrors { get; set; }
        public Snapshot() {
            Findings=new List<Finding>(); Policies=new List<PolicyRow>(); Extensions=new List<ExtensionRow>();
            Processes=new List<BrowserProcess>(); ProxyConnections=new Dictionary<string,int>(); ReadErrors=new List<string>();
        }
    }

    public static class Diagnostics {
        public const string InternetKey=@"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        public static async Task<Snapshot> Collect(string host,int port) {
            string endpoint=Network.Endpoint(host,port);
            var s=await Task.Run(() => ScanLocal(endpoint,port));
            s.PortState=await Network.CheckPort(host,port);
            Evaluate(s);
            return s;
        }
        static Snapshot ScanLocal(string endpoint,int port) {
            var s=new Snapshot { Generated=DateTimeOffset.Now.ToString("o"), ExpectedEndpoint=endpoint, Actual=NativeProxy.Read() };
            using(var key=Registry.CurrentUser.OpenSubKey(InternetKey)) {
                s.RegistryEnabled=Convert.ToString(key==null ? null : key.GetValue("ProxyEnable"));
                s.RegistryServer=Convert.ToString(key==null ? null : key.GetValue("ProxyServer"));
            }
            ScanPolicies(s);
            ScanProcesses(s);
            ScanExtensions(s,"Chrome",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Google\Chrome\User Data"));
            ScanExtensions(s,"Edge",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Microsoft\Edge\User Data"));
            try {
                var connections=TcpTable.BrowserConnections(new HashSet<int>(s.Processes.Select(p=>p.Pid)),port);
                foreach(string browser in new[]{"Chrome","Edge"})
                    s.ProxyConnections[browser]=s.Processes.Where(p=>p.Browser==browser && connections.ContainsKey(p.Pid)).Sum(p=>connections[p.Pid]);
            } catch(Exception e) { s.ReadErrors.Add("TCP 连接表："+e.Message); }
            return s;
        }
        static void ScanPolicies(Snapshot s) {
            foreach(string browser in new[]{"Chrome","Edge"})
            foreach(var hive in new[]{RegistryHive.LocalMachine,RegistryHive.CurrentUser})
            foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
            foreach(string sub in new[]{"",@"\Recommended"}) {
                string source=(hive==RegistryHive.LocalMachine ? "HKLM" : "HKCU")+" / "+(view==RegistryView.Registry64 ? "64 位" : "32 位")+(sub=="" ? " / 强制" : " / 建议");
                try {
                    using(var root=RegistryKey.OpenBaseKey(hive,view))
                    using(var key=root.OpenSubKey(Policies.KeyPath(browser)+sub)) {
                        if(key==null) continue;
                        foreach(string name in key.GetValueNames()) {
                            if(!name.StartsWith("Proxy",StringComparison.OrdinalIgnoreCase)) continue;
                            s.Policies.Add(new PolicyRow {Browser=browser,Source=source,Name=name,Value=Convert.ToString(key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames))});
                        }
                    }
                } catch(Exception e) { s.ReadErrors.Add(browser+" 策略 "+source+"："+e.Message); }
            }
        }
        static void ScanProcesses(Snapshot s) {
            try {
                var options=new EnumerationOptions { Timeout=TimeSpan.FromSeconds(5), ReturnImmediately=false };
                using(var query=new ManagementObjectSearcher("root\\CIMV2", "SELECT Name,ProcessId,CommandLine FROM Win32_Process WHERE Name='chrome.exe' OR Name='msedge.exe'",options))
                using(var rows=query.Get()) foreach(ManagementObject row in rows) using(row) {
                    string cmd=Convert.ToString(row["CommandLine"]);
                    string switches=ExtractProxySwitches(cmd);
                    s.Processes.Add(new BrowserProcess { Browser=Convert.ToString(row["Name"])=="chrome.exe" ? "Chrome" : "Edge", Pid=Convert.ToInt32(row["ProcessId"]), ProxySwitches=switches });
                    if(cmd.IndexOf("--type=",StringComparison.OrdinalIgnoreCase)<0 && cmd.IndexOf("--user-data-dir",StringComparison.OrdinalIgnoreCase)>=0 && !s.ReadErrors.Any(x=>x.StartsWith("自定义用户目录")))
                        s.ReadErrors.Add("自定义用户目录：有浏览器使用 --user-data-dir；扩展列表扫描默认 Chrome/Edge 用户目录，自定义目录请在对应浏览器扩展页核对。");
                }
            } catch(Exception e) { s.ReadErrors.Add("浏览器进程："+e.Message); }
        }
        public static string ExtractProxySwitches(string cmd) {
            // Keep only proxy-related arguments, never URLs or the complete browser command line.
            return string.Join(" ",Regex.Matches(cmd ?? "", @"--(?:no-proxy-server|proxy-auto-detect|proxy-server|proxy-pac-url|proxy-bypass-list)(?:=(?:""[^""]*""|[^\s]+))?(?=\s|$)", RegexOptions.IgnoreCase).Cast<Match>().Select(m=>m.Value));
        }
        static IDictionary<string,object> Dict(object o) { return o as IDictionary<string,object> ?? new Dictionary<string,object>(); }
        static object Get(IDictionary<string,object> d,string key) { object result; return d.TryGetValue(key,out result) ? result : null; }
        public static bool HasEntries(object value) {
            if(value==null) return false;
            var list=value as ICollection; if(list!=null) return list.Count>0;
            int number; return int.TryParse(Convert.ToString(value),out number) && number!=0;
        }
        static bool HasProxyPermission(IDictionary<string,object> extension) {
            var manifest=Dict(Get(extension,"manifest"));
            var permissions=Get(manifest,"permissions") as IEnumerable;
            if(permissions!=null) foreach(var p in permissions) if(Convert.ToString(p)=="proxy") return true;
            foreach(string key in new[]{"active_permissions","granted_permissions"}) {
                var api=Get(Dict(Get(extension,key)),"api") as IEnumerable;
                if(api!=null) foreach(var p in api) if(Convert.ToString(p)=="proxy") return true;
            }
            return false;
        }
        static string ReadShared(string path) {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                if(file.Length>64L*1024*1024) throw new IOException("配置超过 64 MB，已跳过");
                using(var reader=new StreamReader(file,Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        static void ScanExtensions(Snapshot s,string browser,string root) {
            if(!Directory.Exists(root)) return;
            try {
                foreach(string profile in Directory.GetDirectories(root).Where(p=>File.Exists(Path.Combine(p,"Preferences")))) {
                    var found=new Dictionary<string,ExtensionRow>();
                    foreach(string filename in new[]{"Preferences","Secure Preferences"}) {
                        string path=Path.Combine(profile,filename); if(!File.Exists(path)) continue;
                        try {
                            var data=Dict(Store.Serializer().DeserializeObject(ReadShared(path)));
                            var settings=Dict(Get(Dict(Get(data,"extensions")),"settings"));
                            foreach(var pair in settings) {
                                var ext=Dict(pair.Value); if(!HasProxyPermission(ext)) continue;
                                var manifest=Dict(Get(ext,"manifest"));
                                var overrides=new List<string>();
                                foreach(string pref in new[]{"preferences","regular_only_preferences","incognito_preferences"}) {
                                    object proxy=Get(Dict(Get(ext,pref)),"proxy");
                                    if(proxy!=null) overrides.Add(pref+"="+Store.Json(proxy));
                                }
                                string name=Convert.ToString(Get(manifest,"name")); if(string.IsNullOrWhiteSpace(name)) name=pair.Key;
                                object disabled=Get(ext,"disable_reasons");
                                bool off=HasEntries(disabled) || Convert.ToString(Get(ext,"state"))=="0";
                                found[pair.Key]=new ExtensionRow {Browser=browser,Profile=Path.GetFileName(profile),Name=name,Id=pair.Key,
                                    State=off ? "已停用" : "未见禁用标记",Proxy=overrides.Count>0 ? string.Join("; ",overrides) : "未保存代理覆盖值"};
                            }
                        } catch(Exception e) { s.ReadErrors.Add(browser+" / "+Path.GetFileName(profile)+" / "+filename+"："+e.Message); }
                    }
                    s.Extensions.AddRange(found.Values);
                }
            } catch(Exception e) { s.ReadErrors.Add(browser+" 扩展目录："+e.Message); }
        }
        public static void Evaluate(Snapshot s) {
            s.Findings.Clear();
            bool actualOn=(s.Actual.Flags&2)!=0;
            bool surfaceOn=s.RegistryEnabled=="1";
            if(surfaceOn!=actualOn || (surfaceOn && actualOn && !string.Equals(s.RegistryServer,s.Actual.Server,StringComparison.OrdinalIgnoreCase)))
                s.Findings.Add(new Finding("异常","系统配置不一致","注册表 ProxyEnable="+s.RegistryEnabled+"，地址="+s.RegistryServer+"；实际 WinINet Flags="+s.Actual.Flags+"（"+s.Actual.Mode+"），地址="+s.Actual.Server+"。","点击“启用并刷新”，同时重写连接配置和通知应用。"));
            else s.Findings.Add(new Finding("正常","系统配置一致","注册表与 WinINet 手动代理开关一致。实际模式："+s.Actual.Mode+"。",actualOn ? "核对下面的地址、策略与扩展。" : "要使用本地代理，点击“启用并刷新”。"));
            bool match=string.Equals(s.Actual.Server,s.ExpectedEndpoint,StringComparison.OrdinalIgnoreCase);
            s.Findings.Add(new Finding(actualOn && match ? "正常" : "注意","实际代理地址","当前："+(s.Actual.Server=="" ? "（空）" : s.Actual.Server)+"；预期："+s.ExpectedEndpoint+"。",actualOn && match ? "手动代理已指向所选地址。" : "确认上方地址和端口后，点击“启用并刷新”。"));
            s.Findings.Add(new Finding(s.PortState=="可连接" ? "正常" : "异常","本地代理端口",s.ExpectedEndpoint+" · "+s.PortState,s.PortState=="可连接" ? "到“联网测试”检查 HTTP/HTTPS 代理是否可用。" : "启动代理软件，核对监听地址和端口。"));
            if((s.Actual.Flags&12)!=0) s.Findings.Add(new Finding("注意","自动代理配置","PAC / 自动检测处于启用状态。PAC 地址："+s.Actual.PacUrl,"如需固定使用本地代理，点击“启用并刷新”切换为手动模式。"));
            foreach(string browser in new[]{"Chrome","Edge"}) {
                var policies=s.Policies.Where(x=>x.Browser==browser).ToList();
                s.Findings.Add(new Finding(policies.Count>0 ? "注意" : "正常",browser+" 代理策略",policies.Count>0 ? "检测到 "+policies.Count+" 条注册表策略记录，详见“浏览器排查”。" : "未发现注册表代理策略。",policies.Count>0 ? "先核对策略内容；HKLM 的强制策略需由其管理者调整。" : "云端下发策略请在浏览器 policy 页面核对。"));
                var switches=s.Processes.Where(x=>x.Browser==browser && x.ProxySwitches!="").Select(x=>x.ProxySwitches).Distinct().ToArray();
                if(switches.Length>0) s.Findings.Add(new Finding("注意",browser+" 启动覆盖参数",string.Join(" | ",switches),"修改常用快捷方式中的代理参数，再手动退出并重新启动浏览器。"));
                var active=s.Extensions.Where(x=>x.Browser==browser && x.State!="已停用").ToList();
                s.Findings.Add(new Finding(active.Count>0 ? "注意" : "正常",browser+" 代理扩展",active.Count>0 ? string.Join("、",active.Select(x=>x.Name))+"：拥有 proxy 权限，未见禁用标记。" : "已扫描的默认用户目录中，没有发现未停用的代理权限扩展。",active.Count>0 ? "在扩展中选“系统代理”，或手动停用负责代理的扩展后重试。" : "扩展详情和扫描范围见“浏览器排查”。"));
                int count; bool has=s.ProxyConnections.TryGetValue(browser,out count);
                bool running=s.Processes.Any(p=>p.Browser==browser);
                s.Findings.Add(new Finding("信息",browser+" 代理连接",!running ? "未检测到运行进程。" : !has ? "连接表读取未完成。" : "当前连接到本地所选端口的 IPv4 TCP 连接："+count+" 条。",running && has && count>0 ? "已观察到该浏览器连接本地代理。" : "用浏览器打开测试网址后重新诊断，观察连接数。"));
            }
            foreach(string error in s.ReadErrors) s.Findings.Add(new Finding("注意","检查范围 / 读取提示",error,"按提示补查；已成功读取的项目仍会显示。"));
        }
        public static string Report(Snapshot s,string networkLog) {
            var b=new StringBuilder();
            b.AppendLine("浏览器代理诊断助手 1.0"); b.AppendLine("时间："+s.Generated); b.AppendLine("预期代理："+s.ExpectedEndpoint);
            b.AppendLine("实际模式："+s.Actual.Mode+" / Flags="+s.Actual.Flags); b.AppendLine("实际地址："+s.Actual.Server);
            b.AppendLine("注册表 ProxyEnable="+s.RegistryEnabled+" / ProxyServer="+s.RegistryServer);
            b.AppendLine("绕过列表："+s.Actual.Bypass); b.AppendLine("PAC："+s.Actual.PacUrl);
            foreach(var f in s.Findings) b.AppendLine("\r\n["+f.Level+"] "+f.Item+"\r\n"+f.Detail+"\r\n建议："+f.Suggestion);
            b.AppendLine("\r\n=== 注册表代理策略 ==="); foreach(var p in s.Policies) b.AppendLine(p.Browser+" | "+p.Source+" | "+p.Name+" = "+p.Value);
            b.AppendLine("\r\n=== 代理权限扩展（默认用户目录） ==="); foreach(var e in s.Extensions) b.AppendLine(e.Browser+" | "+e.Profile+" | "+e.Name+" | "+e.State+" | "+e.Id+" | "+e.Proxy);
            b.AppendLine("\r\n=== 启动代理参数 ==="); foreach(var p in s.Processes.Where(p=>p.ProxySwitches!="")) b.AppendLine(p.Browser+" PID="+p.Pid+" "+p.ProxySwitches);
            b.AppendLine("\r\n=== 本工具联网测试 ==="); b.AppendLine(networkLog);
            b.AppendLine("\r\n说明：注册表检查覆盖 HKCU/HKLM、32/64 位和 Recommended。浏览器最终策略以 policy 页面为准。联网请求由本工具发出，浏览器需另开测试页验证。");
            return b.ToString();
        }
    }
}
