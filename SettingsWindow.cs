// SysWidget - settings window: what the widget shows, in which order, and how it looks.
// Drawn by hand in the widget's style (same panel colour, accent and type); every change applies live.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class SettingsWindow : Form
{
    readonly WidgetForm w;
    readonly float d;                        // DPI scale
    int tab;                                 // 0 sections, 1 mini mode, 2 appearance
    string colorKey;                         // colour being picked (strip open under its section)
    readonly List<Hit> hits = new List<Hit>();
    Font fText, fBold, fSmall, fHead;
    static readonly string[] Tabs = { "settings.tab.sections", "settings.tab.mini", "settings.tab.appearance" };
    static readonly Color[] Swatches =
    {
        Color.FromArgb(0x4F, 0xC3, 0xF7), Color.FromArgb(0x64, 0xB5, 0xF6), Color.FromArgb(0x79, 0x86, 0xCB), Color.FromArgb(0xBA, 0x68, 0xC8),
        Color.FromArgb(0xF0, 0x62, 0x92), Color.FromArgb(0xE5, 0x73, 0x73), Color.FromArgb(0xFF, 0x8A, 0x65), Color.FromArgb(0xFF, 0xB7, 0x4D),
        Color.FromArgb(0xFF, 0xD5, 0x4F), Color.FromArgb(0xAE, 0xD5, 0x81), Color.FromArgb(0x81, 0xC7, 0x84), Color.FromArgb(0x4D, 0xB6, 0xAC),
        Color.FromArgb(0x90, 0xA4, 0xAE), Color.FromArgb(0xE0, 0xE0, 0xE0)
    };

    sealed class Hit { public RectangleF R; public Action Click; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public SettingsWindow(WidgetForm widget, float dpi)
    {
        w = widget; d = dpi;
        Text = Lang.T("settings.title");
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = true;
        ShowInTaskbar = true; StartPosition = FormStartPosition.Manual; KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        MakeFonts();
        ClientSize = new Size(S(452), S(420));
        KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    int S(float v) { return (int)Math.Round(v * d); }

    void MakeFonts()
    {
        fText = Lang.MakeFont(9.75f, false); fBold = Lang.MakeFont(9.75f, true);
        fSmall = Lang.MakeFont(8.75f, false); fHead = Lang.MakeFont(8.75f, true);
        Font = fText;
    }

    // The widget switched language: new fonts (CJK need their own), new title, new layout.
    public void LanguageChanged()
    {
        var old = new[] { fText, fBold, fSmall, fHead };
        MakeFonts();
        foreach (var f in old) f.Dispose();
        Text = Lang.T("settings.title");
        Reflow();
    }

    static string Upper(string t) { return t.ToUpper(System.Globalization.CultureInfo.CurrentUICulture); }

    static Color Bg { get { return MenuLook.Bg; } }
    static Color Line { get { return MenuLook.Border; } }
    static Color Ink { get { return MenuLook.Text; } }
    static Color Dim { get { return MenuLook.Dim; } }
    static Color Acc { get { return MenuLook.Accent; } }
    static Color Raised { get { return Color.FromArgb(14, 255, 255, 255); } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFrameColors();
    }

    // Dark title bar in the panel colour (Windows 11; Windows 10 1809+ gets the dark bar only).
    public void ApplyFrameColors()
    {
        if (!IsHandleCreated) return;
        try
        {
            int on = 1; DwmSetWindowAttribute(Handle, 20 /*USE_IMMERSIVE_DARK_MODE*/, ref on, 4);
            int cap = Bg.R | Bg.G << 8 | Bg.B << 16; DwmSetWindowAttribute(Handle, 35 /*CAPTION_COLOR*/, ref cap, 4);
            int txt = Ink.R | Ink.G << 8 | Ink.B << 16; DwmSetWindowAttribute(Handle, 36 /*TEXT_COLOR*/, ref txt, 4);
            int brd = Line.R | Line.G << 8 | Line.B << 16; DwmSetWindowAttribute(Handle, 34 /*BORDER_COLOR*/, ref brd, 4);
        }
        catch { }
        Invalidate();
    }

    // Re-measure (the window grows/shrinks to fit the tab) and repaint.
    public void Reflow()
    {
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b))
        {
            int h = (int)Math.Ceiling(Build(g, false));
            if (ClientSize.Height != h) ClientSize = new Size(ClientSize.Width, h);
        }
        Invalidate();
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Reflow(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Build(g, true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = false; foreach (var h in hits) if (h.R.Contains(e.Location)) { over = true; break; }
        Cursor = over ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        for (int i = hits.Count - 1; i >= 0; i--)
            if (hits[i].R.Contains(e.Location)) { hits[i].Click(); Reflow(); return; }
    }

    bool Hover(RectangleF r) { return r.Contains(PointToClient(Cursor.Position)); }

    void Add(RectangleF r, Action a, bool draw) { if (draw) hits.Add(new Hit { R = r, Click = a }); }

    // ------------------------------------------------------------------ layout (draw == false only measures)
    float Build(Graphics g, bool draw)
    {
        if (draw) hits.Clear();
        float x = S(18), width = ClientSize.Width - 2 * x, y = S(14);

        // tabs: a segmented control
        var bar = new RectangleF(x, y, width, S(32));
        if (draw) using (var p = Round(bar, S(8))) using (var b = new SolidBrush(Raised)) g.FillPath(b, p);
        float tw = width / Tabs.Length;
        for (int i = 0; i < Tabs.Length; i++)
        {
            var r = new RectangleF(x + i * tw + S(3), y + S(3), tw - S(6), S(26)); int t = i;
            if (draw)
            {
                if (tab == i) using (var p = Round(r, S(6))) using (var b = new SolidBrush(Color.FromArgb(46, Acc))) g.FillPath(b, p);
                else if (Hover(r)) using (var p = Round(r, S(6))) using (var b = new SolidBrush(Color.FromArgb(10, 255, 255, 255))) g.FillPath(b, p);
                Center(g, Lang.T(Tabs[i]), tab == i ? fBold : fText, tab == i ? Ink : Dim, r);
            }
            Add(r, () => { tab = t; colorKey = null; }, draw);
        }
        y += S(32) + S(16);

        if (tab == 0) y = SectionsTab(g, draw, x, width, y);
        else if (tab == 1) y = MiniTab(g, draw, x, width, y);
        else y = AppearanceTab(g, draw, x, width, y);
        return y + S(16);
    }

    float SectionsTab(Graphics g, bool draw, float x, float width, float y)
    {
        y = Note(g, draw, x, width, y, Lang.T("settings.sections.note"));
        var ids = new List<string>(w.Sections());
        for (int i = 0; i < ids.Count; i++)
        {
            string id = ids[i];
            if (i > 0) { if (draw) using (var p = new Pen(Color.FromArgb(18, 255, 255, 255))) g.DrawLine(p, x, y, x + width, y); y += S(6); }
            float rowH = S(30), cy = y + rowH / 2;

            // on/off
            bool on = w.IsShown(id, false);
            var cb = new RectangleF(x, cy - S(9), S(18), S(18));
            if (draw) Check(g, cb, on);
            Add(Inflate(cb, S(4)), () => w.SetShown(id, false, !on), draw);

            // colour dot(s)
            float cx = x + S(30);
            foreach (var ck in w.ColorKeys(id))
            {
                string key = ck[0];
                var dot = new RectangleF(cx, cy - S(8), S(16), S(16));
                if (draw)
                {
                    using (var b = new SolidBrush(w.ColorOf(key))) g.FillEllipse(b, dot);
                    if (colorKey == key || Hover(Inflate(dot, S(3)))) using (var p = new Pen(Ink, 1.5f * d)) g.DrawEllipse(p, Inflate(dot, S(3)));
                }
                Add(Inflate(dot, S(3)), () => colorKey = colorKey == key ? null : key, draw);
                cx += S(22);
            }

            // name
            if (draw) Txt(g, w.Title(id), fBold, on ? Ink : Dim, cx + S(6), cy - S(10));

            // move up / down
            var up = new RectangleF(x + width - S(56), cy - S(12), S(24), S(24));
            var dn = new RectangleF(x + width - S(28), cy - S(12), S(24), S(24));
            bool canUp = i > 0, canDn = i < ids.Count - 1;
            if (draw) { Arrow(g, up, true, canUp); Arrow(g, dn, false, canDn); }
            if (canUp) Add(up, () => w.MoveSection(id, -1), draw);
            if (canDn) Add(dn, () => w.MoveSection(id, +1), draw);
            y += rowH;

            // colour strip
            foreach (var ck in w.ColorKeys(id))
                if (colorKey == ck[0]) y = Strip(g, draw, x + S(30), width - S(30), y, ck[0], ck[1]);

            // details as toggle chips
            float chipX = x + S(30), chipY = y + S(2);
            foreach (var det in w.DetailsOf(id))
            {
                string dk = det[0]; bool v = w.Opt(id, dk);
                float cw = g.MeasureString(det[1], fSmall).Width + S(16);
                if (chipX + cw > x + width) { chipX = x + S(30); chipY += S(28); }
                var r = new RectangleF(chipX, chipY, cw, S(22));
                if (draw) Chip(g, r, det[1], v, on);
                Add(r, () => w.SetOpt(id, dk, !v), draw);
                chipX += cw + S(6);
            }
            y = chipY + S(22) + S(10);
        }
        return y;
    }

    float MiniTab(Graphics g, bool draw, float x, float width, float y)
    {
        y = Note(g, draw, x, width, y, Lang.T("settings.mini.note"));
        foreach (var id in w.Sections())
        {
            string sid = id; bool on = w.IsShown(id, true);
            float cy = y + S(15);
            var cb = new RectangleF(x, cy - S(9), S(18), S(18));
            if (draw)
            {
                Check(g, cb, on);
                using (var b = new SolidBrush(w.ColorOf(id))) g.FillEllipse(b, x + S(30), cy - S(5), S(10), S(10));
                Txt(g, w.Title(id), fText, on ? Ink : Dim, x + S(48), cy - S(10));
            }
            var row = new RectangleF(x - S(4), y, width + S(8), S(30));
            if (draw && Hover(row)) using (var p = Round(row, S(6))) using (var b = new SolidBrush(Color.FromArgb(8, 255, 255, 255))) g.FillPath(b, p);
            Add(row, () => w.SetShown(sid, true, !on), draw);
            y += S(32);
        }
        return y;
    }

    float AppearanceTab(Graphics g, bool draw, float x, float width, float y)
    {
        y = Choice(g, draw, x, width, y, Lang.T("settings.layout"), new[] { Lang.T("settings.layout.normal"), Lang.T("settings.layout.compact") }, w.Compact ? 1 : 0, i => w.Compact = i == 1);
        y = Choice(g, draw, x, width, y, Lang.T("menu.background"), new[] { Lang.T("menu.bg.dark"), Lang.T("menu.bg.accent") }, w.AccentBackground ? 1 : 0, i => w.AccentBackground = i == 1);
        int[] sizes = { 75, 90, 100, 115, 130, 150, 175 };
        y = Choice(g, draw, x, width, y, Lang.T("menu.size"), Array.ConvertAll(sizes, v => v + "%"), Array.IndexOf(sizes, w.SizePercent), i => w.SizePercent = sizes[i]);
        int[] ops = { 100, 90, 80, 70, 60, 50, 40, 30 };
        y = Choice(g, draw, x, width, y, Lang.T("menu.opacity"), Array.ConvertAll(ops, v => v + "%"), Array.IndexOf(ops, w.OpacityPercent), i => w.OpacityPercent = ops[i]);

        // language: a field that opens the list of available languages
        if (draw) Txt(g, Upper(Lang.T("settings.language")), fHead, Dim, x, y);
        y += S(20);
        string cur = w.LanguageChoice == "" ? Lang.F("settings.language.auto", Lang.NativeName(Lang.AutoCode())) : Lang.NativeName(w.LanguageChoice);
        var lr = new RectangleF(x, y, width, S(32));
        if (draw)
        {
            using (var p = Round(lr, S(8)))
            {
                using (var b = new SolidBrush(Hover(lr) ? Color.FromArgb(22, 255, 255, 255) : Raised)) g.FillPath(b, p);
                using (var pen = new Pen(Line)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, cur, fText, Rectangle.Round(new RectangleF(lr.X + S(12), lr.Y, lr.Width - S(48), lr.Height)), Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            Arrow(g, new RectangleF(lr.Right - S(32), lr.Y + S(4), S(24), S(24)), false, true);
        }
        Add(lr, () => w.ShowLanguageMenu(this, new Point((int)lr.Left, (int)lr.Bottom + S(4))), draw);
        y += S(32) + S(8);
        y = Note(g, draw, x, width, y, Lang.T("settings.language.note"));

        y += S(4);
        string label = Lang.T("settings.reset");
        var r = new RectangleF(x, y, g.MeasureString(label, fText).Width + S(28), S(30));
        if (draw)
        {
            using (var p = Round(r, S(6)))
            {
                using (var b = new SolidBrush(Hover(r) ? Color.FromArgb(22, 255, 255, 255) : Raised)) g.FillPath(b, p);
                using (var pen = new Pen(Line)) g.DrawPath(pen, p);
            }
            Center(g, label, fText, Ink, r);
        }
        Add(r, () => { colorKey = null; w.ResetLayout(); }, draw);
        return y + S(30);
    }

    // ------------------------------------------------------------------ pieces
    float Note(Graphics g, bool draw, float x, float width, float y, string text)
    {
        var sz = g.MeasureString(text, fSmall, (int)width);
        if (draw) using (var b = new SolidBrush(Dim)) g.DrawString(text, fSmall, b, new RectangleF(x, y, width, sz.Height + 2));
        return y + sz.Height + S(12);
    }

    float Choice(Graphics g, bool draw, float x, float width, float y, string label, string[] options, int sel, Action<int> pick)
    {
        if (draw) Txt(g, Upper(label), fHead, Dim, x, y);
        y += S(20);
        var bar = new RectangleF(x, y, width, S(32));
        if (draw) using (var p = Round(bar, S(8))) using (var b = new SolidBrush(Raised)) g.FillPath(b, p);
        float sw = width / options.Length;
        for (int i = 0; i < options.Length; i++)
        {
            var r = new RectangleF(x + i * sw + S(3), y + S(3), sw - S(6), S(26)); int k = i;
            if (draw)
            {
                if (sel == i) using (var p = Round(r, S(6))) using (var b = new SolidBrush(Color.FromArgb(46, Acc))) g.FillPath(b, p);
                else if (Hover(r)) using (var p = Round(r, S(6))) using (var b = new SolidBrush(Color.FromArgb(10, 255, 255, 255))) g.FillPath(b, p);
                Center(g, options[i], sel == i ? fBold : fText, sel == i ? Ink : Dim, r);
            }
            Add(r, () => pick(k), draw);
        }
        return y + S(32) + S(16);
    }

    float Strip(Graphics g, bool draw, float x, float width, float y, string key, string name)
    {
        float sz = S(20), gap = S(7);
        if (draw && name != null) Txt(g, name, fSmall, Dim, x, y + S(1));
        float sx = x + (name != null ? g.MeasureString(name, fSmall).Width + S(8) : 0), sy = y;
        Color cur = w.ColorOf(key);
        foreach (var c in Swatches)
        {
            if (sx + sz > x + width) { sx = x + (name != null ? g.MeasureString(name, fSmall).Width + S(8) : 0); sy += sz + gap; }
            var r = new RectangleF(sx, sy, sz, sz); Color col = c;
            if (draw)
            {
                using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
                if (c.ToArgb() == cur.ToArgb()) using (var p = new Pen(Ink, 2 * d)) g.DrawEllipse(p, Inflate(r, S(3)));
                else if (Hover(r)) using (var p = new Pen(Color.FromArgb(120, Ink), 1.5f * d)) g.DrawEllipse(p, Inflate(r, S(2)));
            }
            Add(r, () => w.SetColor(key, col), draw);
            sx += sz + gap;
        }
        return sy + sz + S(8);
    }

    void Check(Graphics g, RectangleF r, bool on)
    {
        using (var p = Round(r, S(5)))
        {
            if (on) using (var b = new SolidBrush(Acc)) g.FillPath(b, p);
            else using (var pen = new Pen(Hover(Inflate(r, S(4))) ? Ink : Line, 1.5f * d)) g.DrawPath(pen, p);
        }
        if (!on) return;
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, u = 3.2f * d;
        using (var pen = new Pen(Bg, 2f * d) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, new[] { new PointF(cx - u * 1.2f, cy), new PointF(cx - u * 0.3f, cy + u * 0.9f), new PointF(cx + u * 1.3f, cy - u * 1.0f) });
    }

    void Chip(Graphics g, RectangleF r, string text, bool on, bool sectionOn)
    {
        using (var p = Round(r, r.Height / 2))
        {
            if (on) using (var b = new SolidBrush(Color.FromArgb(sectionOn ? 46 : 22, Acc))) g.FillPath(b, p);
            using (var pen = new Pen(on ? Color.FromArgb(sectionOn ? 140 : 70, Acc) : (Hover(r) ? Dim : Line))) g.DrawPath(pen, p);
        }
        Center(g, text, fSmall, on && sectionOn ? Ink : Dim, r);
    }

    void Arrow(Graphics g, RectangleF r, bool up, bool enabled)
    {
        if (enabled && Hover(r)) using (var p = Round(r, S(5))) using (var b = new SolidBrush(Color.FromArgb(16, 255, 255, 255))) g.FillPath(b, p);
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, a = 4 * d;
        using (var pen = new Pen(enabled ? Ink : Color.FromArgb(50, Ink), 1.6f * d) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, up ? new[] { new PointF(cx - a, cy + a / 2), new PointF(cx, cy - a / 2), new PointF(cx + a, cy + a / 2) }
                               : new[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
    }

    static RectangleF Inflate(RectangleF r, float v) { r.Inflate(v, v); return r; }

    static void Txt(Graphics g, string t, Font f, Color c, float x, float y)
    {
        TextRenderer.DrawText(g, t, f, new Point((int)Math.Round(x), (int)Math.Round(y)), c, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    static void Center(Graphics g, string t, Font f, Color c, RectangleF r)
    {
        TextRenderer.DrawText(g, t, f, Rectangle.Round(r), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    static GraphicsPath Round(RectangleF r, float rad) { return MenuLook.Round(r, rad); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { fText.Dispose(); fBold.Dispose(); fSmall.Dispose(); fHead.Dispose(); }
        base.Dispose(disposing);
    }
}
