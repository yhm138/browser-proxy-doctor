using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BrowserProxyDoctor {
    public static class Tests {
        static readonly List<string> lines=new List<string>();
        static void Check(bool result,string label) { if(!result) throw new Exception("测试失败："+label); lines.Add("PASS "+label); }
        public static async Task<string> Run() {
            lines.Clear();
            Check(Network.Endpoint("127.0.0.1",7897)=="127.0.0.1:7897","IPv4 端点");
            Check(Network.Endpoint("::1",7897)=="[::1]:7897","IPv6 回环端点");
            bool invalid=false; try { Network.Endpoint("8.8.8.8",7897); } catch(ArgumentException) { invalid=true; }
            Check(invalid,"拒绝非本机代理地址");
            Check(Diagnostics.ExtractProxySwitches("chrome.exe --type=utility https://private.example --proxy-server=\"127.0.0.1:7897\" --no-proxy-server")=="--proxy-server=\"127.0.0.1:7897\" --no-proxy-server","仅提取代理启动参数");
            Check(!Diagnostics.HasEntries(new object[0]) && Diagnostics.HasEntries(new object[]{1}) && !Diagnostics.HasEntries(0) && Diagnostics.HasEntries(1),"兼容数组及旧版数字禁用原因");
            var fixture=new Snapshot {Actual=new ProxySettings {Flags=1,Server="127.0.0.1:7897"},RegistryEnabled="1",ExpectedEndpoint="127.0.0.1:7897",PortState="可连接"};
            Diagnostics.Evaluate(fixture);
            Check(fixture.Findings.Exists(f=>f.Item=="系统配置不一致" && f.Level=="异常"),"识别本次故障：表面开启 / 实际直连");
            var original=new Backup {Version=1,Kind="System",Settings=new ProxySettings {Flags=13,Server="old:80",Bypass="localhost;10.*",PacUrl="http://127.0.0.1/pac"}};
            var restored=Store.Serializer().Deserialize<Backup>(Store.Json(original));
            Check(restored.Settings.SameAs(original.Settings),"代理备份序列化保留 PAC、绕过列表及所有模式标志");

            string id=Guid.NewGuid().ToString("N");
            using(var parent=Registry.CurrentUser.CreateSubKey(@"Software\BrowserProxyDoctor.Tests")) {
                try {
                    using(var key=parent.CreateSubKey(id)) {
                        key.SetValue("UnrelatedSetting","keep");
                        var values=new List<PolicyValue> {new PolicyValue {Name="ProxySettings",Kind=1,Value="{\"ProxyMode\":\"system\"}"},new PolicyValue {Name="ProxyPacMandatory",Kind=4,Value="1"}};
                        Policies.ReplaceValues(key,values);
                        Check(Store.Json(Policies.ReadValues(key))==Store.Json(values),"代理策略写入及备份类型回读（隔离测试键）");
                        Policies.ReplaceValues(key,new List<PolicyValue>());
                        Check(Policies.ReadValues(key).Count==0 && Convert.ToString(key.GetValue("UnrelatedSetting"))=="keep","清除代理策略时保留其他设置");
                        bool rejected=false; try { Policies.ReplaceValues(key,new List<PolicyValue>{new PolicyValue {Name="UnrelatedSetting",Kind=1,Value="bad"}}); } catch(InvalidDataException) { rejected=true; }
                        Check(rejected && Convert.ToString(key.GetValue("UnrelatedSetting"))=="keep","拒绝备份中的非代理策略值");
                    }
                } finally { parent.DeleteSubKeyTree(id,false); }
            }

            var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
            int port=((IPEndPoint)listener.LocalEndpoint).Port;
            try {
                var received=new TaskCompletionSource<string>();
                var serve=Task.Run(async delegate {
                    using(var client=await listener.AcceptTcpClientAsync())
                    using(var stream=client.GetStream()) {
                        var buffer=new byte[8192]; int count=await stream.ReadAsync(buffer,0,buffer.Length);
                        received.TrySetResult(Encoding.ASCII.GetString(buffer,0,count));
                        byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(response,0,response.Length);
                    }
                });
                string result=await Network.Probe("explicit",new Uri("http://proxy-test.invalid/check"),"127.0.0.1:"+port);
                await serve;
                Check(result.StartsWith("HTTP 204") && (await received.Task).StartsWith("GET http://proxy-test.invalid/check"),"实际 HTTP 请求发往指定代理（本地模拟服务，无外网）");
            } finally { listener.Stop(); }
            var bound=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp); bound.Bind(new IPEndPoint(IPAddress.Loopback,0));
            try { Check(await Network.CheckPort("127.0.0.1",((IPEndPoint)bound.LocalEndPoint).Port)!="可连接","端口未监听时准确报错"); } finally { bound.Dispose(); }
            var native=NativeProxy.Read(); Check(native!=null && native.Flags<=15,"读取当前 Windows 实际配置："+NativeProxy.LayoutInfo());
            lines.Add("测试通过。测试未改动真实浏览器策略和系统代理。");
            return string.Join("\r\n",lines);
        }
    }
}
