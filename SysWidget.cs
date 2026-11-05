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
        if (args.Length >= 2 && args[0] == "--shots")
        {
            // README screenshots: records for a while (so the graphs fill up), saves PNGs into the folder, exits.
            WidgetForm.ShotsDir = Path.GetFullPath(args[1]); int secs;
            WidgetForm.ShotsSecs = args.Length > 2 && int.TryParse(args[2], out secs) ? secs : 60;
            Directory.CreateDirectory(WidgetForm.ShotsDir);
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new WidgetForm());
            return;
        }
        if (args.Length == 2 && args[0] == "--snapshot")
        {
            // Renders one frame to a PNG and exits (used for testing without touching the screen).
            using (var f = new WidgetForm()) { Thread.Sleep(1100); f.SaveSnapshot(args[1]); }
            return;
        }
        // Started without admin rights (Start menu, double-click)? If the startup task exists, start through it
        // instead: it runs with highest privileges (needed for FPS) and shows no UAC prompt.
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

sealed class WidgetForm : Form
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
    int posX = int.MinValue, posY = int.MinValue;

    // ------------------------------------------------------------------ look
    float dpi, s; int W; int H; float pad;
    Font fLabel, fValue, fSmall;
    float scale = 1f; int opacity = 100; bool mini;
    string anchor = ""; Point moveStart, grab;
    readonly StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone();
    readonly SolidBrush brLabel, brValue, brDim, brWarn, brHot;
    static readonly Color cCpu = Color.FromArgb(0x4F, 0xC3, 0xF7), cGpu = Color.FromArgb(0x81, 0xC7, 0x84),
        cRam = Color.FromArgb(0xFF, 0xB7, 0x4D), cDown = Color.FromArgb(0xBA, 0x68, 0xC8), cUp = Color.FromArgb(0xF0, 0x62, 0x92),
        cDisk = Color.FromArgb(0x90, 0xA4, 0xAE), cFps = Color.FromArgb(0xFF, 0xD5, 0x4F), cWarn = Color.FromArgb(0xFF, 0xA7, 0x26), cHot = Color.FromArgb(0xEF, 0x53, 0x50);
    Bitmap canvas;

    // ------------------------------------------------------------------ sensors
    readonly Pdh pdh = new Pdh();
    IntPtr pcCpu, pcTemp, pcDiskR, pcDiskW;
    readonly Ring hCpu = new Ring(60), hDown = new Ring(60), hUp = new Ring(60), hFps = new Ring(60);
    FpsCounter fps; float fpsVal; string fpsApp = ""; int fpsPid = -1; readonly int ownPid = Process.GetCurrentProcess().Id;
    float cpu, cpuTemp = float.NaN, ramUsed, ramTotal, diskR, diskW, down, up;
    List<Gpu> gpus = new List<Gpu>();
    List<NetworkInterface> nics = new List<NetworkInterface>(); string nicKey = ""; long lastRx = -1, lastTx = -1;
    readonly Stopwatch netClock = new Stopwatch();
    List<DriveStat> drives = new List<DriveStat>();
    int tick;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    ContextMenuStrip menu; ToolStripMenuItem miTop, miLock, miClick, miStartup, miHide;
    NotifyIcon tray; bool hidden; string trayText = "", notifiedTag;
    static readonly int WM_SHOWME = Program.RegisterWindowMessage("SysWidget.Show");
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr info);
    WinEventProc fgProc; IntPtr fgHook;

    public WidgetForm()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX / 96f;
        sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        brLabel = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
        brValue = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        brDim = new SolidBrush(Color.FromArgb(105, 255, 255, 255));
        brWarn = new SolidBrush(cWarn); brHot = new SolidBrush(cHot);

        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Text = "SysWidget";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        LoadSettings();
        UpdatePanelColor();
        ApplyScale();
        InitSensors();
        Sample();
        if (mini) W = MiniWidth();
        H = Measure();
        Size = WinSize();
        PlaceBody(ValidPosition(posX, posY));
        if (anchor != "") SnapTo(anchor);
        BuildMenu();
        if (ShotsDir == null) BuildTray();
        timer.Interval = 1000;
        timer.Tick += delegate { OnTick(); };
        // Click-through: poll Ctrl + cursor 20x/s, only while click-through is on.
        ctTimer.Interval = 50;
        ctTimer.Tick += delegate { CheckInteractive(); };
    }

    void ApplyScale()
    {
        s = dpi * scale; W = (int)Math.Round(250 * s); pad = 12 * s;
        M = (int)Math.Ceiling(10 * s) + 2;
        if (fLabel != null) { fLabel.Dispose(); fValue.Dispose(); fSmall.Dispose(); }
        // Whole-pixel font sizes hint much more crisply at small scales.
        fLabel = new Font("Segoe UI Semibold", (float)Math.Max(7, Math.Round(10.5f * s)), GraphicsUnit.Pixel);
        fValue = new Font("Segoe UI", (float)Math.Max(8, Math.Round(12.5f * s)), GraphicsUnit.Pixel);
        fSmall = new Font("Segoe UI", (float)Math.Max(7, Math.Round(10.5f * s)), GraphicsUnit.Pixel);
    }

    void Relayout()
    {
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
    Rectangle Body { get { return new Rectangle(Left + M, Top + M, W, H); } }
    void PlaceBody(Point p) { Location = new Point(p.X - M, p.Y - M); }

    void UpdateDock(Rectangle b)
    {
        var wa = Screen.FromRectangle(b).WorkingArea;
        dL = b.Left <= wa.Left; dT = b.Top <= wa.Top; dR = b.Right >= wa.Right; dB = b.Bottom >= wa.Bottom;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (IsHandleCreated && !inMove) Render();
    }

    // Panel outline: rounded corners when free; square where two docked edges meet;
    // a concave fillet flowing into the screen edge where only one side is docked.
    GraphicsPath PanelPath()
    {
        float rad = 10 * s, d = rad * 2, fd = rad * 2;
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
            if (clickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    void SetTransparent(bool on)
    {
        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
    }

    void CheckInteractive()
    {
        if (!clickThrough) return;
        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool want = (ctrl && Body.Contains(Cursor.Position)) || inMove || menu.Visible;
        if (want == interactive) return;
        interactive = want;
        SetTransparent(!want);
        Render();
    }

    void SetClickThrough(bool on)
    {
        clickThrough = on; interactive = false;
        SetTransparent(on);
        ctTimer.Enabled = on;
        Render();
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
        ctTimer.Enabled = clickThrough;
        StartUpdateChecks();
        if (ShotsDir != null) StartShots();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop(); ctTimer.Stop();
        if (fgHook != IntPtr.Zero) UnhookWinEvent(fgHook);
        SaveSettings();
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
        NormalizeLayout();
        pdh.Collect();
        RefreshNics(); RefreshDrives(); netClock.Start();
        fps = new FpsCounter(); fps.Start();
    }

    void OnTick()
    {
        tick++;
        bool onAC = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;
        timer.Interval = onAC ? 1000 : 2000;
        if (tick % 15 == 0) RefreshNics();
        if (accentBg && tick % 10 == 0) UpdatePanelColor(); // fallback if a colour-change message was missed
        if (tick % 30 == 0)
        {
            int before = drives.Count; RefreshDrives();
            if (drives.Count != before) Relayout();
        }
        Sample();
        UpdateTray();
        if (hidden) return;
        if (!onTop && !desktopRaised && DesktopIsAboveUs()) RaiseOverDesktop();
        if (onTop) KeepOnTop();
        if (!HiddenByFullscreen()) Render();
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

    string FpsText() { return fps == null || fps.Error != null ? (fps == null ? "n/a" : fps.Error) : fpsVal > 0 ? fpsVal.ToString("0", CI) : "\u2014"; }

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

    // ------------------------------------------------------------------ drawing
    int Measure()
    {
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b)) return (int)Math.Ceiling(Draw(g));
    }

    void Render(bool dock = true)
    {
        if (!IsHandleCreated || H <= 0 || ShotsDir != null) return; // screenshot mode never shows the widget itself
        if (dock) UpdateDock(Body);
        var ws = WinSize();
        if (canvas == null || canvas.Width != ws.Width || canvas.Height != ws.Height) { if (canvas != null) canvas.Dispose(); canvas = new Bitmap(ws.Width, ws.Height, PixelFormat.Format32bppArgb); }
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

    void Present(Bitmap bmp)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hbmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hbmp);
            var size = new SIZE(bmp.Width, bmp.Height); var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = (byte)(opacity * 255 / 100), AlphaFormat = 1 };
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

        if (H > 0)
            using (var path = PanelPath())
            using (var b = new SolidBrush(panelBg))
            using (var p = clickThrough && interactive ? new Pen(Color.FromArgb(200, cCpu), 1.5f * s) : new Pen(Color.FromArgb(34, 255, 255, 255), 1))
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

        if (mini) { MiniLayout(g, true); return pad * 1.2f + 17 * s; }

        float gh = (compact ? 16 : 26) * s, sgap = (compact ? 7 : 11) * s, gap = 0;
        hdrH = (compact ? 15.5f : 17) * s; smallH = (compact ? 12.5f : 14) * s;
        foreach (var id in Sections())
        {
            if (!IsShown(id, false)) continue;
            gap = DrawSection(g, id, x, w, ref y, gh, sgap);
            y += gap;
        }
        return y - gap + pad;
    }

    float hdrH = 17, smallH = 14;

    float DrawSection(Graphics g, string id, float x, float w, ref float y, float gh, float sgap)
    {
        Color c = ColorOf(id);
        switch (id)
        {
            case "cpu":
                Header(g, ref y, "CPU", c, Pct(cpu), brValue, Opt(id, "temp") ? TempText(cpuTemp) : null, TempBrush(cpuTemp));
                if (Opt(id, "graph")) { y += 3 * s; Graph(g, x, y, w, gh, hCpu, 100, c, null, Color.Empty); y += gh; }
                return sgap;
            case "fps":
                Header(g, ref y, "FPS", c, FpsText(), fps != null && fps.Error == null ? brValue : brDim, null, null);
                if (Opt(id, "app")) Small(g, ref y, fpsApp == "" ? "no app in focus" : fpsApp, "");
                if (Opt(id, "graph")) { y += 3 * s; Graph(g, x, y, w, gh, hFps, Math.Max(60f, hFps.Max()), c, null, Color.Empty); y += gh; }
                return sgap;
            case "ram":
                Header(g, ref y, "RAM", c, string.Format(CI, "{0:0.0} / {1:0.0} GB", ramUsed, ramTotal), brValue, null, null);
                if (Opt(id, "bar")) { y += 5 * s; Bar(g, x, y, w, 5 * s, ramTotal > 0 ? ramUsed / ramTotal : 0, c); y += 5 * s; return sgap + s; }
                return sgap;
            case "net":
            {
                bool upOn = Opt(id, "up");
                Header(g, ref y, "NET", c, "\u2193 " + Rate(down), brValue, upOn ? "\u2191 " + Rate(up) : null, brLabel);
                if (Opt(id, "graph"))
                {
                    y += 3 * s;
                    float netMax = Math.Max(64 * 1024f, upOn ? Math.Max(hDown.Max(), hUp.Max()) : hDown.Max());
                    Graph(g, x, y, w, gh, hDown, netMax, c, upOn ? hUp : null, ColorOf("net.up")); y += gh;
                }
                return sgap;
            }
            case "disk":
            {
                bool rw = Opt(id, "rw");
                Header(g, ref y, "DISK", c, rw ? "R " + Rate(diskR) : null, brValue, rw ? "W " + Rate(diskW) : null, brValue);
                if (!Opt(id, "free") || drives.Count == 0) return sgap;
                y += 4 * s; float dg = 0;
                foreach (var dr in drives)
                {
                    Small(g, ref y, dr.Name, string.Format(CI, "{0:0} GB free of {1:0}", dr.Free / GB, dr.Total / GB));
                    y += 1 * s;
                    double frac = dr.Total > 0 ? 1 - dr.Free / dr.Total : 0;
                    Bar(g, x, y, w, 4 * s, (float)frac, dr.Total > 0 && dr.Free / dr.Total < 0.1 ? cHot : c);
                    dg = (compact ? 5 : 7) * s; y += 4 * s + dg;
                }
                y -= dg; return sgap;
            }
        }
        var gpu = GpuById(id); if (gpu == null) return 0;
        string label = gpu.Label + " \u00B7 " + gpu.Name;
        bool temp = Opt(id, "temp") && HasTemp(gpu);
        if (gpu.Idle) Header(g, ref y, label, c, "idle (on battery)", brDim, null, null);
        else Header(g, ref y, label, c, Pct(gpu.Load), brValue, temp ? TempText(gpu.Temp) : null, TempBrush(gpu.Temp));
        // The detail row's presence depends only on settings and what the GPU can report, so the height stays put.
        bool mem = Opt(id, "mem") && gpu.MemTotal > 0, pw = Opt(id, "power") && HasPower(gpu);
        if (mem || pw)
            Small(g, ref y, mem && !gpu.Idle ? string.Format(CI, "{0} {1:0.0} / {2:0.0} GB", gpu.MemLabel, gpu.MemUsed / GB, gpu.MemTotal / GB) : "",
                  pw && !gpu.Idle && !float.IsNaN(gpu.Power) ? string.Format(CI, "{0:0.0} W", gpu.Power) : "");
        if (Opt(id, "graph")) { y += 3 * s; Graph(g, x, y, w, gh, gpu.History, 100, c, null, Color.Empty); y += gh; }
        return sgap;
    }

    static bool HasTemp(Gpu g) { return g.NvDev != IntPtr.Zero || g.AdlIndex >= 0; }
    static bool HasPower(Gpu g) { return g.NvDev != IntPtr.Zero || g.AdlPower; }

    // ---- mini mode: one line, fixed-width slots so the strip doesn't jitter ----
    int MiniWidth()
    {
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b)) return (int)Math.Ceiling(MiniLayout(g, false) + pad * 0.9f);
    }

    float MiniLayout(Graphics g, bool draw)
    {
        float x = pad * 0.9f, y = pad * 0.6f; bool any = false;
        string tTemp = "100\u00B0C";
        foreach (var id in Sections())
        {
            if (!IsShown(id, true)) continue;
            Color c = ColorOf(id);
            switch (id)
            {
                case "cpu":
                {
                    bool t = Opt(id, "temp") && pcTemp != IntPtr.Zero;
                    Seg(g, draw, ref x, y, ref any, "CPU", c, Pct(cpu), brValue, "100%", t ? TempText(cpuTemp) : null, TempBrush(cpuTemp), t ? tTemp : null);
                    break;
                }
                case "fps": Seg(g, draw, ref x, y, ref any, "FPS", c, FpsText(), brValue, "999", null, null, null); break;
                case "ram": Seg(g, draw, ref x, y, ref any, "RAM", c, string.Format(CI, "{0:0.0} GB", ramUsed), brValue, "99.9 GB", null, null, null); break;
                case "net":
                {
                    bool upOn = Opt(id, "up");
                    Seg(g, draw, ref x, y, ref any, "NET", c, "\u2193 " + Rate(down), brValue, "\u2193 999 MB/s", upOn ? "\u2191 " + Rate(up) : null, brLabel, upOn ? "\u2191 999 MB/s" : null);
                    break;
                }
                case "disk":
                    if (Opt(id, "free"))
                        foreach (var dr in drives)
                            Seg(g, draw, ref x, y, ref any, dr.Name, c, string.Format(CI, "{0:0} GB", dr.Free / GB), brValue, "9999 GB", null, null, null);
                    else Seg(g, draw, ref x, y, ref any, "DISK", c, "R " + Rate(diskR), brValue, "R 999 MB/s", "W " + Rate(diskW), brValue, "W 999 MB/s");
                    break;
                default:
                {
                    var gp = GpuById(id); if (gp == null) break;
                    bool t = Opt(id, "temp") && HasTemp(gp);
                    if (gp.Idle) Seg(g, draw, ref x, y, ref any, gp.Label, c, "idle", brDim, "100%", null, null, t ? tTemp : null);
                    else Seg(g, draw, ref x, y, ref any, gp.Label, c, Pct(gp.Load), brValue, "100%", t ? TempText(gp.Temp) : null, TempBrush(gp.Temp), t ? tTemp : null);
                    break;
                }
            }
        }
        return x;
    }

    void Seg(Graphics g, bool draw, ref float x, float y, ref bool any, string label, Color accent,
             string v1, Brush b1, string t1, string v2, Brush b2, string t2)
    {
        if (any) x += 14 * s; any = true;
        if (draw) using (var b = new SolidBrush(accent)) g.FillEllipse(b, x, y + 5.5f * s, 6 * s, 6 * s);
        x += 11 * s;
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
            using (var br = new SolidBrush(Color.FromArgb(14, 255, 255, 255))) g.FillPath(br, bg);
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
        if (fill)
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
        using (var p = Round(new RectangleF(x, y, w, h), h / 2)) using (var b = new SolidBrush(Color.FromArgb(26, 255, 255, 255))) g.FillPath(b, p);
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

    static string Pct(float v) { return v.ToString("0", CI) + "%"; }
    static string TempText(float t) { return float.IsNaN(t) ? null : t.ToString("0", CI) + "°C"; }
    Brush TempBrush(float t) { return t >= 90 ? brHot : t >= 80 ? brWarn : brValue; }
    static string Rate(double b)
    {
        if (b < 1024) return b.ToString("0", CI) + " B/s";
        if (b < 1048576) return (b / 1024).ToString(b < 10240 ? "0.0" : "0", CI) + " KB/s";
        return (b / 1048576).ToString(b < 10485760 ? "0.0" : "0", CI) + " MB/s";
    }

    // ------------------------------------------------------------------ window behaviour
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SHOWME && WM_SHOWME != 0) { if (hidden) SetHidden(false); return; }
        switch (m.Msg)
        {
            case WM_NCHITTEST: m.Result = (IntPtr)(locked ? 1 /*HTCLIENT*/ : 2 /*HTCAPTION*/); return;
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
            case WM_EXITSIZEMOVE: inMove = false; hasMoveTarget = false; if (Location != moveStart) anchor = ""; SaveSettings(); break;
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
        if (onTop) { KeepOnTop(); return; }
        var sb = new StringBuilder(32); GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        bool desk = cls == "WorkerW" || cls == "Progman";
        if (desk && !desktopRaised) { if (DesktopIsAboveUs()) RaiseOverDesktop(); }
        else if (!desk && desktopRaised) { desktopRaised = false; SendToDesktopLevel(); }
    }

    // Games often make their own window topmost on start; re-claim the top (not while our menu is open).
    void KeepOnTop()
    {
        if (menu != null && menu.Visible) return;
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
    static readonly string[] BaseSections = { "cpu", "fps", "ram", "net", "disk" };
    static readonly Color[] GpuPalette = { Color.FromArgb(0x81, 0xC7, 0x84), Color.FromArgb(0x4D, 0xB6, 0xAC), Color.FromArgb(0xAE, 0xD5, 0x81), Color.FromArgb(0x64, 0xB5, 0xF6) };
    SettingsWindow settings;

    void NormalizeLayout()
    {
        var all = new List<string> { "cpu" };
        foreach (var g in gpus) all.Add("gpu:" + g.Key);
        all.AddRange(new[] { "fps", "ram", "net", "disk" });
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
        // never end up with an empty widget
        foreach (var set in new[] { hiddenFull, hiddenMini })
        {
            bool any = false; foreach (var id in Sections()) if (!set.Contains(id)) any = true;
            if (!any) set.Remove("cpu");
        }
    }

    internal IEnumerable<string> Sections() { foreach (var id in order) if (Available(id)) yield return id; }
    bool Available(string id) { return !id.StartsWith("gpu:") || GpuById(id) != null; }
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
        switch (id) { case "cpu": return "CPU"; case "fps": return "FPS"; case "ram": return "RAM"; case "net": return "Network"; case "disk": return "Disks"; }
        var g = GpuById(id); return g == null ? id : g.Label + " \u00B7 " + g.Name;
    }

    internal string[][] DetailsOf(string id)
    {
        switch (id)
        {
            case "cpu": return new[] { new[] { "temp", "Temperature" }, new[] { "graph", "Graph" } };
            case "fps": return new[] { new[] { "app", "App name" }, new[] { "graph", "Graph" } };
            case "ram": return new[] { new[] { "bar", "Usage bar" } };
            case "net": return new[] { new[] { "up", "Upload" }, new[] { "graph", "Graph" } };
            case "disk": return new[] { new[] { "rw", "Read / write" }, new[] { "free", "Free space" } };
        }
        var g = GpuById(id); var l = new List<string[]>();
        if (g != null && HasTemp(g)) l.Add(new[] { "temp", "Temperature" });
        l.Add(new[] { "mem", "Memory" });
        if (g != null && HasPower(g)) l.Add(new[] { "power", "Power" });
        l.Add(new[] { "graph", "Graph" });
        return l.ToArray();
    }

    internal string[][] ColorKeys(string id)
    {
        if (id == "net") return new[] { new[] { "net", "Download" }, new[] { "net.up", "Upload" } };
        return new[] { new[] { id, null } };
    }

    internal Color ColorOf(string key) { Color c; return colors.TryGetValue(key, out c) ? c : DefaultColor(key); }
    Color DefaultColor(string key)
    {
        switch (key) { case "cpu": return cCpu; case "fps": return cFps; case "ram": return cRam; case "net": return cDown; case "net.up": return cUp; case "disk": return cDisk; }
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
    internal bool AccentBackground { get { return accentBg; } set { SetAccentBg(value); } }
    internal int SizePercent { get { return (int)Math.Round(scale * 100); } set { scale = value / 100f; ApplyScale(); LayoutChanged(); } }
    internal int OpacityPercent { get { return opacity; } set { opacity = value; Render(); SaveSettings(); } }

    internal void ResetLayout()
    {
        hiddenFull.Clear(); hiddenMini.Clear(); detailsOff.Clear(); colors.Clear(); compact = false; order.Clear();
        NormalizeLayout(); LayoutChanged();
    }

    internal void LayoutChanged()
    {
        Relayout(); SaveSettings();
        if (settings != null && !settings.IsDisposed) settings.Invalidate();
    }

    void OpenSettings()
    {
        if (settings == null || settings.IsDisposed)
        {
            settings = new SettingsWindow(this, dpi);
            try { settings.Icon = AppIcon(new Size(32, 32)); } catch { }
            settings.Reflow(); // final height, before placing it
            // Next to the widget (right side if there's room, else left), so the widget never covers it.
            var b = Body; var wa = Screen.FromRectangle(b).WorkingArea; int gap = (int)Math.Round(12 * dpi);
            int x = b.Right + gap;
            if (x + settings.Width > wa.Right) x = b.Left - gap - settings.Width;
            if (x < wa.Left) x = Math.Max(wa.Left, Math.Min(wa.Right - settings.Width, b.Left + (b.Width - settings.Width) / 2));
            int y = Math.Max(wa.Top, Math.Min(b.Top, wa.Bottom - settings.Height));
            settings.Location = new Point(x, y);
            settings.TopMost = new Rectangle(settings.Location, settings.Size).IntersectsWith(b); // no room beside it: stay above the widget
            settings.FormClosed += delegate { settings = null; };
            settings.Show();
        }
        if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
        settings.Activate();
    }

    // ------------------------------------------------------------------ README screenshots (--shots)
    internal static string ShotsDir; internal static int ShotsSecs = 60;
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT r, int size);

    void StartShots()
    {
        int step = 0;
        var t = new System.Windows.Forms.Timer { Interval = Math.Max(1, ShotsSecs) * 1000 };
        var wa = Screen.PrimaryScreen.WorkingArea;
        var spot = new Point(wa.Left + (int)(80 * dpi), wa.Top + (int)(80 * dpi));
        t.Tick += delegate
        {
            try
            {
                if (step == 0)
                {
                    t.Interval = 900;
                    SaveShot("widget.png");
                    accentBg = true; UpdatePanelColor(); SaveShot("widget-accent.png");
                    accentBg = false; UpdatePanelColor();
                    mini = true; W = MiniWidth(); SaveShot("mini.png");
                    mini = false; ApplyScale(); H = Measure();
                    menu.Show(spot, ToolStripDropDownDirection.BelowRight);
                }
                else if (step == 1)
                {
                    CaptureWindow(menu.Handle, false, "menu.png"); menu.Close();
                    OpenSettings(); settings.TopMost = true; settings.Location = spot;
                }
                else if (step == 2) { CaptureWindow(settings.Handle, true, "settings.png"); settings.Close(); }
                else { t.Stop(); Close(); }
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(ShotsDir, "error.txt"), ex.ToString()); } catch { }
                t.Stop(); Close();
            }
            step++;
        };
        t.Start();
    }

    // The widget drawn on its own, free-floating (no docked edges), with transparent corners.
    void SaveShot(string name)
    {
        dL = dT = dR = dB = false;
        H = Measure();
        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Draw(g);
            }
            bmp.Save(Path.Combine(ShotsDir, name), ImageFormat.Png);
        }
    }

    // A window as it is on screen, with Windows 11's rounded corners made transparent.
    void CaptureWindow(IntPtr hwnd, bool frame, string name)
    {
        RECT r;
        if (!frame || DwmGetWindowAttribute(hwnd, 9 /*EXTENDED_FRAME_BOUNDS*/, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(hwnd, out r);
        int w = r.R - r.L, h = r.B - r.T;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, h));
            RoundCorners(bmp, 8 * dpi);
            bmp.Save(Path.Combine(ShotsDir, name), ImageFormat.Png);
        }
    }

    static void RoundCorners(Bitmap b, float rad)
    {
        int w = b.Width, h = b.Height, n = (int)Math.Ceiling(rad) + 1;
        for (int y = 0; y < n && y < h; y++)
            for (int x = 0; x < n && x < w; x++)
            {
                double dx = rad - (x + 0.5), dy = rad - (y + 0.5);
                if (dx <= 0 || dy <= 0) continue;
                double cover = Math.Max(0, Math.Min(1, rad - Math.Sqrt(dx * dx + dy * dy) + 0.5));
                if (cover >= 1) continue;
                foreach (var p in new[] { new Point(x, y), new Point(w - 1 - x, y), new Point(x, h - 1 - y), new Point(w - 1 - x, h - 1 - y) })
                {
                    var c = b.GetPixel(p.X, p.Y);
                    b.SetPixel(p.X, p.Y, Color.FromArgb((int)(c.A * cover), c));
                }
            }
    }

    // ------------------------------------------------------------------ menu / settings
    void BuildMenu()
    {
        menu = new ToolMenu();
        var miUpdate = new ToolStripMenuItem("", null, delegate { OpenUrl(updateUrl); }) { Visible = false, Name = "accent" };
        var miUpdSep = new ToolStripSeparator { Visible = false };
        menu.Items.Add(miUpdate); menu.Items.Add(miUpdSep);
        menu.Opening += delegate
        {
            miUpdate.Visible = miUpdSep.Visible = updateTag != null;
            if (updateTag != null) miUpdate.Text = "Update available: " + updateTag + "\u2026";
        };
        miTop = new ToolStripMenuItem("Always on top", null, delegate { SetOnTop(!onTop); SaveSettings(); });
        miLock = new ToolStripMenuItem("Lock position", null, delegate { locked = !locked; SaveSettings(); });
        var miMini = new ToolStripMenuItem("Mini mode", null, delegate { mini = !mini; Relayout(); SaveSettings(); });
        miHide = new ToolStripMenuItem("Hide widget", null, delegate { SetHidden(!hidden); });
        menu.Items.Add(miHide); menu.Items.Add(new ToolStripSeparator());
        menu.Opening += delegate { miHide.Text = hidden ? "Show widget" : "Hide widget"; };
        menu.Items.Add(miMini);
        menu.Opening += delegate { miMini.Checked = mini; };
        miClick = new ToolStripMenuItem("Click-through (hold Ctrl to interact)", null, delegate { SetClickThrough(!clickThrough); SaveSettings(); });
        miStartup = new ToolStripMenuItem("Start with Windows", null, delegate { SetStartup(!IsStartup()); });
        menu.Items.Add(miTop); menu.Items.Add(miLock); menu.Items.Add(miClick); menu.Items.Add(miStartup);
        menu.Closed += delegate { CheckInteractive(); };
        var show = new ToolStripMenuItem("Show");
        show.DropDownItems.Add(new ToolStripMenuItem("\u2026")); // filled when opened, from the current order and mode
        show.DropDownOpening += delegate { FillShowMenu(show); };
        show.DropDown.Closing += (o, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        var size = new ToolStripMenuItem("Size");
        foreach (int pct in new[] { 75, 90, 100, 115, 130, 150, 175 })
        {
            int p = pct; var it = new ToolStripMenuItem(p + "%");
            it.Click += delegate { scale = p / 100f; ApplyScale(); Relayout(); SaveSettings(); };
            size.DropDownItems.Add(it);
        }
        size.DropDownOpening += delegate { foreach (ToolStripMenuItem it in size.DropDownItems) it.Checked = it.Text == ((int)Math.Round(scale * 100)) + "%"; };
        var op = new ToolStripMenuItem("Opacity");
        foreach (int pct in new[] { 100, 90, 80, 70, 60, 50, 40, 30 })
        {
            int p = pct; var it = new ToolStripMenuItem(p + "%");
            it.Click += delegate { opacity = p; Render(); SaveSettings(); };
            op.DropDownItems.Add(it);
        }
        op.DropDownOpening += delegate { foreach (ToolStripMenuItem it in op.DropDownItems) it.Checked = it.Text == opacity + "%"; };
        var bgm = new ToolStripMenuItem("Background");
        var bgDark = new ToolStripMenuItem("Dark", null, delegate { SetAccentBg(false); });
        var bgAccent = new ToolStripMenuItem("Windows accent color", null, delegate { SetAccentBg(true); });
        bgm.DropDownItems.Add(bgDark); bgm.DropDownItems.Add(bgAccent);
        bgm.DropDownOpening += delegate { bgDark.Checked = !accentBg; bgAccent.Checked = accentBg; };
        var pos = new ToolStripMenuItem("Position");
        string[] codes = { "TL", "TC", "TR", "-", "ML", "MC", "MR", "-", "BL", "BC", "BR" };
        string[] names = { "Top left", "Top center", "Top right", "", "Middle left", "Center", "Middle right", "", "Bottom left", "Bottom center", "Bottom right" };
        for (int i = 0; i < codes.Length; i++)
        {
            if (codes[i] == "-") { pos.DropDownItems.Add(new ToolStripSeparator()); continue; }
            string c = codes[i]; var it = new ToolStripMenuItem(names[i]) { Tag = c };
            it.Click += delegate { SnapTo(c); };
            pos.DropDownItems.Add(it);
        }
        pos.DropDownOpening += delegate
        {
            foreach (ToolStripItem it in pos.DropDownItems) { var mi = it as ToolStripMenuItem; if (mi != null) mi.Checked = (string)mi.Tag == anchor; }
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(pos); menu.Items.Add(show); menu.Items.Add(size); menu.Items.Add(op); menu.Items.Add(bgm);
        menu.Items.Add(new ToolStripMenuItem("Settings\u2026", null, delegate { OpenSettings(); }));
        menu.Items.Add(new ToolStripSeparator());
        var miChk = new ToolStripMenuItem("Check for updates", null, delegate { checkUpdates = !checkUpdates; SaveSettings(); if (checkUpdates) CheckForUpdate(); });
        menu.Opening += delegate { miChk.Checked = checkUpdates; };
        menu.Items.Add(miChk);
        menu.Items.Add(new ToolStripMenuItem("Task Manager", null, delegate { OpenTaskManager(); }));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Close(); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("SysWidget v" + VersionText + "  \u00B7  by Dimokat", null, delegate { OpenUrl("https://github.com/" + Repo); }) { Name = "dim" });
        menu.Items.Add(new ToolStripMenuItem("Support the developer \u2665", null, delegate { OpenUrl(DonateUrl); }));
        menu.Opening += delegate { miTop.Checked = onTop; miLock.Checked = locked; miClick.Checked = clickThrough; miStartup.Checked = IsStartup(); };
        // Submenus open toward the inside of the screen the menu is on (also when opened from the tray).
        menu.Opening += delegate
        {
            SyncMenus();
            Point c = Cursor.Position; var wa = Screen.FromPoint(c).WorkingArea;
            var dir = c.X > wa.Left + wa.Width / 2 ? ToolStripDropDownDirection.Left : ToolStripDropDownDirection.Right;
            foreach (ToolStripItem it in menu.Items) { var mi = it as ToolStripMenuItem; if (mi != null && mi.HasDropDownItems) mi.DropDownDirection = dir; }
        };
        StyleMenu(menu);
        menuWatch.Tick += delegate { WatchMenu(); };
        menu.Opened += delegate { menuWatch.Start(); };
        menu.Closed += delegate { menuWatch.Stop(); };
    }

    // The widget never takes focus, so Windows doesn't tell the menu when you click another app.
    // While the menu is open, close it on a click outside it (or Esc) - without stealing focus from games.
    readonly System.Windows.Forms.Timer menuWatch = new System.Windows.Forms.Timer { Interval = 40 };
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
    bool accentBg; Color panelBg = DarkBg;
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
        Color c = accentBg ? PanelFromAccent(SystemAccent()) : DarkBg;
        if (c.ToArgb() == panelBg.ToArgb()) return;
        panelBg = c; MenuLook.SetBase(c, accentBg);
        if (IsHandleCreated && !hidden) Render();
        foreach (var dd in menuDropDowns) { dd.BackColor = MenuLook.Bg; if (dd.IsHandleCreated) ApplyMenuFrame(dd); }
        if (settings != null && !settings.IsDisposed) settings.ApplyFrameColors();
    }

    void SetAccentBg(bool on) { accentBg = on; UpdatePanelColor(); SaveSettings(); }

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
        if (menuLook == null)
        {
            menuLook = new MenuLook(dpi);
            menuFont = new Font("Segoe UI", 9.75f); menuBold = new Font("Segoe UI Semibold", 9.75f);
        }
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
        var more = new ToolStripMenuItem("More options\u2026", null, delegate { show.DropDown.Close(); menu.Close(); OpenSettings(); });
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
        tray.BalloonTipClicked += delegate { if (updateUrl != null) OpenUrl(updateUrl); };
        tray.Visible = true;
    }

    void SetHidden(bool h)
    {
        hidden = h;
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
        sb.Append("\nCPU ").Append(cpu.ToString("0", CI)).Append('%');
        foreach (var g in gpus) if (!g.Idle && IsLive("gpu:" + g.Key)) sb.Append("  \u00B7  ").Append(g.Label).Append(' ').Append(g.Load.ToString("0", CI)).Append('%');
        sb.Append("\nRAM ").Append(ramUsed.ToString("0.0", CI)).Append(" / ").Append(ramTotal.ToString("0", CI)).Append(" GB");
        if (fpsVal > 0) sb.Append("  \u00B7  ").Append(fpsVal.ToString("0", CI)).Append(" FPS");
        if (updateTag != null) sb.Append("\nUpdate ").Append(updateTag).Append(" available");
        string t = sb.Length > 63 ? sb.ToString(0, 63) : sb.ToString(); // NotifyIcon's limit
        if (t != trayText) { trayText = t; tray.Text = t; }
    }

    // ---- update check: asks GitHub for the latest release ~30 s after start, then every 6 h ----
    const string Repo = "Dimokat/SysWidget";
    const string DonateUrl = "https://send.monobank.ua/jar/125L2LfHgy";
    bool checkUpdates = true; string updateTag, updateUrl;
    readonly System.Windows.Forms.Timer updTimer = new System.Windows.Forms.Timer();
    static readonly Version AppVersion = typeof(WidgetForm).Assembly.GetName().Version;
    static string VersionText { get { return AppVersion.Major + "." + AppVersion.Minor + "." + AppVersion.Build; } }

    void StartUpdateChecks()
    {
        updTimer.Interval = 30 * 1000;
        updTimer.Tick += delegate { updTimer.Interval = 6 * 3600 * 1000; CheckForUpdate(); };
        updTimer.Start();
    }

    void CheckForUpdate()
    {
        if (!checkUpdates) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
                req.UserAgent = "SysWidget/" + VersionText; req.Accept = "application/vnd.github+json"; req.Timeout = 15000;
                string json;
                using (var resp = req.GetResponse()) using (var rd = new StreamReader(resp.GetResponseStream())) json = rd.ReadToEnd();
                var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){1,3})\"");
                var url = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/releases/tag/[^\"]+)\"");
                if (!tag.Success || new Version(tag.Groups[1].Value) <= new Version(VersionText)) return;
                string t = "v" + tag.Groups[1].Value, u = url.Success ? url.Groups[1].Value : "https://github.com/" + Repo + "/releases/latest";
                BeginInvoke((MethodInvoker)delegate
                {
                    updateTag = t; updateUrl = u; if (!hidden) Render();
                    if (tray != null && notifiedTag != t)
                    {
                        notifiedTag = t;
                        tray.ShowBalloonTip(8000, "SysWidget " + t + " is available", "Click to open the download page.", ToolTipIcon.Info);
                    }
                });
            }
            catch { } // offline, no releases yet, rate-limited: just try again next time
        });
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
        anchor = a;
        var wa = Screen.FromRectangle(Body).WorkingArea;
        int x = a[1] == 'L' ? wa.Left : a[1] == 'R' ? wa.Right - W : wa.Left + (wa.Width - W) / 2;
        int y = a[0] == 'T' ? wa.Top : a[0] == 'B' ? wa.Bottom - H : wa.Top + (wa.Height - H) / 2;
        PlaceBody(new Point(x, y));
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
                else if (k == "mini") mini = v == "1";
                else if (k == "hidden") hidden = v == "1";
                else if (k == "bg") accentBg = v == "accent";
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

    static List<string> Split(string v)
    {
        var l = new List<string>();
        foreach (var p in v.Split(',')) { string t = p.Trim(); if (t != "" && !l.Contains(t)) l.Add(t); }
        return l;
    }

    void SaveSettings()
    {
        try
        {
            File.WriteAllLines(iniPath, new[] {
                "x=" + (Left + M).ToString(CI), "y=" + (Top + M).ToString(CI),
                "ontop=" + (onTop ? "1" : "0"), "locked=" + (locked ? "1" : "0"), "clickthrough=" + (clickThrough ? "1" : "0"), "mini=" + (mini ? "1" : "0"), "hidden=" + (hidden ? "1" : "0"), "bg=" + (accentBg ? "accent" : "dark"), "updates=" + (checkUpdates ? "1" : "0"), "anchor=" + anchor,
                "scale=" + scale.ToString(CI), "opacity=" + opacity.ToString(CI),
                "order=" + string.Join(",", order), "hide=" + string.Join(",", hiddenFull), "minihide=" + string.Join(",", hiddenMini),
                "off=" + string.Join(",", detailsOff), "compact=" + (compact ? "1" : "0"), "colors=" + ColorsText() });
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
