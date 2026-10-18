using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace BrowserProxyDoctor {
    static class Program {
        [STAThread]
        static int Main(string[] args) {
            try {
                if(args.Length==2 && args[0]=="--self-test") {
                    File.WriteAllText(args[1],Tests.Run().GetAwaiter().GetResult(),new UTF8Encoding(true)); return 0;
                }
                if(args.Length==2 && args[0]=="--diagnose-report") {
                    var s=Diagnostics.Collect("127.0.0.1",7897).GetAwaiter().GetResult();
                    File.WriteAllText(args[1],Diagnostics.Report(s,"未运行外网测试"),new UTF8Encoding(true)); return 0;
                }
                if(args.Length==2 && args[0]=="--verify-native") {
                    var before=NativeProxy.Read();
                    string backup=Store.SaveBackup(new Backup { Kind="System", Settings=before });
                    NativeProxy.Write(before);
                    var after=NativeProxy.Read();
                    if(!before.SameAs(after)) throw new IOException("同值写入验证失败；备份："+backup);
                    File.WriteAllText(args[1],"PASS: 同值写入 / 刷新通知 / 回读一致\r\n"+NativeProxy.LayoutInfo()+"\r\n"+Store.Json(after),new UTF8Encoding(true)); return 0;
                }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException+=delegate(object sender,System.Threading.ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.Message,"程序提示",MessageBoxButtons.OK,MessageBoxIcon.Warning); };
                Application.Run(new MainForm()); return 0;
            } catch(Exception e) {
                if(args.Length==2) { File.WriteAllText(args[1],"FAIL\r\n"+e,new UTF8Encoding(true)); return 1; }
                MessageBox.Show(e.Message,"浏览器代理诊断助手",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1;
            }
        }
    }
}
