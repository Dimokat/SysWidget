// SysWidget - the island: mini mode's default style. A rounded strip (or column) of stats; the stat under the
// mouse opens a card (graphs, the busiest apps, album art and media buttons) that hangs from it, then closes.
// While open, the window covers the card's area too; outside the shape it's fully transparent, so clicks pass.
//
// The card's geometry is worked out once in "strip coordinates" (a = along the strip, b = across it, the card
// growing toward +b) and turned into screen coordinates for whichever way it opens: down, up, right or left.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed partial class WidgetForm
{
    bool islandStyle = true, islandPop = true;
    int miniDir; bool miniVert;          // direction: 0 automatic, 1 row, 2 column; miniVert = what automatic chose
    bool islandOpen, islandSwitching, islandAuto;
    int islandDir;                       // 0 down, 1 up, 2 right, 3 left
    string islandId, lastSong;
    Rectangle islandHome;                // the strip on screen when the island opened
    float islandCurH, islandTargetH;     // how far the card sticks out (animated)
    float islandA, islandATarget, islandSegLen; // card centre along the strip (animated), hovered stat's length
    int islandCardW, islandLeaveMs;
    Bitmap islandContent;                // the card, drawn once per second (or when it changes)
    PointF islandContentAt;              // where the card is on screen (for its buttons)
    RectangleF islandSeg;                // the hovered stat (for its highlight)
    DateTime islandAutoUntil;
    readonly List<RectangleF> islandBtns = new List<RectangleF>();
    readonly System.Windows.Forms.Timer islandAnim = new System.Windows.Forms.Timer { Interval = 10 };
    // The animation runs on the clock, not on timer ticks (which come late or bunch up): a critically damped
    // spring for the card's depth and position, so it starts and stops softly, never overshoots, and keeps its
    // speed when the target changes half-way (pointing at the next stat while the card is still moving).
    readonly System.Diagnostics.Stopwatch islandClock = System.Diagnostics.Stopwatch.StartNew();
    double islandLastMs, islandFadeAt;
    float islandVH, islandVA;
    float islandL, islandVL;             // the card's length along the strip (a column's card: its height), animated
    bool islandFresh;                    // opening from nothing: the content fades in as the card grows
    Bitmap islandPrev;                   // the previous card, fading out while switching
    bool hiResTimer;
    const float SpringW = 30f;           // rad/s: ~90% there in 0.13 s, settled in about 0.3 s
    const double FadeMs = 130;
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    void AnimStart()
    {
        if (islandAnim.Enabled) return;
        islandLastMs = islandClock.Elapsed.TotalMilliseconds;
        // Windows' default 15.6 ms timer steps make a 10 ms timer tick unevenly (16, 16, 31 ms...): ask for
        // 1 ms steps, only while animating.
        if (!hiResTimer) { timeBeginPeriod(1); hiResTimer = true; }
        islandAnim.Start();
    }

    void AnimStop()
    {
        islandAnim.Stop();
        if (hiResTimer) { timeEndPeriod(1); hiResTimer = false; }
    }

    // How far the switch crossfade is (0 = old card, 1 = new card); drops the old card when done.
    float SwitchFade()
    {
        if (islandPrev == null) return 1;
        float k = (float)((islandClock.Elapsed.TotalMilliseconds - islandFadeAt) / FadeMs);
        if (k < 1) return Math.Max(0, k * k * (3 - 2 * k));
        islandPrev.Dispose(); islandPrev = null;
        return 1;
    }
    Bitmap artBmp; int artVer = -1;

    bool IslandMode { get { return mini && islandStyle; } }
    // Mini mode as a column (docked to a side of the screen) instead of a row.
    bool Vert { get { return mini && (miniDir == 2 || (miniDir == 0 && miniVert)); } }
    internal bool IslandStyle { get { return islandStyle; } set { islandStyle = value; CloseIslandNow(); Relayout(); SaveSettings(); } }
    internal bool IslandPop { get { return islandPop; } set { islandPop = value; SaveSettings(); } }
    internal int MiniDirection { get { return miniDir; } set { miniDir = value; CloseIslandNow(); Relayout(); AutoDirection(); SaveSettings(); } }

    // Automatic direction: docked to the left or right edge -> column; to the top or bottom -> row; corners and
    // free spots keep what they had.
    void AutoDirection()
    {
        if (miniDir != 0 || !mini || !IsHandleCreated) return;
        var b = Body; var wa = WorkArea(b);
        bool side = b.Left <= wa.Left || b.Right >= wa.Right, edge = b.Top <= wa.Top || b.Bottom >= wa.Bottom;
        bool v = side && !edge ? true : edge && !side ? false : miniVert;
        if (v == miniVert) return;
        miniVert = v;
        Relayout(); SaveSettings();
    }

    // ---- geometry in strip coordinates
    bool IslandVert { get { return islandDir >= 2; } }
    float StripLen { get { return IslandVert ? islandHome.Height : islandHome.Width; } }
    float StripThick { get { return IslandVert ? islandHome.Width : islandHome.Height; } }
    float IslandRad { get { return IslandVert ? Math.Min(14 * s, islandHome.Width / 2f) : Math.Max(10 * s, islandHome.Height / 2f); } }
    float Fillet { get { return 8 * s; } }
    float CardAlong { get { return IslandVert ? (islandContent != null ? islandContent.Height : 0) : islandCardW; } }
    float CardDepth { get { return IslandVert ? islandCardW : (islandContent != null ? islandContent.Height : 0); } }
    // The card fits on the strip's straight part (with its joining curves).
    bool Fits { get { return StripLen >= CardAlong + 2 * (IslandRad + Fillet); } }
    // A row too short for the card grows as a whole instead ("wide"); a column too short lets the card overhang.
    bool IslandWide { get { return !IslandVert && !Fits; } }

    Matrix ToScreen(int ox, int oy)
    {
        var h = islandHome;
        switch (islandDir)
        {
            case 1: return new Matrix(1, 0, 0, -1, h.X - ox, h.Bottom - oy);
            case 2: return new Matrix(0, 1, 1, 0, h.X - ox, h.Y - oy);
            case 3: return new Matrix(0, 1, -1, 0, h.Right - ox, h.Y - oy);
            default: return new Matrix(1, 0, 0, 1, h.X - ox, h.Y - oy);
        }
    }

    RectangleF Map(RectangleF r, int ox, int oy)
    {
        using (var p = new GraphicsPath()) using (var m = ToScreen(ox, oy)) { p.AddRectangle(r); p.Transform(m); return p.GetBounds(); }
    }

    float ATarget(string id)
    {
        RectangleF seg; float len = StripLen, w = CardAlong;
        float c = sectionRects.TryGetValue(id, out seg) ? (IslandVert ? seg.Y + seg.Height / 2 : seg.X + seg.Width / 2) : len / 2;
        float lim = IslandRad + Fillet + w / 2;
        if (lim * 2 <= len) return Math.Max(lim, Math.Min(c, len - lim));
        // overhanging: keep the card on screen
        var wa = WorkArea(islandHome);
        float lo = (IslandVert ? wa.Top - islandHome.Y : wa.Left - islandHome.X) + w / 2, hi = (IslandVert ? wa.Bottom - islandHome.Y : wa.Right - islandHome.X) - w / 2;
        return Math.Max(lo, Math.Min(c, hi));
    }

    // The card in strip coordinates: centred at a, sticking out by depth.
    RectangleF CardCanon(float a, float depth) { return CardCanon(a, depth, islandL > 0 ? islandL : CardAlong); }
    RectangleF CardCanon(float a, float depth, float along)
    {
        float full = CardDepth, frac = full > 0 ? Math.Min(1, depth / full) : 0;
        float w0 = Math.Min(along, Math.Max(islandSegLen, 40 * s)), w = w0 + (along - w0) * frac;
        return new RectangleF(a - w / 2, StripThick - 1, w, depth);
    }

    // "Wide" row: the whole island grows (down or up) and widens to the card if needed.
    RectangleF IslandRect(float h)
    {
        var home = islandHome; var wa = WorkArea(home);
        float full = CardDepth, frac = full > 0 ? Math.Min(1, h / full) : 0;
        bool wide = home.Width >= islandCardW;
        float w = wide ? home.Width : home.Width + (islandCardW - home.Width) * frac;
        float x = wide ? home.X : home.X + home.Width / 2f - w / 2;
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
        float total = home.Height + h;
        return new RectangleF(x, islandDir == 1 ? home.Bottom - total : home.Y, w, total);
    }

    // Is the mouse over the widget: the panel, or the open island (strip + card)?
    bool OverWidget(Point p)
    {
        if (!islandOpen) return Body.Contains(p);
        if (IslandWide) return Rectangle.Round(IslandRect(islandCurH)).Contains(p);
        return islandHome.Contains(p) || (islandCurH > 0.5f && Rectangle.Round(Map(CardCanon(islandA, islandCurH), 0, 0)).Contains(p));
    }

    // Grows the window (never shrinks it while open) so the whole card fits.
    void EnsureIslandWindow()
    {
        float h = Math.Max(islandTargetH, islandCurH);
        RectangleF want;
        if (IslandWide) want = IslandRect(h);
        else
        {
            float along = Math.Max(islandL, CardAlong);
            want = RectangleF.Union(RectangleF.Union(islandHome, Map(CardCanon(islandA, h, along), 0, 0)), Map(CardCanon(islandATarget, h, along), 0, 0));
        }
        var r = Rectangle.Round(want); int f = (int)Math.Ceiling(Fillet);
        r.Inflate(M + f, M + f);
        var cur = new Rectangle(Left, Top, Width, Height);
        var u = Rectangle.Union(cur, r);
        if (u != cur) SetWindowRect(u);
    }

    // ---- open / switch / close
    void OpenIsland(string id)
    {
        if (!IsHandleCreated || hidden || fsHiddenNow || sliding || updProgress >= 0) return;
        bool first = !islandOpen;
        islandCardW = (int)Math.Round(300 * s);
        if (first)
        {
            islandHome = new Rectangle(Left + M, Top + M, W, H);
            var wa = WorkArea(islandHome);
            if (Vert) // away from the side it's on
            {
                bool left = islandHome.Left <= wa.Left || (islandHome.Right < wa.Right && islandHome.X + islandHome.Width / 2 < wa.X + wa.Width / 2);
                islandDir = left ? 2 : 3;
            }
            else islandDir = islandHome.Top + islandHome.Height / 2 > wa.Top + wa.Height / 2 ? 1 : 0; // lower half: up
            islandOpen = true; islandCurH = 0;
            HidePopup();
            RectangleF seg; islandSegLen = sectionRects.TryGetValue(id, out seg) ? (IslandVert ? seg.Height : seg.Width) : 0;
        }
        islandSwitching = islandCurH > 0.5f;
        // Switching to another stat: the old card fades out while the new one fades in.
        if (islandSwitching && id != islandId && islandContent != null)
        {
            if (islandPrev != null) islandPrev.Dispose();
            islandPrev = islandContent; islandContent = null;
            islandFadeAt = islandClock.Elapsed.TotalMilliseconds;
        }
        islandId = id;
        if (id == "cpu" || id == "ram") StartTopApps(); // keeps measuring until the island closes
        islandTargetH = -1; // so BuildIslandContent doesn't retarget
        BuildIslandContent();
        islandTargetH = CardDepth;
        islandATarget = ATarget(id);
        if (first || islandCurH < 0.5f) { islandA = islandATarget; islandVA = 0; islandL = CardAlong; islandVL = 0; islandFresh = true; }
        else islandFresh = false;
        EnsureIslandWindow();
        AnimStart();
    }

    void CloseIsland()
    {
        if (!islandOpen || islandTargetH == 0) return;
        islandSwitching = false; islandAuto = false; islandTargetH = 0;
        AnimStart();
    }

    void CloseIslandNow() { if (islandOpen) FinishClose(); }

    void FinishClose()
    {
        AnimStop();
        islandOpen = false; islandId = null; islandAuto = false; islandCurH = islandTargetH = 0; islandVH = islandVA = 0; islandL = islandVL = 0; islandFresh = false; hoverId = null; hoverMs = 0;
        if (islandContent != null) { islandContent.Dispose(); islandContent = null; }
        if (islandPrev != null) { islandPrev.Dispose(); islandPrev = null; }
        StopTopApps();
        var home = islandHome;
        SetWindowRect(new Rectangle(home.X - M, home.Y - M, W + 2 * M, H + 2 * M));
        Render();
    }

    // One frame: moves by the time since the last one (a late tick moves further instead of slowing down).
    void IslandStep()
    {
        double now = islandClock.Elapsed.TotalMilliseconds;
        float dt = (float)(Math.Min(50, Math.Max(0, now - islandLastMs)) / 1000); islandLastMs = now;
        int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004f)); float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            islandVH += (-SpringW * SpringW * (islandCurH - islandTargetH) - 2 * SpringW * islandVH) * h; islandCurH += islandVH * h;
            islandVA += (-SpringW * SpringW * (islandA - islandATarget) - 2 * SpringW * islandVA) * h; islandA += islandVA * h;
            islandVL += (-SpringW * SpringW * (islandL - CardAlong) - 2 * SpringW * islandVL) * h; islandL += islandVL * h;
        }
        if (islandTargetH == 0 && islandCurH < 0) { islandCurH = 0; islandVH = 0; }
        bool settled = Math.Abs(islandTargetH - islandCurH) < 0.5f && Math.Abs(islandATarget - islandA) < 0.5f
            && Math.Abs(CardAlong - islandL) < 0.5f && Math.Abs(islandVH) < 30 && Math.Abs(islandVA) < 30 && Math.Abs(islandVL) < 30;
        if (settled)
        {
            islandCurH = islandTargetH; islandA = islandATarget; islandL = CardAlong; islandVH = islandVA = islandVL = 0; islandFresh = false;
            if (islandTargetH == 0) { FinishClose(); return; }
            if (islandPrev == null || SwitchFade() >= 1) { AnimStop(); islandSwitching = false; }
        }
        Render();
    }

    // ---- mouse: point at a stat to open it; leave the island to close it
    void IslandHover()
    {
        Point c = Cursor.Position;
        if (!OverWidget(c))
        {
            if (islandOpen && !islandAuto) { islandLeaveMs += hoverTimer.Interval; if (islandLeaveMs >= 300) CloseIsland(); }
            if (!islandOpen || islandTargetH == 0) { hoverTimer.Stop(); hoverId = null; hoverMs = 0; if (!islandOpen) StopTopApps(); }
            return;
        }
        islandLeaveMs = 0;
        // With click-through (or hide under cursor) on, the island only reacts while Ctrl is held.
        bool usable = IslandMode && !inMove && !hidden && !(menu != null && menu.Visible) && !(PassThrough && !interactive);
        var home = islandOpen ? islandHome : Body;
        string id = null;
        if (home.Contains(c))
            foreach (var kv in sectionRects) if (kv.Value.Contains(c.X - home.X, c.Y - home.Y)) { id = kv.Key; break; }
        if (!usable || id == null) { if (!islandOpen) { hoverId = null; hoverMs = 0; } return; }
        if (islandOpen)
        {
            islandAuto = false;
            if (id != islandId || islandTargetH == 0) OpenIsland(id);
            return;
        }
        if (id != hoverId) { hoverId = id; hoverMs = 0; }
        if (id == "cpu" || id == "ram") StartTopApps(); // so the busiest apps are ready when the card opens
        hoverMs += hoverTimer.Interval;
        if (hoverMs >= 250) OpenIsland(id);
    }

    int IslandButtonAt(Point screen)
    {
        if (!islandOpen || islandId != "media" || islandContent == null || islandCurH < CardDepth - 1) return -1;
        var p = new PointF(screen.X - islandContentAt.X, screen.Y - islandContentAt.Y);
        for (int i = 0; i < islandBtns.Count; i++) if (islandBtns[i].Contains(p)) return i;
        return -1;
    }

    // Once a second: fresh numbers in the open card, the auto-close, and the pop-up when the song changes.
    void IslandTick()
    {
        if (islandOpen)
        {
            if (islandTargetH != 0) BuildIslandContent();
            if (islandAuto && DateTime.UtcNow > islandAutoUntil && !OverWidget(Cursor.Position)) CloseIsland();
        }
        if (!IslandMode || !islandPop || !IsLive("media") || !NowPlaying.Polled) { lastSong = null; return; }
        string key = NowPlaying.Has ? NowPlaying.Title + "\n" + NowPlaying.Artist : "";
        if (lastSong == null) { lastSong = key; return; }
        if (key == lastSong) return;
        lastSong = key;
        if (key == "" || islandOpen || hidden || fsActive || inMove || (menu != null && menu.Visible)) return;
        OpenIsland("media");
        islandAuto = true; islandAutoUntil = DateTime.UtcNow.AddSeconds(4);
    }

    // ---- the card
    void BuildIslandContent()
    {
        if (islandId == null) return;
        int cw = islandCardW = (int)Math.Round(300 * s);
        float h;
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b)) h = DrawCard(g, islandId, cw);
        int ch = Math.Max(1, (int)Math.Ceiling(h));
        var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(panelBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            DrawCard(g, islandId, cw);
        }
        int oldH = islandContent != null ? islandContent.Height : -1;
        if (islandContent != null) islandContent.Dispose();
        islandContent = bmp;
        if (islandOpen && islandTargetH > 0 && ch != oldH)
        {
            islandTargetH = CardDepth; islandATarget = ATarget(islandId);
            EnsureIslandWindow(); AnimStart();
        }
    }

    float DrawCard(Graphics g, string id, int cw)
    {
        int oldW = W; float oldHdr = hdrH, oldSmall = smallH;
        W = cw; hdrH = 17 * s; smallH = 14 * s;
        try
        {
            float x = pad, w = cw - 2 * pad, y = pad * 0.8f;
            if (id == "media") y = MediaCard(g, x, w, y);
            else
            {
                DrawSection(g, id, x, w, ref y, 26 * s, 11 * s);
                if ((id == "cpu" || id == "ram") && topApps != null) y = AppsList(g, id == "cpu", x, w, y + 9 * s);
            }
            return y + pad * 0.8f;
        }
        finally { W = oldW; hdrH = oldHdr; smallH = oldSmall; }
    }

    List<string[]> TopRows(bool isCpu, List<float> shares)
    {
        var rows = new List<string[]>();
        if (topApps == null || (isCpu ? !topApps.HasCpu : !topApps.HasMem)) return rows;
        var list = isCpu ? topApps.Cpu : topApps.Mem;
        double max = 0; foreach (var kv in list) max = Math.Max(max, kv.Value);
        foreach (var kv in list)
        {
            double v = kv.Value;
            string val = isCpu ? N((float)v, v < 10 ? "0.0" : "0") + "%"
                : v >= GB ? Lang.F("unit.gb", N((float)(v / GB), "0.0")) : Lang.F("unit.mb", N((float)(v / 1048576), "0"));
            rows.Add(new[] { kv.Key, val }); shares.Add(max > 0 ? (float)(v / max) : 0);
        }
        return rows;
    }

    float AppsList(Graphics g, bool isCpu, float x, float w, float y)
    {
        Txt(g, Lang.T(isCpu ? "apps.cpu" : "apps.ram").ToUpper(CultureInfo.CurrentUICulture), fSmall, brDim, x, y, sf);
        y += smallH + 3 * s;
        var shares = new List<float>(); var rows = TopRows(isCpu, shares);
        if (rows.Count == 0) { Txt(g, "…", fSmall, brDim, x, y, sf); return y + 5 * (smallH + 8 * s) - 4 * s; }
        Color c = Shade(ColorOf(isCpu ? "cpu" : "ram"));
        for (int i = 0; i < rows.Count; i++)
        {
            float vw = g.MeasureString(rows[i][1], fSmall, PointF.Empty, sf).Width;
            Txt(g, Fit(g, rows[i][0], fSmall, w - vw - 10 * s), fSmall, brValue, x, y, sf);
            Txt(g, rows[i][1], fSmall, brLabel, x + w - vw, y, sf);
            y += smallH + 1 * s;
            using (var b = new SolidBrush(InkA(22))) g.FillRectangle(b, x, y, w, 2.5f * s);
            using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, Math.Max(2 * s, w * shares[i]), 2.5f * s);
            y += 7 * s;
        }
        return y - 4 * s;
    }

    float MediaCard(Graphics g, float x, float w, float y)
    {
        islandBtns.Clear();
        UpdateArt();
        float art = 64 * s;
        var ar = new RectangleF(x, y, art, art);
        using (var p = Round(ar, 8 * s))
        {
            if (artBmp != null)
            {
                var saved = g.Clip; g.SetClip(p, CombineMode.Intersect);
                int side = Math.Min(artBmp.Width, artBmp.Height); // square crop from the middle (video thumbnails are wide)
                var src = new RectangleF((artBmp.Width - side) / 2f, (artBmp.Height - side) / 2f, side, side);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(artBmp, ar, src, GraphicsUnit.Pixel);
                g.Clip = saved;
            }
            else
            {
                using (var b = new SolidBrush(InkA(16))) g.FillPath(b, p);
                using (var b = new SolidBrush(InkA(90))) // a music note
                {
                    float cx = ar.X + art / 2, cy = ar.Y + art / 2, u = art / 9;
                    g.FillEllipse(b, cx - 2.2f * u, cy + 0.6f * u, 2.2f * u, 1.7f * u);
                    g.FillRectangle(b, cx - 0.45f * u, cy - 2.6f * u, 0.45f * u, 4f * u);
                    g.FillPolygon(b, new[] { new PointF(cx - 0.45f * u, cy - 2.6f * u), new PointF(cx + 1.8f * u, cy - 1.6f * u), new PointF(cx + 1.8f * u, cy - 0.7f * u), new PointF(cx - 0.45f * u, cy - 1.7f * u) });
                }
            }
        }
        float tx = x + art + 12 * s, tw = w - art - 12 * s, ty = y + 5 * s;
        Txt(g, Fit(g, NowPlaying.Has ? NowPlaying.Title : Lang.T("media.none"), fValue, tw), fValue, NowPlaying.Has ? brValue : brDim, tx, ty, sf);
        ty += hdrH + 2 * s;
        if (NowPlaying.Has)
        {
            Txt(g, Fit(g, NowPlaying.Artist, fSmall, tw), fSmall, brLabel, tx, ty, sf); ty += smallH;
            string album = NowPlaying.Album, app = NowPlaying.App; // album only when the player reports one
            string third = album == "" ? app : app == "" ? album : album + "  ·  " + app;
            Txt(g, Fit(g, third, fSmall, tw), fSmall, brDim, tx, ty, sf);
        }
        y += art + 10 * s;
        double dur = NowPlaying.Dur, pos = NowPlaying.Position();
        if (NowPlaying.Has && dur > 1)
        {
            Bar(g, x, y, w, 4 * s, (float)(pos / dur), Shade(ColorOf("media")));
            y += 4 * s + 3 * s;
            string a = TimeText(pos), b2 = TimeText(dur);
            Txt(g, a, fSmall, brDim, x, y, sf);
            Txt(g, b2, fSmall, brDim, x + w - g.MeasureString(b2, fSmall, PointF.Empty, sf).Width, y, sf);
            y += smallH + 2 * s;
        }
        else y += 2 * s;
        float bs = 34 * s, gap = 16 * s, total = 3 * bs + 2 * gap, bx = x + (w - total) / 2;
        for (int i = 0; i < 3; i++)
        {
            var r = new RectangleF(bx + i * (bs + gap), y, bs, bs);
            islandBtns.Add(r);
            if (i == 1) using (var b = new SolidBrush(InkA(24))) g.FillEllipse(b, r);
            MediaIcon(g, i, r.X + bs / 2, r.Y + bs / 2, (i == 1 ? 6.5f : 5.5f) * s);
        }
        return y + bs;
    }

    void MediaIcon(Graphics g, int i, float cx, float cy, float a)
    {
        using (var b = new SolidBrush(brValue.Color))
        {
            if (i == 1)
            {
                if (NowPlaying.Playing) { g.FillRectangle(b, cx - a * 0.75f, cy - a * 0.9f, a * 0.55f, a * 1.8f); g.FillRectangle(b, cx + a * 0.2f, cy - a * 0.9f, a * 0.55f, a * 1.8f); }
                else g.FillPolygon(b, new[] { new PointF(cx - a * 0.55f, cy - a), new PointF(cx + a * 0.95f, cy), new PointF(cx - a * 0.55f, cy + a) });
                return;
            }
            float dir = i == 0 ? -1 : 1, bw = Math.Max(1.5f * s, a * 0.3f);
            g.FillPolygon(b, new[] { new PointF(cx - dir * a * 0.6f, cy - a * 0.85f), new PointF(cx + dir * a * 0.5f, cy), new PointF(cx - dir * a * 0.6f, cy + a * 0.85f) });
            g.FillRectangle(b, dir > 0 ? cx + a * 0.5f : cx - a * 0.5f - bw, cy - a * 0.85f, bw, a * 1.7f);
        }
    }

    static string TimeText(double sec)
    {
        int t = (int)Math.Max(0, sec);
        return t >= 3600 ? (t / 3600) + ":" + (t / 60 % 60).ToString("00") + ":" + (t % 60).ToString("00") : (t / 60) + ":" + (t % 60).ToString("00");
    }

    void UpdateArt()
    {
        int v = NowPlaying.ArtVersion; if (v == artVer) return;
        artVer = v;
        if (artBmp != null) { artBmp.Dispose(); artBmp = null; }
        var bytes = NowPlaying.Art; if (bytes == null) return;
        try { using (var ms = new MemoryStream(bytes)) using (var img = Image.FromStream(ms)) artBmp = new Bitmap(img); } catch { }
    }

    // ---- the column: mini mode stood on its side. Each stat is a small centred tile: an icon in the section's
    // colour (instead of its name, which is often what's widest), the value, and an optional second value.
    float colW; // measured width of the column's contents

    int MiniColumnWidth()
    {
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b)) { colW = 0; MiniLayoutV(g, false); }
        return (int)Math.Ceiling(Math.Max(46 * s, colW + 16 * s));
    }

    float MiniLayoutV(Graphics g, bool draw)
    {
        float y = 9 * s; bool any = false;
        string tTemp = fahrenheit ? "212\u00B0F" : "100\u00B0C";
        foreach (var id in Sections())
        {
            if (!IsShown(id, true)) continue;
            Color c = Shade(ColorOf(id));
            float y0 = y;
            switch (id)
            {
                case "cpu":
                {
                    bool t = Opt(id, "temp") && pcTemp != IntPtr.Zero;
                    Tile(g, draw, ref y, ref any, "cpu", null, c, Pct(cpu), brValue, "100%", t ? (TempText(cpuTemp) ?? "") : null, TempBrush(cpuTemp), tTemp);
                    break;
                }
                case "fps": Tile(g, draw, ref y, ref any, "fps", null, c, FpsText(), brValue, "999", null, null, null); break;
                case "ram": Tile(g, draw, ref y, ref any, "ram", null, c, Lang.F("ram.short", N(ramUsed, "0.0")), brValue, Lang.F("ram.short", N(99.9f, "0.0")), null, null, null); break;
                case "net":
                {
                    bool upOn = Opt(id, "up");
                    string wide = netBits ? Lang.F("unit.mbits", "999") : Lang.F("unit.mbps", "999");
                    Tile(g, draw, ref y, ref any, "net", null, c, "\u2193 " + RateNet(down), brValue, "\u2193 " + wide, upOn ? "\u2191 " + RateNet(up) : null, brLabel, "\u2191 " + wide);
                    break;
                }
                case "ping": Tile(g, draw, ref y, ref any, "ping", null, c, float.IsNaN(pinger.Ms) ? "\u2014" : PingText(pinger.Ms), float.IsNaN(pinger.Ms) ? brDim : brValue, Lang.F("unit.ms", "999"), null, null, null); break;
                case "battery": Tile(g, draw, ref y, ref any, "battery", null, c, Battery.Percent >= 0 ? Pct(Battery.Percent) : "\u2014", brValue, "100%", null, null, null); break;
                case "disk":
                    if (Opt(id, "free"))
                        foreach (var dr in drives)
                            Tile(g, draw, ref y, ref any, "disk", dr.Name, c, Lang.F("disk.short", N((float)(dr.Free / GB), "0")), brValue, Lang.F("disk.short", "9999"), null, null, null);
                    else Tile(g, draw, ref y, ref any, "disk", null, c, Lang.F("disk.read", Rate(diskR)), brValue, Lang.F("disk.read", Lang.F("unit.mbps", "999")),
                              Lang.F("disk.write", Rate(diskW)), brValue, Lang.F("disk.write", Lang.F("unit.mbps", "999")));
                    break;
                case "media":
                {
                    // The song title gets as many letters as the column has room for; it never widens it.
                    float w = W - 12 * s;
                    Tile(g, draw, ref y, ref any, "media", null, c, null, null, null, null, null, null);
                    y -= 8 * s;
                    if (draw)
                    {
                        Center(g, Fit(g, NowPlaying.Has ? NowPlaying.Title : Lang.T("media.none"), fSmall, w), fSmall, NowPlaying.Has ? brValue : brDim, y);
                        if (NowPlaying.Has) Center(g, Fit(g, NowPlaying.Artist, fSmall, w), fSmall, brDim, y + 14 * s);
                    }
                    // Always room for title and artist: the height is measured once, maybe before anything plays.
                    y += 28 * s + 8 * s;
                    break;
                }
                default:
                {
                    var gp = GpuById(id); if (gp == null) break;
                    bool t = Opt(id, "temp") && HasTemp(gp);
                    string num = gp.Number > 0 ? gp.Number.ToString(CultureInfo.InvariantCulture) : null;
                    string kind = gp.Integrated ? "igpu" : "gpu";
                    if (gp.Idle) Tile(g, draw, ref y, ref any, kind, num, c, Lang.T("gpu.idle.short"), brDim, "100%", t ? "" : null, brValue, tTemp);
                    else Tile(g, draw, ref y, ref any, kind, num, c, Pct(gp.Load), brValue, "100%", t ? (TempText(gp.Temp) ?? "") : null, TempBrush(gp.Temp), tTemp);
                    break;
                }
            }
            if (y > y0) sectionRects[id] = new RectangleF(0, y0 - 4 * s, W, y - y0);
        }
        return y - (any ? 8 * s : 0) + 9 * s;
    }

    void Center(Graphics g, string t, Font f, Brush b, float y)
    {
        float w = g.MeasureString(t, f, PointF.Empty, sf).Width;
        Txt(g, t, f, b, (W - w) / 2, y, sf);
    }

    // One tile: icon (+ a short tag such as the drive letter or GPU number), then the value, then an optional
    // second value. t1/t2 are the widest texts expected, so the column keeps its width as numbers change.
    void Tile(Graphics g, bool draw, ref float y, ref bool any, string icon, string tag, Color accent, string v1, Brush b1, string t1, string v2, Brush b2, string t2)
    {
        any = true;
        float isz = 13 * s, tw = tag != null ? g.MeasureString(tag, fLabel, PointF.Empty, sf).Width + 3 * s : 0;
        colW = Math.Max(colW, isz + tw);
        if (draw)
        {
            float x0 = (W - isz - tw) / 2;
            TileIcon(g, icon, x0 + isz / 2, y + 7 * s, isz, accent);
            if (tag != null) Txt(g, tag, fLabel, brLabel, x0 + isz + 3 * s, y + 0.5f * s, sf);
        }
        y += 16 * s;
        if (v1 != null || t1 != null)
        {
            if (t1 != null) colW = Math.Max(colW, g.MeasureString(t1, fValue, PointF.Empty, sf).Width);
            if (draw && v1 != null) Center(g, Fit(g, v1, fValue, W - 6 * s), fValue, b1, y);
            y += 16 * s;
        }
        if (v2 != null)
        {
            if (t2 != null) colW = Math.Max(colW, g.MeasureString(t2, fSmall, PointF.Empty, sf).Width);
            if (draw) Center(g, Fit(g, v2, fSmall, W - 6 * s), fSmall, b2, y);
            y += 14 * s;
        }
        y += 8 * s;
    }

    string segIcon; // icon for the mini row's next item (null = a plain dot)
    string IconKind(string id)
    {
        if (!id.StartsWith("gpu:")) return id;
        var gp = GpuById(id); return gp != null && gp.Integrated ? "igpu" : "gpu";
    }

    // Small line icons, drawn in the section's colour inside a sz x sz box centred on (cx, cy).
    void TileIcon(Graphics g, string kind, float cx, float cy, float sz, Color c)
    {
        float u = sz / 2;
        using (var p = new Pen(c, Math.Max(1f, 1.3f * s)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var b = new SolidBrush(c))
        {
            switch (kind)
            {
                case "cpu": // a chip with pins
                {
                    float h = u * 0.62f;
                    using (var rp = Round(new RectangleF(cx - h, cy - h, 2 * h, 2 * h), 1.5f * s)) g.DrawPath(p, rp);
                    g.FillRectangle(b, cx - h * 0.4f, cy - h * 0.4f, h * 0.8f, h * 0.8f);
                    for (int i = -1; i <= 1; i++)
                    {
                        float o = i * h * 0.6f;
                        g.DrawLine(p, cx + o, cy - h, cx + o, cy - u); g.DrawLine(p, cx + o, cy + h, cx + o, cy + u);
                        g.DrawLine(p, cx - h, cy + o, cx - u, cy + o); g.DrawLine(p, cx + h, cy + o, cx + u, cy + o);
                    }
                    break;
                }
                case "gpu": case "igpu": // a graphics card with its fan; integrated: a smaller board
                {
                    float w = kind == "gpu" ? u * 0.95f : u * 0.8f, h = kind == "gpu" ? u * 0.62f : u * 0.55f;
                    using (var rp = Round(new RectangleF(cx - w, cy - h, 2 * w, 2 * h), 1.5f * s)) g.DrawPath(p, rp);
                    float r = h * 0.6f;
                    if (kind == "gpu") g.DrawEllipse(p, cx + w * 0.3f - r, cy - r, 2 * r, 2 * r);
                    else g.FillRectangle(b, cx - w * 0.35f, cy - h * 0.35f, w * 0.7f, h * 0.7f);
                    if (kind == "gpu") g.DrawLine(p, cx - w * 0.6f, cy - h * 0.3f, cx - w * 0.6f, cy + h * 0.3f);
                    break;
                }
                case "fps": // a gauge
                    g.DrawArc(p, cx - u * 0.9f, cy - u * 0.8f, u * 1.8f, u * 1.8f, 150, 240);
                    g.DrawLine(p, cx, cy + u * 0.1f, cx + u * 0.5f, cy - u * 0.4f);
                    g.FillEllipse(b, cx - 1.5f * s, cy + u * 0.1f - 1.5f * s, 3 * s, 3 * s);
                    break;
                case "ram": // a memory stick
                {
                    float w = u * 0.95f, h = u * 0.45f;
                    g.DrawRectangle(p, cx - w, cy - h, 2 * w, 2 * h);
                    for (int i = -1; i <= 1; i++) g.FillRectangle(b, cx + i * w * 0.55f - w * 0.15f, cy - h * 0.5f, w * 0.3f, h);
                    for (int i = -2; i <= 2; i++) g.DrawLine(p, cx + i * w * 0.4f, cy + h, cx + i * w * 0.4f, cy + h + u * 0.3f);
                    break;
                }
                case "net": // down and up arrows
                {
                    float x1 = cx - u * 0.4f, x2 = cx + u * 0.4f, t = cy - u * 0.85f, bt = cy + u * 0.85f, a = u * 0.35f;
                    g.DrawLine(p, x1, t, x1, bt); g.DrawLines(p, new[] { new PointF(x1 - a, bt - a), new PointF(x1, bt), new PointF(x1 + a, bt - a) });
                    g.DrawLine(p, x2, bt, x2, t); g.DrawLines(p, new[] { new PointF(x2 - a, t + a), new PointF(x2, t), new PointF(x2 + a, t + a) });
                    break;
                }
                case "ping": // signal waves
                {
                    float by = cy + u * 0.7f;
                    g.FillEllipse(b, cx - 1.6f * s, by - 1.6f * s, 3.2f * s, 3.2f * s);
                    for (int i = 1; i <= 2; i++) { float r = u * 0.55f * i; g.DrawArc(p, cx - r, by - r, 2 * r, 2 * r, 225, 90); }
                    break;
                }
                case "disk": // a drive
                {
                    float w = u * 0.9f, h = u * 0.6f;
                    using (var rp = Round(new RectangleF(cx - w, cy - h, 2 * w, 2 * h), 2 * s)) g.DrawPath(p, rp);
                    g.DrawLine(p, cx - w, cy + h * 0.15f, cx + w, cy + h * 0.15f);
                    g.FillEllipse(b, cx + w * 0.5f - 1.3f * s, cy + h * 0.55f - 1.3f * s, 2.6f * s, 2.6f * s);
                    break;
                }
                case "battery": // a battery with its charge
                {
                    float w = u * 0.8f, h = u * 0.5f, lvl = Battery.Percent >= 0 ? Battery.Percent / 100f : 0;
                    g.DrawRectangle(p, cx - w - u * 0.1f, cy - h, 2 * w, 2 * h);
                    g.FillRectangle(b, cx + w - u * 0.1f, cy - h * 0.4f, u * 0.25f, h * 0.8f);
                    if (lvl > 0) g.FillRectangle(b, cx - w - u * 0.1f + 2 * s, cy - h + 2 * s, Math.Max(0, (2 * w - 4 * s) * lvl), 2 * h - 4 * s);
                    break;
                }
                case "media": // a music note
                {
                    g.FillEllipse(b, cx - u * 0.85f, cy + u * 0.25f, u * 0.8f, u * 0.6f);
                    g.FillRectangle(b, cx - u * 0.12f, cy - u * 0.85f, Math.Max(1.4f * s, u * 0.16f), u * 1.4f);
                    g.FillPolygon(b, new[] { new PointF(cx - u * 0.1f, cy - u * 0.85f), new PointF(cx + u * 0.75f, cy - u * 0.5f), new PointF(cx + u * 0.75f, cy - u * 0.2f), new PointF(cx - u * 0.1f, cy - u * 0.5f) });
                    break;
                }
            }
        }
    }

    // ---- drawing the open island
    void RenderIsland()
    {
        var win = WinRect;
        EnsureCanvas(win.Width, win.Height);
        if (IslandWide) RenderWide(win); else RenderTab(win);
        Present(canvas);
    }

    // The strip draws its stats (row or column) at its place, with the open stat highlighted.
    void DrawStrip(Graphics g, RectangleF strip)
    {
        if (islandSeg.Width > 0 && islandCurH > 0.5f)
        {
            RectangleF hr = IslandVert
                ? new RectangleF(strip.X + 4 * s, strip.Y + islandSeg.Y + 1 * s, strip.Width - 8 * s, islandSeg.Height - 2 * s)
                : new RectangleF(strip.X + islandSeg.X - 2 * s, strip.Y + 4 * s, islandSeg.Width - 3 * s, strip.Height - 8 * s);
            using (var hp = Round(hr, IslandVert ? Math.Min(8 * s, hr.Height / 2) : hr.Height / 2))
            using (var b = new SolidBrush(InkA(lightBg ? 14 : 20))) g.FillPath(b, hp);
        }
        var state = g.Save();
        g.TranslateTransform(strip.X, strip.Y);
        sectionRects.Clear();
        if (Vert) MiniLayoutV(g, true); else MiniLayout(g, true);
        g.Restore(state);
        RectangleF seg;
        islandSeg = islandId != null && sectionRects.TryGetValue(islandId, out seg) ? seg : RectangleF.Empty;
    }

    Pen OutlinePen() { return PassThrough && interactive ? new Pen(Color.FromArgb(200, cCpu), 1.5f * s) : new Pen(InkA(34), 1); }

    void ExcludeDocked(Graphics g, RectangleF r, bool dl, bool dt, bool dr, bool db)
    {
        if (dt) g.ExcludeClip(new Region(new RectangleF(r.X - M, r.Y - M, r.Width + 2 * M, M + 1)));
        if (db) g.ExcludeClip(new Region(new RectangleF(r.X - M, r.Bottom - 1, r.Width + 2 * M, M + 1)));
        if (dl) g.ExcludeClip(new Region(new RectangleF(r.X - M, r.Y - M, M + 1, r.Height + 2 * M)));
        if (dr) g.ExcludeClip(new Region(new RectangleF(r.Right - 1, r.Y - M, M + 1, r.Height + 2 * M)));
    }

    // The strip stays; the card hangs from the open stat, joined by two small inward curves.
    void RenderTab(Rectangle win)
    {
        var home = islandHome; var wa = WorkArea(home);
        var strip = new RectangleF(home.X - win.X, home.Y - win.Y, home.Width, home.Height);
        var cc = CardCanon(islandA, islandCurH);
        bool showCard = islandCurH > 0.5f;
        bool overhang = !Fits;
        bool dl = home.Left <= wa.Left, dt = home.Top <= wa.Top, dr = home.Right >= wa.Right, db = home.Bottom >= wa.Bottom;
        float f = overhang ? 0 : Math.Min(Fillet, cc.Height / 2), cRad = Math.Min(14 * s, Math.Min(cc.Height / 2, cc.Width / 2));
        float jb = StripThick - 1; // where the card meets the strip (b)
        using (var g = Graphics.FromImage(canvas))
        using (var m = ToScreen(win.X, win.Y))
        using (var sp = ShapePath(strip, IslandRad, 10 * s, dl, dt, dr, db))
        using (var cp = CardPath(cc, cRad, false, 0, overhang))
        using (var outline = CardPath(cc, cRad, true, f, overhang))
        using (var fl = new GraphicsPath())
        using (var bg = new SolidBrush(panelBg))
        {
            cp.Transform(m); outline.Transform(m);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.FillPath(bg, sp);
            float L = cc.Left, R = cc.Right;
            if (showCard)
            {
                g.FillPath(bg, cp);
                if (f > 0.5f)
                {
                    fl.StartFigure(); fl.AddLine(L, jb, L - f, jb); fl.AddArc(L - 2 * f, jb, 2 * f, 2 * f, 270, 90); fl.CloseFigure();
                    fl.StartFigure(); fl.AddLine(R, jb, R + f, jb); fl.AddArc(R, jb, 2 * f, 2 * f, 270, -90); fl.CloseFigure();
                    fl.Transform(m);
                    g.FillPath(bg, fl);
                }
                if (islandContent != null)
                {
                    var saved = g.Clip;
                    g.SetClip(cp, CombineMode.Intersect);
                    float cw = islandContent.Width, ch = islandContent.Height, cx, cy;
                    switch (islandDir)
                    {
                        case 1: cx = strip.X + islandA - cw / 2; cy = strip.Top + 1 - ch; break;
                        case 2: cx = strip.Right - 1; cy = strip.Y + islandA - ch / 2; break;
                        case 3: cx = strip.Left + 1 - cw; cy = strip.Y + islandA - ch / 2; break;
                        default: cx = strip.X + islandA - cw / 2; cy = strip.Bottom - 1; break;
                    }
                    islandContentAt = new PointF(win.X + cx, win.Y + cy);
                    float alpha = islandFresh || islandTargetH == 0 ? Math.Min(1, islandCurH / Math.Max(1, CardDepth)) : 1, k = SwitchFade();
                    if (islandPrev != null)
                    {
                        float ph = islandPrev.Height, px = cx, py = cy;
                        if (islandDir == 1) py = strip.Top + 1 - ph; else if (islandDir >= 2) py = strip.Y + islandA - ph / 2;
                        DrawFaded(g, islandPrev, px, py, alpha * (1 - k));
                    }
                    DrawFaded(g, islandContent, cx, cy, alpha * k);
                    g.Clip = saved;
                }
            }
            DrawStrip(g, strip);

            // outline: the strip's (minus where the card joins it, and screen edges it's docked to), then the card's
            using (var pen = OutlinePen())
            {
                var saved = g.Clip;
                ExcludeDocked(g, strip, dl, dt, dr, db);
                if (showCard)
                    using (var band = new GraphicsPath())
                    {
                        band.AddRectangle(new RectangleF(L - f, jb - 2, R - L + 2 * f, 4)); band.Transform(m);
                        g.ExcludeClip(new Region(band.GetBounds()));
                    }
                g.DrawPath(pen, sp);
                g.Clip = saved;
                if (showCard)
                {
                    g.DrawPath(pen, outline);
                    if (f > 0.5f)
                        using (var arcs = new GraphicsPath())
                        {
                            arcs.StartFigure(); arcs.AddArc(L - 2 * f, jb, 2 * f, 2 * f, 270, 90);
                            arcs.StartFigure(); arcs.AddArc(R, jb, 2 * f, 2 * f, 180, 90);
                            arcs.Transform(m);
                            g.DrawPath(pen, arcs);
                        }
                }
            }
        }
    }

    // A row too short for a hanging card: the whole island grows, the strip staying at its edge.
    void RenderWide(Rectangle win)
    {
        var home = islandHome; var r = IslandRect(islandCurH); var wa = WorkArea(home);
        var local = new RectangleF(r.X - win.X, r.Y - win.Y, r.Width, r.Height);
        bool dl = r.Left <= wa.Left, dt = r.Top <= wa.Top, dr = r.Right >= wa.Right, db = r.Bottom >= wa.Bottom;
        bool up = islandDir == 1;
        var strip = new RectangleF(local.X + (home.X - r.X), up ? local.Bottom - home.Height : local.Y, home.Width, home.Height);
        using (var g = Graphics.FromImage(canvas))
        using (var path = ShapePath(local, IslandRad, 10 * s, dl, dt, dr, db))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(panelBg)) g.FillPath(b, path);
            if (islandContent != null && islandCurH > 0.5f)
            {
                var saved = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                g.SetClip(new RectangleF(local.X, up ? local.Y : strip.Bottom, local.Width, islandCurH), CombineMode.Intersect);
                float cw = islandContent.Width;
                float segC = islandSeg.Width > 0 ? strip.X + islandSeg.X + islandSeg.Width / 2 : local.X + local.Width / 2;
                float cx = Math.Max(local.X, Math.Min(segC - cw / 2, local.Right - cw));
                float cy = up ? strip.Top - islandContent.Height : strip.Bottom;
                islandContentAt = new PointF(win.X + cx, win.Y + cy);
                float alpha = islandFresh || islandTargetH == 0 ? Math.Min(1, islandCurH / Math.Max(1, islandContent.Height)) : 1, k = SwitchFade();
                if (islandPrev != null) DrawFaded(g, islandPrev, cx, up ? strip.Top - islandPrev.Height : strip.Bottom, alpha * (1 - k));
                DrawFaded(g, islandContent, cx, cy, alpha * k);
                float ly = up ? strip.Top : strip.Bottom;
                using (var p = new Pen(InkA(20))) g.DrawLine(p, local.X + pad, ly, local.Right - pad, ly);
                g.Clip = saved;
            }
            DrawStrip(g, strip);
            var saved2 = g.Clip;
            ExcludeDocked(g, local, dl, dt, dr, db);
            using (var p = OutlinePen()) g.DrawPath(p, path);
            g.Clip = saved2;
        }
    }

    // In strip coordinates the card always grows toward +b: rounded on that side; as an outline it's open on the
    // strip side, starting `skip` away from it where the joining curves take over. An overhanging card (column
    // too short) is rounded on all four corners.
    static GraphicsPath CardPath(RectangleF r, float rad, bool open, float skip, bool allRound)
    {
        var p = new GraphicsPath(); float d = Math.Max(0.1f, rad * 2);
        if (allRound)
        {
            p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        p.AddLine(r.Left, r.Top + skip, r.Left, r.Bottom - rad);
        p.AddArc(r.Left, r.Bottom - d, d, d, 180, -90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 90, -90);
        p.AddLine(r.Right, r.Bottom - rad, r.Right, r.Top + skip);
        if (!open) p.CloseFigure();
        return p;
    }

    static void DrawFaded(Graphics g, Bitmap b, float x, float y, float a)
    {
        var dest = new Rectangle((int)Math.Round(x), (int)Math.Round(y), b.Width, b.Height);
        if (a <= 0.01f) return;
        if (a >= 0.99f) { g.DrawImage(b, dest, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel); return; }
        using (var ia = new ImageAttributes())
        {
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Max(0, a) });
            g.DrawImage(b, dest, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
        }
    }

    // Same outline rules as the panel: rounded when free, square where two docked edges meet,
    // a concave fillet into the screen edge where one side is docked.
    static GraphicsPath ShapePath(RectangleF r, float rad, float fillet, bool dL, bool dT, bool dR, bool dB)
    {
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height)), fd = fillet * 2;
        float L = r.Left + (dL ? -0.5f : 0.5f), T = r.Top + (dT ? -0.5f : 0.5f), R = r.Right - 0.5f, B = r.Bottom - 0.5f;
        var p = new GraphicsPath();
        if (dT && dL) p.AddLine(L, T, L, T);
        else if (dT) p.AddArc(L - fd, T, fd, fd, 0, -90);
        else if (dL) p.AddArc(L, T - fd, fd, fd, 180, -90);
        else p.AddArc(L, T, d, d, 180, 90);
        if (dT && dR) p.AddLine(R, T, R, T);
        else if (dT) p.AddArc(R, T, fd, fd, 270, -90);
        else if (dR) p.AddArc(R - fd, T - fd, fd, fd, 90, -90);
        else p.AddArc(R - d, T, d, d, 270, 90);
        if (dB && dR) p.AddLine(R, B, R, B);
        else if (dR) p.AddArc(R - fd, B, fd, fd, 0, -90);
        else if (dB) p.AddArc(R, B - fd, fd, fd, 180, -90);
        else p.AddArc(R - d, B - d, d, d, 0, 90);
        if (dB && dL) p.AddLine(L, B, L, B);
        else if (dB) p.AddArc(L - fd, B - fd, fd, fd, 90, -90);
        else if (dL) p.AddArc(L, B, fd, fd, 270, -90);
        else p.AddArc(L, B - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
