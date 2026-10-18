using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace BrowserProxyDoctor {
    public static class Store {
        public static readonly string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrowserProxyDoctor");
        public static readonly string Backups=Path.Combine(Root,"Backups");
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=64*1024*1024, RecursionLimit=150 }; }
        public static string Json(object value) { return Serializer().Serialize(value); }
        public static T Read<T>(string path) { return Serializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
        public static void Save(string path, object value) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(tmp, Json(value), new UTF8Encoding(true));
            try {
                if(File.Exists(path)) File.Replace(tmp,path,null); else File.Move(tmp,path);
            } finally { if(File.Exists(tmp)) File.Delete(tmp); }
        }
        public static string SaveBackup(Backup backup) {
            backup.Version=1; backup.Created=DateTimeOffset.Now.ToString("o");
            string path=Path.Combine(Backups, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+backup.Kind+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".json");
            Save(path,backup); return path;
        }
    }
    public sealed class AppConfig {
        public string Host { get; set; } public int Port { get; set; } public string Url { get; set; }
        public AppConfig() { Host="127.0.0.1"; Port=7897; Url="https://www.google.com/generate_204"; }
    }
    public sealed class PolicyValue {
        public string Name { get; set; } public string Value { get; set; } public int Kind { get; set; }
    }
    public sealed class Backup {
        public int Version { get; set; } public string Kind { get; set; } public string Created { get; set; }
        public string Browser { get; set; } public ProxySettings Settings { get; set; } public List<PolicyValue> Values { get; set; }
    }
    public static class Policies {
        public static readonly string[] Names={"ProxySettings","ProxyMode","ProxyServer","ProxyPacUrl","ProxyBypassList","ProxyPacMandatory","ProxyServerMode","ProxyOverrideRules"};
        public static string KeyPath(string browser) {
            if(browser=="Chrome") return @"Software\Policies\Google\Chrome";
            if(browser=="Edge") return @"Software\Policies\Microsoft\Edge";
            throw new ArgumentException("未知浏览器");
        }
        public static List<PolicyValue> ReadValues(RegistryKey key) {
            var values=new List<PolicyValue>(); if(key==null) return values;
            foreach(string name in Names) if(key.GetValueNames().Contains(name)) {
                var kind=key.GetValueKind(name);
                if(kind!=RegistryValueKind.String && kind!=RegistryValueKind.ExpandString && kind!=RegistryValueKind.DWord && kind!=RegistryValueKind.QWord)
                    throw new InvalidDataException("代理策略值类型异常："+name);
                values.Add(new PolicyValue { Name=name, Value=Convert.ToString(key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames)), Kind=(int)kind });
            }
            return values;
        }
        // Only the proxy policy allowlist is touched; all other browser policies remain in place.
        public static void ReplaceValues(RegistryKey key, List<PolicyValue> values) {
            foreach(var v in values) {
                if(!Names.Contains(v.Name)) throw new InvalidDataException("备份含未知策略名称");
                if(v.Kind!=1 && v.Kind!=2 && v.Kind!=4 && v.Kind!=11) throw new InvalidDataException("备份策略类型无效");
            }
            // Validate and convert everything before deleting the first value.
            var data=new List<object>();
            foreach(var v in values) data.Add(v.Kind==4 ? (object)int.Parse(v.Value) : v.Kind==11 ? (object)long.Parse(v.Value) : v.Value);
            foreach(string name in Names) key.DeleteValue(name,false);
            for(int i=0;i<values.Count;i++) key.SetValue(values[i].Name,data[i],(RegistryValueKind)values[i].Kind);
        }
        public static string Apply(string browser, string mode, string endpoint) {
            if(mode!="system" && mode!="fixed_servers" && mode!="clear") throw new ArgumentException("代理策略模式无效");
            using(var root=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64))
            using(var key=root.CreateSubKey(KeyPath(browser))) {
                var old=ReadValues(key);
                string path=Store.SaveBackup(new Backup { Kind="Policy", Browser=browser, Values=old });
                var next=new List<PolicyValue>();
                if(mode!="clear") {
                    var json=new Dictionary<string,object>(); json["ProxyMode"]=mode;
                    if(mode=="fixed_servers") {
                        json["ProxyServer"]=endpoint;
                        json["ProxyBypassList"]="localhost;127.*;[::1];<local>";
                    }
                    next.Add(new PolicyValue { Name="ProxySettings",Kind=1,Value=Store.Json(json) });
                }
                try {
                    ReplaceValues(key,next);
                    if(Store.Json(ReadValues(key))!=Store.Json(next)) throw new IOException("策略回读检查失败");
                } catch { ReplaceValues(key,old); throw; }
                return path;
            }
        }
        public static void Restore(Backup saved) {
            using(var root=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64))
            using(var key=root.CreateSubKey(KeyPath(saved.Browser))) {
                var current=ReadValues(key);
                Store.SaveBackup(new Backup { Kind="Policy", Browser=saved.Browser, Values=current });
                try {
                    ReplaceValues(key,saved.Values);
                    if(Store.Json(ReadValues(key))!=Store.Json(saved.Values)) throw new IOException("恢复策略回读检查失败");
                } catch { ReplaceValues(key,current); throw; }
            }
        }
    }

    public static class Network {
        public static string Endpoint(string host,int port) {
            host=host.Trim(); IPAddress ip;
            if(port<1 || port>65535 || !(host.Equals("localhost",StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host,out ip) && IPAddress.IsLoopback(ip))))
                throw new ArgumentException("请输入本机回环地址（如 127.0.0.1、localhost、::1）和 1–65535 端口");
            return (host.IndexOf(':')>=0 ? "["+host+"]" : host)+":"+port;
        }
        public static async Task<string> CheckPort(string host,int port) {
            using(var client=new TcpClient()) {
                try {
                    var connect=client.ConnectAsync(host,port);
                    if(await Task.WhenAny(connect,Task.Delay(1800))!=connect) {
                        // Observe a possible late fault after disposing the socket.
                        ObserveLateFailure(connect);
                        return "连接超时";
                    }
                    await connect; return "可连接";
                } catch(Exception e) { return "未连接："+e.GetBaseException().Message; }
            }
        }
        static void ObserveLateFailure(Task task) {
            task.ContinueWith(t => { var ignored=t.Exception; },TaskContinuationOptions.OnlyOnFaulted);
        }
        public static async Task<string> Probe(string mode,Uri url,string endpoint) {
            var watch=System.Diagnostics.Stopwatch.StartNew();
            using(var handler=new HttpClientHandler()) {
                handler.AllowAutoRedirect=false; handler.UseCookies=false;
                if(mode=="direct") handler.UseProxy=false;
                else if(mode=="explicit") { handler.UseProxy=true; handler.Proxy=new WebProxy("http://"+endpoint) { BypassProxyOnLocal=false }; }
                else { handler.UseProxy=true; handler.Proxy=WebRequest.GetSystemWebProxy(); }
                using(var client=new HttpClient(handler)) {
                    client.Timeout=TimeSpan.FromSeconds(12);
                    try {
                        using(var request=new HttpRequestMessage(HttpMethod.Get,url)) {
                            request.Headers.UserAgent.ParseAdd("BrowserProxyDoctor/1.0");
                            using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead)) {
                                int code=(int)response.StatusCode;
                                return "HTTP "+code+" "+response.ReasonPhrase+" · "+watch.ElapsedMilliseconds+" ms"+(code>=400 ? "（已收到目标站响应，请检查访问限制或站点状态）" : "");
                            }
                        }
                    } catch(TaskCanceledException) {
                        return "超时（12 秒） · "+watch.ElapsedMilliseconds+" ms";
                    } catch(Exception e) {
                        return "失败 · "+watch.ElapsedMilliseconds+" ms · "+e.GetBaseException().Message;
                    }
                }
            }
        }
        public static Uri TestUrl(string value) {
            Uri uri;
            if(!Uri.TryCreate(value.Trim(),UriKind.Absolute,out uri) || (uri.Scheme!="https" && uri.Scheme!="http") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("测试地址须为 HTTP/HTTPS，且不含用户名或密码");
            return uri;
        }
    }
}
