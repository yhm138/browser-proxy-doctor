using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace BrowserProxyDoctor {
    public sealed class ProxySettings {
        public uint Flags { get; set; }
        public string Server { get; set; }
        public string Bypass { get; set; }
        public string PacUrl { get; set; }
        public ProxySettings() { Server = Bypass = PacUrl = ""; }
        public ProxySettings Copy() { return (ProxySettings)MemberwiseClone(); }
        public bool SameAs(ProxySettings other) {
            return other != null && Flags == other.Flags && Server == other.Server && Bypass == other.Bypass && PacUrl == other.PacUrl;
        }
        public string Mode {
            get {
                var modes = new List<string>();
                if ((Flags & 2) != 0) modes.Add("手动代理");
                if ((Flags & 4) != 0) modes.Add("PAC 脚本");
                if ((Flags & 8) != 0) modes.Add("自动检测");
                return modes.Count == 0 ? "直连" : string.Join(" + ", modes);
            }
        }
    }

    public static class NativeProxy {
        [StructLayout(LayoutKind.Explicit)]
        struct Value {
            [FieldOffset(0)] public uint Number;
            [FieldOffset(0)] public IntPtr Text;
            [FieldOffset(0)] public long FileTime;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Option { public uint Id; public Value Data; }
        [StructLayout(LayoutKind.Sequential)]
        struct OptionList {
            public uint Size; public IntPtr Connection; public uint Count; public uint Error; public IntPtr Options;
        }
        [DllImport("wininet.dll", SetLastError=true, CharSet=CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool InternetQueryOptionW(IntPtr handle, uint option, ref OptionList list, ref uint size);
        [DllImport("wininet.dll", SetLastError=true, CharSet=CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool InternetSetOptionW(IntPtr handle, uint option, IntPtr buffer, uint size);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);

        public static ProxySettings Read() {
            int stride = Marshal.SizeOf(typeof(Option));
            IntPtr buffer = Marshal.AllocHGlobal(stride * 4);
            for (int i=0; i<stride*4; i++) Marshal.WriteByte(buffer, i, 0);
            try {
                for (int i=0; i<4; i++) {
                    var opt = new Option(); opt.Id = (uint)(i+1);
                    Marshal.StructureToPtr(opt, IntPtr.Add(buffer, i*stride), false);
                }
                var list = new OptionList { Size=(uint)Marshal.SizeOf(typeof(OptionList)), Count=4, Options=buffer };
                uint size = list.Size;
                if (!InternetQueryOptionW(IntPtr.Zero, 75, ref list, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 Windows 实际代理配置失败");
                var result = new ProxySettings();
                result.Flags = ((Option)Marshal.PtrToStructure(buffer, typeof(Option))).Data.Number;
                string[] text = new string[3];
                for (int i=1; i<4; i++) {
                    var opt = (Option)Marshal.PtrToStructure(IntPtr.Add(buffer, i*stride), typeof(Option));
                    text[i-1] = Marshal.PtrToStringUni(opt.Data.Text) ?? "";
                }
                result.Server=text[0]; result.Bypass=text[1]; result.PacUrl=text[2];
                return result;
            } finally {
                for (int i=1; i<4; i++) {
                    var opt = (Option)Marshal.PtrToStructure(IntPtr.Add(buffer, i*stride), typeof(Option));
                    if (opt.Data.Text != IntPtr.Zero) GlobalFree(opt.Data.Text);
                }
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static void Write(ProxySettings settings) {
            int stride = Marshal.SizeOf(typeof(Option));
            IntPtr buffer = Marshal.AllocHGlobal(stride * 4), listBuffer = IntPtr.Zero;
            IntPtr[] strings = new IntPtr[3];
            try {
                string[] values = {settings.Server, settings.Bypass, settings.PacUrl};
                for (int i=0; i<4; i++) {
                    var opt = new Option(); opt.Id=(uint)(i+1);
                    if (i==0) opt.Data.Number=settings.Flags;
                    else { strings[i-1]=Marshal.StringToHGlobalUni(values[i-1] ?? ""); opt.Data.Text=strings[i-1]; }
                    Marshal.StructureToPtr(opt, IntPtr.Add(buffer, i*stride), false);
                }
                var list = new OptionList { Size=(uint)Marshal.SizeOf(typeof(OptionList)), Count=4, Options=buffer };
                listBuffer=Marshal.AllocHGlobal((int)list.Size);
                Marshal.StructureToPtr(list, listBuffer, false);
                if (!InternetSetOptionW(IntPtr.Zero, 75, listBuffer, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "写入系统代理失败");
                Refresh();
            } finally {
                foreach (IntPtr str in strings) if (str != IntPtr.Zero) Marshal.FreeHGlobal(str);
                if (listBuffer != IntPtr.Zero) Marshal.FreeHGlobal(listBuffer);
                Marshal.FreeHGlobal(buffer);
            }
        }
        public static void Refresh() {
            foreach (uint option in new uint[] {39, 37})
                if (!InternetSetOptionW(IntPtr.Zero, option, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 刷新通知发送失败；请重新诊断确认当前设置");
        }
        public static string LayoutInfo() {
            return "Pointer=" + IntPtr.Size + ", Option=" + Marshal.SizeOf(typeof(Option)) + ", List=" + Marshal.SizeOf(typeof(OptionList));
        }
    }

    // Read existing browser -> local proxy connections (IPv4), without attaching to a browser.
    public static class TcpTable {
        [DllImport("iphlpapi.dll", SetLastError=true)]
        static extern uint GetExtendedTcpTable(IntPtr buffer, ref int size, bool order, int family, int tableClass, uint reserved);
        public static Dictionary<int,int> BrowserConnections(HashSet<int> processIds, int proxyPort) {
            var result = new Dictionary<int,int>();
            int size=0;
            uint status=GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
            if (status!=122 && status!=0) throw new Win32Exception((int)status);
            IntPtr buffer=Marshal.AllocHGlobal(size);
            try {
                status=GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                if (status!=0) throw new Win32Exception((int)status);
                int count=Marshal.ReadInt32(buffer);
                for(int i=0; i<count; i++) {
                    IntPtr row=IntPtr.Add(buffer, 4+i*24);
                    int state=Marshal.ReadInt32(row), pid=Marshal.ReadInt32(row,20);
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid.
                    int remotePort=Marshal.ReadByte(row,16)*256+Marshal.ReadByte(row,17);
                    bool loopback=Marshal.ReadByte(row,12)==127;
                    if(state==5 && loopback && remotePort==proxyPort && processIds.Contains(pid)) {
                        if(!result.ContainsKey(pid)) result[pid]=0;
                        result[pid]++;
                    }
                }
                return result;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
