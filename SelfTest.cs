// SysWidget - self-test: "SysWidget.exe --selftest [report.txt]" builds the widget off screen and runs it through
// every mode, look, size and language. It checks the layout rules (text inside the panel and not overlapping,
// island cards inside their window and the screen, a clean close) and times the drawing and the animations.
// Your settings are read but never written, nothing appears on screen or in the tray, and the running widget's
// FPS session is left alone. Exit code = number of failed checks. tools\test.ps1 builds and runs it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

sealed partial class WidgetForm
{
    static bool selfTesting;
    static List<KeyValuePair<string, RectangleF>> textLog; // every text drawn while a check listens (see Txt)

    sealed class TestLog
    {
        public readonly StringBuilder Text = new StringBuilder();
        public int Fails, Checks, Warnings;
        public void Check(bool ok, string what) { Checks++; if (!ok) { Fails++; Text.AppendLine("FAIL  " + what); } }
        public void Warn(string what) { Warnings++; Text.AppendLine("WARN  " + what); }
        public void Line(string s) { Text.AppendLine(s); }
    }

    public static int RunSelfTest(string report)
    {
        selfTesting = true;
        var log = new TestLog();
        log.Line("SysWidget " + VersionText + " self-test, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        try
        {
            using (var w = new WidgetForm())
            {
                var h = w.Handle; // a window to draw into; it's never shown
                w.RunTests(log);
            }
        }
        catch (Exception ex) { log.Fails++; log.Line("CRASH " + ex); }
        log.Line("");
        log.Line(string.Format(CultureInfo.InvariantCulture, "{0} checks, {1} failed, {2} warnings", log.Checks, log.Fails, log.Warnings));
        try { File.WriteAllText(report, log.Text.ToString(), new UTF8Encoding(true)); } catch { }
        return log.Fails;
    }

    // "SysWidget.exe --shots folder": pictures of every look at 125% (for the website and for comparing with it).
    public static int SaveShots(string dir)
    {
        selfTesting = true;
        Directory.CreateDirectory(dir);
        using (var w = new WidgetForm()) { var h = w.Handle; w.Shots(dir); }
        return 0;
    }

    void Shots(string dir)
    {
        Sample(); Thread.Sleep(1000); Sample();
        hiddenFull.Clear(); hiddenMini.Clear(); detailsOff.Clear();
        scale = 1.25f / dpi; ApplyScale();
        Action<string> save = name => { Render(); canvas.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png); };
        foreach (int theme in new[] { 0, 1, 2 })
        {
            SetThemeQuiet(theme); miniPref = false; islandStyle = true; Relayout();
            save("full-" + new[] { "dark", "accent", "light" }[theme]);
        }
        SetThemeQuiet(0);
        foreach (int dir2 in new[] { 1, 2 })
        {
            string kind = dir2 == 1 ? "row" : "column";
            miniPref = true; miniDir = dir2; Relayout(); save(kind);
            foreach (var id in new[] { "cpu", "ram", "net", "media" })
            {
                OpenIsland(id); RunAnimation(new List<double>());
                var c = Stopwatch.StartNew();
                while (Ms(c) < 1500) { Application.DoEvents(); Thread.Sleep(20); }
                BuildIslandContent(); save(kind + "-" + id);
                CloseIslandNow();
            }
        }
    }

    // ------------------------------------------------------------------ the run
    void RunTests(TestLog log)
    {
        Sample(); Sample();
        // Show every section everywhere for the run (only in memory: nothing is saved).
        hiddenFull.Clear(); hiddenMini.Clear(); detailsOff.Clear();
        log.Line("Screen: " + Screen.FromRectangle(Body).WorkingArea + ", DPI scale " + dpi.ToString("0.##", CultureInfo.InvariantCulture) + ", sections: " + string.Join(", ", new List<string>(Sections())));
        log.Line("");

        TestFormatting(log);
        TestRelease(log);
        TestLooks(log);
        TestSamplingStall(log);
        TestIsland(log, 1, "row");
        TestIsland(log, 2, "column");
        TestSettingsWindow(log);
        TestLanguages(log);
    }

    static double Ms(Stopwatch sw) { return sw.Elapsed.TotalMilliseconds; }

    static string Stats(List<double> v)
    {
        if (v.Count == 0) return "-";
        var s = new List<double>(v); s.Sort();
        double sum = 0; foreach (var x in s) sum += x;
        return string.Format(CultureInfo.InvariantCulture, "avg {0:0.0} ms, p95 {1:0.0} ms, max {2:0.0} ms ({3} frames)", sum / s.Count, s[(int)Math.Min(s.Count - 1, Math.Floor(s.Count * 0.95))], s[s.Count - 1], s.Count);
    }

    static double Max(List<double> v) { double m = 0; foreach (var x in v) m = Math.Max(m, x); return m; }

    // ---- small pure functions
    void TestFormatting(TestLog log)
    {
        log.Line("== Formatting");
        log.Check(TimeText(0) == "0:00" && TimeText(83) == "1:23" && TimeText(3725) == "1:02:05", "TimeText");
        log.Check(NowPlaying.GuessName("Spotify.exe") == "Spotify", "app name: Spotify.exe");
        log.Check(NowPlaying.GuessName("com.deezer.deezer-desktop") == "Deezer", "app name: com.deezer.deezer-desktop");
        log.Check(NowPlaying.GuessName("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic") == "ZuneMusic", "app name: ZuneMusic");
        log.Check(NowPlaying.GuessName("Brave.UO3XOVLQWRHXOHZXGF5GZSGVGU") == "Brave", "app name: Brave");
        bool bits = netBits; netBits = true;
        log.Check(RateNet(125000).Contains("1") && RateNet(125000).Contains(Lang.F("unit.mbits", "").Trim()), "1 Mb/s in bits: " + RateNet(125000));
        netBits = bits;
        log.Check(PingText(float.NaN) == Lang.T("ping.timeout"), "ping timeout text");
        log.Check(ValidHost("1.1.1.1") && ValidHost("example.com") && !ValidHost("a b") && !ValidHost("x;rm"), "ValidHost");
        log.Check(!string.IsNullOrEmpty(RtssText()), "RivaTuner text is built");
        log.Line("");
    }

    void TestRelease(TestLog log)
    {
        log.Line("== Update check parsing");
        string json = "{\"html_url\":\"https://github.com/Dimokat/SysWidget/releases/tag/v99.1.0\",\"tag_name\":\"v99.1.0\"," +
            "\"body\":\"SHA-256:\\n- `SysWidget-v99.1.0.zip`: `" + new string('a', 64) + "`\\n- `SysWidget.exe`: `" + new string('b', 64) + "`\"," +
            "\"assets\":[{\"browser_download_url\":\"https://github.com/Dimokat/SysWidget/releases/download/v99.1.0/SysWidget-v99.1.0.zip\"}]}";
        string t, u, z, h;
        bool ok = ParseRelease(json, out t, out u, out z, out h);
        log.Check(ok && t == "v99.1.0", "newer release found");
        log.Check(z == "https://github.com/Dimokat/SysWidget/releases/download/v99.1.0/SysWidget-v99.1.0.zip", "zip url");
        log.Check(h == new string('a', 64), "zip SHA-256 (not the exe's)");
        log.Check(!ParseRelease(json.Replace("99.1.0", "0.0.1"), out t, out u, out z, out h), "older release ignored");
        log.Check(!ParseRelease(json.Replace("github.com/Dimokat/SysWidget/releases/download", "evil.example/x"), out t, out u, out z, out h) || z == null, "zip only from this repo");
        log.Line("");
    }

    // ---- full widget and mini mode in every look: layout rules + drawing time
    void TestLooks(TestLog log)
    {
        log.Line("== Looks (layout rules and drawing time)");
        float oldScale = scale; int oldTheme = Theme, oldGraph = graphStyle; bool oldCompact = compact, oldMini = miniPref, oldIsland = islandStyle; int oldDir = miniDir;
        var times = new List<double>();
        foreach (float sc in new[] { 0.75f, 1f, 1.3f, 1.75f })
            foreach (int theme in new[] { 0, 1, 2 })
                foreach (bool comp in new[] { false, true })
                {
                    scale = sc; accentBg = theme == 1; lightBg = theme == 2; UpdatePanelColor(); UpdateInk();
                    compact = comp; graphStyle = (graphStyle + 1) % 3; ApplyScale();
                    string name = string.Format(CultureInfo.InvariantCulture, "{0:0}% {1}{2}", sc * 100, new[] { "dark", "accent", "light" }[theme], comp ? " compact" : "");
                    miniPref = false; Relayout();
                    CheckLayout(log, "full " + name, times);
                    miniPref = true; islandStyle = true; miniDir = 1; Relayout();
                    CheckLayout(log, "row " + name, times);
                    miniDir = 2; Relayout();
                    CheckLayout(log, "column " + name, times);
                }
        log.Line("Drawing a frame (any look): " + Stats(times));
        if (Max(times) > 16) log.Warn("a frame took over 16 ms to draw");
        scale = oldScale; ApplyScale(); SetThemeQuiet(oldTheme); graphStyle = oldGraph; compact = oldCompact; miniPref = oldMini; islandStyle = oldIsland; miniDir = oldDir; Relayout();
        log.Line("");
    }

    void SetThemeQuiet(int t) { accentBg = t == 1; lightBg = t == 2; UpdatePanelColor(); UpdateInk(); }

    // Draws once while recording every text, then checks: inside the panel, no two texts overlapping.
    void CheckLayout(TestLog log, string name, List<double> times)
    {
        log.Check(W > 0 && H > 0, name + ": has a size (" + W + "x" + H + ")");
        textLog = new List<KeyValuePair<string, RectangleF>>();
        var sw = Stopwatch.StartNew();
        Render();
        if (times != null) times.Add(Ms(sw));
        var texts = textLog; textLog = null;
        CheckTexts(log, name, texts, W, H);
        foreach (var kv in sectionRects)
        {
            var r = kv.Value;
            log.Check(r.Right <= W + 1 && r.Bottom <= H + 1 + (mini ? 0 : 12 * s), name + ": " + kv.Key + " inside the panel (" + Fmt(r) + " in " + W + "x" + H + ")");
        }
    }

    static string Fmt(RectangleF r) { return string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0} {2:0}x{3:0}", r.X, r.Y, r.Width, r.Height); }

    void CheckTexts(TestLog log, string name, List<KeyValuePair<string, RectangleF>> texts, float w, float h)
    {
        foreach (var t in texts)
        {
            var r = t.Value;
            log.Check(r.Left >= -0.5f && r.Right <= w + 0.5f && r.Top >= -0.5f && r.Bottom <= h + 2,
                name + ": text \"" + t.Key + "\" inside (" + Fmt(r) + " in " + w.ToString("0", CultureInfo.InvariantCulture) + "x" + h.ToString("0", CultureInfo.InvariantCulture) + ")");
        }
        for (int i = 0; i < texts.Count; i++)
            for (int j = i + 1; j < texts.Count; j++)
            {
                RectangleF a = texts[i].Value, b = texts[j].Value;
                float vy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top), vx = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                bool sameLine = vy > 0.5f * Math.Min(a.Height, b.Height);
                if (sameLine && vx > 1.5f) log.Check(false, name + ": \"" + texts[i].Key + "\" and \"" + texts[j].Key + "\" overlap");
            }
    }

    // ---- the island: open every stat, switch quickly, close; geometry rules + frame times
    void TestIsland(TestLog log, int dir, string what)
    {
        log.Line("== Island (" + what + ")");
        bool oldMini = miniPref, oldIsland = islandStyle; int oldDir = miniDir;
        miniPref = true; islandStyle = true; miniDir = dir; Relayout(); Render();
        var ids = new List<string>(); foreach (var id in Sections()) if (IsShown(id, true)) ids.Add(id);
        var open = new List<double>(); var frames = new List<double>(); var switches = new List<double>();
        var home = Body;
        foreach (var id in ids)
        {
            var sw = Stopwatch.StartNew();
            OpenIsland(id);
            open.Add(Ms(sw));
            { RECT wr; GetWindowRect(Handle, out wr); log.Check(wr.L == Left && wr.T == Top && wr.R - wr.L == Width && wr.B - wr.T == Height, what + " " + id + ": the window is where the widget thinks it is"); }
            if (Ms(sw) > 30) log.Warn(what + ": opening " + id + " took " + Ms(sw).ToString("0", CultureInfo.InvariantCulture) + " ms");
            RunAnimation(frames);
            CheckIsland(log, what + " " + id);
            // the card's texts stay inside the card
            textLog = new List<KeyValuePair<string, RectangleF>>();
            using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b)) { float ch = DrawCard(g, id, islandCardW); var tl = textLog; textLog = null; CheckTexts(log, what + " card " + id, tl, islandCardW, ch); }
            CloseIslandNow();
            log.Check(!islandOpen && Body == home && Width == W + 2 * M, what + " " + id + ": closes back to the strip");
        }
        // Moving quickly along the strip: a new stat every few frames.
        cardOut = 0; cardOutAt = null;
        if (ids.Count > 1)
        {
            OpenIsland(ids[0]); RunAnimation(frames, 3);
            for (int k = 1; k < ids.Count; k++)
            {
                var sw = Stopwatch.StartNew(); OpenIsland(ids[k]); switches.Add(Ms(sw));
                RunAnimation(frames, 3);
            }
            for (int k = ids.Count - 2; k >= 0; k--) { var sw = Stopwatch.StartNew(); OpenIsland(ids[k]); switches.Add(Ms(sw)); RunAnimation(frames, 2); }
            RunAnimation(frames);
            CheckIsland(log, what + " after quick switching");
            log.Check(cardOut == 0, what + ": while switching, the card never sticks out past the strip's ends (" + cardOut + " frames, first: " + cardOutAt + ")");
            CloseIsland(); RunAnimation(frames);
            log.Check(!islandOpen && Body == home, what + ": closes after quick switching");
        }
        log.Check(islandPrev == null && !hiResTimer, what + ": the crossfade and the fast timer end with the animation");
        TestMotion(log, what, ids);
        if (ids.Contains("cpu")) TestTopApps(log, what);
        log.Line("Opening a card: " + Stats(open));
        log.Line("Switching cards: " + Stats(switches));
        log.Line("Animation frames: " + Stats(frames));
        if (Max(frames) > 16) log.Warn(what + ": an animation frame took over 16 ms (a visible stutter at 60 Hz)");
        if (Max(switches) > 16) log.Warn(what + ": switching cards took over 16 ms");
        miniPref = oldMini; islandStyle = oldIsland; miniDir = oldDir; Relayout();
        log.Line("");
    }

    // Steps the animation like its timer would (every ~10 ms), timing each frame. limit = stop after that many.
    // trace gets the card's depth after each frame.
    void RunAnimation(List<double> frames, int limit = 400, List<float> trace = null, int every = 10)
    {
        for (int n = 0; n < limit && islandAnim.Enabled; n++)
        {
            var sw = Stopwatch.StartNew();
            IslandStep();
            frames.Add(Ms(sw));
            if (trace != null) trace.Add(islandCurH);
            WatchCard();
            Thread.Sleep(Math.Max(0, every - (int)Ms(sw)));
        }
    }

    int cardOut; string cardOutAt;
    void WatchCard()
    {
        if (!islandOpen || IslandWide || !Fits || islandCurH < 0.5f) return;
        var cc = CardCanon(islandA, islandCurH);
        if (cc.Left < -0.5f || cc.Right > StripLen + 0.5f) { cardOut++; if (cardOutAt == null) cardOutAt = islandId + " " + Fmt(cc) + " strip length " + StripLen; }
    }

    // How the card moves: it gets there in a sensible time, only ever moves toward its target (no bounce or
    // jitter), and moves the same however the frames are spaced (here 10 ms vs uneven 8-40 ms ticks).
    void TestMotion(TestLog log, string what, List<string> ids)
    {
        string id = ids.Contains("ram") ? "ram" : ids[0];
        foreach (int every in new[] { 10, 0 })
        {
            string how = every == 10 ? "even frames" : "uneven frames";
            var frames = new List<double>(); var up = new List<float>(); var down = new List<float>();
            var clock = Stopwatch.StartNew();
            OpenIsland(id);
            if (every == 10) RunAnimation(frames, 400, up, 10);
            else { var rnd = new Random(7); for (int n = 0; n < 400 && islandAnim.Enabled; n++) { IslandStep(); up.Add(islandCurH); Thread.Sleep(rnd.Next(4) == 0 ? 40 : 8); } }
            double openMs = Ms(clock);
            float target = islandTargetH;
            clock.Restart(); CloseIsland(); RunAnimation(frames, 400, down, 10); double closeMs = Ms(clock);
            bool upOk = true, downOk = true;
            for (int i = 1; i < up.Count; i++) if (up[i] < up[i - 1] - 0.01f || up[i] > target + 0.5f) upOk = false;
            for (int i = 1; i < down.Count; i++) if (down[i] > down[i - 1] + 0.01f || down[i] < -0.01f) downOk = false;
            log.Check(upOk, what + " " + how + ": opening only moves outward, no overshoot");
            log.Check(downOk, what + " " + how + ": closing only moves inward");
            log.Check(openMs > 120 && openMs < 600, what + " " + how + ": opening takes " + openMs.ToString("0", CultureInfo.InvariantCulture) + " ms (120-600)");
            log.Check(closeMs > 100 && closeMs < 600, what + " " + how + ": closing takes " + closeMs.ToString("0", CultureInfo.InvariantCulture) + " ms (100-600)");
            log.Check(!islandOpen && !hiResTimer, what + " " + how + ": closed and the timer is back to normal");
            log.Line(what + " " + how + ": open " + openMs.ToString("0", CultureInfo.InvariantCulture) + " ms in " + up.Count + " frames, close " + closeMs.ToString("0", CultureInfo.InvariantCulture) + " ms");
        }
        // Retargeting mid-way keeps the speed: pointing at a stat while the card is still moving doesn't stop it.
        if (ids.Count > 1)
        {
            var frames = new List<double>();
            OpenIsland(ids[0]); RunAnimation(frames, 5);
            float v = islandVH; OpenIsland(ids[1]);
            log.Check(Math.Abs(islandVH - v) < 0.01f && islandPrev != null, what + ": switching mid-way keeps the card moving and crossfades");
            RunAnimation(frames);
            log.Check(islandPrev == null && islandContent != null, what + ": the old card is dropped after the crossfade");
            CloseIslandNow();
        }
    }

    // The widget's own measuring (on the UI thread, every second) must not wait while the busiest apps start
    // being measured on the other thread: a wait there freezes the widget mid-animation.
    void TestSamplingStall(TestLog log)
    {
        log.Line("== Measuring");
        var alone = new List<double>(); var during = new List<double>();
        for (int round = 0; round < 10; round++)
        {
            bool apps = round % 2 == 1;
            if (apps) StartTopApps();
            var clock = Stopwatch.StartNew();
            while (Ms(clock) < 700) { var c = Stopwatch.StartNew(); Sample(); (apps ? during : alone).Add(Ms(c)); Thread.Sleep(15); }
            StopTopApps(); Thread.Sleep(300);
        }
        log.Line("Widget measuring alone: " + Stats(alone));
        log.Line("Widget measuring while busiest apps start: " + Stats(during));
        var a = new List<double>(alone); a.Sort(); var d = new List<double>(during); d.Sort();
        double pa = a[(int)(a.Count * 0.95)], pd = d[(int)(d.Count * 0.95)];
        log.Check(pd < pa + 5, "the widget's measuring isn't held up by the busiest apps (p95 " + pd.ToString("0.0", CultureInfo.InvariantCulture) + " vs " + pa.ToString("0.0", CultureInfo.InvariantCulture) + " ms alone)");
        log.Line("");
    }

    // The busiest apps are measured on another thread: opening the CPU card doesn't wait for them, the list
    // arrives within about a second, and the card's size doesn't jump when it does.
    void TestTopApps(TestLog log, string what)
    {
        var sw = Stopwatch.StartNew();
        OpenIsland("cpu");
        double openMs = Ms(sw);
        RunAnimation(new List<double>());
        int h0 = islandContent != null ? islandContent.Height : -1;
        sw.Restart();
        while (Ms(sw) < 3000 && (topApps == null || !topApps.HasCpu || topApps.Cpu.Count == 0)) { Application.DoEvents(); Thread.Sleep(20); }
        double gotMs = Ms(sw);
        Application.DoEvents(); // the rebuild it asked for
        int h1 = islandContent != null ? islandContent.Height : -1;
        log.Check(topApps != null && topApps.Cpu.Count > 0 && topApps.Mem.Count > 0, what + ": busiest apps arrive (" + gotMs.ToString("0", CultureInfo.InvariantCulture) + " ms)");
        log.Check(openMs < 16, what + ": opening the CPU card doesn't wait for them (" + openMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms)");
        log.Check(Math.Abs(h1 - h0) <= 2 * s + 1, what + ": the CPU card keeps its size when they arrive (" + h0 + " -> " + h1 + " px)");
        CloseIslandNow();
        log.Check(topApps == null, what + ": measuring them stops when the island closes");
    }

    void CheckIsland(TestLog log, string name)
    {
        log.Check(islandOpen && Math.Abs(islandCurH - islandTargetH) < 0.01f && !islandAnim.Enabled, name + ": animation finished");
        log.Check(islandContent != null && islandContent.Width == islandCardW, name + ": card drawn");
        var win = new Rectangle(Left, Top, Width, Height);
        var wa = Screen.FromRectangle(islandHome).WorkingArea;
        RectangleF shape = IslandWide ? IslandRect(islandCurH) : RectangleF.Union(islandHome, Map(CardCanon(islandA, islandCurH), 0, 0));
        var r = Rectangle.Round(shape);
        log.Check(win.Contains(r), name + ": the window covers the whole island (" + r + " in " + win + ")");
        var card = IslandWide ? IslandRect(islandCurH) : Map(CardCanon(islandA, islandCurH), 0, 0);
        bool inX = card.Left >= wa.Left - 1 && card.Right <= wa.Right + 1, inY = card.Top >= wa.Top - 1 && card.Bottom <= wa.Bottom + 1;
        log.Check(inX && inY, name + ": the card is on screen (" + Fmt(card) + " in " + wa + ")");
    }

    // ---- settings window: every tab, height and drawing time
    void TestSettingsWindow(TestLog log)
    {
        log.Line("== Settings window");
        var wa = Screen.FromRectangle(Body).WorkingArea;
        using (var sw = new SettingsWindow(this, dpi))
        {
            var heights = new List<string>(); var times = new List<double>();
            for (int t = 0; t < 5; t++)
            {
                var clock = Stopwatch.StartNew();
                int h = sw.TestTabHeight(t); sw.TestPaint();
                times.Add(Ms(clock));
                heights.Add(h.ToString(CultureInfo.InvariantCulture));
                if (h + 40 > wa.Height) log.Warn("settings tab " + t + " is taller than the screen (" + h + " px)");
            }
            log.Line("Tab heights: " + string.Join(", ", heights) + " px; first pass: " + Stats(times));
            var per = new List<string>(); times.Clear();
            for (int t = 0; t < 5; t++)
            {
                var c1 = Stopwatch.StartNew(); sw.TestTabHeight(t); double m1 = Ms(c1);
                c1.Restart(); sw.TestPaint(); double m2 = Ms(c1);
                times.Add(m1 + m2); per.Add(t + ": " + m1.ToString("0.0", CultureInfo.InvariantCulture) + "+" + m2.ToString("0.0", CultureInfo.InvariantCulture));
            }
            log.Line("Switching a tab (measure+paint ms): " + string.Join(", ", per) + "; " + Stats(times));
            if (Max(times) > 40) log.Warn("settings: switching a tab takes over 40 ms");
            // Where it opens: never under the widget when there's room beside, below or above it.
            var sz = new Size(sw.Width, sw.Height); int bw = (int)Math.Round(700 * dpi), bh = (int)Math.Round(40 * dpi), fw = (int)Math.Round(260 * dpi), fh = (int)Math.Round(420 * dpi);
            var spots = new Dictionary<string, Rectangle>
            {
                { "row at the top centre", new Rectangle(wa.Left + (wa.Width - bw) / 2, wa.Top, bw, bh) },
                { "full-width row at the top", new Rectangle(wa.Left, wa.Top, wa.Width, bh) },
                { "full-width row at the bottom", new Rectangle(wa.Left, wa.Bottom - bh, wa.Width, bh) },
                { "panel top left", new Rectangle(wa.Left, wa.Top, fw, fh) },
                { "panel top right", new Rectangle(wa.Right - fw, wa.Top, fw, fh) },
                { "panel in the middle", new Rectangle(wa.Left + (wa.Width - fw) / 2, wa.Top + (wa.Height - fh) / 2, fw, fh) },
                { "column on the left", new Rectangle(wa.Left, wa.Top + 100, (int)Math.Round(60 * dpi), wa.Height - 200) },
            };
            foreach (var kv in spots)
            {
                var p = SettingsSpot(sz, kv.Value, wa); var r = new Rectangle(p, sz);
                bool room = wa.Width - kv.Value.Width >= sz.Width || wa.Height - kv.Value.Height >= sz.Height;
                log.Check(wa.Contains(r), "settings next to a " + kv.Key + ": on screen (" + r + ")");
                log.Check(!room || !r.IntersectsWith(kv.Value), "settings next to a " + kv.Key + ": not under the widget (" + r + " vs " + kv.Value + ")");
            }
            bool same = true; foreach (var hh in heights) if (hh != heights[0]) same = false;
            log.Check(same, "settings: every tab has the same height, so switching tabs doesn't resize the window");
        }
        log.Line("");
    }

    // ---- every language: full, row, column, cards, settings, tips (no errors, texts inside and apart)
    void TestLanguages(TestLog log)
    {
        log.Line("== Languages");
        string old = langChoice; bool oldMini = miniPref; int oldDir = miniDir;
        var codes = Lang.Available(); codes.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            int before = log.Fails;
            try
            {
                langChoice = code; ApplyLanguage();
                miniPref = false; Relayout(); CheckLayout(log, code + " full", null);
                miniPref = true; miniDir = 1; Relayout(); CheckLayout(log, code + " row", null);
                miniDir = 2; Relayout(); CheckLayout(log, code + " column", null);
                foreach (var id in Sections())
                {
                    textLog = new List<KeyValuePair<string, RectangleF>>();
                    using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b))
                    { int cw = (int)Math.Round(300 * s); float ch = DrawCard(g, id, cw); var tl = textLog; textLog = null; CheckTexts(log, code + " card " + id, tl, cw, ch); }
                }
                using (var sw = new SettingsWindow(this, dpi)) for (int t = 0; t < 5; t++) { sw.TestTabHeight(t); sw.TestPaint(); }
                using (var tw = new TipsWindow(dpi)) log.Check(tw.Height < Screen.FromRectangle(Body).WorkingArea.Height, code + ": How to use fits on screen");
            }
            catch (Exception ex) { textLog = null; log.Check(false, code + ": " + ex.GetType().Name + ": " + ex.Message); }
            log.Line(code + (log.Fails == before ? ": ok" : ": " + (log.Fails - before) + " problem(s)"));
        }
        langChoice = old; ApplyLanguage(); miniPref = oldMini; miniDir = oldDir; Relayout();
        log.Line("");
    }
}
