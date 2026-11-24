// SysWidget - tiny native system monitor widget for Windows.
// C# 5 / .NET Framework 4.x, no dependencies. Build with build.bat (uses csc.exe shipped with Windows).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("SysWidget")]
[assembly: System.Reflection.AssemblyDescription("Tiny native system monitor widget")]
[assembly: System.Reflection.AssemblyProduct("SysWidget")]
[assembly: System.Reflection.AssemblyCompany("Dimokat")]
[assembly: System.Reflection.AssemblyCopyright("\u00A9 2026 Dimokat")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    const string MutexName = "Local\\SysWidget.SingleInstance";

    // Launching SysWidget again while it runs (Start menu, shortcut) brings a hidden widget back.
    // (Posted straight to its windows: Windows drops broadcasts from a normal process to the elevated one.)
    delegate bool EnumProc(IntPtr hwnd, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    static void PingRunning()
    {
        int msg = RegisterWindowMessage("SysWidget.Show");
        var self = Process.GetCurrentProcess(); var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(self.ProcessName)) { if (p.Id != self.Id) pids.Add(p.Id); p.Dispose(); }
        if (pids.Count == 0) return;
        EnumWindows((h, l) => { int pid; GetWindowThreadProcessId(h, out pid); if (pids.Contains(pid)) PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero);
    }
    static bool AlreadyRunning()
    {
        try { using (Mutex.OpenExisting(MutexName)) return true; }
        catch (UnauthorizedAccessException) { return true; } // owned by the elevated instance
        catch { return false; }
    }

    static bool IsAdmin()
    {
        try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--shots") { SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Environment.Exit(WidgetForm.SaveShots(args[1])); }
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            // Off-screen checks of every mode and language (see SelfTest.cs); runs beside a running widget.
            SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Environment.Exit(WidgetForm.RunSelfTest(args.Length > 1 ? args[1] : "selftest.txt"));
        }
        if (args.Length == 2 && args[0] == "--snapshot")
        {
            // Renders one frame to a PNG and exits (used for testing without touching the screen).
            using (var f = new WidgetForm()) { Thread.Sleep(1100); f.SaveSnapshot(args[1]); }
            return;
        }
        // Started without admin rights (Start menu, double-click)? If the startup task exists, start through it
        // instead: it runs with highest privileges (needed for FPS) and shows no UAC prompt.
        // After importing settings the widget restarts itself: wait for the old instance to exit first.
        if (Array.IndexOf(args, "--restart") >= 0) for (int i = 0; i < 100 && AlreadyRunning(); i++) Thread.Sleep(100);
        if (AlreadyRunning()) { PingRunning(); return; }
        if (args.Length == 0 && !IsAdmin() && WidgetForm.Schtasks("/Query /TN \"SysWidget\"") == 0 &&
            WidgetForm.Schtasks("/Run /TN \"SysWidget\"") == 0) return;
        bool created;
        using (var mutex = new Mutex(true, MutexName, out created))
        {
            if (!created) { PingRunning(); return; }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new WidgetForm());
        }
    }
}

sealed class Ring
{
    readonly float[] data; int head, count;
    public Ring(int capacity) { data = new float[capacity]; }
    public int Count { get { return count; } }
    public int Capacity { get { return data.Length; } }
    public void Add(float v) { data[head] = v; head = (head + 1) % data.Length; if (count < data.Length) count++; }
    public float this[int k] { get { int start = (head - count + data.Length) % data.Length; return data[(start + k) % data.Length]; } }
    public float Max() { float m = 0; for (int k = 0; k < count; k++) { float v = this[k]; if (v > m) m = v; } return m; }
    public Ring Resized(int cap) { var r = new Ring(cap); for (int k = Math.Max(0, count - cap); k < count; k++) r.Add(this[k]); return r; }
}

static class Nvml
{
    [StructLayout(LayoutKind.Sequential)] public struct Util { public uint gpu, memory; }
    [StructLayout(LayoutKind.Sequential)] public struct Mem { public ulong total, free, used; }
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlShutdown();
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Util util);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Mem mem);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
}

struct DriveStat { public string Name; public double Free, Total; }

// Counts frames presented by one process, using the same ETW events PresentMon uses.
// Real-time ETW sessions need admin rights; without them Error is set and FPS shows "needs admin".
sealed class FpsCounter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct EVENT_TRACE_HEADER { public ushort Size, FieldTypeFlags; public byte Type, Level; public ushort Version; public int ThreadId, ProcessId; public long TimeStamp; public Guid Guid; public int KernelTime, UserTime; }
    [StructLayout(LayoutKind.Sequential)] struct EVENT_TRACE { public EVENT_TRACE_HEADER Header; public int InstanceId, ParentInstanceId; public Guid ParentGuid; public IntPtr MofData; public int MofLength, ClientContext; }
    [StructLayout(LayoutKind.Sequential)] struct SYSTEMTIME { public short a, b, c, d, e, f, g, h; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TIME_ZONE_INFORMATION
    {
        public int Bias; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName; public SYSTEMTIME StandardDate; public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName; public SYSTEMTIME DaylightDate; public int DaylightBias;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct TRACE_LOGFILE_HEADER
    {
        public int BufferSize, Version, ProviderVersion, NumberOfProcessors; public long EndTime;
        public int TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten; public Guid LogInstanceGuid;
        public IntPtr LoggerName, LogFileName; public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime, PerfFreq, StartTime; public int ReservedFlags, BuffersLost;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void EventRecordCallback(IntPtr record);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int BufferCallback(IntPtr logfile);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct EVENT_TRACE_LOGFILEW
    {
        public string LogFileName, LoggerName; public long CurrentTime; public int BuffersRead, ProcessTraceMode;
        public EVENT_TRACE CurrentEvent; public TRACE_LOGFILE_HEADER LogfileHeader; public BufferCallback BufferCallback;
        public int BufferSize, Filled, EventsLost; public EventRecordCallback EventRecordCallback; public int IsKernelTrace; public IntPtr Context;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int StartTraceW(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int ControlTraceW(ulong handle, string name, IntPtr props, uint code);
    [DllImport("advapi32.dll")] static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr param);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);
    [DllImport("advapi32.dll")] static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern int CloseTrace(ulong handle);

    const string Session = "SysWidgetFPS";
    const ulong INVALID = 0xFFFFFFFFFFFFFFFF;
    const int PropsSize = 120 + 128;
    static readonly Guid DXGI = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    static readonly Guid DXGK = new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D");

    ulong session, trace = INVALID; EventRecordCallback cb; Thread thread;
    public string Error;
    volatile int target;
    readonly object gate = new object();
    readonly int[] n = new int[3]; readonly long[] first = new long[3], last = new long[3];
    float lastFps; readonly Stopwatch sinceFrames = Stopwatch.StartNew();

    static void InitProps(IntPtr p)
    {
        Marshal.Copy(new byte[PropsSize], 0, p, PropsSize);
        Marshal.WriteInt32(p, 0, PropsSize);     // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);            // Wnode.ClientContext = QPC timestamps
        Marshal.WriteInt32(p, 44, 0x20000);      // WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);           // buffer size, KB
        Marshal.WriteInt32(p, 64, 0x100);        // EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);            // flush every second
        Marshal.WriteInt32(p, 116, 120);         // LoggerNameOffset
    }

    public bool Start()
    {
        IntPtr p = Marshal.AllocHGlobal(PropsSize);
        try
        {
            InitProps(p); ControlTraceW(0, Session, p, 1); // stop a session left over from a crash
            InitProps(p);
            int rc = StartTraceW(out session, Session, p);
            if (rc != 0) { session = 0; Error = rc == 5 ? "needs admin" : "error " + rc; return false; }
        }
        finally { Marshal.FreeHGlobal(p); }
        Guid g = DXGI; EnableTraceEx2(session, ref g, 1, 4, 0, 0, 0, IntPtr.Zero);
        g = D3D9; EnableTraceEx2(session, ref g, 1, 4, 0, 0, 0, IntPtr.Zero);
        // DxgKrnl Present_Info (184) is emitted for every present, whatever the API (DirectX, OpenGL, Vulkan).
        // It sits under the very busy "Base" keyword, so ask Windows to deliver only event 184.
        if (!EnableIds(DXGK, 0x1 /*Base*/, 184)) { g = DXGK; EnableTraceEx2(session, ref g, 1, 4, 0x1, 0, 0, IntPtr.Zero); }
        cb = OnEvent;
        var lf = new EVENT_TRACE_LOGFILEW { LoggerName = Session, ProcessTraceMode = 0x100 | 0x10000000, EventRecordCallback = cb };
        trace = OpenTraceW(ref lf);
        if (trace == INVALID) { Error = "error"; Dispose(); return false; }
        thread = new Thread(delegate () { ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); });
        thread.IsBackground = true; thread.Start();
        return true;
    }

    // EnableTraceEx2 with an EVENT_FILTER_TYPE_EVENT_ID filter (Windows 8.1+), so only the listed event IDs are delivered.
    bool EnableIds(Guid provider, ulong keywords, params ushort[] ids)
    {
        int dataSize = 4 + 2 * ids.Length;
        IntPtr data = Marshal.AllocHGlobal(dataSize), desc = Marshal.AllocHGlobal(16), prm = Marshal.AllocHGlobal(48);
        try
        {
            Marshal.WriteByte(data, 0, 1);                       // FilterIn = TRUE
            Marshal.WriteByte(data, 1, 0);                       // Reserved
            Marshal.WriteInt16(data, 2, (short)ids.Length);      // Count
            for (int i = 0; i < ids.Length; i++) Marshal.WriteInt16(data, 4 + 2 * i, (short)ids[i]);
            Marshal.WriteInt64(desc, 0, data.ToInt64());         // EVENT_FILTER_DESCRIPTOR.Ptr
            Marshal.WriteInt32(desc, 8, dataSize);               //   .Size
            Marshal.WriteInt32(desc, 12, unchecked((int)0x80000200)); // .Type = EVENT_FILTER_TYPE_EVENT_ID
            Marshal.Copy(new byte[48], 0, prm, 48);
            Marshal.WriteInt32(prm, 0, 2);                       // ENABLE_TRACE_PARAMETERS_VERSION_2
            Marshal.WriteInt64(prm, 32, desc.ToInt64());         // EnableFilterDesc
            Marshal.WriteInt32(prm, 40, 1);                      // FilterDescCount
            return EnableTraceEx2(session, ref provider, 1, 4, keywords, 0, 0, prm) == 0;
        }
        finally { Marshal.FreeHGlobal(prm); Marshal.FreeHGlobal(desc); Marshal.FreeHGlobal(data); }
    }

    void OnEvent(IntPtr r)
    {
        int pid = Marshal.ReadInt32(r, 12);
        if (pid == 0 || pid != target) return;
        int g0 = Marshal.ReadInt32(r, 24), id = (ushort)Marshal.ReadInt16(r, 40), src;
        if (g0 == unchecked((int)0xCA11C036)) { if (id != 42) return; src = 0; }       // DXGI Present_Start
        else if (g0 == 0x783ACA0A) { if (id != 1) return; src = 1; }                    // D3D9 Present_Start
        else if (g0 == unchecked((int)0x802EC45A)) { if (id != 184) return; src = 2; }  // DxgKrnl Present_Info
        else return;
        long ts = Marshal.ReadInt64(r, 16);
        lock (gate) { if (n[src] == 0) first[src] = ts; last[src] = ts; n[src]++; }
    }

    public void SetTarget(int pid) { target = pid; lock (gate) { n[0] = n[1] = n[2] = 0; } lastFps = 0; }

    // Rate from event timestamps, so ETW's batched delivery doesn't make the number jump.
    public float Read()
    {
        int c = 0; long a = 0, b = 0;
        lock (gate)
        {
            int src = n[0] >= 2 ? 0 : n[1] >= 2 ? 1 : n[2] >= 2 ? 2 : -1;
            if (src >= 0) { c = n[src]; a = first[src]; b = last[src]; }
            n[0] = n[1] = n[2] = 0;
        }
        if (c >= 2 && b > a) { lastFps = (float)((c - 1) * (double)Stopwatch.Frequency / (b - a)); sinceFrames.Restart(); }
        else if (sinceFrames.Elapsed.TotalSeconds > 2.5) lastFps = 0;
        return lastFps;
    }

    public void Dispose()
    {
        if (trace != INVALID) { CloseTrace(trace); trace = INVALID; }
        if (session != 0)
        {
            IntPtr p = Marshal.AllocHGlobal(PropsSize);
            try { InitProps(p); ControlTraceW(session, null, p, 1); } finally { Marshal.FreeHGlobal(p); }
            session = 0;
        }
    }
}

// Thin wrapper over the native PDH API (much lighter than System.Diagnostics.PerformanceCounter).
sealed class Pdh : IDisposable
{
    [StructLayout(LayoutKind.Explicit)] struct FMT { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr counter, uint fmt, IntPtr type, out FMT value);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhExpandWildCardPath(string src, string path, char[] list, ref uint len, uint flags);

    IntPtr q;
    public Pdh() { if (PdhOpenQuery(null, IntPtr.Zero, out q) != 0) q = IntPtr.Zero; }
    public IntPtr Add(string path) { IntPtr c; return q != IntPtr.Zero && PdhAddEnglishCounter(q, path, IntPtr.Zero, out c) == 0 ? c : IntPtr.Zero; }
    public void Collect() { if (q != IntPtr.Zero) PdhCollectQueryData(q); }
    public static double Get(IntPtr c)
    {
        FMT v; if (c == IntPtr.Zero) return double.NaN;
        return PdhGetFormattedCounterValue(c, 0x200 | 0x8000 /*DOUBLE|NOCAP100*/, IntPtr.Zero, out v) == 0 && (v.CStatus == 0 || v.CStatus == 1) ? v.Value : double.NaN;
    }
    public static string[] Expand(string path)
    {
        uint len = 0; PdhExpandWildCardPath(null, path, null, ref len, 0);
        if (len == 0) return new string[0];
        var buf = new char[len]; if (PdhExpandWildCardPath(null, path, buf, ref len, 0) != 0) return new string[0];
        return new string(buf).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
    }
    public void Dispose() { if (q != IntPtr.Zero) { PdhCloseQuery(q); q = IntPtr.Zero; } }
}

sealed partial class WidgetForm : Form
{
    // ------------------------------------------------------------------ Win32
    const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TRANSPARENT = 0x20;
    const int GWL_EXSTYLE = -20, WM_ENTERSIZEMOVE = 0x231, VK_CONTROL = 0x11;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    const int WM_NCHITTEST = 0x84, WM_NCRBUTTONDOWN = 0xA4, WM_NCRBUTTONUP = 0xA5, WM_RBUTTONUP = 0x205,
              WM_NCLBUTTONDBLCLK = 0xA3, WM_LBUTTONDBLCLK = 0x203, WM_WINDOWPOSCHANGING = 0x46,
              WM_EXITSIZEMOVE = 0x232, WM_MOUSEACTIVATE = 0x21, WM_DISPLAYCHANGE = 0x7E;
    static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_BOTTOM = new IntPtr(1), HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
    const uint GW_HWNDPREV = 3;

    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int a, int b) { cx = a; cy = b; } }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    class MEMSTATUS
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMSTATUS)); public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow")] static extern bool UpdateLayeredWindowAt(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    // Same call with only the opacity changed (no new picture), used for fading.
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow")] static extern bool UpdateLayeredAlpha(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMSTATUS m);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint evMin, uint evMax, IntPtr hmod, WinEventProc cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

    // ------------------------------------------------------------------ settings
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    const double GB = 1073741824.0;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly string iniPath = IniPath();

    // A copy that already keeps SysWidget.ini next to itself stays portable; otherwise settings live in %APPDATA%\SysWidget
    // (an installed copy in Program Files can't write next to itself).
    static string IniPath()
    {
        string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SysWidget.ini");
        if (File.Exists(local)) return local;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysWidget");
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, "SysWidget.ini");
    }
    bool onTop, locked, desktopRaised;
    bool clickThrough, interactive, inMove, hasMoveTarget; Point moveTarget;
    readonly System.Windows.Forms.Timer ctTimer = new System.Windows.Forms.Timer();
    // Hide under cursor: the widget fades out and lets clicks through while the mouse is over it; Ctrl brings it back.
    bool hideHover; float fade = 1f, fadeTarget = 1f;
    readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
    int posX = int.MinValue, posY = int.MinValue;

    // ------------------------------------------------------------------ look
    float dpi, s; int W; int H; float pad;
    Font fLabel, fValue, fSmall;
    float scale = 1f; int opacity = 100; bool miniPref;
    // Mini mode as chosen, or forced while a fullscreen app is in front (game-aware mode).
    bool mini { get { return miniPref || (fsActive && fsMode == 1); } }
    string anchor = ""; Point moveStart, grab;
    readonly StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone();
    SolidBrush brLabel, brValue, brDim, brWarn, brHot;
    Color ink = Color.White; // text and lines: white on the dark/accent panel, near-black on the light one
    static readonly Color cCpu = Color.FromArgb(0x4F, 0xC3, 0xF7), cGpu = Color.FromArgb(0x81, 0xC7, 0x84),
        cRam = Color.FromArgb(0xFF, 0xB7, 0x4D), cDown = Color.FromArgb(0xBA, 0x68, 0xC8), cUp = Color.FromArgb(0xF0, 0x62, 0x92),
        cDisk = Color.FromArgb(0x90, 0xA4, 0xAE), cFps = Color.FromArgb(0xFF, 0xD5, 0x4F),
        cPing = Color.FromArgb(0x4D, 0xD0, 0xE1), cBat = Color.FromArgb(0xAE, 0xD5, 0x81), cMedia = Color.FromArgb(0xCE, 0x93, 0xD8), cWarn = Color.FromArgb(0xFF, 0xA7, 0x26), cHot = Color.FromArgb(0xEF, 0x53, 0x50);
    Bitmap canvas;

    // ------------------------------------------------------------------ sensors
    readonly Pdh pdh = new Pdh();
    IntPtr pcCpu, pcTemp, pcDiskR, pcDiskW;
    Ring hCpu = new Ring(60), hDown = new Ring(60), hUp = new Ring(60), hFps = new Ring(60), hPing = new Ring(60);
    readonly Pinger pinger = new Pinger();
    FpsCounter fps; float fpsVal; string fpsApp = ""; int fpsPid = -1; readonly int ownPid = Process.GetCurrentProcess().Id;
    float cpu, cpuTemp = float.NaN, ramUsed, ramTotal, diskR, diskW, down, up;
    List<Gpu> gpus = new List<Gpu>();
    List<NetworkInterface> nics = new List<NetworkInterface>(); string nicKey = ""; long lastRx = -1, lastTx = -1;
    readonly Stopwatch netClock = new Stopwatch();
    List<DriveStat> drives = new List<DriveStat>();
    int tick;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    ContextMenuStrip menu; ToolStripMenuItem miTop, miLock, miHide;
    NotifyIcon tray; bool hidden; string trayText = "", notifiedTag;
    static readonly int WM_SHOWME = Program.RegisterWindowMessage("SysWidget.Show");
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr info);
    WinEventProc fgProc; IntPtr fgHook;

    public WidgetForm()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX / 96f;
        sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        UpdateInk();

        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Text = "SysWidget";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        LoadSettings();
        Lang.CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysWidget", "lang");
        Lang.Load(langChoice);
        UpdatePanelColor();
        ApplyScale();
        InitSensors();
        ResizeHistory();
        Sample();
        if (mini) W = MiniWidth();
        H = Measure();
        Size = WinSize();
        PlaceBody(ValidPosition(posX, posY));
        if (anchor != "") SnapTo(anchor);
        BuildMenu();
        BuildTray();
        timer.Interval = intervalMs;
        timer.Tick += delegate { OnTick(); };
        // Click-through / hide under cursor: poll Ctrl + cursor 20x/s, only while one of them is on.
        ctTimer.Interval = 50;
        ctTimer.Tick += delegate { CheckInteractive(); };
        fadeTimer.Tick += delegate { FadeStep(); };
        hoverTimer.Tick += delegate { HoverStep(); };
        islandAnim.Tick += delegate { IslandStep(); };
    }

    void ApplyScale()
    {
        s = dpi * scale; W = (int)Math.Round(250 * s); pad = 12 * s;
        M = (int)Math.Ceiling(10 * s) + 2;
        if (fLabel != null) { fLabel.Dispose(); fValue.Dispose(); fSmall.Dispose(); }
        // Whole-pixel font sizes hint much more crisply at small scales.
        fLabel = Lang.MakeFont((float)Math.Max(7, Math.Round(10.5f * s)), true, GraphicsUnit.Pixel);
        fValue = Lang.MakeFont((float)Math.Max(8, Math.Round(12.5f * s)), false, GraphicsUnit.Pixel);
        fSmall = Lang.MakeFont((float)Math.Max(7, Math.Round(10.5f * s)), false, GraphicsUnit.Pixel);
    }

    void Relayout()
    {
        CloseIslandNow();
        Rectangle old = IsHandleCreated ? Body : Rectangle.Empty;
        ApplyScale();
        if (mini) W = MiniWidth();
        H = Measure();
        Size = WinSize();
        if (IsHandleCreated)
        {
            if (anchor != "") { SnapTo(anchor); Render(); return; }
            // Keep the right edge (right half of screen) and a bottom-docked edge in place.
            var wa = Screen.FromRectangle(old).WorkingArea;
            int x = old.X + old.Width / 2 > wa.Left + wa.Width / 2 ? old.Right - W : old.X;
            int y = old.Bottom >= wa.Bottom ? old.Bottom - H : old.Y;
            PlaceBody(ClampTo(new Point(x, y), new Size(W, H), wa));
        }
        Render();
    }

    // ---- the panel ("body") vs the window: the window has a transparent margin M around the
    // panel so edge fillets can be drawn when the panel is docked against a screen edge.
    int M;
    bool dL, dT, dR, dB;
    Size WinSize() { return new Size(W + 2 * M, H + 2 * M); }
    Rectangle Body { get { return islandOpen ? islandHome : pendingWin.HasValue ? new Rectangle(pendingWin.Value.X + M, pendingWin.Value.Y + M, W, H) : new Rectangle(Left + M, Top + M, W, H); } }

    // Moving or resizing a layered window and then giving it its new picture are two steps, and Windows can show
    // a frame in between: the old picture at the new place, a visible jump. Instead the new picture is drawn for
    // the new rectangle and UpdateLayeredWindow moves, resizes and repaints the window in one go.
    Rectangle? pendingWin; bool presenting;
    Rectangle WinRect { get { return pendingWin ?? new Rectangle(Left, Top, Width, Height); } }
    void SetWindowRect(Rectangle r)
    {
        if (r == new Rectangle(Left, Top, Width, Height)) return;
        pendingWin = r;
        try { Render(); }
        finally { pendingWin = null; }
        // WinForms doesn't always hear about a move made by UpdateLayeredWindow: re-read the real rectangle.
        presenting = true;
        try { UpdateBounds(); } finally { presenting = false; }
        if (r != new Rectangle(Left, Top, Width, Height)) SetBounds(r.X, r.Y, r.Width, r.Height); // nothing was drawn: just move
    }
    void PlaceBody(Point p) { Location = new Point(p.X - M, p.Y - M); }

    void UpdateDock(Rectangle b)
    {
        var wa = Screen.FromRectangle(b).WorkingArea;
        dL = b.Left <= wa.Left; dT = b.Top <= wa.Top; dR = b.Right >= wa.Right; dB = b.Bottom >= wa.Bottom;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (IsHandleCreated && !inMove && !presenting) Render();
    }

    // Panel outline: rounded corners when free; square where two docked edges meet;
    // a concave fillet flowing into the screen edge where only one side is docked.
    GraphicsPath PanelPath()
    {
        // The island (mini mode's default look) is a pill: fully rounded ends.
        float rad = mini && islandStyle ? (Vert ? Math.Min(14 * s, W / 2f) : Math.Max(10 * s, H / 2f)) : 10 * s, d = rad * 2, fd = Math.Min(rad, 10 * s) * 2;
        // Docked edges sit at -0.5 / W-0.5 so the outermost pixel row is fully covered (GDI+ pixel centres are integers).
        float L = dL ? -0.5f : 0.5f, T = dT ? -0.5f : 0.5f, R = W - 0.5f, B = H - 0.5f;
        var p = new GraphicsPath();
        if (dT && dL) p.AddLine(L, T, L, T);                       // top-left
        else if (dT) p.AddArc(L - fd, T, fd, fd, 0, -90);
        else if (dL) p.AddArc(L, T - fd, fd, fd, 180, -90);
        else p.AddArc(L, T, d, d, 180, 90);
        if (dT && dR) p.AddLine(R, T, R, T);                       // top-right
        else if (dT) p.AddArc(R, T, fd, fd, 270, -90);
        else if (dR) p.AddArc(R - fd, T - fd, fd, fd, 90, -90);
        else p.AddArc(R - d, T, d, d, 270, 90);
        if (dB && dR) p.AddLine(R, B, R, B);                       // bottom-right
        else if (dR) p.AddArc(R - fd, B, fd, fd, 0, -90);
        else if (dB) p.AddArc(R, B - fd, fd, fd, 180, -90);
        else p.AddArc(R - d, B - d, d, d, 0, 90);
        if (dB && dL) p.AddLine(L, B, L, B);                       // bottom-left
        else if (dB) p.AddArc(L - fd, B - fd, fd, fd, 90, -90);
        else if (dL) p.AddArc(L, B, fd, fd, 270, -90);
        else p.AddArc(L, B - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams; cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (PassThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    void SetTransparent(bool on)
    {
        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
    }

    // Clicks go through the widget unless Ctrl is held over it (click-through and hide under cursor).
    bool PassThrough { get { return clickThrough || hideHover; } }

    void CheckInteractive()
    {
        if (!PassThrough) { FadeTo(1f); return; }
        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool over = OverWidget(Cursor.Position);
        bool want = (ctrl && over) || inMove || (menu != null && menu.Visible);
        FadeTo(hideHover && over && !want ? 0f : 1f);
        if (want == interactive) return;
        interactive = want;
        SetTransparent(!want);
        Render();
    }

    void ApplyPassThrough()
    {
        interactive = false;
        SetTransparent(PassThrough);
        ctTimer.Enabled = PassThrough;
        if (!PassThrough) FadeTo(1f);
        Render();
    }

    // 0 normal, 1 clicks go through (hold Ctrl to use it), 2 hides under the cursor (hold Ctrl to use it)
    void SetMouseMode(int m) { clickThrough = m == 1; hideHover = m == 2; ApplyPassThrough(); SaveSettings(); }

    byte Alpha() { return (byte)Math.Round(opacity * 255 / 100.0 * fade); }

    void FadeTo(float t)
    {
        if (fadeTarget == t && (fade == t || fadeTimer.Enabled)) return;
        fadeTarget = t;
        fadeTimer.Enabled = true;
    }

    // ~140 ms from shown to gone (and back).
    void FadeStep()
    {
        const float step = 15f / 140f;
        fade = fade < fadeTarget ? Math.Min(fadeTarget, fade + step) : Math.Max(fadeTarget, fade - step);
        if (IsHandleCreated && !hidden)
        {
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = Alpha(), AlphaFormat = 1 };
            UpdateLayeredAlpha(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        if (fade == fadeTarget) fadeTimer.Enabled = false;
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (WM_SHOWME != 0) ChangeWindowMessageFilterEx(Handle, (uint)WM_SHOWME, 1 /*MSGFLT_ALLOW*/, IntPtr.Zero);
        if (hidden) ShowWindow(Handle, 0); // a layered window shows nothing until its first frame, so no flash
        else Render();
        if (onTop) SetOnTop(true); else SendToDesktopLevel();
        fgProc = OnForeground;
        fgHook = SetWinEventHook(3 /*EVENT_SYSTEM_FOREGROUND*/, 3, IntPtr.Zero, fgProc, 0, 0, 2 /*OUTOFCONTEXT|SKIPOWNPROCESS*/);
        timer.Start();
        ctTimer.Enabled = PassThrough;
        StartUpdateChecks();
        ApplyHotkeys();
        CheckFullscreen();
        if (!tipsSeen)
        {
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += delegate { t.Stop(); t.Dispose(); ShowTips(); };
            t.Start();
        }
        // First start after an update (one-click or Setup): say so, with a link to what's new.
        Version last;
        if (lastVersion != null && Version.TryParse(lastVersion, out last) && last < new Version(VersionText))
        {
            var t2 = new System.Windows.Forms.Timer { Interval = 2500 };
            t2.Tick += delegate
            {
                t2.Stop(); t2.Dispose();
                if (tray == null) return;
                balloonUpdate = false; balloonTaskMgr = false; balloonUrl = "https://github.com/" + Repo + "/releases/tag/v" + VersionText;
                tray.ShowBalloonTip(8000, Lang.F("notify.updated.title", "v" + VersionText), Lang.T("notify.updated.text"), ToolTipIcon.Info);
            };
            t2.Start();
        }
        if (lastVersion != VersionText) SaveSettings();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop(); ctTimer.Stop(); fadeTimer.Stop(); hoverTimer.Stop(); AnimStop();
        if (fgHook != IntPtr.Zero) UnhookWinEvent(fgHook);
        UnregisterHotKey(Handle, 1); UnregisterHotKey(Handle, 2);
        if (rtss) Rtss.Clear();
        HidePopup();
        if (!skipSave) SaveSettings();
        pdh.Dispose();
        if (fps != null) fps.Dispose();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        Gpus.Shutdown();
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------------ sampling
    void InitSensors()
    {
        pcCpu = pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        if (pcCpu == IntPtr.Zero) pcCpu = pdh.Add(@"\Processor(_Total)\% Processor Time");
        try
        {
            string pick = null;
            foreach (var p in Pdh.Expand(@"\Thermal Zone Information(*)\Temperature"))
            {
                if (pick == null) pick = p;
                if (p.IndexOf("thrm", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0) { pick = p; break; }
            }
            if (pick != null) pcTemp = pdh.Add(pick);
        }
        catch { }
        pcDiskR = pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        pcDiskW = pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        gpus = Gpus.Enumerate();
        Gpus.AttachNvml(gpus); Gpus.AttachAdl(gpus);
        foreach (var g in gpus)
        {
            if (g.NvDev != IntPtr.Zero) continue; // NVML reports memory itself
            string inst = Gpus.PdhInstance(g);
            g.PcDedicated = pdh.Add(@"\GPU Adapter Memory(" + inst + @")\Dedicated Usage");
            if (g.Integrated) g.PcShared = pdh.Add(@"\GPU Adapter Memory(" + inst + @")\Shared Usage");
            g.MemTotal = g.Integrated ? g.DedicatedTotal + g.SharedTotal : g.DedicatedTotal;
        }
        Battery.Sample();
        NormalizeLayout();
        pdh.Collect();
        RefreshNics(); RefreshDrives(); netClock.Start();
        fps = new FpsCounter();
        if (!selfTesting) fps.Start(); else fps.Error = "test"; // the self-test leaves the running widget's FPS session alone
    }

    void OnTick()
    {
        tick++;
        bool onAC = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;
        timer.Interval = onAC ? intervalMs : Math.Max(2000, intervalMs * 2);
        if (tick % 15 == 0) RefreshNics();
        if (accentBg && tick % 10 == 0) UpdatePanelColor(); // fallback if a colour-change message was missed
        if (tick % 30 == 0)
        {
            int before = drives.Count; RefreshDrives();
            if (drives.Count != before) Relayout();
        }
        Sample();
        UpdateTray();
        CheckAlerts();
        CheckFullscreen();
        // In mini mode the music part is as wide as the song title: re-measure when the song changes.
        if (mini && IsLive("media"))
        {
            string mt = NowPlaying.Has ? NowPlaying.Title : "";
            if (mt != miniTitle) { miniTitle = mt; if (MiniWidth() != W) Relayout(); }
        }
        IslandTick();
        UpdateRtss();
        if (hidden || fsHiddenNow) return;
        if (!onTop && !desktopRaised && DesktopIsAboveUs()) RaiseOverDesktop();
        if (onTop) KeepOnTop();
        // Nothing can see the widget (a fullscreen app or another window covers it): keep measuring, skip drawing.
        if (HiddenByFullscreen() || Covered()) stale = true;
        else { stale = false; Render(); }
    }

    void Sample()
    {
        bool onAC = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;
        pdh.Collect();
        double v = Pdh.Get(pcCpu); cpu = double.IsNaN(v) ? 0 : (float)Math.Max(0, Math.Min(100, v));
        hCpu.Add(cpu);
        v = Pdh.Get(pcTemp);
        if (!double.IsNaN(v)) { if (v > 1000) v /= 10; cpuTemp = v > 200 ? (float)(v - 273.15) : float.NaN; }
        var ms = new MEMSTATUS();
        if (GlobalMemoryStatusEx(ms)) { ramTotal = (float)(ms.ullTotalPhys / GB); ramUsed = (float)((ms.ullTotalPhys - ms.ullAvailPhys) / GB); }
        v = Pdh.Get(pcDiskR); diskR = double.IsNaN(v) ? 0 : (float)v;
        v = Pdh.Get(pcDiskW); diskW = double.IsNaN(v) ? 0 : (float)v;
        if (IsLive("net")) SampleNet();
        SampleGpus(onAC);
        if (IsLive("fps")) SampleFps();
        if (IsLive("battery")) Battery.Sample();
        if (IsLive("ping")) { pinger.Send(); float pm = pinger.Ms; hPing.Add(float.IsNaN(pm) ? 0 : pm); }
        if (IsLive("media") && tick % 2 == 0) NowPlaying.Poll();
    }

    void SampleGpus(bool onAC)
    {
        foreach (var g in gpus)
        {
            if (!IsLive("gpu:" + g.Key)) continue;
            // A laptop's discrete GPU is normally powered down on battery; querying it would wake it, so don't.
            g.Idle = !onAC && !g.Integrated;
            if (g.Idle) { g.History.Add(0); continue; }
            if (g.NvDev != IntPtr.Zero) Gpus.SampleNvml(g);
            else
            {
                if (!Gpus.SampleAdl(g)) Gpus.SampleKmt(g);
                double ded = Pdh.Get(g.PcDedicated), sh = Pdh.Get(g.PcShared);
                if (!double.IsNaN(ded)) g.MemUsed = (float)(ded + (double.IsNaN(sh) ? 0 : sh));
            }
            g.History.Add(g.Load);
        }
    }

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    void SampleFps()
    {
        if (fps == null || fps.Error != null) { fpsVal = 0; hFps.Add(0); return; }
        IntPtr fg = GetForegroundWindow(); int pid = 0;
        if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out pid);
        if (pid == ownPid) pid = 0;
        if (pid != fpsPid)
        {
            fpsPid = pid; fps.SetTarget(pid); fpsApp = "";
            try { if (pid > 0) using (var p = Process.GetProcessById(pid)) fpsApp = p.ProcessName; } catch { }
        }
        fpsVal = fps.Read(); hFps.Add(fpsVal);
    }

    string FpsText()
    {
        if (fps == null) return "\u2014";
        if (fps.Error != null) return Lang.T(fps.Error == "needs admin" ? "fps.admin" : "fps.error");
        return fpsVal > 0 ? N(fpsVal, "0") : "\u2014";
    }

    void SampleNet()
    {
        long rx = 0, tx = 0;
        foreach (var n in nics) { try { var st = n.GetIPStatistics(); rx += st.BytesReceived; tx += st.BytesSent; } catch { } }
        double sec = netClock.Elapsed.TotalSeconds; netClock.Restart();
        if (lastRx >= 0 && sec > 0.2) { down = (float)Math.Max(0, (rx - lastRx) / sec); up = (float)Math.Max(0, (tx - lastTx) / sec); }
        else { down = up = 0; }
        lastRx = rx; lastTx = tx;
        hDown.Add(down); hUp.Add(up);
    }

    void RefreshNics()
    {
        var list = new List<NetworkInterface>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                bool hasGateway = false;
                try
                {
                    foreach (var a in n.GetIPProperties().GatewayAddresses)
                        if (a.Address != null && !a.Address.Equals(IPAddress.Any) && !a.Address.Equals(IPAddress.IPv6Any)) { hasGateway = true; break; }
                }
                catch { }
                if (hasGateway) list.Add(n);
            }
        }
        catch { }
        var ids = new StringBuilder(); foreach (var n in list) ids.Append(n.Id).Append('|');
        string key = ids.ToString();
        if (key != nicKey) { nicKey = key; lastRx = lastTx = -1; }
        nics = list;
    }

    void RefreshDrives()
    {
        var list = new List<DriveStat>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (list.Count >= 4) break;
                try { if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(new DriveStat { Name = d.Name.TrimEnd('\\'), Free = d.TotalFreeSpace, Total = d.TotalSize }); }
                catch { }
            }
        }
        catch { }
        drives = list;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    // Skip redrawing only when we're hidden: not on top, and the foreground window is
    // fullscreen on *our* monitor (a fullscreen video on another screen doesn't count).
    bool HiddenByFullscreen()
    {
        if (onTop) return false;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return false;
        var sb = new StringBuilder(32); GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls == "WorkerW" || cls == "Progman" || cls == "Shell_TrayWnd") return false;
        RECT r; if (!GetWindowRect(fg, out r)) return false;
        Rectangle mon = Screen.FromHandle(Handle).Bounds;
        return r.L <= mon.Left && r.T <= mon.Top && r.R >= mon.Right && r.B >= mon.Bottom;
    }

    // ---- game-aware mode: while a fullscreen app (game, video) is in front on our screen, switch to mini or hide
    string miniTitle;
    int fsMode; bool fsActive, fsHiddenNow, fsOverride; // fsOverride: shown again by hand during that fullscreen app
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    bool FullscreenInFront()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle || IsZoomed(fg)) return false; // a maximized window isn't fullscreen
        int pid; GetWindowThreadProcessId(fg, out pid); if (pid == ownPid) return false;
        var sb = new StringBuilder(32); GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls == "WorkerW" || cls == "Progman" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
        RECT r; if (!GetWindowRect(fg, out r)) return false;
        Rectangle mon = Screen.FromHandle(Handle).Bounds;
        return r.L <= mon.Left && r.T <= mon.Top && r.R >= mon.Right && r.B >= mon.Bottom;
    }

    void CheckFullscreen()
    {
        if (!IsHandleCreated) return;
        bool a = fsMode != 0 && FullscreenInFront();
        if (a != fsActive) { fsActive = a; if (!a) fsOverride = false; if (fsMode == 1) Relayout(); }
        bool hide = fsActive && fsMode == 2 && !hidden && !fsOverride;
        if (hide == fsHiddenNow) return;
        fsHiddenNow = hide;
        if (hide) { HidePopup(); CloseIslandNow(); ShowWindow(Handle, 0 /*SW_HIDE*/); }
        else if (!hidden)
        {
            Render(); ShowWindow(Handle, 8 /*SW_SHOWNA*/);
            if (onTop) SetOnTop(true); else { desktopRaised = false; SendToDesktopLevel(); }
        }
    }

    // True when a normal window above us covers the whole panel (only matters while not on top).
    bool stale;
    bool Covered()
    {
        if (onTop || !IsHandleCreated) return false;
        var b = Body; int guard = 0;
        for (IntPtr w = GetWindow(Handle, GW_HWNDPREV); w != IntPtr.Zero && guard++ < 500; w = GetWindow(w, GW_HWNDPREV))
        {
            if (!IsWindowVisible(w) || IsIconic(w)) continue;
            long ex = GetWindowLongPtr(w, GWL_EXSTYLE).ToInt64();
            if ((ex & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) != 0) continue; // overlays and see-through windows
            int cloaked; if (DwmGetWindowAttribute(w, 14 /*DWMWA_CLOAKED*/, out cloaked, 4) == 0 && cloaked != 0) continue;
            RECT r; if (!GetWindowRect(w, out r)) continue;
            if (r.L <= b.Left && r.T <= b.Top && r.R >= b.Right && r.B >= b.Bottom) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ drawing
    int Measure()
    {
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b)) return (int)Math.Ceiling(Draw(g));
    }

    void Render(bool dock = true)
    {
        if (!IsHandleCreated || H <= 0) return;
        if (islandOpen) { RenderIsland(); return; }
        if (dock) UpdateDock(Body);
        var ws = WinSize();
        EnsureCanvas(ws.Width, ws.Height);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.TranslateTransform(M, M);
            Draw(g);
        }
        Present(canvas);
    }

    public void SaveSnapshot(string path)
    {
        Sample();
        H = Measure();
        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(255, 40, 60, 45)); // stand-in wallpaper
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Draw(g);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    // The window's pixels live in one DIB that GDI+ draws into directly (premultiplied, the format
    // UpdateLayeredWindow wants), so a frame doesn't copy and convert the whole bitmap first.
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    IntPtr canvasDC, canvasDib, canvasOld;

    void EnsureCanvas(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (canvas != null && canvas.Width == w && canvas.Height == h) return;
        FreeCanvas();
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr bits = IntPtr.Zero, screen = GetDC(IntPtr.Zero);
        canvasDC = CreateCompatibleDC(screen); ReleaseDC(IntPtr.Zero, screen);
        canvasDib = canvasDC == IntPtr.Zero ? IntPtr.Zero : CreateDIBSection(canvasDC, ref bi, 0, out bits, IntPtr.Zero, 0);
        if (canvasDib == IntPtr.Zero)
        {
            if (canvasDC != IntPtr.Zero) DeleteDC(canvasDC);
            canvasDC = IntPtr.Zero;
            canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            return;
        }
        canvasOld = SelectObject(canvasDC, canvasDib);
        canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
    }

    void FreeCanvas()
    {
        if (canvas != null) { canvas.Dispose(); canvas = null; }
        if (canvasDC != IntPtr.Zero) { SelectObject(canvasDC, canvasOld); DeleteObject(canvasDib); DeleteDC(canvasDC); }
        canvasDC = canvasDib = canvasOld = IntPtr.Zero;
    }

    void Present(Bitmap bmp)
    {
        if (bmp == canvas && canvasDC != IntPtr.Zero)
        {
            GdiFlush();
            IntPtr sdc = GetDC(IntPtr.Zero);
            try
            {
                var sz = new SIZE(bmp.Width, bmp.Height); var p0 = new POINT();
                var bf = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = Alpha(), AlphaFormat = 1 };
                if (pendingWin.HasValue && pendingWin.Value.Size == bmp.Size)
                {
                    var at = new POINT { x = pendingWin.Value.X, y = pendingWin.Value.Y };
                    presenting = true;
                    try { UpdateLayeredWindowAt(Handle, sdc, ref at, ref sz, canvasDC, ref p0, 0, ref bf, 2 /*ULW_ALPHA*/); }
                    finally { presenting = false; }
                }
                else UpdateLayeredWindow(Handle, sdc, IntPtr.Zero, ref sz, canvasDC, ref p0, 0, ref bf, 2 /*ULW_ALPHA*/);
            }
            finally { ReleaseDC(IntPtr.Zero, sdc); }
            return;
        }
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hbmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hbmp);
            var size = new SIZE(bmp.Width, bmp.Height); var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = Alpha(), AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    float Draw(Graphics g)
    {
        float x = pad, w = W - 2 * pad, y = pad;
        sectionRects.Clear(); mediaBtns.Clear();

        if (H > 0)
            using (var path = PanelPath())
            using (var b = new SolidBrush(panelBg))
            using (var p = PassThrough && interactive ? new Pen(Color.FromArgb(200, cCpu), 1.5f * s) : new Pen(InkA(34), 1))
            {
                g.FillPath(b, path);
                var saved = g.Clip;
                if (dT) g.ExcludeClip(new Region(new RectangleF(-M, -M, W + 2 * M, M + 1)));
                if (dB) g.ExcludeClip(new Region(new RectangleF(-M, H - 1, W + 2 * M, M + 1)));
                if (dL) g.ExcludeClip(new Region(new RectangleF(-M, -M, M + 1, H + 2 * M)));
                if (dR) g.ExcludeClip(new Region(new RectangleF(W - 1, -M, M + 1, H + 2 * M)));
                g.DrawPath(p, path);
                g.Clip = saved;
            }

        if (updateTag != null) // small dot: an update is available
            using (var b = new SolidBrush(cFps)) g.FillEllipse(b, W - 7.5f * s, 3 * s, 4.5f * s, 4.5f * s);

        if (mini) { if (Vert) return MiniLayoutV(g, true); MiniLayout(g, true); return pad * 1.2f + 17 * s; }

        float gh = (compact ? 16 : 26) * s, sgap = (compact ? 7 : 11) * s, gap = 0;
        hdrH = (compact ? 15.5f : 17) * s; smallH = (compact ? 12.5f : 14) * s;
        foreach (var id in Sections())
        {
            if (!IsShown(id, false)) continue;
            float top = y;
            gap = DrawSection(g, id, x, w, ref y, gh, sgap);
            sectionRects[id] = new RectangleF(0, top - sgap / 2, W, y - top + sgap);
            y += gap;
        }
        return y - gap + pad;
    }

    float hdrH = 17, smallH = 14;

    float DrawSection(Graphics g, string id, float x, float w, ref float y, float gh, float sgap)
    {
        Color c = Shade(ColorOf(id));
        switch (id)
        {
            case "cpu":
                Header(g, ref y, Lang.T("label.cpu"), c, Pct(cpu), brValue, Opt(id, "temp") ? TempText(cpuTemp) : null, TempBrush(cpuTemp));
                if (Opt(id, "graph")) { y += 3 * s; Graph(g, x, y, w, gh, hCpu, 100, c, null, Color.Empty); y += gh; }
                return sgap;
            case "fps":
                Header(g, ref y, Lang.T("label.fps"), c, FpsText(), fps != null && fps.Error == null ? brValue : brDim, null, null);
                if (Opt(id, "app")) Small(g, ref y, fpsApp == "" ? Lang.T("fps.noapp") : fpsApp, "");
                if (Opt(id, "graph")) { y += 3 * s; Graph(g, x, y, w, gh, hFps, Math.Max(60f, hFps.Max()), c, null, Color.Empty); y += gh; }
                return sgap;
            case "ram":
                Header(g, ref y, Lang.T("label.ram"), c, Lang.F("ram.value", N(ramUsed, "0.0"), N(ramTotal, "0.0")), brValue, null, null);
                if (Opt(id, "bar")) { y += 5 * s; Bar(g, x, y, w, 5 * s, ramTotal > 0 ? ramUsed / ramTotal : 0, c); y += 5 * s; return sgap + s; }
                return sgap;
            case "net":
            {
                bool upOn = Opt(id, "up");
                Header(g, ref y, Lang.T("label.net"), c, "\u2193 " + RateNet(down), brValue, upOn ? "\u2191 " + RateNet(up) : null, brLabel);
                if (Opt(id, "graph"))
                {
                    y += 3 * s;
                    float netMax = Math.Max(64 * 1024f, upOn ? Math.Max(hDown.Max(), hUp.Max()) : hDown.Max());
                    Graph(g, x, y, w, gh, hDown, netMax, c, upOn ? hUp : null, Shade(ColorOf("net.up"))); y += gh;
                }
                return sgap;
            }
            case "disk":
            {
                bool rw = Opt(id, "rw");
                Header(g, ref y, Lang.T("label.disk"), c, rw ? Lang.F("disk.read", Rate(diskR)) : null, brValue, rw ? Lang.F("disk.write", Rate(diskW)) : null, brValue);
                if (!Opt(id, "free") || drives.Count == 0) return sgap;
                y += 4 * s; float dg = 0;
                foreach (var dr in drives)
                {
                    Small(g, ref y, dr.Name, Lang.F("disk.free", N((float)(dr.Free / GB), "0"), N((float)(dr.Total / GB), "0")));
                    y += 1 * s;
                    double frac = dr.Total > 0 ? 1 - dr.Free / dr.Total : 0;
                    Bar(g, x, y, w, 4 * s, (float)frac, dr.Total > 0 && dr.Free / dr.Total < 0.1 ? cHot : c);
                    dg = (compact ? 5 : 7) * s; y += 4 * s + dg;
                }
                y -= dg; return sgap;
            }
            case "ping":
            {
                float ms = pinger.Ms;
                Header(g, ref y, Lang.T("label.ping"), c, PingText(ms), float.IsNaN(ms) ? brDim : ms >= 150 ? brHot : ms >= 80 ? brWarn : brValue, null, null);
                if (Opt(id, "host")) Small(g, ref y, PingHostText(), "");
                if (Opt(id, "graph")) { y += 3 * s; Graph(g, x, y, w, gh, hPing, Math.Max(50f, hPing.Max()), c, null, Color.Empty); y += gh; }
                return sgap;
            }
            case "battery":
            {
                int pct = Battery.Percent; bool low = !Battery.OnAC && pct >= 0 && pct <= 20;
                Header(g, ref y, Lang.T("label.battery"), c, pct >= 0 ? Pct(pct) : "\u2014", low ? (pct <= 10 ? brHot : brWarn) : brValue, null, null);
                bool bTime = Opt(id, "time"), bPow = Opt(id, "power");
                if (bTime || bPow) Small(g, ref y, bTime ? BatteryText() : "", bPow && !float.IsNaN(Battery.Watts) ? Lang.F("unit.watts", N(Math.Abs(Battery.Watts), "0.0")) : "");
                if (Opt(id, "bar")) { y += 5 * s; Bar(g, x, y, w, 5 * s, pct / 100f, low ? cHot : c); y += 5 * s; return sgap + s; }
                return sgap;
            }
            case "media":
            {
                float top = y;
                Header(g, ref y, Lang.T("label.media"), c, null, null, null, null);
                if (Opt(id, "controls")) MediaButtons(g, top);
                Txt(g, Fit(g, NowPlaying.Has ? NowPlaying.Title : Lang.T("media.none"), fValue, w), fValue, NowPlaying.Has ? brValue : brDim, pad, y, sf);
                y += hdrH;
                string sub = NowPlaying.Has ? NowPlaying.Artist : "";
                if (NowPlaying.Has && Opt(id, "app") && NowPlaying.App != "") sub = sub == "" ? NowPlaying.App : sub + "  \u00B7  " + NowPlaying.App;
                Txt(g, Fit(g, sub, fSmall, w), fSmall, brDim, pad, y, sf);
                y += smallH;
                return sgap;
            }
        }
        var gpu = GpuById(id); if (gpu == null) return 0;
        string label = GpuLabel(gpu) + " \u00B7 " + gpu.Name;
        bool temp = Opt(id, "temp") && HasTemp(gpu);
        if (gpu.Idle) Header(g, ref y, label, c, Lang.T("gpu.idle"), brDim, null, null);
        else Header(g, ref y, label, c, Pct(gpu.Load), brValue, temp ? TempText(gpu.Temp) : null, TempBrush(gpu.Temp));
        // The detail row's presence depends only on settings and what the GPU can report, so the height stays put.
        bool mem = Opt(id, "mem") && gpu.MemTotal > 0, pw = Opt(id, "power") && HasPower(gpu);
        if (mem || pw)
            Small(g, ref y, mem && !gpu.Idle ? Lang.F(gpu.Integrated ? "gpu.memory" : "gpu.vram", N((float)(gpu.MemUsed / GB), "0.0"), N((float)(gpu.MemTotal / GB), "0.0")) : "",
                  pw && !gpu.Idle && !float.IsNaN(gpu.Power) ? Lang.F("unit.watts", N(gpu.Power, "0.0")) : "");
        if (Opt(id, "graph")) { y += 3 * s; Graph(g, x, y, w, gh, gpu.History, 100, c, null, Color.Empty); y += gh; }
        return sgap;
    }

    static bool HasTemp(Gpu g) { return g.NvDev != IntPtr.Zero || g.AdlIndex >= 0; }
    static bool HasPower(Gpu g) { return g.NvDev != IntPtr.Zero || g.AdlPower; }

    // ---- mini mode: one line, fixed-width slots so the strip doesn't jitter ----
    int MiniWidth()
    {
        if (Vert) return MiniColumnWidth();
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b)) return (int)Math.Ceiling(MiniLayout(g, false) + pad * 0.9f);
    }

    float MiniLayout(Graphics g, bool draw)
    {
        float x = pad * 0.9f, y = pad * 0.6f; bool any = false;
        string tTemp = fahrenheit ? "212\u00B0F" : "100\u00B0C";
        foreach (var id in Sections())
        {
            if (!IsShown(id, true)) continue;
            Color c = Shade(ColorOf(id));
            float x0 = x;
            segIcon = IconKind(id); // mini mode shows the section's icon instead of a dot
            switch (id)
            {
                case "cpu":
                {
                    bool t = Opt(id, "temp") && pcTemp != IntPtr.Zero;
                    Seg(g, draw, ref x, y, ref any, Lang.T("label.cpu"), c, Pct(cpu), brValue, "100%", t ? TempText(cpuTemp) : null, TempBrush(cpuTemp), t ? tTemp : null);
                    break;
                }
                case "fps": Seg(g, draw, ref x, y, ref any, Lang.T("label.fps"), c, FpsText(), brValue, "999", null, null, null); break;
                case "ram": Seg(g, draw, ref x, y, ref any, Lang.T("label.ram"), c, Lang.F("ram.short", N(ramUsed, "0.0")), brValue, Lang.F("ram.short", N(99.9f, "0.0")), null, null, null); break;
                case "net":
                {
                    bool upOn = Opt(id, "up");
                    string wide = netBits ? Lang.F("unit.mbits", "999") : Lang.F("unit.mbps", "999");
                    Seg(g, draw, ref x, y, ref any, Lang.T("label.net"), c, "\u2193 " + RateNet(down), brValue, "\u2193 " + wide, upOn ? "\u2191 " + RateNet(up) : null, brLabel, upOn ? "\u2191 " + wide : null);
                    break;
                }
                case "disk":
                    if (Opt(id, "free"))
                        foreach (var dr in drives)
                            Seg(g, draw, ref x, y, ref any, dr.Name, c, Lang.F("disk.short", N((float)(dr.Free / GB), "0")), brValue, Lang.F("disk.short", "9999"), null, null, null);
                    else Seg(g, draw, ref x, y, ref any, Lang.T("label.disk"), c, Lang.F("disk.read", Rate(diskR)), brValue, Lang.F("disk.read", Lang.F("unit.mbps", "999")),
                             Lang.F("disk.write", Rate(diskW)), brValue, Lang.F("disk.write", Lang.F("unit.mbps", "999")));
                    break;
                case "ping": Seg(g, draw, ref x, y, ref any, Lang.T("label.ping"), c, PingText(pinger.Ms), brValue, Lang.F("unit.ms", "999"), null, null, null); break;
                case "battery": Seg(g, draw, ref x, y, ref any, Lang.T("label.battery"), c, Battery.Percent >= 0 ? Pct(Battery.Percent) : "\u2014", brValue, "100%", null, null, null); break;
                case "media":
                {
                    if (any) x += 14 * s; any = true;
                    if (draw) TileIcon(g, "media", x + 6.5f * s, y + 8.5f * s, 13 * s, c);
                    x += 17 * s;
                    string mt = NowPlaying.Has ? NowPlaying.Title : Lang.T("media.none");
                    float mw = Math.Min(150 * s, g.MeasureString(mt, fValue, PointF.Empty, sf).Width); // as wide as the title, up to a limit
                    if (draw) Txt(g, Fit(g, mt, fValue, mw + 1), fValue, NowPlaying.Has ? brValue : brDim, x, y, sf);
                    x += mw;
                    break;
                }
                default:
                {
                    var gp = GpuById(id); if (gp == null) break;
                    bool t = Opt(id, "temp") && HasTemp(gp);
                    if (gp.Idle) Seg(g, draw, ref x, y, ref any, GpuLabel(gp), c, Lang.T("gpu.idle.short"), brDim, "100%", null, null, t ? tTemp : null);
                    else Seg(g, draw, ref x, y, ref any, GpuLabel(gp), c, Pct(gp.Load), brValue, "100%", t ? TempText(gp.Temp) : null, TempBrush(gp.Temp), t ? tTemp : null);
                    break;
                }
            }
            if (x > x0) sectionRects[id] = new RectangleF(x0, 0, x - x0 + 7 * s, H);
        }
        return x;
    }

    void Seg(Graphics g, bool draw, ref float x, float y, ref bool any, string label, Color accent,
             string v1, Brush b1, string t1, string v2, Brush b2, string t2)
    {
        if (any) x += 14 * s; any = true;
        if (segIcon != null) { if (draw) TileIcon(g, segIcon, x + 6.5f * s, y + 8.5f * s, 13 * s, accent); x += 17 * s; }
        else { if (draw) using (var b = new SolidBrush(accent)) g.FillEllipse(b, x, y + 5.5f * s, 6 * s, 6 * s); x += 11 * s; }
        if (draw) Txt(g, label, fLabel, brLabel, x, y + 1.5f * s, sf);
        x += g.MeasureString(label, fLabel, PointF.Empty, sf).Width + 6 * s;
        x += Slot(g, draw, x, y, v1, b1, t1);
        if (t2 != null) { x += 6 * s; x += Slot(g, draw, x, y, v2, b2, t2); }
    }

    float Slot(Graphics g, bool draw, float x, float y, string v, Brush b, string t)
    {
        float tw = g.MeasureString(t, fValue, PointF.Empty, sf).Width;
        if (v == null) return tw; // keep the space reserved (e.g. temperature not read yet)
        if (draw) Txt(g, v, fValue, b, x, y, sf);
        return Math.Max(tw, g.MeasureString(v, fValue, PointF.Empty, sf).Width);
    }

    // Text snapped to whole pixels, drawn with ClearType onto the opaque panel.
    static void Txt(Graphics g, string t, Font f, Brush b, float x, float y, StringFormat fmt)
    {
        g.DrawString(t, f, b, (float)Math.Round(x), (float)Math.Round(y), fmt);
        if (textLog != null && !string.IsNullOrEmpty(t)) // the self-test checks where texts land
            textLog.Add(new KeyValuePair<string, RectangleF>(t, new RectangleF((float)Math.Round(x), (float)Math.Round(y), g.MeasureString(t, f, PointF.Empty, fmt).Width, f.GetHeight(g))));
    }

    void Header(Graphics g, ref float y, string label, Color accent, string v1, Brush b1, string v2, Brush b2)
    {
        using (var b = new SolidBrush(accent)) g.FillEllipse(b, pad, y + 5.5f * s, 6 * s, 6 * s);
        Txt(g, label, fLabel, brLabel, pad + 11 * s, y + 1.5f * s, sf);
        float right = W - pad;
        if (v2 != null) { float w2 = g.MeasureString(v2, fValue, PointF.Empty, sf).Width; Txt(g, v2, fValue, b2, right - w2, y, sf); right -= w2 + 10 * s; }
        if (v1 != null) { float w1 = g.MeasureString(v1, fValue, PointF.Empty, sf).Width; Txt(g, v1, fValue, b1, right - w1, y, sf); }
        y += hdrH;
    }

    void Small(Graphics g, ref float y, string left, string right)
    {
        Txt(g, left, fSmall, brDim, pad, y, sf);
        float w = g.MeasureString(right, fSmall, PointF.Empty, sf).Width;
        Txt(g, right, fSmall, brLabel, W - pad - w, y, sf);
        y += smallH;
    }

    void Graph(Graphics g, float x, float y, float w, float h, Ring a, float max, Color ca, Ring b, Color cb)
    {
        using (var bg = Round(new RectangleF(x, y, w, h), 4 * s))
        {
            using (var br = new SolidBrush(InkA(lightBg ? 10 : 14))) g.FillPath(br, bg);
            var saved = g.Clip; g.SetClip(bg, CombineMode.Intersect);
            Series(g, x, y, w, h, a, max, ca, true);
            if (b != null) Series(g, x, y, w, h, b, max, cb, false);
            g.Clip = saved;
        }
    }

    void Series(Graphics g, float x, float y, float w, float h, Ring r, float max, Color c, bool fill)
    {
        int n = r.Count; if (n < 2 || max <= 0) return;
        float step = w / (r.Capacity - 1);
        var pts = new PointF[n];
        for (int k = 0; k < n; k++)
        {
            float v = Math.Max(0, Math.Min(1, r[k] / max));
            pts[k] = new PointF(x + w - (n - 1 - k) * step, y + h - 1 - v * (h - 2));
        }
        if (fill && graphStyle == 2) // bars
        {
            float bw = Math.Max(1f, step * 0.7f);
            using (var b = new SolidBrush(Color.FromArgb(210, c)))
                for (int k = 0; k < n; k++) if (pts[k].Y < y + h - 1) g.FillRectangle(b, pts[k].X - bw / 2, pts[k].Y, bw, y + h - pts[k].Y);
            return;
        }
        if (fill && graphStyle == 0) // filled area; graphStyle 1 = line only
        {
            var poly = new PointF[n + 2]; Array.Copy(pts, poly, n);
            poly[n] = new PointF(pts[n - 1].X, y + h); poly[n + 1] = new PointF(pts[0].X, y + h);
            using (var b = new SolidBrush(Color.FromArgb(55, c))) g.FillPolygon(b, poly);
        }
        using (var p = new Pen(Color.FromArgb(fill ? 230 : 200, c), 1.3f * s) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
    }

    void Bar(Graphics g, float x, float y, float w, float h, float frac, Color c)
    {
        frac = Math.Max(0, Math.Min(1, frac));
        using (var p = Round(new RectangleF(x, y, w, h), h / 2)) using (var b = new SolidBrush(InkA(26))) g.FillPath(b, p);
        if (frac > 0)
            using (var p = Round(new RectangleF(x, y, Math.Max(h, w * frac), h), h / 2)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Max(1f, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // Shown numbers follow the Windows regional format; the settings file keeps using CI.
    static string N(float v, string fmt) { return v.ToString(fmt, Lang.Num); }
    static string Pct(float v) { return N(v, "0") + "%"; }
    string TempText(float t) { return float.IsNaN(t) ? null : fahrenheit ? N(t * 9 / 5 + 32, "0") + "°F" : N(t, "0") + "°C"; }
    internal string TempLabel(int celsius) { return TempText(celsius); }
    Color InkA(int a) { return Color.FromArgb(a, ink); }
    // Section colours are pastels made for a dark panel; on the light panel they're deepened to stay readable.
    Color Shade(Color c) { return !lightBg ? c : Color.FromArgb((int)(c.R * 0.62), (int)(c.G * 0.62), (int)(c.B * 0.62)); }

    // Network speed in bytes (MB/s, like Task Manager) or bits (Mb/s, like internet plans and speed tests).
    string RateNet(double b)
    {
        if (!netBits) return Rate(b);
        double v = b * 8;
        if (v < 1000) return Lang.F("unit.bits", v.ToString("0", Lang.Num));
        if (v < 1e6) return Lang.F("unit.kbits", (v / 1e3).ToString(v < 1e4 ? "0.0" : "0", Lang.Num));
        return Lang.F("unit.mbits", (v / 1e6).ToString(v < 1e7 ? "0.0" : "0", Lang.Num));
    }

    static string PingText(float ms) { return float.IsNaN(ms) ? Lang.T("ping.timeout") : Lang.F("unit.ms", ms < 1 ? "<1" : N(ms, "0")); }
    string PingHostText() { return pinger.Host == "router" ? Lang.T("ping.router") : pinger.Host; }

    static string BatteryText()
    {
        if (Battery.Charging) return Lang.T("battery.charging");
        if (Battery.OnAC) return Lang.T(Battery.Percent >= 99 ? "battery.full" : "battery.plugged");
        if (Battery.SecondsLeft > 0) return Lang.F("battery.left", Battery.SecondsLeft / 3600, Battery.SecondsLeft % 3600 / 60);
        return Lang.T("battery.onbattery");
    }

    // Cuts text to fit, ending in "…".
    string Fit(Graphics g, string t, Font f, float width)
    {
        if (string.IsNullOrEmpty(t)) return "";
        if (g.MeasureString(t, f, PointF.Empty, sf).Width <= width) return t;
        int lo = 0, hi = t.Length;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (g.MeasureString(t.Substring(0, mid) + "\u2026", f, PointF.Empty, sf).Width <= width) lo = mid; else hi = mid - 1; }
        return t.Substring(0, lo).TrimEnd() + "\u2026";
    }

    // ---- now playing: previous / play-pause / next, drawn at the right of the section's header row
    readonly List<RectangleF> mediaBtns = new List<RectangleF>();
    void MediaButtons(Graphics g, float top)
    {
        float bs = 18 * s, gap = 4 * s, cy = top + 8.5f * s, right = W - pad + 3 * s, a = 4.2f * s;
        var rects = new RectangleF[3];
        for (int i = 2; i >= 0; i--) { rects[i] = new RectangleF(right - bs, cy - bs / 2, bs, bs); right -= bs + gap; }
        mediaBtns.AddRange(rects);
        using (var b = new SolidBrush(brValue.Color))
            for (int i = 0; i < 3; i++)
            {
                float cx = rects[i].X + bs / 2;
                if (i == 1)
                {
                    if (NowPlaying.Playing) { g.FillRectangle(b, cx - a * 0.75f, cy - a * 0.9f, a * 0.55f, a * 1.8f); g.FillRectangle(b, cx + a * 0.2f, cy - a * 0.9f, a * 0.55f, a * 1.8f); }
                    else g.FillPolygon(b, new[] { new PointF(cx - a * 0.6f, cy - a), new PointF(cx + a * 0.9f, cy), new PointF(cx - a * 0.6f, cy + a) });
                    continue;
                }
                float dir = i == 0 ? -1 : 1, bw = 1.6f * s;
                g.FillPolygon(b, new[] { new PointF(cx - dir * a * 0.6f, cy - a * 0.85f), new PointF(cx + dir * a * 0.5f, cy), new PointF(cx - dir * a * 0.6f, cy + a * 0.85f) });
                g.FillRectangle(b, dir > 0 ? cx + a * 0.5f : cx - a * 0.5f - bw, cy - a * 0.85f, bw, a * 1.7f);
            }
    }

    int MediaButtonAt(Point screen)
    {
        if (islandOpen) return IslandButtonAt(screen);
        if (mediaBtns.Count == 0 || !IsHandleCreated) return -1;
        var b = Body; var p = new PointF(screen.X - b.X, screen.Y - b.Y);
        for (int i = 0; i < mediaBtns.Count; i++) if (mediaBtns[i].Contains(p)) return i;
        return -1;
    }
    static string GpuLabel(Gpu g) { return Lang.T(g.Integrated ? "label.igpu" : "label.gpu") + (g.Number > 0 ? g.Number.ToString(CI) : ""); }
    Brush TempBrush(float t) { return t >= 90 ? brHot : t >= 80 ? brWarn : brValue; }
    static string Rate(double b)
    {
        if (b < 1024) return Lang.F("unit.bps", b.ToString("0", Lang.Num));
        if (b < 1048576) return Lang.F("unit.kbps", (b / 1024).ToString(b < 10240 ? "0.0" : "0", Lang.Num));
        return Lang.F("unit.mbps", (b / 1048576).ToString(b < 10485760 ? "0.0" : "0", Lang.Num));
    }

    // ------------------------------------------------------------------ window behaviour
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SHOWME && WM_SHOWME != 0) { if (hidden) SetHidden(false); return; }
        if (m.Msg == 0x201 /*WM_LBUTTONDOWN*/ || m.Msg == WM_LBUTTONDBLCLK)
        {
            int btn = MediaButtonAt(Cursor.Position);
            if (btn >= 0) { NowPlaying.Command(btn); return; }
        }
        if (m.Msg == 0xA0 /*WM_NCMOUSEMOVE*/ || m.Msg == 0x200 /*WM_MOUSEMOVE*/) StartHover();
        switch (m.Msg)
        {
            // Media buttons are clickable; everywhere else the panel drags (unless locked).
            case WM_NCHITTEST: m.Result = (IntPtr)(locked || islandOpen || MediaButtonAt(Cursor.Position) >= 0 ? 1 /*HTCLIENT*/ : 2 /*HTCAPTION*/); return;
            case 0x312: // WM_HOTKEY
                if (m.WParam.ToInt64() == 1) SetHidden(!hidden);
                else if (m.WParam.ToInt64() == 2) { miniPref = !miniPref; Relayout(); AutoDirection(); SaveSettings(); }
                return;
            case WM_MOUSEACTIVATE: m.Result = (IntPtr)3; /*MA_NOACTIVATE*/ return;
            case WM_NCRBUTTONDOWN: return;
            case WM_NCRBUTTONUP:
            case WM_RBUTTONUP: ShowMenu(); return;
            case 0x216: // WM_MOVING: keep the whole widget inside the screen under the cursor
            {
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                var c = Cursor.Position; // follow the cursor, so leaving a magnetic edge is never blocked
                var p = new Point(c.X - grab.X, c.Y - grab.Y);
                int sd = (int)Math.Round(10 * dpi); // magnetic edges
                if (Math.Abs(p.X - wa.Left) < sd) p.X = wa.Left;
                if (Math.Abs(p.X + W - wa.Right) < sd) p.X = wa.Right - W;
                if (Math.Abs(p.Y - wa.Top) < sd) p.Y = wa.Top;
                if (Math.Abs(p.Y + H - wa.Bottom) < sd) p.Y = wa.Bottom - H;
                p = ClampTo(p, new Size(W, H), wa);
                int ww = r.R - r.L, wh = r.B - r.T;
                r.L = p.X - M; r.T = p.Y - M; r.R = r.L + ww; r.B = r.T + wh;
                Marshal.StructureToPtr(r, m.LParam, false);
                moveTarget = new Point(r.L, r.T); hasMoveTarget = true;
                UpdateDock(new Rectangle(p.X, p.Y, W, H)); Render(false);
                m.Result = (IntPtr)1; return;
            }
            case WM_NCLBUTTONDBLCLK:
            case WM_LBUTTONDBLCLK: OpenTaskManager(); return;
            case WM_WINDOWPOSCHANGING:
            {
                var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS)); bool changed = false;
                // Windows 11 keeps a dragged window's top edge on screen, which pushed a top-docked panel down by
                // its transparent margin and broke the dock. Put the window exactly where WM_MOVING placed it.
                if (inMove && hasMoveTarget && (wp.flags & SWP_NOMOVE) == 0 && (wp.x != moveTarget.X || wp.y != moveTarget.Y))
                { wp.x = moveTarget.X; wp.y = moveTarget.Y; changed = true; }
                if (!onTop && !desktopRaised && IsHandleCreated && (wp.flags & SWP_NOZORDER) == 0)
                {
                    IntPtr after;
                    if (DesktopSlot(out after)) wp.hwndInsertAfter = after; else wp.flags |= SWP_NOZORDER;
                    changed = true;
                }
                if (changed) Marshal.StructureToPtr(wp, m.LParam, false);
                break;
            }
            case 0x320: // WM_DWMCOLORIZATIONCOLORCHANGED: the Windows accent colour changed
            case 0x1A:  // WM_SETTINGCHANGE (also sent for theme / colour changes)
                if (accentBg) BeginInvoke((MethodInvoker)UpdatePanelColor);
                break;
            case WM_ENTERSIZEMOVE: hasMoveTarget = false; inMove = true; moveStart = Location; grab = new Point(Cursor.Position.X - (Left + M), Cursor.Position.Y - (Top + M)); break;
            case WM_EXITSIZEMOVE: inMove = false; hasMoveTarget = false; if (Location != moveStart) { anchor = ""; AutoDirection(); } SaveSettings(); break;
            case WM_DISPLAYCHANGE: BeginInvoke((MethodInvoker)delegate { if (anchor != "") SnapTo(anchor); else PlaceBody(ValidPosition(Left + M, Top + M)); }); break;
        }
        base.WndProc(ref m);
    }

    // The window that holds the desktop icons (Progman, or a WorkerW when a wallpaper engine is active).
    static IntPtr FindDesktopWindow()
    {
        IntPtr p = FindWindow("Progman", null);
        if (p != IntPtr.Zero && FindWindowEx(p, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return p;
        IntPtr w = IntPtr.Zero;
        while ((w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
            if (FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return w;
        return p;
    }

    // Where to insert so we sit directly above the desktop. Returns false if already there.
    bool DesktopSlot(out IntPtr after)
    {
        after = HWND_BOTTOM;
        IntPtr d = FindDesktopWindow();
        if (d == IntPtr.Zero) return true;
        IntPtr prev = GetWindow(d, GW_HWNDPREV);
        if (prev == Handle) return false;
        after = prev == IntPtr.Zero ? HWND_TOP : prev;
        return true;
    }

    bool DesktopIsAboveUs()
    {
        IntPtr d = FindDesktopWindow(); if (d == IntPtr.Zero) return false;
        int guard = 0;
        for (IntPtr w = GetWindow(Handle, GW_HWNDPREV); w != IntPtr.Zero && guard < 2000; w = GetWindow(w, GW_HWNDPREV), guard++)
            if (w == d) return true;
        return false;
    }

    void SendToDesktopLevel()
    {
        IntPtr after;
        if (DesktopSlot(out after)) SetWindowPos(Handle, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void RaiseOverDesktop()
    {
        desktopRaised = true;
        SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // Keeps the widget visible after Win+D ("show desktop") and back at desktop level afterwards.
    void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        CheckFullscreen();
        if (onTop) { KeepOnTop(); return; }
        var sb = new StringBuilder(32); GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        bool desk = cls == "WorkerW" || cls == "Progman";
        if (desk && !desktopRaised) { if (DesktopIsAboveUs()) RaiseOverDesktop(); }
        else if (!desk && desktopRaised) { desktopRaised = false; SendToDesktopLevel(); }
        // Drawing was paused while covered: catch up as soon as it can be seen again.
        if (stale && !hidden && !fsHiddenNow && !HiddenByFullscreen() && !Covered()) { stale = false; Render(); }
    }

    // Games often make their own window topmost on start; re-claim the top (not while our menu is open).
    void KeepOnTop()
    {
        if (menu != null && menu.Visible) return;
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        KeepSettingsReachable();
    }

    // Settings over (part of) the always-on-top widget would end up under it, title bar and all: while they
    // overlap, Settings stays above the widget.
    internal void KeepSettingsReachable()
    {
        if (settings == null || settings.IsDisposed || !settings.Visible || settings.WindowState == FormWindowState.Minimized) return;
        bool over = onTop && settings.Bounds.IntersectsWith(new Rectangle(Left, Top, Width, Height));
        if (settings.TopMost != over) settings.TopMost = over;
        if (over) SetWindowPos(settings.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void SetOnTop(bool value)
    {
        onTop = value; desktopRaised = false;
        if (value) SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else { SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); SendToDesktopLevel(); }
    }

    static Point ClampTo(Point p, Size sz, Rectangle wa)
    {
        return new Point(Math.Max(wa.Left, Math.Min(p.X, wa.Right - sz.Width)), Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - sz.Height)));
    }

    // Open the menu and its submenus toward the inside of the current screen, never onto the next monitor.
    void ShowMenu()
    {
        Point c = Cursor.Position;
        var wa = Screen.FromPoint(c).WorkingArea;
        bool right = c.X > wa.Left + wa.Width / 2, bottom = c.Y > wa.Top + wa.Height / 2;
        menu.Show(c, right ? (bottom ? ToolStripDropDownDirection.AboveLeft : ToolStripDropDownDirection.BelowLeft)
                           : (bottom ? ToolStripDropDownDirection.AboveRight : ToolStripDropDownDirection.BelowRight));
    }

    Point ValidPosition(int x, int y)
    {
        if (x != int.MinValue)
        {
            var probe = new Point(x + W / 2, y + Math.Min(H / 2, 40));
            foreach (var sc in Screen.AllScreens) if (sc.WorkingArea.Contains(probe)) return ClampTo(new Point(x, y), new Size(W, H), sc.WorkingArea);
        }
        var wa = Screen.PrimaryScreen.WorkingArea;
        return new Point(wa.Right - W - (int)(16 * s), wa.Top + (int)(16 * s));
    }

    // ------------------------------------------------------------------ layout model
    // Sections have ids: cpu, fps, ram, net, disk and gpu:<vendor-device>. The order list also keeps GPUs
    // that aren't present right now (an eGPU, a GPU disabled in BIOS) so they return to their place.
    List<string> order = new List<string>();
    readonly HashSet<string> hiddenFull = new HashSet<string>(), hiddenMini = new HashSet<string>(), detailsOff = new HashSet<string>();
    readonly Dictionary<string, Color> colors = new Dictionary<string, Color>();
    bool compact, hasHide, hasMiniHide, legacyHideGpus, legacyHideGpusMini; string legacyShow, legacyFps;
    static readonly string[] BaseSections = { "cpu", "fps", "ram", "net", "ping", "disk", "battery", "media" };
    readonly HashSet<string> known = new HashSet<string>(); // sections this settings file has seen
    static readonly Color[] GpuPalette = { Color.FromArgb(0x81, 0xC7, 0x84), Color.FromArgb(0x4D, 0xB6, 0xAC), Color.FromArgb(0xAE, 0xD5, 0x81), Color.FromArgb(0x64, 0xB5, 0xF6) };
    SettingsWindow settings;

    void NormalizeLayout()
    {
        var all = new List<string> { "cpu" };
        foreach (var g in gpus) all.Add("gpu:" + g.Key);
        all.AddRange(new[] { "fps", "ram", "net", "ping", "disk", "battery", "media" });
        order = order.FindAll(id => id.StartsWith("gpu:") || Array.IndexOf(BaseSections, id) >= 0);
        for (int i = 0; i < all.Count; i++)
        {
            if (order.Contains(all[i])) continue;
            int at = i == 0 ? 0 : order.IndexOf(all[i - 1]) + 1;
            order.Insert(Math.Max(0, Math.Min(at, order.Count)), all[i]);
        }
        foreach (var g in gpus)
        {
            if (legacyHideGpus) hiddenFull.Add("gpu:" + g.Key);
            if (legacyHideGpusMini) hiddenMini.Add("gpu:" + g.Key);
        }
        legacyHideGpus = legacyHideGpusMini = false;
        // Sections added later start hidden where they'd surprise: ping and now playing everywhere, all of them in mini mode.
        foreach (var id in new[] { "ping", "battery", "media" })
        {
            if (known.Contains(id)) continue;
            known.Add(id); hiddenMini.Add(id);
            if (id != "battery") hiddenFull.Add(id);
        }
        // never end up with an empty widget
        foreach (var set in new[] { hiddenFull, hiddenMini })
        {
            bool any = false; foreach (var id in Sections()) if (!set.Contains(id)) any = true;
            if (!any) set.Remove("cpu");
        }
    }

    internal IEnumerable<string> Sections() { foreach (var id in order) if (Available(id)) yield return id; }
    bool Available(string id) { return id == "battery" ? Battery.Present : !id.StartsWith("gpu:") || GpuById(id) != null; }
    Gpu GpuById(string id) { foreach (var g in gpus) if ("gpu:" + g.Key == id) return g; return null; }
    bool IsLive(string id) { return Available(id) && IsShown(id, mini); }

    internal bool IsShown(string id, bool inMini) { return !(inMini ? hiddenMini : hiddenFull).Contains(id); }

    // Returns false (and changes nothing) when it would hide the last visible section.
    internal bool SetShown(string id, bool inMini, bool on)
    {
        var set = inMini ? hiddenMini : hiddenFull;
        if (!on)
        {
            int left = 0; foreach (var sid in Sections()) if (sid != id && !set.Contains(sid)) left++;
            if (left == 0) return false;
            set.Add(id);
        }
        else { set.Remove(id); if (id == "net") lastRx = lastTx = -1; }
        LayoutChanged(); return true;
    }

    internal bool Opt(string id, string detail) { return !detailsOff.Contains(id + "." + detail); }
    internal void SetOpt(string id, string detail, bool on)
    {
        if (on) detailsOff.Remove(id + "." + detail); else detailsOff.Add(id + "." + detail);
        LayoutChanged();
    }

    internal string Title(string id)
    {
        if (Array.IndexOf(BaseSections, id) >= 0) return Lang.T("section." + id);
        var g = GpuById(id); return g == null ? id : GpuLabel(g) + " \u00B7 " + g.Name;
    }

    internal string[][] DetailsOf(string id)
    {
        string[] keys;
        switch (id)
        {
            case "cpu": keys = new[] { "temp", "graph" }; break;
            case "fps": keys = new[] { "app", "graph" }; break;
            case "ram": keys = new[] { "bar" }; break;
            case "net": keys = new[] { "up", "graph" }; break;
            case "disk": keys = new[] { "rw", "free" }; break;
            case "ping": keys = new[] { "host", "graph" }; break;
            case "battery": keys = new[] { "time", "power", "bar" }; break;
            case "media": keys = new[] { "app", "controls" }; break;
            default:
            {
                var g = GpuById(id); var l = new List<string>();
                if (g != null && HasTemp(g)) l.Add("temp");
                l.Add("mem");
                if (g != null && HasPower(g)) l.Add("power");
                l.Add("graph");
                keys = l.ToArray(); break;
            }
        }
        return Array.ConvertAll(keys, k => new[] { k, Lang.T("detail." + k) });
    }

    internal string[][] ColorKeys(string id)
    {
        if (id == "net") return new[] { new[] { "net", Lang.T("color.down") }, new[] { "net.up", Lang.T("color.up") } };
        return new[] { new[] { id, null } };
    }

    internal Color ColorOf(string key) { Color c; return colors.TryGetValue(key, out c) ? c : DefaultColor(key); }
    Color DefaultColor(string key)
    {
        switch (key) { case "cpu": return cCpu; case "fps": return cFps; case "ram": return cRam; case "net": return cDown; case "net.up": return cUp; case "disk": return cDisk;
                     case "ping": return cPing; case "battery": return cBat; case "media": return cMedia; }
        int i = gpus.FindIndex(g => "gpu:" + g.Key == key);
        return GpuPalette[Math.Max(0, i) % GpuPalette.Length];
    }
    internal void SetColor(string key, Color c)
    {
        if (c.ToArgb() == DefaultColor(key).ToArgb()) colors.Remove(key); else colors[key] = c;
        LayoutChanged();
    }
    string ColorsText()
    {
        var l = new List<string>(); foreach (var kv in colors) l.Add(kv.Key + ":" + (kv.Value.ToArgb() & 0xFFFFFF).ToString("X6"));
        return string.Join(",", l);
    }

    // Moves a section past its nearest present neighbour (absent GPUs keep their slot).
    internal void MoveSection(string id, int dir)
    {
        var present = new List<string>(Sections());
        int i = present.IndexOf(id), j = i + dir; if (i < 0 || j < 0 || j >= present.Count) return;
        int a = order.IndexOf(id), b = order.IndexOf(present[j]);
        order[a] = present[j]; order[b] = id;
        LayoutChanged();
    }

    internal bool Compact { get { return compact; } set { compact = value; LayoutChanged(); } }
    internal int Theme { get { return lightBg ? 2 : accentBg ? 1 : 0; } set { SetTheme(value); } }
    internal int GraphStyle { get { return graphStyle; } set { graphStyle = value; Render(); SaveSettings(); } }
    internal int IntervalMs { get { return intervalMs; } set { intervalMs = value; timer.Interval = value; ResizeHistory(); SaveSettings(); } }
    internal int GraphSeconds { get { return graphSec; } set { graphSec = value; ResizeHistory(); Render(); SaveSettings(); } }
    internal bool Fahrenheit { get { return fahrenheit; } set { fahrenheit = value; LayoutChanged(); } }
    internal bool NetBits { get { return netBits; } set { netBits = value; LayoutChanged(); } }
    internal bool Startup { get { if (startupCache == null) startupCache = IsStartup(); return startupCache.Value; } set { SetStartup(value); startupCache = IsStartup(); } }
    internal bool CheckUpdates { get { return checkUpdates; } set { checkUpdates = value; SaveSettings(); if (value) CheckForUpdate(); } }
    internal bool Hotkeys { get { return hotkeys; } set { hotkeys = value; ApplyHotkeys(); SaveSettings(); } }
    internal bool HotkeysFailed { get { return hotkeys && hotkeyFailed; } }
    internal bool HoverApps { get { return hoverApps; } set { hoverApps = value; if (!value) HidePopup(); SaveSettings(); } }
    internal int FullscreenMode { get { return fsMode; } set { fsMode = value; fsActive = false; fsOverride = false; CheckFullscreen(); Relayout(); SaveSettings(); } }
    internal string PingHost { get { return pinger.Host; } set { pinger.Host = value; pinger.Ms = float.NaN; hPing = new Ring(HistoryCap); Render(); SaveSettings(); } }
    internal bool AlertOn(int i) { return alerts[i].On; }
    internal int AlertAt(int i) { return alerts[i].At; }
    internal void SetAlert(int i, bool on, int at) { alerts[i].On = on; alerts[i].At = at; alerts[i].Since = DateTime.MinValue; SaveSettings(); }
    bool? startupCache;

    int intervalMs = 1000, graphSec = 60, graphStyle; bool fahrenheit, netBits;
    int HistoryCap { get { return Math.Max(10, graphSec * 1000 / intervalMs); } }

    // Graphs keep graphSec seconds of samples; changing the interval or length keeps the newest ones.
    void ResizeHistory()
    {
        int cap = HistoryCap;
        hCpu = hCpu.Resized(cap); hDown = hDown.Resized(cap); hUp = hUp.Resized(cap); hFps = hFps.Resized(cap); hPing = hPing.Resized(cap);
        foreach (var g in gpus) g.History = g.History.Resized(cap);
    }

    // ---- keyboard shortcuts: Ctrl+Alt+W hides/shows the widget, Ctrl+Alt+M switches mini mode (from any app)
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    bool hotkeys = true, hotkeyFailed;
    void ApplyHotkeys()
    {
        if (!IsHandleCreated) return;
        UnregisterHotKey(Handle, 1); UnregisterHotKey(Handle, 2); hotkeyFailed = false;
        if (!hotkeys) return;
        const uint mods = 0x1 /*ALT*/ | 0x2 /*CONTROL*/ | 0x4000 /*NOREPEAT*/;
        bool a = RegisterHotKey(Handle, 1, mods, 'W'), b = RegisterHotKey(Handle, 2, mods, 'M');
        hotkeyFailed = !a || !b;
    }

    // ---- alerts: a notification when something stays too hot or too full for a while
    sealed class Alert { public bool On = true; public int At; public DateTime Since = DateTime.MinValue, Last = DateTime.MinValue; }
    readonly Alert[] alerts = { new Alert { At = 95 }, new Alert { At = 90 }, new Alert { At = 95 }, new Alert { At = 5 } }; // CPU °C, GPU °C, RAM %, disk free %
    string balloonUrl; bool balloonTaskMgr;

    void CheckAlerts()
    {
        if (tray == null) return;
        Raise(alerts[0], !float.IsNaN(cpuTemp) && cpuTemp >= alerts[0].At, Lang.T("alert.cpu.title"), Lang.F("alert.temp.text", TempText(cpuTemp)), true, 30);
        Gpu hot = null;
        foreach (var g in gpus) if (!g.Idle && !float.IsNaN(g.Temp) && g.Temp >= alerts[1].At && (hot == null || g.Temp > hot.Temp)) hot = g;
        Raise(alerts[1], hot != null, hot == null ? "" : Lang.F("alert.gpu.title", GpuLabel(hot) + " \u00B7 " + hot.Name), hot == null ? "" : Lang.F("alert.temp.text", TempText(hot.Temp)), true, 30);
        float ramPct = ramTotal > 0 ? ramUsed * 100 / ramTotal : 0;
        Raise(alerts[2], ramPct >= alerts[2].At, Lang.T("alert.ram.title"), Lang.F("alert.ram.text", N(ramPct, "0")), true, 30);
        DriveStat? full = null;
        foreach (var d in drives) if (d.Total > 0 && d.Free * 100 / d.Total < alerts[3].At) { full = d; break; }
        Raise(alerts[3], full != null, full == null ? "" : Lang.F("alert.disk.title", full.Value.Name), full == null ? "" : Lang.F("alert.disk.text", N((float)(full.Value.Free / GB), "0.0")), false, 360);
    }

    // Fires after the condition has held for 15 s, then stays quiet for the cooldown.
    void Raise(Alert a, bool cond, string title, string text, bool taskMgr, int cooldownMin)
    {
        var now = DateTime.UtcNow;
        if (!a.On || !cond) { a.Since = DateTime.MinValue; return; }
        if (a.Since == DateTime.MinValue) a.Since = now;
        if ((now - a.Since).TotalSeconds < 15 || (now - a.Last).TotalMinutes < cooldownMin) return;
        a.Last = now; balloonUrl = null; balloonTaskMgr = taskMgr; balloonUpdate = false;
        tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Warning);
    }

    // ---- RivaTuner: the Mini mode sections as colored lines in games' on-screen display
    bool rtss;
    internal bool RivaTuner { get { return rtss; } set { rtss = value; if (value) UpdateRtss(); else Rtss.Clear(); SaveSettings(); } }
    internal bool RivaTunerFound { get { return Rtss.Running; } }

    void UpdateRtss() { if (rtss) Rtss.Show(RtssText()); }

    static string Col(Color c) { return "<C=" + (c.ToArgb() & 0xFFFFFF).ToString("X6") + ">"; }

    string RtssText()
    {
        var sb = new StringBuilder();
        foreach (var id in Sections())
        {
            if (!IsShown(id, true)) continue;
            string label, value = null;
            switch (id)
            {
                case "cpu": label = Lang.T("label.cpu"); value = Pct(cpu) + (Opt(id, "temp") && TempText(cpuTemp) != null ? "  " + TempText(cpuTemp) : ""); break;
                case "fps": label = Lang.T("label.fps"); value = FpsText(); break;
                case "ram": label = Lang.T("label.ram"); value = Lang.F("ram.value", N(ramUsed, "0.0"), N(ramTotal, "0.0")); break;
                case "net":
                    label = Lang.T("label.net"); value = RateNet(down);
                    if (Opt(id, "up")) value += "  " + Col(ColorOf("net.up")) + RateNet(up) + "<C>";
                    break;
                case "ping": label = Lang.T("label.ping"); value = PingText(pinger.Ms); break;
                case "battery":
                    label = Lang.T("label.battery");
                    value = Battery.Percent >= 0 ? Pct(Battery.Percent) + (Battery.Charging ? "  " + Lang.T("battery.charging") : "") : "\u2014";
                    break;
                case "disk":
                    label = Lang.T("label.disk");
                    if (Opt(id, "free") && drives.Count > 0)
                    {
                        var l = new List<string>();
                        foreach (var d in drives) l.Add(d.Name + " " + Lang.F("disk.short", N((float)(d.Free / GB), "0")));
                        value = string.Join("  ", l);
                    }
                    else value = Lang.F("disk.read", Rate(diskR)) + "  " + Lang.F("disk.write", Rate(diskW));
                    break;
                case "media":
                    label = Lang.T("label.media");
                    if (NowPlaying.Has) value = NowPlaying.Title + (NowPlaying.Artist != "" ? " - " + NowPlaying.Artist : "");
                    break;
                default:
                {
                    var gp = GpuById(id); if (gp == null) continue;
                    label = GpuLabel(gp);
                    value = gp.Idle ? Lang.T("gpu.idle.short") : Pct(gp.Load) + (Opt(id, "temp") && HasTemp(gp) && TempText(gp.Temp) != null ? "  " + TempText(gp.Temp) : "");
                    break;
                }
            }
            if (value == null) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Col(ColorOf(id))).Append(label).Append("<C>  ").Append(value.Replace("\u2193", "").Replace("\u2191", ""));
        }
        return sb.ToString();
    }

    string AlertsText()
    {
        var l = new List<string>(); foreach (var a in alerts) l.Add((a.On ? "1" : "0") + ":" + a.At.ToString(CI));
        return string.Join(",", l);
    }

    // ---- busiest apps: pointing at CPU or RAM opens a small list next to the widget
    readonly System.Windows.Forms.Timer hoverTimer = new System.Windows.Forms.Timer { Interval = 40 };
    readonly Dictionary<string, RectangleF> sectionRects = new Dictionary<string, RectangleF>();
    bool hoverApps = true; string hoverId; int hoverMs; AppsPopup popup; TopApps topApps;

    void StartHover() { if ((hoverApps || IslandMode) && !hoverTimer.Enabled) { hoverId = null; hoverMs = 0; hoverTimer.Start(); } }

    void HoverStep()
    {
        if (IslandMode || islandOpen) { IslandHover(); return; }
        Point c = Cursor.Position; var b = Body;
        if (!b.Contains(c)) { hoverTimer.Stop(); hoverId = null; HidePopup(); StopTopApps(); return; }
        bool usable = hoverApps && !inMove && !hidden && !(menu != null && menu.Visible) && !(PassThrough && !interactive);
        string id = null;
        if (usable)
            foreach (var kv in sectionRects)
                if ((kv.Key == "cpu" || kv.Key == "ram") && kv.Value.Contains(c.X - b.X, c.Y - b.Y)) { id = kv.Key; break; }
        if (id != hoverId) { hoverId = id; hoverMs = 0; HidePopup(); }
        if (id == null) return;
        StartTopApps(); // starts measuring now, so the list is ready when the popup shows
        hoverMs += hoverTimer.Interval;
        int t = hoverMs - 450; // wait a moment, so passing over doesn't flash a popup
        if (t >= 0 && t < hoverTimer.Interval) ShowPopup(id);
    }

    void StartTopApps()
    {
        if (topApps != null) return;
        topApps = new TopApps(5);
        topApps.Updated += () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)TopAppsUpdated); } catch { } };
    }

    void StopTopApps() { if (topApps != null) { topApps.Dispose(); topApps = null; } }

    // A new sample of the busiest apps (on the UI thread): refresh whatever shows them.
    void TopAppsUpdated()
    {
        if (topApps == null) return;
        if (islandOpen)
        {
            if ((islandId == "cpu" || islandId == "ram") && islandTargetH != 0) { BuildIslandContent(); if (!islandAnim.Enabled) Render(); }
        }
        else if (popup != null && !popup.IsDisposed && popup.Visible && (hoverId == "cpu" || hoverId == "ram")) ShowPopup(hoverId);
    }

    void ShowPopup(string id)
    {
        if (topApps == null) return;
        if (popup == null || popup.IsDisposed) popup = new AppsPopup(dpi);
        bool isCpu = id == "cpu";
        var list = isCpu ? topApps.Cpu : topApps.Mem;
        var rows = new List<string[]>(); var shares = new List<float>();
        if (!isCpu || topApps.HasCpu)
        {
            double max = 0; foreach (var kv in list) max = Math.Max(max, kv.Value);
            foreach (var kv in list)
            {
                double v = kv.Value;
                string val = isCpu ? N((float)v, v < 10 ? "0.0" : "0") + "%"
                    : v >= GB ? Lang.F("unit.gb", N((float)(v / GB), "0.0")) : Lang.F("unit.mb", N((float)(v / 1048576), "0"));
                rows.Add(new[] { kv.Key, val }); shares.Add(max > 0 ? (float)(v / max) : 0);
            }
        }
        popup.SetData(Lang.T(isCpu ? "apps.cpu" : "apps.ram"), ColorOf(id), rows, shares);
        var b = Body; var r = sectionRects[id]; var wa = Screen.FromRectangle(b).WorkingArea; int gap = (int)Math.Round(8 * dpi);
        int x, y;
        if (mini && !Vert)
        {
            x = b.Left + (int)r.Left; y = b.Bottom + gap;
            if (y + popup.Height > wa.Bottom) y = b.Top - gap - popup.Height;
        }
        else
        {
            x = b.Right + gap; if (x + popup.Width > wa.Right) x = b.Left - gap - popup.Width;
            y = b.Top + (int)Math.Max(0, r.Top);
        }
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - popup.Width)); y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - popup.Height));
        popup.Location = new Point(x, y);
        if (!popup.Visible) popup.Show();
    }

    void HidePopup()
    {
        if (popup != null && !popup.IsDisposed && popup.Visible) popup.Hide();
    }

    // ---- settings file: export to a file, or import one (the widget restarts to apply it)
    bool skipSave;
    internal void ExportSettings(IWin32Window owner)
    {
        using (var d = new SaveFileDialog { Filter = Lang.T("settings.file") + " (*.ini)|*.ini", FileName = "SysWidget.ini", Title = Lang.T("settings.export").TrimEnd('\u2026', '.') })
        {
            if (d.ShowDialog(owner) != DialogResult.OK) return;
            SaveSettings();
            try { File.Copy(iniPath, d.FileName, true); }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, "SysWidget", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    internal void ImportSettings(IWin32Window owner)
    {
        using (var d = new OpenFileDialog { Filter = Lang.T("settings.file") + " (*.ini)|*.ini", Title = Lang.T("settings.import").TrimEnd('\u2026', '.') })
        {
            if (d.ShowDialog(owner) != DialogResult.OK) return;
            string[] lines;
            try { lines = File.ReadAllLines(d.FileName); }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, "SysWidget", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            int good = 0; foreach (var l in lines) if (Regex.IsMatch(l, "^[a-z]+=")) good++;
            if (good < 3 || lines.Length > 200) { MessageBox.Show(owner, Lang.T("settings.import.bad"), "SysWidget", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try { File.WriteAllLines(iniPath, lines); }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, "SysWidget", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            skipSave = true;
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--restart") { UseShellExecute = false }); } catch { }
            Close();
        }
    }

    // ---- How to use (also shown once on first start)
    bool tipsSeen; TipsWindow tips;
    internal void ShowTips()
    {
        if (!tipsSeen) { tipsSeen = true; SaveSettings(); }
        if (tips == null || tips.IsDisposed)
        {
            tips = new TipsWindow(dpi);
            try { tips.Icon = AppIcon(new Size(32, 32)); } catch { }
            var wa = Screen.FromRectangle(Body).WorkingArea;
            tips.Location = new Point(wa.Left + (wa.Width - tips.Width) / 2, wa.Top + (wa.Height - tips.Height) / 2);
            tips.Show();
        }
        if (tips.WindowState == FormWindowState.Minimized) tips.WindowState = FormWindowState.Normal;
        tips.Activate();
    }

    internal void ShowInput(IWin32Window owner, string title, string prompt, string value, Action<string> done)
    {
        string v = InputBox.Ask(owner, title, prompt, value, dpi);
        if (v != null) done(v);
    }
    internal int SizePercent { get { return (int)Math.Round(scale * 100); } set { scale = value / 100f; ApplyScale(); LayoutChanged(); } }
    internal int OpacityPercent { get { return opacity; } set { opacity = value; Render(); SaveSettings(); } }

    internal void ResetLayout()
    {
        hiddenFull.Clear(); hiddenMini.Clear(); detailsOff.Clear(); colors.Clear(); compact = false; order.Clear(); known.Clear();
        NormalizeLayout(); LayoutChanged();
    }

    internal void LayoutChanged()
    {
        Relayout(); SaveSettings();
        if (settings != null && !settings.IsDisposed) settings.Invalidate();
    }

    // Where Settings opens: next to the widget, so the widget never covers it: right, left, below or above it,
    // the first that fits on the screen (a mini row along the top of the screen: below it). Nothing fits: below
    // or above, whichever has more room, and Settings stays above the widget (KeepSettingsReachable).
    internal Point SettingsSpot(Size sz, Rectangle b, Rectangle wa)
    {
        int gap = (int)Math.Round(12 * dpi);
        int cx = b.Left + (b.Width - sz.Width) / 2, cy = Math.Max(wa.Top, Math.Min(b.Top, wa.Bottom - sz.Height));
        var beside = new[] { new Point(b.Right + gap, cy), new Point(b.Left - gap - sz.Width, cy) };
        var across = new[] { new Point(cx, b.Bottom + gap), new Point(cx, b.Top - gap - sz.Height) };
        foreach (var p in beside) { var c = ClampTo(p, sz, wa); if (c.X == p.X && !new Rectangle(c, sz).IntersectsWith(b)) return c; }
        foreach (var p in across) { var c = ClampTo(p, sz, wa); if (c.Y == p.Y && !new Rectangle(c, sz).IntersectsWith(b)) return c; }
        return ClampTo(b.Top - wa.Top > wa.Bottom - b.Bottom ? across[1] : across[0], sz, wa);
    }

    void OpenSettings()
    {
        if (settings == null || settings.IsDisposed)
        {
            startupCache = null;
            settings = new SettingsWindow(this, dpi);
            try { settings.Icon = AppIcon(new Size(32, 32)); } catch { }
            settings.Reflow(); // final height, before placing it
            var at = SettingsSpot(settings.Size, Body, Screen.FromRectangle(Body).WorkingArea);
            settings.Location = at;
            settings.TopMost = onTop && new Rectangle(at, settings.Size).IntersectsWith(new Rectangle(Left, Top, Width, Height));
            settings.FormClosed += delegate { settings = null; };
            settings.Show();
        }
        if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
        settings.Activate();
        KeepSettingsReachable();
    }

    // ------------------------------------------------------------------ menu / settings
    void BuildMenu()
    {
        menu = new ToolMenu();
        var miUpdate = new ToolStripMenuItem("", null, delegate { StartUpdate(); }) { Visible = false, Name = "accent" };
        var miUpdSep = new ToolStripSeparator { Visible = false };
        menu.Items.Add(miUpdate); menu.Items.Add(miUpdSep);
        menu.Opening += delegate
        {
            miUpdate.Visible = miUpdSep.Visible = updateTag != null;
            if (updateTag != null) miUpdate.Text = updating ? Lang.T("menu.update.downloading") : Lang.F(CanSelfUpdate ? "menu.update.install" : "menu.update", updateTag);
            miUpdate.Enabled = !updating;
        };
        miTop = new ToolStripMenuItem(Lang.T("menu.ontop"), null, delegate { SetOnTop(!onTop); SaveSettings(); });
        miLock = new ToolStripMenuItem(Lang.T("menu.lock"), null, delegate { locked = !locked; SaveSettings(); });
        var miMini = new ToolStripMenuItem(Lang.T("menu.mini"), null, delegate { miniPref = !miniPref; Relayout(); AutoDirection(); SaveSettings(); });
        miHide = new ToolStripMenuItem(Lang.T("menu.hide"), null, delegate { SetHidden(!hidden); });
        menu.Items.Add(miHide); menu.Items.Add(new ToolStripSeparator());
        menu.Opening += delegate { miHide.Text = Lang.T(hidden ? "menu.unhide" : "menu.hide"); };
        menu.Items.Add(miMini);
        menu.Opening += delegate { miMini.Checked = miniPref; };
        // What the mouse does over the widget: one choice instead of two separate switches.
        var mouse = new ToolStripMenuItem(Lang.T("menu.mouse"));
        var mNormal = new ToolStripMenuItem(Lang.T("menu.mouse.normal"), null, delegate { SetMouseMode(0); });
        var mClick = new ToolStripMenuItem(Lang.T("menu.clickthrough"), null, delegate { SetMouseMode(1); });
        var mHover = new ToolStripMenuItem(Lang.T("menu.hidehover"), null, delegate { SetMouseMode(2); });
        mouse.DropDownItems.Add(mNormal); mouse.DropDownItems.Add(mClick); mouse.DropDownItems.Add(mHover);
        mouse.DropDownOpening += delegate { mNormal.Checked = !clickThrough && !hideHover; mClick.Checked = clickThrough && !hideHover; mHover.Checked = hideHover; };
        menu.Items.Add(miTop); menu.Items.Add(miLock); menu.Items.Add(mouse);
        menu.Closed += delegate { CheckInteractive(); };
        var show = new ToolStripMenuItem(Lang.T("menu.show"));
        show.DropDownItems.Add(new ToolStripMenuItem("\u2026")); // filled when opened, from the current order and mode
        show.DropDownOpening += delegate { FillShowMenu(show); };
        show.DropDown.Closing += (o, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        var pos = new ToolStripMenuItem(Lang.T("menu.position"));
        string[] codes = { "TL", "TC", "TR", "-", "ML", "MC", "MR", "-", "BL", "BC", "BR" };

        for (int i = 0; i < codes.Length; i++)
        {
            if (codes[i] == "-") { pos.DropDownItems.Add(new ToolStripSeparator()); continue; }
            string c = codes[i]; var it = new ToolStripMenuItem(Lang.T("pos." + c.ToLowerInvariant())) { Tag = c };
            it.Click += delegate { SnapTo(c); };
            pos.DropDownItems.Add(it);
        }
        pos.DropDownOpening += delegate
        {
            foreach (ToolStripItem it in pos.DropDownItems) { var mi = it as ToolStripMenuItem; if (mi != null) mi.Checked = (string)mi.Tag == anchor; }
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(pos); menu.Items.Add(show);
        menu.Items.Add(new ToolStripMenuItem(Lang.T("menu.settings"), null, delegate { OpenSettings(); }));
        menu.Items.Add(new ToolStripMenuItem(Lang.T("menu.tips"), null, delegate { ShowTips(); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Lang.T("menu.taskmgr"), null, delegate { OpenTaskManager(); }));
        menu.Items.Add(new ToolStripMenuItem(Lang.T("menu.exit"), null, delegate { Close(); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Lang.F("menu.about", VersionText, "Dimokat"), null, delegate { OpenUrl("https://github.com/" + Repo); }) { Name = "dim" });
        menu.Items.Add(new ToolStripMenuItem(Lang.T("menu.donate"), null, delegate { OpenUrl(DonateUrl); }));
        menu.Opening += delegate { CloseIslandNow(); miTop.Checked = onTop; miLock.Checked = locked; };
        // Submenus open toward the inside of the screen the menu is on (also when opened from the tray).
        menu.Opening += delegate
        {
            SyncMenus();
            Point c = Cursor.Position; var wa = Screen.FromPoint(c).WorkingArea;
            var dir = c.X > wa.Left + wa.Width / 2 ? ToolStripDropDownDirection.Left : ToolStripDropDownDirection.Right;
            foreach (ToolStripItem it in menu.Items) { var mi = it as ToolStripMenuItem; if (mi != null && mi.HasDropDownItems) mi.DropDownDirection = dir; }
        };
        StyleMenu(menu);
        if (!menuWatchHooked) { menuWatchHooked = true; menuWatch.Tick += delegate { WatchMenu(); }; }
        menu.Closed += delegate { if (langPending) BeginInvoke((MethodInvoker)ApplyLanguage); };
        menu.Opened += delegate { menuWatch.Start(); };
        menu.Closed += delegate { menuWatch.Stop(); };
    }

    // The widget never takes focus, so Windows doesn't tell the menu when you click another app.
    // While the menu is open, close it on a click outside it (or Esc) - without stealing focus from games.
    readonly System.Windows.Forms.Timer menuWatch = new System.Windows.Forms.Timer { Interval = 40 };
    bool menuWatchHooked, langPending;
    static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    void WatchMenu()
    {
        if (!menu.Visible) { menuWatch.Stop(); return; }
        bool esc = Down(0x1B);
        bool click = Down(0x01) || Down(0x02) || Down(0x04); // left, right, middle button
        if (!esc && !click) return;
        if (!esc)
        {
            Point c = Cursor.Position;
            foreach (var dd in menuDropDowns) if (dd.Visible && dd.Bounds.Contains(c)) return;
        }
        menu.Close(esc ? ToolStripDropDownCloseReason.Keyboard : ToolStripDropDownCloseReason.AppClicked);
    }

    // ---- background: near-black, or the Windows accent colour (darkened only as far as white text needs)
    static readonly Color DarkBg = Color.FromArgb(18, 19, 22);
    static readonly Color LightBg = Color.FromArgb(243, 244, 246);
    bool accentBg, lightBg; Color panelBg = DarkBg;
    [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint color, out bool opaque);

    static Color SystemAccent()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
            {
                object v = k == null ? null : k.GetValue("AccentColor");
                if (v is int) { uint c = unchecked((uint)(int)v); return Color.FromArgb((int)(c & 0xFF), (int)(c >> 8 & 0xFF), (int)(c >> 16 & 0xFF)); } // 0xAABBGGRR
            }
        }
        catch { }
        try { uint c; bool o; if (DwmGetColorizationColor(out c, out o) == 0) return Color.FromArgb((int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF)); } catch { }
        return Color.FromArgb(0, 120, 215);
    }

    static double Luminance(Color c)
    {
        Func<int, double> f = v => { double x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); };
        return 0.2126 * f(c.R) + 0.7152 * f(c.G) + 0.0722 * f(c.B);
    }

    // Keeps the hue but darkens a light accent until white text has about 10:1 contrast on it.
    static Color PanelFromAccent(Color a)
    {
        for (int i = 0; i < 60 && Luminance(a) > 0.055; i++) a = Color.FromArgb((int)(a.R * 0.93), (int)(a.G * 0.93), (int)(a.B * 0.93));
        return a;
    }

    void UpdatePanelColor()
    {
        Color c = accentBg ? PanelFromAccent(SystemAccent()) : lightBg ? LightBg : DarkBg;
        if (c.ToArgb() == panelBg.ToArgb()) return;
        panelBg = c; MenuLook.SetBase(lightBg ? DarkBg : c, accentBg); // menus and windows stay dark with the light panel
        UpdateInk();
        if (IsHandleCreated && !hidden) Render();
        foreach (var dd in menuDropDowns) { dd.BackColor = MenuLook.Bg; if (dd.IsHandleCreated) ApplyMenuFrame(dd); }
        if (settings != null && !settings.IsDisposed) settings.ApplyFrameColors();
    }

    void SetTheme(int t) { accentBg = t == 1; lightBg = t == 2; UpdatePanelColor(); Render(); SaveSettings(); }

    void UpdateInk()
    {
        ink = lightBg ? Color.FromArgb(24, 26, 30) : Color.White;
        var old = new[] { brLabel, brValue, brDim, brWarn, brHot };
        brLabel = new SolidBrush(Color.FromArgb(lightBg ? 175 : 150, ink));
        brValue = new SolidBrush(Color.FromArgb(lightBg ? 240 : 235, ink));
        brDim = new SolidBrush(Color.FromArgb(lightBg ? 130 : 105, ink));
        brWarn = new SolidBrush(lightBg ? Color.FromArgb(0xD9, 0x74, 0x00) : cWarn);
        brHot = new SolidBrush(lightBg ? Color.FromArgb(0xC6, 0x28, 0x28) : cHot);
        foreach (var b in old) if (b != null) b.Dispose();
    }

    // ---- menu look: same dark panel, accent and type as the widget; rounded on Windows 11 by DWM, by a region elsewhere
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    MenuLook menuLook; Font menuFont, menuBold;
    readonly List<ToolStripDropDownMenu> menuDropDowns = new List<ToolStripDropDownMenu>();

    // Menus share the widget's opacity (never below 50%, so they stay readable).
    double MenuOpacity { get { return Math.Max(50, opacity) / 100.0; } }

    void SyncMenus()
    {
        double o = MenuOpacity;
        foreach (var dd in menuDropDowns)
            if (Math.Abs(dd.Opacity - o) > 0.001) { dd.Opacity = o; if (dd.IsHandleCreated) MarkToolWindow(dd); }
    }

    // Without an owner a popup gets a taskbar button while open; a tool window never does. WinForms rewrites
    // the extended style when a menu turns translucent, so KeepToolWindow also guards every later change.
    readonly List<KeepToolWindow> toolWindowKeepers = new List<KeepToolWindow>();
    void MarkToolWindow(ToolStripDropDown dd)
    {
        long ex = GetWindowLongPtr(dd.Handle, GWL_EXSTYLE).ToInt64(), want = (ex | WS_EX_TOOLWINDOW) & ~0x40000L /*APPWINDOW*/;
        if (want != ex) SetWindowLongPtr(dd.Handle, GWL_EXSTYLE, new IntPtr(want));
    }

    // Windows 11 rounds and borders an opaque menu itself; set to false if it ever stops doing so for translucent ones.
    static bool UseDwmFrame(ToolStripDropDownMenu dd) { return true; }

    // Rounded frame: Windows 11 draws it (corners + border in our colour); elsewhere a rounded region.
    void ApplyMenuFrame(ToolStripDropDownMenu dd)
    {
        bool dwm = MenuLook.DwmCorners && UseDwmFrame(dd);
        if (dd.IsHandleCreated && MenuLook.DwmCorners)
        {
            int pref = dwm ? 2 : 1 /*round / don't round*/; DwmSetWindowAttribute(dd.Handle, 33 /*WINDOW_CORNER_PREFERENCE*/, ref pref, 4);
            Color b = MenuLook.Border; int col = dwm ? (b.R | b.G << 8 | b.B << 16) : unchecked((int)0xFFFFFFFE) /*no border*/;
            DwmSetWindowAttribute(dd.Handle, 34 /*BORDER_COLOR*/, ref col, 4);
        }
        var old = dd.Region;
        if (dwm) dd.Region = null;
        else using (var p = MenuLook.Round(new RectangleF(0, 0, dd.Width, dd.Height), 8 * dpi)) dd.Region = new Region(p);
        if (old != null) old.Dispose();
        MenuLook.OwnBorder = !dwm;
    }
    void StyleMenu(ToolStripDropDownMenu dd)
    {
        if (menuLook == null) menuLook = new MenuLook(dpi);
        if (menuFont == null) { menuFont = Lang.MakeFont(9.75f, false); menuBold = Lang.MakeFont(9.75f, true); }
        dd.Renderer = menuLook; dd.Font = menuFont;
        dd.ShowImageMargin = false; dd.ShowCheckMargin = true; dd.DropShadowEnabled = false;
        int v = (int)Math.Round(5 * dpi); dd.Padding = new Padding(0, v, 0, v);
        menuDropDowns.Add(dd);
        dd.HandleCreated += delegate
        {
            toolWindowKeepers.Add(new KeepToolWindow(dd.Handle));
            MarkToolWindow(dd);
            int off = 1; DwmSetWindowAttribute(dd.Handle, 3 /*TRANSITIONS_FORCEDISABLED*/, ref off, 4); // no window-open animation
            ApplyMenuFrame(dd);
        };
        dd.Opening += delegate { if (dd.IsHandleCreated) { MarkToolWindow(dd); ApplyMenuFrame(dd); } };
        dd.SizeChanged += delegate { ApplyMenuFrame(dd); };
        foreach (ToolStripItem it in dd.Items)
        {
            var mi = it as ToolStripMenuItem; if (mi == null) continue;
            StyleItem(mi);
            if (mi.HasDropDownItems) StyleMenu((ToolStripDropDownMenu)mi.DropDown);
        }
    }

    void StyleItem(ToolStripMenuItem mi)
    {
        int ip = (int)Math.Round(3 * dpi);
        mi.Padding = new Padding(4, ip, 4 + ip, ip);
        if (mi.Name == "accent") mi.Font = menuBold;
    }

    // "Show" lists the sections in their current order and toggles them for the mode you're in (full or mini).
    void FillShowMenu(ToolStripMenuItem show)
    {
        show.DropDownItems.Clear();
        foreach (var sid in Sections())
        {
            string id = sid; bool inMini = mini;
            var it = new ToolStripMenuItem(Title(id)) { Checked = IsShown(id, inMini) };
            it.Click += delegate { if (SetShown(id, inMini, !IsShown(id, inMini))) it.Checked = IsShown(id, inMini); };
            StyleItem(it); show.DropDownItems.Add(it);
        }
        show.DropDownItems.Add(new ToolStripSeparator());
        var more = new ToolStripMenuItem(Lang.T("menu.more"), null, delegate { show.DropDown.Close(); menu.Close(); OpenSettings(); });
        StyleItem(more); show.DropDownItems.Add(more);
    }

    // ---- tray icon: left click hides/shows the widget, right click opens the same menu
    static Icon AppIcon(Size sz)
    {
        try { using (var st = typeof(WidgetForm).Assembly.GetManifestResourceStream("SysWidget.ico")) if (st != null) return new Icon(st, sz); } catch { }
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return SystemIcons.Application; }
    }

    void BuildTray()
    {
        tray = new NotifyIcon { Icon = AppIcon(SystemInformation.SmallIconSize), Text = "SysWidget", ContextMenuStrip = menu };
        tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) SetHidden(!hidden); };
        tray.BalloonTipClicked += delegate { if (balloonUpdate) StartUpdate(); else if (balloonUrl != null) OpenUrl(balloonUrl); else if (balloonTaskMgr) OpenTaskManager(); };
        tray.Visible = !selfTesting;
    }

    void SetHidden(bool h)
    {
        hidden = h;
        if (!h && fsActive && fsMode == 2) fsOverride = true; // shown by hand during a fullscreen app: stay
        fsHiddenNow = false;
        if (h) { HidePopup(); CloseIslandNow(); }
        if (IsHandleCreated)
        {
            if (h) ShowWindow(Handle, 0 /*SW_HIDE*/);
            else
            {
                Render(); ShowWindow(Handle, 8 /*SW_SHOWNA*/);
                if (onTop) SetOnTop(true); else { desktopRaised = false; SendToDesktopLevel(); }
            }
        }
        SaveSettings();
    }

    void UpdateTray()
    {
        if (tray == null) return;
        var sb = new StringBuilder("SysWidget");
        sb.Append('\n').Append(Lang.T("label.cpu")).Append(' ').Append(Pct(cpu));
        foreach (var g in gpus) if (!g.Idle && IsLive("gpu:" + g.Key)) sb.Append("  \u00B7  ").Append(GpuLabel(g)).Append(' ').Append(Pct(g.Load));
        sb.Append('\n').Append(Lang.T("label.ram")).Append(' ').Append(Lang.F("ram.value", N(ramUsed, "0.0"), N(ramTotal, "0")));
        if (fpsVal > 0) sb.Append("  \u00B7  ").Append(N(fpsVal, "0")).Append(' ').Append(Lang.T("label.fps"));
        if (updateTag != null) sb.Append('\n').Append(Lang.F("tray.update", updateTag));
        string t = sb.Length > 63 ? sb.ToString(0, 63) : sb.ToString(); // NotifyIcon's limit
        if (t != trayText) { trayText = t; tray.Text = t; }
    }

    // ---- update check: asks GitHub for the latest release ~30 s after start, then every 6 h ----
    const string Repo = "SysWidget/SysWidget"; // moved from Dimokat/SysWidget (GitHub redirects the old address)
    const string DonateUrl = "https://send.monobank.ua/jar/125L2LfHgy";
    bool checkUpdates = true; string updateTag, updateUrl, updateZip, updateSha;
    readonly System.Windows.Forms.Timer updTimer = new System.Windows.Forms.Timer();
    static readonly Version AppVersion = typeof(WidgetForm).Assembly.GetName().Version;
    static string VersionText { get { return AppVersion.Major + "." + AppVersion.Minor + "." + AppVersion.Build; } }

    // ---- one-click update: download the release zip, check it, unpack it and let its Setup.exe update us
    bool updating, balloonUpdate; string lastVersion; // the version that ran last time
    static string UpdateDir { get { return Path.Combine(Path.GetTempPath(), "SysWidget-update"); } }
    static string InstalledExe { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SysWidget", "SysWidget.exe"); } }
    // Only the installed copy updates itself; a copy elsewhere opens the download page instead.
    bool CanSelfUpdate
    {
        get { try { return updateZip != null && string.Equals(Path.GetFullPath(Application.ExecutablePath), InstalledExe, StringComparison.OrdinalIgnoreCase); } catch { return false; } }
    }

    void StartUpdate()
    {
        if (updating || updateTag == null) return;
        if (!CanSelfUpdate) { OpenUrl(updateUrl); return; }
        updating = true;
        string tag = updateTag, zipUrl = updateZip, sha = updateSha, page = updateUrl;
        if (tray != null) tray.ShowBalloonTip(5000, Lang.F("notify.update.title", tag), Lang.T("notify.update.downloading"), ToolTipIcon.Info);
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                string dir = Path.Combine(UpdateDir, tag);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                string zip = Path.Combine(dir, "update.zip"), files = Path.Combine(dir, "files");
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var wc = new WebClient()) { wc.Headers[HttpRequestHeader.UserAgent] = "SysWidget/" + VersionText; wc.DownloadFile(zipUrl, zip); }
                if (sha != null)
                    using (var hash = System.Security.Cryptography.SHA256.Create())
                    using (var st = File.OpenRead(zip))
                        if (BitConverter.ToString(hash.ComputeHash(st)).Replace("-", "").ToLowerInvariant() != sha) throw new Exception(Lang.T("notify.update.badfile"));
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, files);
                string setup = Path.Combine(files, "SysWidget", "Setup.exe");
                if (!File.Exists(setup) || !File.Exists(Path.Combine(files, "SysWidget", "SysWidget.exe"))) throw new FileNotFoundException("Setup.exe");
                BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        SaveSettings();
                        Process.Start(new ProcessStartInfo(setup, "/update") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setup) });
                        skipSave = true; Close(); // Setup waits for us to exit, installs, then starts the new version
                    }
                    catch (Exception ex) { UpdateFailed(tag, page, ex.Message); }
                });
            }
            catch (Exception ex) { var msg = ex.Message; BeginInvoke((MethodInvoker)delegate { UpdateFailed(tag, page, msg); }); }
        });
    }

    void UpdateFailed(string tag, string page, string why)
    {
        updating = false;
        if (tray == null) return;
        balloonUpdate = false; balloonTaskMgr = false; balloonUrl = page;
        tray.ShowBalloonTip(8000, Lang.F("notify.update.title", tag), Lang.F("notify.update.failed", why), ToolTipIcon.Warning);
    }

    void StartUpdateChecks()
    {
        try { Directory.Delete(UpdateDir, true); } catch { } // what the last update unpacked
        updTimer.Interval = 30 * 1000;
        updTimer.Tick += delegate { updTimer.Interval = 6 * 3600 * 1000; CheckForUpdate(); };
        updTimer.Start();
    }

    // GitHub's "latest release" JSON -> the tag (only if newer than us), its page, the zip and the zip's SHA-256
    // (from the release notes, which the release workflow writes). The zip is only accepted from this repo.
    internal static bool ParseRelease(string json, out string tag, out string page, out string zipUrl, out string sha)
    {
        tag = page = zipUrl = sha = null;
        var tm = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){1,3})\"");
        var um = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/releases/tag/[^\"]+)\"");
        if (!tm.Success || new Version(tm.Groups[1].Value) <= new Version(VersionText)) return false;
        tag = "v" + tm.Groups[1].Value;
        page = um.Success ? um.Groups[1].Value : "https://github.com/" + Repo + "/releases/latest";
        var zm = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/(?:SysWidget|Dimokat)/SysWidget/releases/download/[^\"]+\\.zip)\"");
        var sm = Regex.Match(json, "\\.zip`?\\s*:\\s*`?([0-9a-fA-F]{64})");
        zipUrl = zm.Success ? zm.Groups[1].Value : null;
        sha = sm.Success ? sm.Groups[1].Value.ToLowerInvariant() : null;
        return true;
    }

    void CheckForUpdate()
    {
        if (!checkUpdates) return;
        CheckTranslation();
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
                req.UserAgent = "SysWidget/" + VersionText; req.Accept = "application/vnd.github+json"; req.Timeout = 15000;
                string json;
                using (var resp = req.GetResponse()) using (var rd = new StreamReader(resp.GetResponseStream())) json = rd.ReadToEnd();
                string t, u, z, h;
                if (!ParseRelease(json, out t, out u, out z, out h)) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    updateTag = t; updateUrl = u; updateZip = z; updateSha = h; if (!hidden) Render();
                    if (tray != null && notifiedTag != t)
                    {
                        notifiedTag = t; balloonUpdate = CanSelfUpdate; balloonUrl = balloonUpdate ? null : u; balloonTaskMgr = false;
                        tray.ShowBalloonTip(8000, Lang.F("notify.update.title", t), Lang.T(balloonUpdate ? "notify.update.click" : "notify.update.text"), ToolTipIcon.Info);
                    }
                });
            }
            catch { } // offline, no releases yet, rate-limited: just try again next time
        });
    }

    // ---- translations: the file for the current language is refreshed from the repo's main branch, so a
    // translation merged on GitHub reaches everyone without a new release. Only text is downloaded; Lang.Parse
    // keeps known keys with valid placeholders, so a bad file can only fall back to English.
    void CheckTranslation()
    {
        var codes = new List<string>();
        if (Lang.Code != "en") codes.Add(Lang.Code);
        else foreach (var c in Lang.Candidates(langChoice)) { if (c == "en" || codes.Count == 2) break; codes.Add(c); }
        if (codes.Count == 0 || Lang.CacheDir == null) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            bool changed = false;
            foreach (var c in codes)
            {
                try
                {
                    Directory.CreateDirectory(Lang.CacheDir);
                    string file = Path.Combine(Lang.CacheDir, c + ".txt"), etagFile = Path.Combine(Lang.CacheDir, c + ".etag");
                    var req = (HttpWebRequest)WebRequest.Create("https://raw.githubusercontent.com/" + Repo + "/main/lang/" + Uri.EscapeDataString(c) + ".txt");
                    req.UserAgent = "SysWidget/" + VersionText; req.Timeout = 15000;
                    if (File.Exists(file) && File.Exists(etagFile)) req.Headers[HttpRequestHeader.IfNoneMatch] = File.ReadAllText(etagFile).Trim();
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        var buf = new char[256 * 1024]; int n = rd.ReadBlock(buf, 0, buf.Length);
                        string text = new string(buf, 0, n);
                        if (n == buf.Length || !Lang.LooksValid(text)) continue;
                        string old = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
                        if (old != text) { File.WriteAllText(file, text, new UTF8Encoding(false)); changed = true; }
                        string etag = resp.Headers[HttpResponseHeader.ETag];
                        if (!string.IsNullOrEmpty(etag)) File.WriteAllText(etagFile, etag);
                    }
                    break;
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r != null && r.StatusCode == HttpStatusCode.NotModified) break; // our copy is current
                    // 404: the repo has no file for this code, try the next one; offline: try again next time
                }
                catch { }
            }
            if (changed) BeginInvoke((MethodInvoker)ApplyLanguage);
        });
    }

    // ---- language: "" follows Windows; a change re-creates fonts and the menu and redraws everything
    string langChoice = "";
    internal string LanguageChoice { get { return langChoice; } }

    internal void SetLanguage(string c)
    {
        langChoice = c ?? ""; SaveSettings(); ApplyLanguage();
        if (checkUpdates) CheckTranslation(); // downloads only while update checks are on (see the privacy policy)
    }

    void ApplyLanguage()
    {
        if (menu != null && menu.Visible) { langPending = true; return; }
        langPending = false;
        Lang.Load(langChoice);
        ApplyScale();
        if (menuFont != null) { menuFont.Dispose(); menuBold.Dispose(); menuFont = menuBold = null; }
        var old = menu;
        menuDropDowns.Clear(); toolWindowKeepers.Clear();
        BuildMenu();
        if (tray != null) tray.ContextMenuStrip = menu;
        if (old != null) old.Dispose();
        if (langMenu != null) { langMenu.Dispose(); langMenu = null; }
        trayText = ""; UpdateTray();
        Relayout();
        if (settings != null && !settings.IsDisposed) settings.LanguageChanged();
    }

    // The language list in Settings: Automatic first, then every available language by its own name.
    ToolMenu langMenu;
    internal void ShowLanguageMenu(Control owner, Point at)
    {
        if (langMenu != null) { menuDropDowns.Remove(langMenu); langMenu.Dispose(); }
        langMenu = new ToolMenu();
        var auto = new ToolStripMenuItem(Lang.F("settings.language.auto", Lang.NativeName(Lang.AutoCode()))) { Checked = langChoice == "" };
        auto.Click += delegate { SetLanguage(""); };
        langMenu.Items.Add(auto);
        langMenu.Items.Add(new ToolStripSeparator());
        var codes = Lang.Available();
        codes.Sort((a, b) => string.Compare(Lang.NativeName(a), Lang.NativeName(b), StringComparison.CurrentCultureIgnoreCase));
        foreach (var code in codes)
        {
            string c = code;
            var it = new ToolStripMenuItem(Lang.NativeName(c)) { Checked = langChoice.Equals(c, StringComparison.OrdinalIgnoreCase) };
            it.Click += delegate { SetLanguage(c); };
            langMenu.Items.Add(it);
        }
        StyleMenu(langMenu);
        langMenu.Show(owner, at);
    }

    // Open links through Explorer so the browser doesn't inherit the widget's admin rights.
    static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !(url.StartsWith("https://github.com/") || url.StartsWith("https://send.monobank.ua/"))) return;
        try { Process.Start("explorer.exe", "\"" + url + "\""); } catch { }
    }

    // Snap to one of 9 spots on the current screen: [T|M|B][L|C|R]. Stays snapped through size/mode changes.
    void SnapTo(string a)
    {
        CloseIslandNow();
        anchor = a;
        var wa = Screen.FromRectangle(Body).WorkingArea;
        int x = a[1] == 'L' ? wa.Left : a[1] == 'R' ? wa.Right - W : wa.Left + (wa.Width - W) / 2;
        int y = a[0] == 'T' ? wa.Top : a[0] == 'B' ? wa.Bottom - H : wa.Top + (wa.Height - H) / 2;
        PlaceBody(new Point(x, y));
        AutoDirection();
        SaveSettings();
    }

    static void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
    }

    // Start with Windows = a Task Scheduler logon task with highest privileges (FPS needs admin),
    // so there's no UAC prompt at login; set to keep running on battery.
    internal static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(10000); return p.ExitCode; }
    }

    static bool IsStartup()
    {
        try { return Schtasks("/Query /TN \"SysWidget\"") == 0; } catch { return false; }
    }

    static void SetStartup(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.DeleteValue("SysWidget", false); // old method
            if (!on) { Schtasks("/Delete /TN \"SysWidget\" /F"); return; }
            string exe = Application.ExecutablePath, dir = Path.GetDirectoryName(exe);
            string user = System.Security.SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--task</Arguments><WorkingDirectory>" +
                System.Security.SecurityElement.Escape(dir) + "</WorkingDirectory></Exec></Actions></Task>";
            string xmlPath = Path.Combine(Path.GetTempPath(), "SysWidget.task.xml");
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            Schtasks("/Create /TN \"SysWidget\" /XML \"" + xmlPath + "\" /F");
            File.Delete(xmlPath);
        }
        catch { }
    }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(iniPath)) return;
            foreach (var line in File.ReadAllLines(iniPath))
            {
                int i = line.IndexOf('='); if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim(); int n;
                if (k == "x" && int.TryParse(v, out n)) posX = n;
                else if (k == "y" && int.TryParse(v, out n)) posY = n;
                else if (k == "ontop") onTop = v == "1";
                else if (k == "locked") locked = v == "1";
                else if (k == "clickthrough") clickThrough = v == "1";
                else if (k == "hidehover") hideHover = v == "1";
                else if (k == "mini") miniPref = v == "1";
                else if (k == "hidden") hidden = v == "1";
                else if (k == "bg") { accentBg = v == "accent"; lightBg = v == "light"; }
                else if (k == "lang") langChoice = Regex.IsMatch(v, @"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$") ? v : "";
                else if (k == "fps") legacyFps = v;
                else if (k == "order") order = Split(v);
                else if (k == "hide") { hiddenFull.Clear(); hiddenFull.UnionWith(Split(v)); hasHide = true; }
                else if (k == "minihide") { hiddenMini.Clear(); hiddenMini.UnionWith(Split(v)); hasMiniHide = true; }
                else if (k == "off") { detailsOff.Clear(); detailsOff.UnionWith(Split(v)); }
                else if (k == "compact") compact = v == "1";
                else if (k == "colors")
                    foreach (var kv in Split(v))
                    {
                        int c = kv.LastIndexOf(':'); int rgb;
                        if (c > 0 && int.TryParse(kv.Substring(c + 1), NumberStyles.HexNumber, CI, out rgb)) colors[kv.Substring(0, c)] = Color.FromArgb(255, Color.FromArgb(rgb));
                    }
                else if (k == "updates") checkUpdates = v != "0";
                else if (k == "anchor") { if (v.Length == 2 && "TMB".IndexOf(v[0]) >= 0 && "LCR".IndexOf(v[1]) >= 0) anchor = v; }
                else if (k == "scale") { float f; if (float.TryParse(v, NumberStyles.Float, CI, out f) && f >= 0.5f && f <= 3f) scale = f; }
                else if (k == "opacity") { if (int.TryParse(v, out n) && n >= 10 && n <= 100) opacity = n; }
                else if (k == "show") legacyShow = v;
                else if (k == "graph") graphStyle = v == "line" ? 1 : v == "bars" ? 2 : 0;
                else if (k == "interval") { if (int.TryParse(v, out n) && (n == 500 || n == 1000 || n == 2000)) intervalMs = n; }
                else if (k == "history") { if (int.TryParse(v, out n) && (n == 60 || n == 300)) graphSec = n; }
                else if (k == "temp") fahrenheit = v == "f";
                else if (k == "netunit") netBits = v == "bits";
                else if (k == "ping") { if (ValidHost(v)) pinger.Host = v; }
                else if (k == "fullscreen") fsMode = v == "mini" ? 1 : v == "hide" ? 2 : 0;
                else if (k == "hotkeys") hotkeys = v != "0";
                else if (k == "hoverapps") hoverApps = v != "0";
                else if (k == "known") known.UnionWith(Split(v));
                else if (k == "tips") tipsSeen = v == "1";
                else if (k == "ministyle") islandStyle = v != "classic";
                else if (k == "islandpop") islandPop = v != "0";
                else if (k == "minidir") miniDir = v == "row" ? 1 : v == "column" ? 2 : 0;
                else if (k == "minivert") miniVert = v == "1";
                else if (k == "rtss") rtss = v == "1";
                else if (k == "version") lastVersion = v;
                else if (k == "alerts")
                {
                    var parts = v.Split(',');
                    for (int j = 0; j < parts.Length && j < alerts.Length; j++)
                    {
                        var ap = parts[j].Split(':'); int at;
                        if (ap.Length == 2 && int.TryParse(ap[1], NumberStyles.Integer, CI, out at) && at > 0 && at < 200) { alerts[j].On = ap[0] == "1"; alerts[j].At = at; }
                    }
                }
            }
        }
        catch { }
        // Settings from before sections were configurable: show=cpu,gpu,ram,net,disk and fps=0/1.
        if (!hasHide)
        {
            if (legacyShow != null) foreach (var id in new[] { "cpu", "ram", "net", "disk" }) if (!legacyShow.Contains(id)) hiddenFull.Add(id);
            if (legacyShow != null && !legacyShow.Contains("gpu")) legacyHideGpus = true;
            if (legacyFps == "0") hiddenFull.Add("fps");
        }
        if (!hasMiniHide) { hiddenMini.UnionWith(hiddenFull); legacyHideGpusMini = legacyHideGpus; }
    }

    internal static bool ValidHost(string v) { return v != null && Regex.IsMatch(v, @"^[A-Za-z0-9][A-Za-z0-9.:\-]{0,252}$"); }

    static List<string> Split(string v)
    {
        var l = new List<string>();
        foreach (var p in v.Split(',')) { string t = p.Trim(); if (t != "" && !l.Contains(t)) l.Add(t); }
        return l;
    }

    void SaveSettings()
    {
        if (selfTesting) return; // the self-test reads your settings but never writes them
        try
        {
            File.WriteAllLines(iniPath, new[] {
                "x=" + (Left + M).ToString(CI), "y=" + (Top + M).ToString(CI),
                "ontop=" + (onTop ? "1" : "0"), "locked=" + (locked ? "1" : "0"), "clickthrough=" + (clickThrough ? "1" : "0"), "hidehover=" + (hideHover ? "1" : "0"), "mini=" + (miniPref ? "1" : "0"), "hidden=" + (hidden ? "1" : "0"), "bg=" + (accentBg ? "accent" : lightBg ? "light" : "dark"), "lang=" + langChoice, "updates=" + (checkUpdates ? "1" : "0"), "anchor=" + anchor,
                "scale=" + scale.ToString(CI), "opacity=" + opacity.ToString(CI),
                "order=" + string.Join(",", order), "hide=" + string.Join(",", hiddenFull), "minihide=" + string.Join(",", hiddenMini),
                "off=" + string.Join(",", detailsOff), "compact=" + (compact ? "1" : "0"), "colors=" + ColorsText(),
                "graph=" + (graphStyle == 1 ? "line" : graphStyle == 2 ? "bars" : "area"), "interval=" + intervalMs.ToString(CI), "history=" + graphSec.ToString(CI),
                "temp=" + (fahrenheit ? "f" : "c"), "netunit=" + (netBits ? "bits" : "bytes"), "ping=" + pinger.Host,
                "fullscreen=" + (fsMode == 1 ? "mini" : fsMode == 2 ? "hide" : "off"), "hotkeys=" + (hotkeys ? "1" : "0"), "hoverapps=" + (hoverApps ? "1" : "0"),
                "alerts=" + AlertsText(), "known=" + string.Join(",", known), "tips=" + (tipsSeen ? "1" : "0"),
                "ministyle=" + (islandStyle ? "island" : "classic"), "islandpop=" + (islandPop ? "1" : "0"),
                "minidir=" + (miniDir == 1 ? "row" : miniDir == 2 ? "column" : "auto"), "minivert=" + (miniVert ? "1" : "0"), "rtss=" + (rtss ? "1" : "0"), "version=" + VersionText });
        }
        catch { }
    }
}

// The context menu always asks for a tool window, also when WinForms rebuilds its style.
sealed class ToolMenu : ContextMenuStrip
{
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle = (cp.ExStyle | 0x80 /*TOOLWINDOW*/) & ~0x40000 /*APPWINDOW*/; return cp; }
    }
}

// Keeps WS_EX_TOOLWINDOW on a window whatever later rewrites its extended style.
sealed class KeepToolWindow : NativeWindow
{
    public KeepToolWindow(IntPtr hwnd) { AssignHandle(hwnd); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x7C /*WM_STYLECHANGING*/ && (int)m.WParam.ToInt64() == -20 /*GWL_EXSTYLE*/)
        {
            int ex = Marshal.ReadInt32(m.LParam, 4); // STYLESTRUCT.styleNew
            Marshal.WriteInt32(m.LParam, 4, (ex | 0x80 /*TOOLWINDOW*/) & ~0x40000 /*APPWINDOW*/);
        }
        base.WndProc(ref m);
    }
}

// Context menu drawn like the widget: dark panel, soft accent highlight, accent check marks.
sealed class MenuLook : ToolStripProfessionalRenderer
{
    public static readonly Color Text = Color.FromArgb(232, 233, 236), Dim = Color.FromArgb(150, 152, 160), Accent = Color.FromArgb(0x4F, 0xC3, 0xF7);
    public static Color Bg = Color.FromArgb(22, 23, 27), Border = Color.FromArgb(56, 58, 66), Sep = Color.FromArgb(44, 46, 53), Hover = Color.FromArgb(46, Accent);
    public static bool OwnBorder = true;

    // Derives the menu colours from the widget's panel colour.
    public static void SetBase(Color panel, bool accent)
    {
        Bg = Mix(panel, Color.White, 0.015);
        Border = Mix(panel, Color.White, 0.15);
        Sep = Mix(panel, Color.White, 0.09);
        Hover = accent ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(46, Accent);
    }

    static Color Mix(Color a, Color b, double t)
    {
        return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
    }
    public static readonly bool DwmCorners = IsWin11();
    readonly float dpi;

    public MenuLook(float dpi) : base(new Palette()) { this.dpi = dpi; RoundedEdges = false; }

    static bool IsWin11()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            { int b; return k != null && int.TryParse(k.GetValue("CurrentBuildNumber") as string, out b) && b >= 22000; }
        }
        catch { return false; }
    }

    sealed class Palette : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color SeparatorDark { get { return Sep; } }
        public override Color SeparatorLight { get { return Bg; } }
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Bg); }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (!OwnBorder) return; // Windows 11 draws a rounded border in our color
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Round(new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1.5f, e.ToolStrip.Height - 1.5f), 8 * dpi))
        using (var pen = new Pen(Border)) g.DrawPath(pen, p);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float ins = 5 * dpi;
        using (var p = Round(new RectangleF(ins, 0.5f, e.Item.Width - 2 * ins, e.Item.Height - 1), 5 * dpi))
        using (var b = new SolidBrush(Hover)) g.FillPath(b, p);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        string n = e.Item.Name;
        e.TextColor = !e.Item.Enabled || n == "dim" ? (e.Item.Selected && n == "dim" ? Text : Dim) : n == "accent" ? Accent : Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ArrowRectangle; float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, a = 3.2f * dpi;
        using (var pen = new Pen(e.Item != null && e.Item.Selected ? Text : Dim, 1.4f * dpi))
        {
            pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, new[] { new PointF(cx - a / 2, cy - a), new PointF(cx + a / 2, cy), new PointF(cx - a / 2, cy + a) });
        }
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle; float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = 3.4f * dpi;
        using (var pen = new Pen(Accent, 1.8f * dpi))
        {
            pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, new[] { new PointF(cx - u * 1.15f, cy + u * 0.05f), new PointF(cx - u * 0.35f, cy + u * 0.85f), new PointF(cx + u * 1.2f, cy - u * 0.9f) });
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2; float ins = 10 * dpi;
        using (var pen = new Pen(Sep)) e.Graphics.DrawLine(pen, ins, y, e.Item.Width - ins, y);
    }
}
