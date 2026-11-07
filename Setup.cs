// SysWidget Setup - installer/uninstaller. Author: Dimokat.
// Ships next to SysWidget.exe (in the release zip) and copies it into Program Files.
// Build (see build.bat): needs SysWidget.ico, which is embedded for the window logo.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("SysWidget Setup")]
[assembly: AssemblyDescription("Installer for SysWidget")]
[assembly: AssemblyProduct("SysWidget")]
[assembly: AssemblyCompany("Dimokat")]
[assembly: AssemblyCopyright("© 2026 Dimokat")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Setup
{
    public const string Version = "1.0.0", Author = "Dimokat";
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SysWidget";
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SysWidget");
    static string Exe { get { return Path.Combine(Dir, "SysWidget.exe"); } }
    static string Uninst { get { return Path.Combine(Dir, "Uninstall.exe"); } }
    static string StartLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "SysWidget.lnk"); } }
    static string DeskLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "SysWidget.lnk"); } }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Lang.Load(""); // the Windows display language
        bool uninstall = args.Length > 0 && args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase);
        Application.Run(new SetupForm(uninstall));
    }

    // SysWidget.exe is shipped next to Setup.exe, not embedded in it.
    public static string Source { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "SysWidget.exe"); } }
    public static bool HasSource { get { return File.Exists(Source); } }

    public static bool Installed
    {
        get { using (var k = Registry.LocalMachine.OpenSubKey(UninstKey)) return k != null; }
    }

    static int Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(15000); return p.ExitCode; }
    }

    static void StopWidget()
    {
        foreach (var p in Process.GetProcessesByName("SysWidget"))
            try { p.Kill(); p.WaitForExit(3000); } catch { }
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    public static void Install(bool startup, bool startMenu, bool desktop, Action<string> status)
    {
        if (!HasSource) throw new Exception(Lang.T("setup.err.nosource"));
        status(Lang.T("setup.st.stopping")); StopWidget();

        status(Lang.T("setup.st.copying"));
        Directory.CreateDirectory(Dir);
        if (!Source.Equals(Exe, StringComparison.OrdinalIgnoreCase)) File.Copy(Source, Exe, true);
        string self = Application.ExecutablePath;
        if (!self.Equals(Uninst, StringComparison.OrdinalIgnoreCase)) File.Copy(self, Uninst, true);

        status(Lang.T("setup.st.startup"));
        if (startup) CreateTask(); else Run("schtasks.exe", "/Delete /TN \"SysWidget\" /F");

        status(Lang.T("setup.st.shortcuts"));
        if (startMenu) Shortcut(StartLnk); else TryDelete(StartLnk);
        if (desktop) Shortcut(DeskLnk); else TryDelete(DeskLnk);

        status(Lang.T("setup.st.registering"));
        using (var k = Registry.LocalMachine.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayName", "SysWidget");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", Author);
            k.SetValue("DisplayIcon", Exe);
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", "\"" + Uninst + "\" /uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)((new FileInfo(Exe).Length + new FileInfo(Uninst).Length) / 1024), RegistryValueKind.DWord);
        }
    }

    public static void Launch(bool viaTask)
    {
        if (viaTask && Run("schtasks.exe", "/Run /TN \"SysWidget\"") == 0) return;
        try { Process.Start("explorer.exe", "\"" + Exe + "\""); } catch { } // explorer starts it without admin rights
    }

    // Logon task with highest privileges: starts the widget at sign-in with admin rights (FPS needs them), no UAC prompt.
    static void CreateTask()
    {
        Func<string, string> esc = System.Security.SecurityElement.Escape;
        string user = esc(Environment.UserDomainName + "\\" + Environment.UserName);
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<RegistrationInfo><Author>Dimokat</Author><Description>Starts SysWidget at sign-in.</Description></RegistrationInfo>" +
            "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>" +
            "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
            "<Actions Context=\"Author\"><Exec><Command>" + esc(Exe) + "</Command><Arguments>--task</Arguments><WorkingDirectory>" + esc(Dir) +
            "</WorkingDirectory></Exec></Actions></Task>";
        string tmp = Path.Combine(Path.GetTempPath(), "SysWidget.task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        int rc = Run("schtasks.exe", "/Create /TN \"SysWidget\" /XML \"" + tmp + "\" /F");
        TryDelete(tmp);
        if (rc != 0) throw new Exception(Lang.F("setup.err.task", rc));
    }

    static void Shortcut(string path)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        object sh = Activator.CreateInstance(t);
        object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { path });
        var lt = lnk.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Exe });
        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Dir });
        lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { Lang.T("setup.shortcut") });
        lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { Exe + ",0" });
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }

    public static void Uninstall(bool removeSettings)
    {
        StopWidget();
        Run("schtasks.exe", "/Delete /TN \"SysWidget\" /F");
        TryDelete(StartLnk); TryDelete(DeskLnk);
        try { Registry.LocalMachine.DeleteSubKeyTree(UninstKey, false); } catch { }
        if (removeSettings)
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysWidget"), true); } catch { }
        TryDelete(Exe);
        // Uninstall.exe may be the program running right now: remove the folder a moment after it exits.
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + Dir + "\"") { CreateNoWindow = true, UseShellExecute = false });
    }
}

sealed class SetupForm : Form
{
    static readonly Color Bg = Color.FromArgb(24, 25, 29), Fg = Color.FromArgb(232, 232, 235), Dim = Color.FromArgb(150, 152, 160),
        Accent = Color.FromArgb(0x4F, 0xC3, 0xF7), BtnBg = Color.FromArgb(40, 42, 48), BtnMain = Color.FromArgb(0x1F, 0x6F, 0x9A);
    readonly bool uninstallMode;
    readonly CheckBox cbStartup, cbMenu, cbDesk, cbLaunch, cbSettings;
    readonly Button btnMain, btnAlt, btnCancel;
    readonly Label lblStatus;

    public SetupForm(bool uninstall)
    {
        uninstallMode = uninstall;
        Text = Lang.T(uninstall ? "setup.title.uninstall" : "setup.title");
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 300);
        BackColor = Bg; ForeColor = Fg; Font = Lang.MakeFont(9.5f, false);

        Image logo = null;
        try
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SysWidget.ico"))
            using (var ic = new Icon(s, 48, 48)) logo = ic.ToBitmap();
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SysWidget.ico")) Icon = new Icon(s);
        }
        catch { }
        Controls.Add(new PictureBox { Image = logo, Location = new Point(24, 22), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom });
        Controls.Add(new Label { Text = "SysWidget", Font = Lang.MakeFont(16f, true), Location = new Point(84, 17), AutoSize = true });
        Controls.Add(new Label { Text = Lang.F("setup.version", Setup.Version, Setup.Author), ForeColor = Dim, Location = new Point(87, 50), AutoSize = true });
        string desc = uninstall ? Lang.T("setup.desc.uninstall") : Lang.F("setup.desc", Setup.Dir);
        int descH = TextRenderer.MeasureText(desc, Font, new Size(412, 0), TextFormatFlags.WordBreak).Height + 4;
        var lblDesc = new Label { Text = desc, ForeColor = Dim, Location = new Point(24, 88), Size = new Size(412, descH) };
        Controls.Add(lblDesc);

        int y = lblDesc.Bottom + 12;
        if (uninstall) cbSettings = Check(Lang.T("setup.opt.settings"), false, ref y);
        else
        {
            cbStartup = Check(Lang.T("setup.opt.startup"), true, ref y);
            cbMenu = Check(Lang.T("setup.opt.startmenu"), true, ref y);
            cbDesk = Check(Lang.T("setup.opt.desktop"), false, ref y);
            cbLaunch = Check(Lang.T("setup.opt.launch"), true, ref y);
        }
        ClientSize = new Size(460, y + 92);

        lblStatus = new Label { ForeColor = Accent, Location = new Point(24, ClientSize.Height - 78), Size = new Size(412, 22) };
        Controls.Add(lblStatus);
        // Buttons from the right edge, each as wide as its text needs.
        int right = 436;
        btnCancel = Btn(Lang.T("setup.btn.cancel"), ref right, BtnBg); btnCancel.Click += delegate { Close(); };
        btnMain = Btn(Lang.T(uninstall ? "setup.btn.uninstall" : Setup.Installed ? "setup.btn.update" : "setup.btn.install"), ref right, BtnMain);
        btnMain.Click += delegate { Go(); };
        if (!uninstall && Setup.Installed)
        {
            btnAlt = Btn(Lang.T("setup.btn.uninstall"), ref right, BtnBg);
            btnAlt.Click += delegate { using (var f = new SetupForm(true)) f.ShowDialog(this); if (!Setup.Installed) Close(); };
        }
        AcceptButton = btnMain; CancelButton = btnCancel;
        if (!uninstall && !Setup.HasSource)
        {
            lblStatus.ForeColor = Color.FromArgb(0xFF, 0xA7, 0x26);
            lblStatus.Text = Lang.T("setup.extract");
            btnMain.Enabled = false;
        }
    }

    CheckBox Check(string text, bool on, ref int y)
    {
        var c = new CheckBox { Text = text, Checked = on, Location = new Point(26, y), AutoSize = true, ForeColor = Fg };
        Controls.Add(c); y += 27; return c;
    }

    Button Btn(string text, ref int right, Color bg)
    {
        int w = Math.Max(86, TextRenderer.MeasureText(text, Font).Width + 28);
        right -= w;
        var b = new Button { Text = text, Location = new Point(right, ClientSize.Height - 48), Size = new Size(w, 30), FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = Fg };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 72, 80);
        Controls.Add(b); right -= 8; return b;
    }

    void Status(string s) { lblStatus.Text = s; lblStatus.Refresh(); }

    void Go()
    {
        btnMain.Enabled = false; btnCancel.Enabled = false; if (btnAlt != null) btnAlt.Enabled = false;
        Cursor = Cursors.WaitCursor;
        try
        {
            if (uninstallMode)
            {
                Status(Lang.T("setup.st.removing"));
                Setup.Uninstall(cbSettings.Checked);
                Status(Lang.T("setup.st.removed"));
            }
            else
            {
                Setup.Install(cbStartup.Checked, cbMenu.Checked, cbDesk.Checked, Status);
                if (cbLaunch.Checked) Setup.Launch(cbStartup.Checked);
                Status(Lang.T("setup.st.done"));
            }
            btnCancel.Text = Lang.T("setup.btn.close");
        }
        catch (Exception ex)
        {
            Status(Lang.T("setup.st.failed"));
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            btnMain.Enabled = true;
        }
        finally { Cursor = Cursors.Default; btnCancel.Enabled = true; }
    }
}
