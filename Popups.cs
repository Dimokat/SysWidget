// SysWidget - small windows in the menu's look: the busiest-apps popup, "How to use", and a text prompt.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Frame
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static int Rgb(Color c) { return c.R | c.G << 8 | c.B << 16; }

    // Dark title bar in the menu colours (Windows 11; Windows 10 gets the dark bar only), or rounded corners for a popup.
    public static void Dark(Form f, bool caption)
    {
        try
        {
            int on = 1; DwmSetWindowAttribute(f.Handle, 20 /*USE_IMMERSIVE_DARK_MODE*/, ref on, 4);
            if (caption)
            {
                int cap = Rgb(MenuLook.Bg); DwmSetWindowAttribute(f.Handle, 35 /*CAPTION_COLOR*/, ref cap, 4);
                int txt = Rgb(MenuLook.Text); DwmSetWindowAttribute(f.Handle, 36 /*TEXT_COLOR*/, ref txt, 4);
            }
            else { int round = 2; DwmSetWindowAttribute(f.Handle, 33 /*WINDOW_CORNER_PREFERENCE*/, ref round, 4); }
            int brd = Rgb(MenuLook.Border); DwmSetWindowAttribute(f.Handle, 34 /*BORDER_COLOR*/, ref brd, 4);
        }
        catch { }
    }

    public static Button Btn(string text, bool main, float d)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, ForeColor = MenuLook.Text, UseVisualStyleBackColor = false,
            BackColor = main ? Color.FromArgb(0x1F, 0x6F, 0x9A) : Color.FromArgb(40, 42, 48),
            Height = (int)Math.Round(30 * d)
        };
        b.FlatAppearance.BorderColor = MenuLook.Border;
        b.Width = Math.Max((int)Math.Round(86 * d), TextRenderer.MeasureText(text, b.Font).Width + (int)Math.Round(28 * d));
        return b;
    }
}

// The apps using the most CPU or memory, shown next to the widget while you point at that section.
sealed class AppsPopup : Form
{
    readonly float d; readonly Font fHead, fText;
    string title = ""; Color accent;
    List<string[]> rows = new List<string[]>(); List<float> shares = new List<float>();

    public AppsPopup(float dpi)
    {
        d = dpi;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        fHead = Lang.MakeFont(8.25f, true); fText = Lang.MakeFont(9f, false);
        Size = new Size(S(240), S(60));
    }

    int S(float v) { return (int)Math.Round(v * d); }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x8000000 /*NOACTIVATE*/; return cp; }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Frame.Dark(this, false); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84 /*WM_NCHITTEST*/) { m.Result = (IntPtr)(-1) /*HTTRANSPARENT*/; return; }
        base.WndProc(ref m);
    }

    // rows: { name, value }, shares: 0..1 for the little bars. No rows yet = "…".
    public void SetData(string t, Color c, List<string[]> r, List<float> sh)
    {
        title = t; accent = c; rows = r; shares = sh;
        int h = S(10) + S(22) + Math.Max(1, rows.Count) * S(27) + S(6);
        if (Height != h) Height = h;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(MenuLook.Bg); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        if (!MenuLook.DwmCorners) using (var p = new Pen(MenuLook.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        int x = S(12), w = Width - 2 * x, y = S(10);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, title.ToUpper(CultureInfo.CurrentUICulture), fHead, new Point(x, y), MenuLook.Dim, flags);
        y += S(22);
        if (rows.Count == 0) { TextRenderer.DrawText(g, "…", fText, new Point(x, y), MenuLook.Dim, flags); return; }
        for (int i = 0; i < rows.Count; i++)
        {
            int vw = TextRenderer.MeasureText(g, rows[i][1], fText, Size.Empty, flags).Width;
            TextRenderer.DrawText(g, rows[i][0], fText, new Rectangle(x, y, w - vw - S(10), S(18)), MenuLook.Text, flags | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, rows[i][1], fText, new Point(x + w - vw, y), MenuLook.Dim, flags);
            float by = y + S(19), share = i < shares.Count ? Math.Max(0, Math.Min(1, shares[i])) : 0;
            using (var b = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillRectangle(b, x, by, w, S(3));
            using (var b = new SolidBrush(accent)) g.FillRectangle(b, x, by, Math.Max(S(2), w * share), S(3));
            y += S(27);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { fHead.Dispose(); fText.Dispose(); }
        base.Dispose(disposing);
    }
}

// "How to use": every way to interact with the widget, on one page.
sealed class TipsWindow : Form
{
    readonly float d; readonly Font fText, fBold; readonly Button ok;
    static readonly string[] TipKeys = { "drag", "dbl", "right", "hover", "island", "ctrl", "tray", "keys", "media" };

    public TipsWindow(float dpi)
    {
        d = dpi;
        Text = Lang.T("tips.title");
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = true; StartPosition = FormStartPosition.Manual; KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = MenuLook.Bg; ForeColor = MenuLook.Text;
        fText = Lang.MakeFont(9.75f, false); fBold = Lang.MakeFont(9.75f, true);
        Font = fText;
        ClientSize = new Size(S(540), S(200));
        ok = Frame.Btn(Lang.T("tips.ok"), true, d); ok.Click += delegate { Close(); };
        Controls.Add(ok); AcceptButton = ok;
        KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b))
        {
            int h = (int)Math.Ceiling(Content(g, false));
            ClientSize = new Size(ClientSize.Width, h + S(14) + ok.Height + S(18));
        }
        ok.Location = new Point(ClientSize.Width - S(20) - ok.Width, ClientSize.Height - S(18) - ok.Height);
    }

    int S(float v) { return (int)Math.Round(v * d); }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Frame.Dark(this, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(MenuLook.Bg); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Content(g, true);
    }

    float Content(Graphics g, bool draw)
    {
        int x = S(20), keyW = S(180), tx = x + keyW + S(14), tw = ClientSize.Width - tx - S(20);
        float y = S(18);
        foreach (var k in TipKeys)
        {
            string key = Lang.T("tip." + k + ".k"), text = Lang.T("tip." + k);
            int th = TextRenderer.MeasureText(g, text, fText, new Size(tw, 0), TextFormatFlags.WordBreak).Height;
            int cw = Math.Min(keyW, TextRenderer.MeasureText(g, key, fBold).Width + S(18));
            if (draw)
            {
                var r = new RectangleF(x, y, cw, S(26));
                using (var p = MenuLook.Round(r, S(6)))
                {
                    using (var b = new SolidBrush(Color.FromArgb(16, 255, 255, 255))) g.FillPath(b, p);
                    using (var pen = new Pen(MenuLook.Border)) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, key, fBold, Rectangle.Round(r), MenuLook.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, text, fText, new Rectangle(tx, (int)y + S(4), tw, th + 2), MenuLook.Dim, TextFormatFlags.WordBreak);
            }
            y += Math.Max(S(26), th + S(6)) + S(10);
        }
        return y;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { fText.Dispose(); fBold.Dispose(); }
        base.Dispose(disposing);
    }
}

// A one-line question (e.g. a custom ping server). Returns null when cancelled.
static class InputBox
{
    public static string Ask(IWin32Window owner, string title, string prompt, string value, float d)
    {
        Func<float, int> S = v => (int)Math.Round(v * d);
        using (var f = new Form())
        {
            f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
            f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.CenterParent; f.AutoScaleMode = AutoScaleMode.None;
            f.BackColor = MenuLook.Bg; f.ForeColor = MenuLook.Text; f.Font = Lang.MakeFont(9.75f, false);
            f.ClientSize = new Size(S(380), S(124));
            var lbl = new Label { Text = prompt, ForeColor = MenuLook.Dim, Location = new Point(S(14), S(14)), Size = new Size(S(352), S(22)) };
            var tb = new TextBox
            {
                Text = value ?? "", Location = new Point(S(14), S(40)), Width = S(352), BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(40, 42, 48), ForeColor = MenuLook.Text
            };
            var okB = Frame.Btn(Lang.T("button.ok"), true, d); okB.DialogResult = DialogResult.OK;
            var cancel = Frame.Btn(Lang.T("setup.btn.cancel"), false, d); cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(f.ClientSize.Width - S(14) - cancel.Width, f.ClientSize.Height - S(14) - cancel.Height);
            okB.Location = new Point(cancel.Left - S(8) - okB.Width, cancel.Top);
            f.Controls.AddRange(new Control[] { lbl, tb, okB, cancel });
            f.AcceptButton = okB; f.CancelButton = cancel;
            f.HandleCreated += delegate { Frame.Dark(f, true); };
            return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
        }
    }
}
