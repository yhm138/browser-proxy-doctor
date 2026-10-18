using System;
using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BrowserProxyDoctor {
    public sealed class MainForm : Form {
        readonly Color Navy=Color.FromArgb(20,40,63), Teal=Color.FromArgb(0,114,110), Light=Color.FromArgb(242,246,249);
        readonly TextBox host=new TextBox(), url=new TextBox(), bypass=new TextBox();
        readonly TextBox port=new TextBox();
        readonly Label live=new Label(), overview=new Label(), footer=new Label();
        readonly RichTextBox networkLog=new RichTextBox(), log=new RichTextBox();
        readonly ComboBox browser=new ComboBox(), policyMode=new ComboBox();
        readonly TabControl tabs=new TabControl();
        readonly DataGridView findings=new DataGridView(), policyGrid=new DataGridView(), extensionGrid=new DataGridView();
        readonly TableLayoutPanel body=new TableLayoutPanel();
        readonly System.Windows.Forms.Timer watch=new System.Windows.Forms.Timer();
        readonly List<Control> actions=new List<Control>();
        Snapshot latest;
        ProxySettings observed;
        bool busy;

        public MainForm() {
            SuspendLayout();
            Text="浏览器代理诊断助手 · Chrome / Edge";
            Font=new Font("Microsoft YaHei UI",9F);
            AutoScaleDimensions=new SizeF(96F,96F); AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(1120,790); MinimumSize=new Size(1000,720);
            StartPosition=FormStartPosition.CenterScreen; BackColor=Color.White;
            Icon=SystemIcons.Application;
            BuildUi();
            var config=new AppConfig();
            try { if(File.Exists(Path.Combine(Store.Root,"settings.json"))) config=Store.Read<AppConfig>(Path.Combine(Store.Root,"settings.json")); } catch { }
            host.Text=config.Host ?? "127.0.0.1"; port.Text=(config.Port>=1 && config.Port<=65535 ? config.Port : 7897).ToString(); url.Text=config.Url ?? new AppConfig().Url;
            watch.Interval=5000; watch.Tick+=delegate { WatchSettings(); };
            Shown+=async delegate { watch.Start(); await Run("正在读取代理配置和浏览器状态…",Diagnose); };
            FormClosed+=delegate { watch.Stop(); watch.Dispose(); };
            ResumeLayout(true);
        }
        TableLayoutPanel MakeLayout(int columns) {
            return new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=columns,Padding=new Padding(0),Margin=new Padding(0),BackColor=Color.White };
        }
        Label TextLabel(string text) { return new Label { Text=text,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,6,8,6) }; }
        Button Button(string text,Func<Task> action,bool primary) {
            var b=new Button {Text=text,AutoSize=true,Height=34,Padding=new Padding(12,4,12,4),Margin=new Padding(0,3,8,3),FlatStyle=FlatStyle.Flat,Cursor=Cursors.Hand};
            b.FlatAppearance.BorderSize=primary ? 0 : 1; b.FlatAppearance.BorderColor=Color.FromArgb(208,217,224);
            b.BackColor=primary ? Teal : Color.White; b.ForeColor=primary ? Color.White : Navy;
            b.Click+=async delegate { await Run(text+"…",action); }; actions.Add(b); return b;
        }
        FlowLayoutPanel Flow() { return new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Margin=new Padding(0),Padding=new Padding(0) }; }
        Label Note(string text) { return new Label { Text=text,Dock=DockStyle.Fill,AutoSize=true,ForeColor=Color.FromArgb(86,102,117),Padding=new Padding(2,7,2,7),Margin=new Padding(0) }; }
        TabPage Page(string title) { var p=new TabPage(title) { Padding=new Padding(16),BackColor=Color.White }; tabs.TabPages.Add(p); return p; }
        void BuildUi() {
            body.RowCount=5; body.ColumnCount=1; body.Dock=DockStyle.Fill;
            body.RowStyles.Add(new RowStyle(SizeType.Absolute,88));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute,66));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            body.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute,34));
            Controls.Add(body);
            var head=new Panel { Dock=DockStyle.Fill,BackColor=Navy,Padding=new Padding(22,14,20,10) };
            var title=new Label {Text="浏览器代理诊断助手",Font=new Font(Font.FontFamily,19,FontStyle.Bold),AutoSize=true,ForeColor=Color.White,Location=new Point(22,12)};
            var sub=new Label {Text="Windows 实际连接配置  /  Chrome 与 Edge 排查  /  修改前自动备份",AutoSize=true,ForeColor=Color.FromArgb(192,215,226),Location=new Point(24,55)};
            head.Controls.Add(title); head.Controls.Add(sub); body.Controls.Add(head,0,0);

            var commands=MakeLayout(9); commands.Padding=new Padding(18,8,10,5); commands.RowCount=1;
            foreach(int width in new[]{70,140,20,90,118,143,180,120}) commands.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,width));
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); commands.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            commands.Controls.Add(TextLabel("本地代理"),0,0);
            host.Anchor=AnchorStyles.Left|AnchorStyles.Right; host.Margin=new Padding(0,3,6,3); host.AccessibleName="本地代理地址";
            port.MaxLength=5; port.Anchor=AnchorStyles.Left|AnchorStyles.Right; port.Margin=new Padding(0,3,12,3); port.AccessibleName="本地代理端口";
            commands.Controls.Add(host,1,0); commands.Controls.Add(TextLabel(":"),2,0); commands.Controls.Add(port,3,0);
            commands.Controls.Add(Button("重新诊断",Diagnose,false),4,0);
            commands.Controls.Add(Button("启用并刷新",EnableProxy,true),5,0);
            commands.Controls.Add(Button("仅发送刷新通知",async delegate { NativeProxy.Refresh(); AddLog("已发送 SETTINGS_CHANGED (39) 与 REFRESH (37)。"); await Diagnose(); },false),6,0);
            commands.Controls.Add(Button("切换直连",async delegate { var target=NativeProxy.Read(); target.Flags=1; await WriteSystem(target,"切换为直连（关闭手动代理、PAC 和自动检测）"); },false),7,0);
            body.Controls.Add(commands,0,1); actions.Add(host); actions.Add(port);

            live.Dock=DockStyle.Fill; live.BackColor=Light; live.ForeColor=Navy; live.TextAlign=ContentAlignment.MiddleLeft; live.Padding=new Padding(22,0,0,0); live.Text="正在读取 Windows 实际代理配置…";
            body.Controls.Add(live,0,2);
            tabs.Dock=DockStyle.Fill; tabs.Font=Font; tabs.Padding=new Point(16,8); tabs.Margin=new Padding(12,8,12,4);
            body.Controls.Add(tabs,0,3);
            BuildOverview(); BuildSystem(); BuildBrowsers(); BuildNetwork(); BuildLogs();
            footer.Text="就绪"; footer.Dock=DockStyle.Fill; footer.TextAlign=ContentAlignment.MiddleLeft; footer.Padding=new Padding(20,0,10,0); footer.ForeColor=Color.FromArgb(84,102,118); footer.BackColor=Light; body.Controls.Add(footer,0,4);
        }
        void SetupGrid(DataGridView grid,params string[] columns) {
            grid.Dock=DockStyle.Fill; grid.BackgroundColor=Color.White; grid.BorderStyle=BorderStyle.None;
            grid.ReadOnly=true; grid.AllowUserToAddRows=false; grid.AllowUserToDeleteRows=false;
            grid.AllowUserToResizeRows=false; grid.RowHeadersVisible=false; grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect=false; grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
            grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True; grid.DefaultCellStyle.Padding=new Padding(7,7,7,7);
            grid.DefaultCellStyle.SelectionBackColor=Color.FromArgb(225,239,243); grid.DefaultCellStyle.SelectionForeColor=Navy;
            grid.GridColor=Color.FromArgb(228,234,239); grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;
            grid.EnableHeadersVisualStyles=false; grid.ColumnHeadersDefaultCellStyle.BackColor=Light; grid.ColumnHeadersDefaultCellStyle.ForeColor=Navy;
            grid.ColumnHeadersDefaultCellStyle.Font=new Font(Font,FontStyle.Bold); grid.ColumnHeadersDefaultCellStyle.Padding=new Padding(7);
            grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            foreach(string c in columns) grid.Columns.Add(c,c);
            grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;
        }
        void BuildOverview() {
            var page=Page("诊断总览"); var layout=MakeLayout(1); layout.RowCount=3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,39));
            overview.Dock=DockStyle.Fill; overview.Font=new Font(Font.FontFamily,11,FontStyle.Bold); overview.ForeColor=Navy;
            overview.Text="点击“重新诊断”查看检查结果"; layout.Controls.Add(overview,0,0);
            SetupGrid(findings,"状态","检查项","发现","建议操作"); findings.Columns[0].FillWeight=9; findings.Columns[1].FillWeight=20; findings.Columns[2].FillWeight=39; findings.Columns[3].FillWeight=38;
            findings.Columns[0].MinimumWidth=65;
            layout.Controls.Add(findings,0,1);
            layout.Controls.Add(Note("诊断自动检查本地配置与端口；外网请求请到“联网测试”手动运行。每 5 秒监测系统代理变化。"),0,2);
            page.Controls.Add(layout);
        }
        void BuildSystem() {
            var page=Page("系统代理编辑"); var layout=MakeLayout(1); layout.RowCount=7;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,55));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));
            layout.Controls.Add(Note("“启用并刷新”会把当前用户的默认 LAN 连接设为上方代理，停用 PAC 和自动检测，并保留下面的绕过列表。\r\n每次修改前保存完整连接配置；用“恢复备份”可恢复当时的模式、地址、绕过列表和 PAC 地址。"),0,0);
            layout.Controls.Add(TextLabel("绕过代理的地址（分号分隔；可编辑）"),0,1);
            bypass.Multiline=true; bypass.ScrollBars=ScrollBars.Vertical; bypass.Dock=DockStyle.Fill; bypass.Font=new Font("Consolas",10F); bypass.AccessibleName="代理绕过列表";
            layout.Controls.Add(bypass,0,2); actions.Add(bypass);
            var row=Flow();
            row.Controls.Add(Button("仅保存绕过列表",async delegate { var current=NativeProxy.Read(); current.Bypass=bypass.Text.Trim(); await WriteSystem(current,"更新绕过列表"); },true));
            row.Controls.Add(Button("填入常用本地地址",delegate { bypass.Text="localhost;127.*;[::1];192.168.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;<local>"; return Task.FromResult(0); },false));
            layout.Controls.Add(row,0,3);
            layout.Controls.Add(Note("只需恢复浏览器代理时，优先使用“启用并刷新”。\r\n仅调整绕过列表时，当前的手动代理 / PAC / 自动检测模式保持原值。"),0,4);
            var backupRow=Flow(); backupRow.Controls.Add(Button("恢复备份…",RestoreBackup,false)); backupRow.Controls.Add(Button("打开备份文件夹",delegate { Directory.CreateDirectory(Store.Backups); Process.Start("explorer.exe",Store.Backups); return Task.FromResult(0); },false)); layout.Controls.Add(backupRow,0,5);
            layout.Controls.Add(Note("备份保存在 %LOCALAPPDATA%\\BrowserProxyDoctor\\Backups。此工具修改当前用户配置，无需管理员权限。"),0,6); page.Controls.Add(layout);
        }
        void BuildBrowsers() {
            var page=Page("浏览器排查"); var layout=MakeLayout(1); layout.RowCount=4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,57)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,43)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.Controls.Add(Note("先核对下面的代理策略、扩展及总览中的启动参数。浏览器内的 policy 页面可查看最终生效策略。\r\n这里的修改只写 HKCU 的代理策略，可能使浏览器显示“由组织管理”；HKLM 强制策略需由其管理者调整。"),0,0);
            var controls=Flow(); browser.DropDownStyle=ComboBoxStyle.DropDownList; browser.Items.AddRange(new object[]{"Chrome","Edge"}); browser.SelectedIndex=0; browser.Width=100; browser.Margin=new Padding(0,7,10,3);
            policyMode.DropDownStyle=ComboBoxStyle.DropDownList; policyMode.Items.AddRange(new object[]{"跟随 Windows 系统代理","固定使用上方本地代理","清除当前用户代理策略"}); policyMode.SelectedIndex=0; policyMode.Width=228; policyMode.Margin=new Padding(0,7,10,3);
            controls.Controls.Add(browser); controls.Controls.Add(policyMode);
            controls.Controls.Add(Button("应用所选浏览器策略",ApplyPolicy,true));
            controls.Controls.Add(Button("恢复备份…",RestoreBackup,false));
            layout.Controls.Add(controls,0,1); actions.Add(browser); actions.Add(policyMode);
            var shortcuts=Flow();
            shortcuts.Controls.Add(Button("复制策略页地址",delegate { Clipboard.SetText(browser.Text=="Chrome" ? "chrome://policy" : "edge://policy"); AddLog("已复制策略页地址；粘贴到对应浏览器地址栏，点击 Reload policies / 重新加载策略。"); return Task.FromResult(0); },false));
            shortcuts.Controls.Add(Button("复制扩展页地址",delegate { Clipboard.SetText(browser.Text=="Chrome" ? "chrome://extensions" : "edge://extensions"); AddLog("已复制扩展页地址；在浏览器中检查代理扩展。"); return Task.FromResult(0); },false));
            shortcuts.Controls.Add(TextLabel("应用策略后，请在 policy 页重新加载策略或手动重启浏览器。")); layout.Controls.Add(shortcuts,0,2);
            var inner=new TabControl {Dock=DockStyle.Fill};
            var p1=new TabPage("代理策略"); var p2=new TabPage("拥有代理权限的扩展"); inner.TabPages.Add(p1); inner.TabPages.Add(p2);
            SetupGrid(policyGrid,"浏览器","来源","策略名称","策略值"); policyGrid.Columns[0].FillWeight=12; policyGrid.Columns[1].FillWeight=25; policyGrid.Columns[2].FillWeight=24; policyGrid.Columns[3].FillWeight=60; p1.Controls.Add(policyGrid);
            SetupGrid(extensionGrid,"浏览器","用户配置","扩展名称","状态","保存的覆盖值","扩展 ID"); extensionGrid.Columns[0].FillWeight=10; extensionGrid.Columns[1].FillWeight=12; extensionGrid.Columns[2].FillWeight=26; extensionGrid.Columns[3].FillWeight=17; extensionGrid.Columns[4].FillWeight=28; extensionGrid.Columns[5].FillWeight=28; p2.Controls.Add(extensionGrid);
            layout.Controls.Add(inner,0,3); page.Controls.Add(layout);
        }
        void BuildNetwork() {
            var page=Page("联网测试"); var layout=MakeLayout(1); layout.RowCount=5;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,60)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,49)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));
            layout.Controls.Add(Note("并行比较：指定 HTTP 代理、.NET 读取的系统代理、直连。每项最多等待 12 秒，只读取响应头。\r\n这些请求由本工具发出；用下面的 Chrome / Edge 按钮打开同一网址，再回总览重新诊断可观察代理连接。"),0,0);
            url.Dock=DockStyle.Fill; url.Margin=new Padding(0,6,0,4); url.AccessibleName="联网测试网址"; layout.Controls.Add(url,0,1); actions.Add(url);
            var row=Flow(); row.Controls.Add(Button("运行三路对比",TestNetwork,true));
            row.Controls.Add(Button("用 Chrome 打开测试地址",delegate { OpenBrowser("Chrome"); return Task.FromResult(0); },false));
            row.Controls.Add(Button("用 Edge 打开测试地址",delegate { OpenBrowser("Edge"); return Task.FromResult(0); },false)); layout.Controls.Add(row,0,2);
            ConfigureText(networkLog); layout.Controls.Add(networkLog,0,3);
            layout.Controls.Add(Note("若指定代理成功、直连失败：代理链路可用，继续核对浏览器策略和扩展。\r\n若指定代理也失败：核对代理节点、规则、HTTP 代理协议及目标站点响应。"),0,4); page.Controls.Add(layout);
        }
        void BuildLogs() {
            var page=Page("操作日志 / 导出"); var layout=MakeLayout(1); layout.RowCount=3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            var row=Flow(); row.Controls.Add(Button("导出诊断报告…",ExportReport,true)); row.Controls.Add(Button("打开数据文件夹",delegate { Directory.CreateDirectory(Store.Root); Process.Start("explorer.exe",Store.Root); return Task.FromResult(0); },false)); layout.Controls.Add(row,0,0);
            ConfigureText(log); layout.Controls.Add(log,0,1);
            layout.Controls.Add(Note("报告包含代理配置、代理策略和扩展名称，以及本次测试结果；保存后可自行检查再分享。"),0,2); page.Controls.Add(layout);
        }
        void ConfigureText(RichTextBox box) {
            box.Dock=DockStyle.Fill; box.ReadOnly=true; box.BackColor=Light; box.ForeColor=Navy; box.BorderStyle=BorderStyle.None; box.Font=new Font("Microsoft YaHei UI",10F); box.DetectUrls=false;
        }
        int Port() { int result; if(!int.TryParse(port.Text.Trim(),out result) || result<1 || result>65535) throw new ArgumentException("端口须为 1–65535 的整数"); return result; }
        string Endpoint() { return Network.Endpoint(host.Text,Port()); }
        void SaveConfig() { Store.Save(Path.Combine(Store.Root,"settings.json"),new AppConfig { Host=host.Text.Trim(),Port=Port(),Url=url.Text.Trim() }); }
        async Task Run(string message,Func<Task> action) {
            if(busy) return; busy=true; footer.Text=message;
            foreach(var c in actions) c.Enabled=false; UseWaitCursor=true;
            try { await action(); footer.Text="完成 · "+DateTime.Now.ToString("HH:mm:ss"); }
            catch(Exception e) { footer.Text="操作未完成："+e.GetBaseException().Message; AddLog("错误："+e.GetBaseException().Message); MessageBox.Show(this,e.GetBaseException().Message,"操作结果",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
            finally { if(!IsDisposed) { foreach(var c in actions) c.Enabled=true; UseWaitCursor=false; } busy=false; }
        }
        void AddLog(string text) { log.AppendText("["+DateTime.Now.ToString("HH:mm:ss")+"] "+text+"\r\n"); log.SelectionStart=log.TextLength; log.ScrollToCaret(); }
        async Task Diagnose() {
            Endpoint(); SaveConfig();
            latest=await Diagnostics.Collect(host.Text.Trim(),Port());
            if(IsDisposed) return;
            observed=latest.Actual.Copy(); ShowLive(observed); bypass.Text=latest.Actual.Bypass;
            int bad=latest.Findings.Count(f=>f.Level=="异常"), warnings=latest.Findings.Count(f=>f.Level=="注意");
            overview.Text="检查完成  ·  "+bad+" 项异常  /  "+warnings+" 项需核对  ·  "+DateTime.Now.ToString("HH:mm:ss");
            findings.Rows.Clear(); foreach(var f in latest.Findings) {
                int i=findings.Rows.Add(f.Level,f.Item,f.Detail,f.Suggestion);
                findings.Rows[i].Cells[0].Style.ForeColor=f.Level=="异常" ? Color.FromArgb(174,45,49) : f.Level=="注意" ? Color.FromArgb(157,104,0) : f.Level=="正常" ? Teal : Navy;
            }
            policyGrid.Rows.Clear(); foreach(var p in latest.Policies) policyGrid.Rows.Add(p.Browser,p.Source,p.Name,p.Value);
            extensionGrid.Rows.Clear(); foreach(var e in latest.Extensions) extensionGrid.Rows.Add(e.Browser,e.Profile,e.Name,e.State,e.Proxy,e.Id);
            AddLog("诊断完成：实际 "+latest.Actual.Mode+" / "+latest.Actual.Server+" / Flags="+latest.Actual.Flags+"；端口 "+latest.PortState+"。");
        }
        void ShowLive(ProxySettings current) {
            live.Text="Windows 实际模式："+current.Mode+"     |     地址："+(current.Server=="" ? "（空）" : current.Server)+"     |     Flags = "+current.Flags;
        }
        void WatchSettings() {
            if(busy || IsDisposed) return;
            try {
                var current=NativeProxy.Read();
                if(observed!=null && !observed.SameAs(current)) {
                    AddLog("检测到系统代理变化："+current.Mode+" / "+current.Server+" / Flags="+current.Flags+"。其他代理软件可能正在改写设置。");
                    ShowLive(current); footer.Text="系统代理发生变化，请点击“重新诊断”更新完整结果。";
                    overview.Text="系统代理已变化，下面为上次诊断结果；请重新诊断。";
                }
                observed=current;
            } catch(Exception e) { footer.Text="监测提示："+e.Message; }
        }
        async Task EnableProxy() {
            string endpoint=Endpoint();
            string state=await Network.CheckPort(host.Text.Trim(),Port());
            if(state!="可连接") throw new IOException("本地代理端口 "+endpoint+" "+state+"。请先启动代理软件；系统设置保持原值。");
            var target=NativeProxy.Read(); target.Flags=3; target.Server=endpoint; target.Bypass=bypass.Text.Trim();
            await WriteSystem(target,"启用 "+endpoint+"，并发送刷新通知");
        }
        async Task WriteSystem(ProxySettings target,string description) {
            var current=NativeProxy.Read();
            string backup=Store.SaveBackup(new Backup {Kind="System",Settings=current});
            AddLog("修改前备份："+backup);
            await Task.Run(()=>NativeProxy.Write(target));
            var after=NativeProxy.Read();
            if(!after.SameAs(target)) throw new IOException("回读与预期不一致，其他程序可能正在改写系统代理。备份："+backup);
            AddLog(description+"；已通过 Windows 标准接口写入并回读核对。"); await Diagnose();
        }
        async Task ApplyPolicy() {
            string name=browser.Text, mode=policyMode.SelectedIndex==0 ? "system" : policyMode.SelectedIndex==1 ? "fixed_servers" : "clear";
            string endpoint=Endpoint();
            if(mode=="fixed_servers" && await Network.CheckPort(host.Text.Trim(),Port())!="可连接") throw new IOException("本地代理端口未连接，请先启动代理软件。");
            string path=await Task.Run(()=>Policies.Apply(name,mode,endpoint));
            AddLog(name+" 当前用户代理策略已更新："+mode+"。备份："+path+"。请到浏览器 policy 页重新加载策略。");
            await Diagnose();
        }
        async Task RestoreBackup() {
            Directory.CreateDirectory(Store.Backups);
            using(var dialog=new OpenFileDialog {Title="选择要恢复的备份（系统配置或浏览器代理策略）",InitialDirectory=Store.Backups,Filter="代理配置备份 (*.json)|*.json",CheckFileExists=true}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK) return;
                var saved=Store.Read<Backup>(dialog.FileName);
                if(saved.Version!=1) throw new InvalidDataException("不支持的备份版本");
                if(saved.Kind=="System" && saved.Settings!=null) {
                    if((saved.Settings.Flags & ~15u)!=0) throw new InvalidDataException("备份包含无效代理标志");
                    await WriteSystem(saved.Settings,"恢复系统代理备份 "+saved.Created);
                } else if(saved.Kind=="Policy" && saved.Values!=null && (saved.Browser=="Chrome" || saved.Browser=="Edge")) {
                    await Task.Run(()=>Policies.Restore(saved)); AddLog("已恢复 "+saved.Browser+" 当前用户代理策略备份 "+saved.Created+"，请到 policy 页重新加载策略。"); await Diagnose();
                } else throw new InvalidDataException("备份内容不完整或类型不支持");
            }
        }
        async Task TestNetwork() {
            string endpoint=Endpoint(); var target=Network.TestUrl(url.Text); SaveConfig();
            networkLog.AppendText("\r\n["+DateTime.Now.ToString("HH:mm:ss")+"] 测试 "+target.AbsoluteUri+"\r\n代理："+endpoint+"\r\n");
            var results=await Task.WhenAll(Network.Probe("explicit",target,endpoint),Network.Probe("system",target,endpoint),Network.Probe("direct",target,endpoint));
            string[] names={"指定 HTTP 代理",".NET 系统代理","直接连接"};
            for(int i=0;i<3;i++) networkLog.AppendText(names[i]+"："+results[i]+"\r\n");
            AddLog("联网三路对比完成，详情见“联网测试”。");
        }
        void OpenBrowser(string name) {
            var target=Network.TestUrl(url.Text);
            string relative=name=="Chrome" ? @"Google\Chrome\Application\chrome.exe" : @"Microsoft\Edge\Application\msedge.exe";
            var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)};
            string exe=roots.Select(p=>Path.Combine(p,relative)).FirstOrDefault(File.Exists);
            if(exe==null) throw new FileNotFoundException("未在常用安装目录找到 "+name+"。可复制测试网址手动打开。");
            Process.Start(new ProcessStartInfo(exe,"\""+target.AbsoluteUri+"\"") {UseShellExecute=true});
            AddLog("已请 "+name+" 打开测试地址。刷新页面后点击“重新诊断”观察代理连接。");
        }
        async Task ExportReport() {
            if(latest==null) await Diagnose();
            using(var dialog=new SaveFileDialog {Title="保存诊断报告",Filter="文本报告 (*.txt)|*.txt",FileName="代理诊断-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt"}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK) return;
                File.WriteAllText(dialog.FileName,Diagnostics.Report(latest,networkLog.Text)+"\r\n=== 操作日志 ===\r\n"+log.Text,new UTF8Encoding(true)); AddLog("诊断报告已保存："+dialog.FileName);
            }
        }
    }
}
