// SysWidget - GPU discovery and sampling for any number of GPUs (NVIDIA, AMD, Intel, ...).
// Discovery and the generic load figure use D3DKMT, the kernel graphics interface Task Manager uses
// (it doesn't wake a sleeping laptop dGPU). NVIDIA cards are read through NVML and AMD ones through
// ADL, which also give temperature (and power); memory for the rest comes from Windows' GPU counters.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

sealed class Gpu
{
    public string Key;          // stable id, e.g. "10DE-25A2"
    public string Name;         // short name, e.g. "RTX 3050"
    public int Number;          // 1, 2... when there are several of the same kind (discrete / integrated), else 0
    public bool Integrated;
    public int Vendor, Device;
    public uint LuidLow; public int LuidHigh;
    public long DedicatedTotal, SharedTotal;
    public IntPtr PcDedicated, PcShared;    // PDH counters (non-NVIDIA)
    public IntPtr NvDev;                    // NVML device (NVIDIA)
    public int AdlIndex = -1;               // ADL adapter (AMD)
    public bool AdlPower;                   // ADL reports board power (discrete AMD)
    internal long lastRun = -1, lastStamp;

    public float Load, Temp = float.NaN, Power = float.NaN, MemUsed, MemTotal;
    public bool Idle;                       // a discrete GPU on battery: not queried, so it can stay asleep
    public readonly Ring History = new Ring(60);

    public bool IsNvidia { get { return Vendor == 0x10DE; } }
    public bool IsAmd { get { return Vendor == 0x1002; } }
}

static class Gpus
{
    // ------------------------------------------------------------------ D3DKMT
    [StructLayout(LayoutKind.Sequential)] struct ENUMADAPTERS2 { public uint Num; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential)] struct ADAPTERINFO { public uint Handle; public uint LuidLow; public int LuidHigh; public uint Sources; public int Precise; }
    [StructLayout(LayoutKind.Sequential)] struct QUERYADAPTERINFO { public uint Handle; public int Type; public IntPtr Data; public uint Size; }
    [StructLayout(LayoutKind.Sequential)] struct CLOSEADAPTER { public uint Handle; }
    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 e);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO q);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER c);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryStatistics(IntPtr q);

    const int QAI_REGISTRYINFO = 8, QAI_ADAPTERTYPE = 15, QAI_SEGMENTSIZE = 3, QAI_DEVICEIDS = 31;
    const int TYPE_RENDER = 0x1, TYPE_SOFTWARE = 0x4, TYPE_HYBRID_DISCRETE = 0x10, TYPE_HYBRID_INTEGRATED = 0x20;

    static byte[] QueryInfo(uint handle, int type, int size)
    {
        IntPtr b = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, b, size);
            var q = new QUERYADAPTERINFO { Handle = handle, Type = type, Data = b, Size = (uint)size };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
            var r = new byte[size]; Marshal.Copy(b, r, 0, size); return r;
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    public static List<Gpu> Enumerate()
    {
        var list = new List<Gpu>();
        try
        {
            var e = new ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.Num == 0) return list;
            int asz = Marshal.SizeOf(typeof(ADAPTERINFO));
            e.Adapters = Marshal.AllocHGlobal(asz * (int)e.Num);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return list;
                for (int i = 0; i < e.Num; i++)
                {
                    var a = (ADAPTERINFO)Marshal.PtrToStructure(e.Adapters + i * asz, typeof(ADAPTERINFO));
                    try
                    {
                        byte[] t = QueryInfo(a.Handle, QAI_ADAPTERTYPE, 4);
                        int flags = t != null ? BitConverter.ToInt32(t, 0) : TYPE_RENDER;
                        if ((flags & TYPE_SOFTWARE) != 0 || (flags & TYPE_RENDER) == 0) continue;
                        byte[] ids = QueryInfo(a.Handle, QAI_DEVICEIDS, 28);
                        int vendor = ids != null ? BitConverter.ToInt32(ids, 4) : 0, device = ids != null ? BitConverter.ToInt32(ids, 8) : 0;
                        if (vendor == 0x1414) continue; // Microsoft Basic Render / remote display adapters
                        byte[] reg = QueryInfo(a.Handle, QAI_REGISTRYINFO, 2080);
                        string name = reg != null ? Encoding.Unicode.GetString(reg, 0, 520) : "";
                        int z = name.IndexOf('\0'); if (z >= 0) name = name.Substring(0, z);
                        byte[] seg = QueryInfo(a.Handle, QAI_SEGMENTSIZE, 24);
                        var g = new Gpu
                        {
                            Vendor = vendor, Device = device, LuidLow = a.LuidLow, LuidHigh = a.LuidHigh,
                            DedicatedTotal = seg != null ? BitConverter.ToInt64(seg, 0) : 0,
                            SharedTotal = seg != null ? BitConverter.ToInt64(seg, 16) : 0,
                            Name = ShortName(name)
                        };
                        g.Integrated = (flags & TYPE_HYBRID_INTEGRATED) != 0 ||
                                       ((flags & TYPE_HYBRID_DISCRETE) == 0 && vendor != 0x10DE && g.DedicatedTotal < (1L << 30));
                        list.Add(g);
                    }
                    finally { var c = new CLOSEADAPTER { Handle = a.Handle }; D3DKMTCloseAdapter(ref c); }
                }
            }
            finally { Marshal.FreeHGlobal(e.Adapters); }
        }
        catch { }

        // Discrete cards first, then integrated; stable keys and labels.
        list.Sort((x, y) => x.Integrated == y.Integrated ? 0 : x.Integrated ? 1 : -1);
        var keys = new Dictionary<string, int>(); int nd = 0, ni = 0;
        foreach (var g in list) { if (g.Integrated) ni++; else nd++; }
        int kd = 0, ki = 0;
        foreach (var g in list)
        {
            string k = string.Format("{0:X4}-{1:X4}", g.Vendor, g.Device); int n;
            keys[k] = keys.TryGetValue(k, out n) ? n + 1 : 1;
            g.Key = keys[k] > 1 ? k + "-" + keys[k] : k;
            if (g.Integrated) g.Number = ni > 1 ? ++ki : 0;
            else g.Number = nd > 1 ? ++kd : 0;
        }
        return list;
    }

    static string ShortName(string n)
    {
        foreach (var junk in new[] { "NVIDIA", "GeForce", "AMD", "Intel(R)", "Intel", "(TM)", "(R)", "Laptop GPU", "Graphics", "GPU" })
            n = Regex.Replace(n, Regex.Escape(junk), " ", RegexOptions.IgnoreCase);
        n = Regex.Replace(n, @"\s+", " ").Trim();
        if (n.StartsWith("Radeon RX ", StringComparison.OrdinalIgnoreCase)) n = n.Substring(7);
        return n == "" ? "GPU" : n;
    }

    // Generic load: busy time of the main (3D) engine, node 0, as a share of wall time.
    const int QS_SIZE = 0x328, QS_RESULT = 24, QS_QUERY = 0x320, QS_NODE = 5;
    static readonly IntPtr qs = Marshal.AllocHGlobal(QS_SIZE);
    static readonly byte[] zero = new byte[QS_SIZE];

    public static bool SampleKmt(Gpu g)
    {
        try
        {
            Marshal.Copy(zero, 0, qs, QS_SIZE);
            Marshal.WriteInt32(qs, 0, QS_NODE); Marshal.WriteInt32(qs, 4, (int)g.LuidLow); Marshal.WriteInt32(qs, 8, g.LuidHigh);
            Marshal.WriteInt32(qs, QS_QUERY, 0);
            if (D3DKMTQueryStatistics(qs) != 0) return false;
            long run = Marshal.ReadInt64(qs, QS_RESULT), now = Stopwatch.GetTimestamp();
            if (g.lastRun >= 0 && now > g.lastStamp)
            {
                double wall = (now - g.lastStamp) * 1e7 / Stopwatch.Frequency; // 100 ns units, like RunningTime
                g.Load = (float)Math.Max(0, Math.Min(100, (run - g.lastRun) * 100.0 / wall));
            }
            g.lastRun = run; g.lastStamp = now;
            return true;
        }
        catch { return false; }
    }

    public static string PdhInstance(Gpu g) { return string.Format("luid_0x{0:x8}_0x{1:x8}_phys_0", g.LuidHigh, g.LuidLow); }

    // ------------------------------------------------------------------ NVML (NVIDIA)
    public static bool NvmlOk;
    public static void AttachNvml(List<Gpu> list)
    {
        bool any = false; foreach (var g in list) if (g.IsNvidia) any = true;
        if (!any) return;
        try
        {
            if (Nvml.nvmlInit_v2() != 0) return;
            NvmlOk = true;
            uint count; if (Nvml.nvmlDeviceGetCount_v2(out count) != 0) count = 1;
            uint idx = 0;
            foreach (var g in list)
            {
                if (!g.IsNvidia || idx >= count) continue;
                IntPtr dev; if (Nvml.nvmlDeviceGetHandleByIndex_v2(idx++, out dev) == 0) g.NvDev = dev;
            }
        }
        catch { NvmlOk = false; }
    }

    public static void SampleNvml(Gpu g)
    {
        try
        {
            Nvml.Util u; if (Nvml.nvmlDeviceGetUtilizationRates(g.NvDev, out u) == 0) g.Load = u.gpu;
            uint t; g.Temp = Nvml.nvmlDeviceGetTemperature(g.NvDev, 0, out t) == 0 ? t : float.NaN;
            uint mw; g.Power = Nvml.nvmlDeviceGetPowerUsage(g.NvDev, out mw) == 0 ? mw / 1000f : float.NaN;
            Nvml.Mem m; if (Nvml.nvmlDeviceGetMemoryInfo(g.NvDev, out m) == 0) { g.MemUsed = m.used; g.MemTotal = m.total; }
        }
        catch { }
    }

    // ------------------------------------------------------------------ ADL (AMD)
    delegate IntPtr AdlAlloc(int size);
    static readonly AdlAlloc adlAlloc = size => Marshal.AllocCoTaskMem(size);
    [DllImport("atiadlxx.dll")] static extern int ADL2_Main_Control_Create(AdlAlloc cb, int connectedOnly, out IntPtr ctx);
    [DllImport("atiadlxx.dll")] static extern int ADL2_Main_Control_Destroy(IntPtr ctx);
    [DllImport("atiadlxx.dll")] static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr ctx, out int n);
    [DllImport("atiadlxx.dll")] static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr ctx, IntPtr info, int size);
    [DllImport("atiadlxx.dll")] static extern int ADL2_New_QueryPMLogData_Get(IntPtr ctx, int adapter, IntPtr data);
    static IntPtr adl, pmlog;
    const int ADL_INFO_SIZE = 1572, PMLOG_SIZE = 4 + 256 * 8;
    const int PM_TEMP_EDGE = 8, PM_ACTIVITY_GFX = 19, PM_ASIC_POWER = 23, PM_TEMP_HOTSPOT = 27, PM_TEMP_GFX = 28;

    public static void AttachAdl(List<Gpu> list)
    {
        var amd = list.FindAll(g => g.IsAmd);
        if (amd.Count == 0) return;
        try
        {
            if (ADL2_Main_Control_Create(adlAlloc, 1, out adl) != 0) { adl = IntPtr.Zero; return; }
            int n; if (ADL2_Adapter_NumberOfAdapters_Get(adl, out n) != 0 || n <= 0) { CloseAdl(); return; }
            IntPtr info = Marshal.AllocHGlobal(ADL_INFO_SIZE * n);
            try
            {
                Marshal.Copy(new byte[ADL_INFO_SIZE * n], 0, info, ADL_INFO_SIZE * n);
                if (ADL2_Adapter_AdapterInfo_Get(adl, info, ADL_INFO_SIZE * n) != 0) { CloseAdl(); return; }
                pmlog = Marshal.AllocHGlobal(PMLOG_SIZE);
                var seenBus = new HashSet<int>(); var used = new HashSet<Gpu>();
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = info + i * ADL_INFO_SIZE;
                    int index = Marshal.ReadInt32(p, 4), bus = Marshal.ReadInt32(p, 264);
                    string pnp = Marshal.PtrToStringAnsi(p + 1312) ?? "";
                    if (pnp.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) < 0 || !seenBus.Add(bus)) continue;
                    var m = Regex.Match(pnp, @"DEV_([0-9A-Fa-f]{4})"); if (!m.Success) continue;
                    int dev = Convert.ToInt32(m.Groups[1].Value, 16);
                    var g = amd.Find(x => x.Device == dev && !used.Contains(x)); if (g == null) continue;
                    if (!ReadPmLog(index)) continue;
                    if (Supported(PM_ACTIVITY_GFX) || TempSensor() >= 0)
                    {
                        used.Add(g); g.AdlIndex = index;
                        g.AdlPower = !g.Integrated && Supported(PM_ASIC_POWER); // on an APU this is the whole chip, not the GPU
                    }
                }
            }
            finally { Marshal.FreeHGlobal(info); }
            if (used0(list)) CloseAdl();
        }
        catch { CloseAdl(); }
    }

    static bool used0(List<Gpu> list) { foreach (var g in list) if (g.AdlIndex >= 0) return false; return true; }

    static bool ReadPmLog(int index)
    {
        Marshal.Copy(new byte[PMLOG_SIZE], 0, pmlog, PMLOG_SIZE);
        Marshal.WriteInt32(pmlog, 0, PMLOG_SIZE);
        return ADL2_New_QueryPMLogData_Get(adl, index, pmlog) == 0;
    }
    static bool Supported(int sensor) { return Marshal.ReadInt32(pmlog, 4 + sensor * 8) != 0; }
    static int Value(int sensor) { return Marshal.ReadInt32(pmlog, 8 + sensor * 8); }
    static int TempSensor()
    {
        foreach (int s in new[] { PM_TEMP_EDGE, PM_TEMP_GFX, PM_TEMP_HOTSPOT }) if (Supported(s)) return s;
        return -1;
    }

    // Returns false when ADL can't give the load, so the caller falls back to D3DKMT.
    public static bool SampleAdl(Gpu g)
    {
        if (adl == IntPtr.Zero || g.AdlIndex < 0) return false;
        try
        {
            if (!ReadPmLog(g.AdlIndex)) return false;
            int ts = TempSensor(); g.Temp = ts >= 0 ? Value(ts) : float.NaN;
            g.Power = g.AdlPower ? Value(PM_ASIC_POWER) : float.NaN;
            if (!Supported(PM_ACTIVITY_GFX)) return false;
            g.Load = Math.Max(0, Math.Min(100, Value(PM_ACTIVITY_GFX)));
            return true;
        }
        catch { return false; }
    }

    static void CloseAdl()
    {
        try { if (adl != IntPtr.Zero) ADL2_Main_Control_Destroy(adl); } catch { }
        adl = IntPtr.Zero;
    }

    public static void Shutdown()
    {
        CloseAdl();
        try { if (NvmlOk) Nvml.nvmlShutdown(); } catch { }
    }
}
