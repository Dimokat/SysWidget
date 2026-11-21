// SysWidget - extra sources: battery, ping, now playing and the busiest apps.
// Each one is read only while its section (or popup) is on screen.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// Laptop battery: charge, charging state, time left and charge/discharge power (from the battery driver).
static class Battery
{
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr inBuf, int inLen, byte[] outBuf, int outLen);

    public static bool Present, OnAC, Charging;
    public static int Percent = -1, SecondsLeft = -1;
    public static float Watts = float.NaN;   // positive while charging, negative on battery

    public static void Sample()
    {
        var ps = SystemInformation.PowerStatus;
        var b = new byte[32]; // SYSTEM_BATTERY_STATE
        bool ok = CallNtPowerInformation(5 /*SystemBatteryState*/, IntPtr.Zero, 0, b, b.Length) == 0;
        Present = ok && b[1] != 0 && (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0;
        if (!Present) return;
        OnAC = b[0] != 0; Charging = b[2] != 0;
        uint max = BitConverter.ToUInt32(b, 8), rem = BitConverter.ToUInt32(b, 12);
        int rate = BitConverter.ToInt32(b, 16);
        // Windows' own percentage matches the taskbar; the capacities are the fallback.
        if (ps.BatteryLifePercent >= 0 && ps.BatteryLifePercent <= 1) Percent = (int)Math.Round(ps.BatteryLifePercent * 100);
        else Percent = max > 0 ? (int)Math.Round(Math.Min(100.0, rem * 100.0 / max)) : -1;
        SecondsLeft = !OnAC && ps.BatteryLifeRemaining > 0 ? ps.BatteryLifeRemaining : -1;
        Watts = rate == 0 || rate == int.MinValue /*unknown*/ ? float.NaN : rate / 1000f;
    }
}

// Round-trip time to a server, sent from a worker thread so a slow reply never holds up the widget.
sealed class Pinger
{
    public volatile string Host = "1.1.1.1";   // "router" = the default gateway
    public float Ms = float.NaN;                // last reply, NaN = no reply
    int busy;

    public void Send()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        string host = Host;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                string target = host == "router" ? Gateway() : host;
                if (string.IsNullOrEmpty(target)) { Ms = float.NaN; return; }
                using (var p = new Ping())
                {
                    var r = p.Send(target, 1000);
                    Ms = r.Status == IPStatus.Success ? r.RoundtripTime : float.NaN;
                }
            }
            catch { Ms = float.NaN; }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
    }

    static string Gateway()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var g in n.GetIPProperties().GatewayAddresses)
                    if (g.Address != null && g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                        return g.Address.ToString();
            }
        }
        catch { }
        return null;
    }
}

// What's playing (Spotify, browsers, players...): Windows' media sessions, read on a worker thread.
static class NowPlaying
{
    public static volatile string Title = "", Artist = "", Album = "", App = "";
    public static volatile bool Has, Playing;
    public static volatile bool Polled;          // read at least once
    public static volatile byte[] Art;           // cover image (PNG/JPEG), null if none
    public static volatile int ArtVersion;       // changes whenever Art does
    public static double Pos, Dur;               // seconds; Dur 0 = the app doesn't report a timeline
    public static DateTime Stamp;                // when Pos was measured (UTC)

    public static double Position()
    {
        double p = Pos;
        if (Playing && Stamp.Year > 2000) p += (DateTime.UtcNow - Stamp).TotalSeconds;
        return Math.Max(0, Math.Min(Dur, p));
    }
    static int busy, failures;

    public static void Poll()
    {
        if (failures > 20 || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { MediaSessions.Read(); failures = 0; }
            catch { failures++; Has = false; } // e.g. a Windows version without media sessions
            finally { Interlocked.Exchange(ref busy, 0); }
        });
    }

    // 0 previous, 1 play/pause, 2 next
    public static void Command(int c)
    {
        ThreadPool.QueueUserWorkItem(delegate { try { MediaSessions.Command(c); } catch { } Thread.Sleep(250); Poll(); });
    }

    // The player's name as the Start menu shows it ("com.deezer.deezer-desktop" -> Deezer), else a guess from its id.
    static readonly Dictionary<string, string> appNames = new Dictionary<string, string>();
    public static string AppName(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        string n;
        lock (appNames) if (appNames.TryGetValue(aumid, out n)) return n;
        n = StartMenuName(aumid) ?? GuessName(aumid);
        lock (appNames) appNames[aumid] = n;
        return n;
    }

    static string StartMenuName(string aumid)
    {
        string result = null;
        var t = new Thread(delegate ()
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                object sh = Activator.CreateInstance(type);
                object folder = type.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, sh, new object[] { "shell:AppsFolder" });
                if (folder == null) return;
                object item = folder.GetType().InvokeMember("ParseName", BindingFlags.InvokeMethod, null, folder, new object[] { aumid });
                if (item != null) result = item.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, item, null) as string;
            }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start(); t.Join(3000);
        return string.IsNullOrEmpty(result) ? null : result.Trim();
    }

    // "Spotify.exe" -> Spotify, "Brave.UO3X..." -> Brave, "com.deezer.deezer-desktop" -> Deezer,
    // "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> ZuneMusic
    static string GuessName(string aumid)
    {
        string s = aumid; int bang = s.LastIndexOf('!'); if (bang >= 0) s = s.Substring(bang + 1);
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
        var parts = s.Split('.');
        string first = parts[0].ToLowerInvariant();
        if (parts.Length > 1 && (first == "com" || first == "org" || first == "net" || first == "io" || first == "app" || first == "dev")) s = parts[1];
        else if (parts.Length > 1 && (parts[0] == "Microsoft" || parts[0] == "Windows")) s = parts[parts.Length - 1];
        else s = parts[0];
        s = Regex.Replace(s, "_[a-z0-9]{13}$", "");
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;
    }
}

// Kept apart so a Windows without these APIs only fails inside these calls.
static class MediaSessions
{
    static Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager mgr;

    static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op)
    {
        for (int i = 0; i < 300 && op.Status == Windows.Foundation.AsyncStatus.Started; i++) Thread.Sleep(10);
        return op.Status == Windows.Foundation.AsyncStatus.Completed ? op.GetResults() : default(T);
    }

    static Windows.Media.Control.GlobalSystemMediaTransportControlsSession Session()
    {
        if (mgr == null) mgr = Wait(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        return mgr == null ? null : mgr.GetCurrentSession();
    }

    static string artKey; static int artTries;

    public static void Read()
    {
        var s = Session();
        if (s == null) { NowPlaying.Has = false; NowPlaying.Playing = false; SetArt(null, ""); NowPlaying.Polled = true; return; }
        var p = Wait(s.TryGetMediaPropertiesAsync());
        NowPlaying.Title = p != null && p.Title != null ? p.Title.Trim() : "";
        NowPlaying.Artist = p != null && p.Artist != null ? p.Artist.Trim() : "";
        NowPlaying.Album = p != null && p.AlbumTitle != null ? p.AlbumTitle.Trim() : "";
        NowPlaying.App = NowPlaying.AppName(s.SourceAppUserModelId);
        NowPlaying.Playing = s.GetPlaybackInfo().PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        NowPlaying.Has = NowPlaying.Title != "" || NowPlaying.Artist != "";
        try
        {
            var tl = s.GetTimelineProperties();
            NowPlaying.Dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            NowPlaying.Pos = (tl.Position - tl.StartTime).TotalSeconds;
            NowPlaying.Stamp = tl.LastUpdatedTime.UtcDateTime;
        }
        catch { NowPlaying.Dur = 0; }
        // The cover is read once per song (a few retries: some players attach it a moment later).
        string key = NowPlaying.Title + "\n" + NowPlaying.Artist + "\n" + NowPlaying.App;
        if (key != artKey) artTries = 0;
        if (key != artKey || (NowPlaying.Art == null && artTries < 3))
        {
            artTries++;
            byte[] art = null;
            try { art = Thumbnail(p); } catch { }
            artKey = null; SetArt(art, key);
        }
        NowPlaying.Polled = true;
    }

    static void SetArt(byte[] art, string key)
    {
        if (key == artKey) return;
        artKey = key; NowPlaying.Art = art; NowPlaying.ArtVersion++;
    }

    static byte[] Thumbnail(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties p)
    {
        if (p == null || p.Thumbnail == null) return null;
        var st = Wait(p.Thumbnail.OpenReadAsync());
        if (st == null || st.Size == 0 || st.Size > 8 * 1024 * 1024) return null;
        var rd = new Windows.Storage.Streams.DataReader(st.GetInputStreamAt(0));
        uint n = Wait((Windows.Foundation.IAsyncOperation<uint>)rd.LoadAsync((uint)st.Size));
        var buf = new byte[n]; rd.ReadBytes(buf);
        return buf;
    }

    public static void Command(int c)
    {
        var s = Session(); if (s == null) return;
        if (c == 0) Wait(s.TrySkipPreviousAsync());
        else if (c == 1) Wait(s.TryTogglePlayPauseAsync());
        else Wait(s.TrySkipNextAsync());
    }
}

// The apps using the most CPU and memory, from Windows' per-process counters, grouped by name like
// Task Manager. Only runs while the popup is open (reading every process costs a little CPU).
sealed class TopApps : IDisposable
{
    [StructLayout(LayoutKind.Explicit)] struct FMT { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint fmt, ref uint size, ref uint count, IntPtr items);

    IntPtr q, cCpu, cMem; int samples;
    public readonly List<KeyValuePair<string, double>> Cpu = new List<KeyValuePair<string, double>>(), Mem = new List<KeyValuePair<string, double>>();
    public bool HasCpu { get { return samples >= 2; } }

    public TopApps()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out q) != 0) { q = IntPtr.Zero; return; }
        PdhAddEnglishCounter(q, @"\Process(*)\% Processor Time", IntPtr.Zero, out cCpu);
        PdhAddEnglishCounter(q, @"\Process(*)\Working Set - Private", IntPtr.Zero, out cMem);
    }

    public void Sample(int top)
    {
        if (q == IntPtr.Zero) return;
        PdhCollectQueryData(q); samples++;
        Fill(Cpu, cCpu, top, Environment.ProcessorCount);
        Fill(Mem, cMem, top, 1);
    }

    void Fill(List<KeyValuePair<string, double>> list, IntPtr c, int top, int div)
    {
        list.Clear();
        if (c == IntPtr.Zero) return;
        uint size = 0, count = 0;
        PdhGetFormattedCounterArray(c, 0x200 | 0x8000 /*DOUBLE|NOCAP100*/, ref size, ref count, IntPtr.Zero);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(c, 0x200 | 0x8000, ref size, ref count, buf) != 0) return;
            var sum = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int item = IntPtr.Size + 16; // PDH_FMT_COUNTERVALUE_ITEM: name pointer + value
            for (int i = 0; i < count; i++)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + i * item);
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
                var v = (FMT)Marshal.PtrToStructure(new IntPtr(p.ToInt64() + IntPtr.Size), typeof(FMT));
                if (name == null || (v.CStatus != 0 && v.CStatus != 1)) continue;
                int hash = name.IndexOf('#'); if (hash > 0) name = name.Substring(0, hash);
                if (name == "_Total" || name == "Idle") continue;
                double val = v.Value / div, cur;
                sum[name] = sum.TryGetValue(name, out cur) ? cur + val : val;
            }
            foreach (var kv in sum) list.Add(kv);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (list.Count > top) list.RemoveRange(top, list.Count - top);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose() { if (q != IntPtr.Zero) { PdhCloseQuery(q); q = IntPtr.Zero; } }
}

// Hands SysWidget's numbers to RivaTuner Statistics Server, which draws them in games' on-screen display (also
// in exclusive fullscreen). This uses RTSS's documented shared memory, the way other monitoring tools do: SysWidget
// only writes text into one OSD slot it owns; RTSS does all the drawing, and only in the games set up in RTSS.
static class Rtss
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    const uint FILE_MAP_ALL_ACCESS = 0xF001F;
    const string Owner = "SysWidget";
    const int OwnerAt = 256, TextExAt = 512; // RTSS_SHARED_MEMORY_OSD_ENTRY: szOSD[256], szOSDOwner[256], szOSDEx[4096]

    /// <summary>RTSS was found the last time we looked.</summary>
    public static volatile bool Running;

    /// <summary>Puts the text in SysWidget's OSD slot. False when RTSS isn't running.</summary>
    public static bool Show(string text) { return Use(text ?? ""); }

    /// <summary>Gives the slot back (the text disappears from games).</summary>
    public static void Clear() { Use(null); }

    static bool Use(string text)
    {
        IntPtr map = OpenFileMapping(FILE_MAP_ALL_ACCESS, false, "RTSSSharedMemoryV2");
        if (map == IntPtr.Zero) { Running = false; return false; }
        IntPtr mem = MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
        try
        {
            if (mem == IntPtr.Zero) return false;
            uint sig = (uint)Marshal.ReadInt32(mem, 0), ver = (uint)Marshal.ReadInt32(mem, 4);
            if (sig != 0x52545353 /*'RTSS'*/ || ver < 0x00020000) { Running = false; return false; }
            Running = true;
            int entrySize = Marshal.ReadInt32(mem, 20), arrOffset = Marshal.ReadInt32(mem, 24), arrSize = Marshal.ReadInt32(mem, 28);
            if (entrySize < OwnerAt + 256 || arrSize <= 1) return false;
            // Slot 0 is left to the main OSD client (MSI Afterburner); others start from slot 1.
            int passes = text == null ? 1 : 2; // clearing: just find our slot; showing: find it, else take a free one
            for (int pass = 0; pass < passes; pass++)
                for (int i = 1; i < arrSize; i++)
                {
                    IntPtr e = new IntPtr(mem.ToInt64() + arrOffset + (long)i * entrySize);
                    string owner = Marshal.PtrToStringAnsi(new IntPtr(e.ToInt64() + OwnerAt)) ?? "";
                    if (text == null)
                    {
                        if (owner != Owner) continue;
                        Marshal.Copy(new byte[entrySize], 0, e, entrySize);
                        Bump(mem); return true;
                    }
                    if (pass == 1 && owner == "") { Write(new IntPtr(e.ToInt64() + OwnerAt), Owner, 256); owner = Owner; }
                    if (owner != Owner) continue;
                    if (ver >= 0x00020007 && entrySize >= TextExAt + 4096) Write(new IntPtr(e.ToInt64() + TextExAt), text, 4096);
                    else Write(e, text, 256);
                    Bump(mem); return true;
                }
            return text == null;
        }
        finally { if (mem != IntPtr.Zero) UnmapViewOfFile(mem); CloseHandle(map); }
    }

    static void Write(IntPtr at, string s, int max)
    {
        byte[] b = Encoding.Default.GetBytes(s); // RTSS reads text in the Windows ANSI code page
        int n = Math.Min(b.Length, max - 1);
        Marshal.Copy(b, 0, at, n); Marshal.WriteByte(at, n, 0);
    }

    static void Bump(IntPtr mem) { Marshal.WriteInt32(mem, 32, Marshal.ReadInt32(mem, 32) + 1); } // dwOSDFrame: "text changed"
}
