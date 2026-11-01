// SysWidget Setup - single-file installer/uninstaller. Author: Dimokat.
// Build (see build.bat): needs SysWidget.exe and SysWidget.ico next to it; they are embedded as resources.
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
        bool uninstall = args.Length > 0 && args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase);
        Application.Run(new SetupForm(uninstall));
    }

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
        status("Stopping SysWidget..."); StopWidget();

        status("Copying files...");
        Directory.CreateDirectory(Dir);
        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("SysWidget.exe"))
        using (var f = File.Create(Exe)) res.CopyTo(f);
        string self = Application.ExecutablePath;
        if (!self.Equals(Uninst, StringComparison.OrdinalIgnoreCase)) File.Copy(self, Uninst, true);

        status("Setting up startup...");
        if (startup) CreateTask(); else Run("schtasks.exe", "/Delete /TN \"SysWidget\" /F");

        status("Creating shortcuts...");
        if (startMenu) Shortcut(StartLnk); else TryDelete(StartLnk);
        if (desktop) Shortcut(DeskLnk); else TryDelete(DeskLnk);

        status("Registering in Apps & Features...");
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
        if (rc != 0) throw new Exception("Could not create the startup task (schtasks exit code " + rc + ").");
    }

    static void Shortcut(string path)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        object sh = Activator.CreateInstance(t);
        object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { path });
        var lt = lnk.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Exe });
        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Dir });
        lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "SysWidget - system monitor widget" });
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
        Text = uninstall ? "Uninstall SysWidget" : "SysWidget Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, uninstall ? 250 : 330);
        BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9.5f);

        Image logo = null;
        try
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SysWidget.ico"))
            using (var ic = new Icon(s, 48, 48)) logo = ic.ToBitmap();
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SysWidget.ico")) Icon = new Icon(s);
        }
        catch { }
        Controls.Add(new PictureBox { Image = logo, Location = new Point(24, 22), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom });
        Controls.Add(new Label { Text = "SysWidget", Font = new Font("Segoe UI Semibold", 16f), Location = new Point(84, 17), AutoSize = true });
        Controls.Add(new Label { Text = "v" + Setup.Version + "   ·   by " + Setup.Author, ForeColor = Dim, Location = new Point(87, 50), AutoSize = true });
        string desc = uninstall
            ? "This removes SysWidget, its shortcuts and its startup task from this PC."
            : "A tiny desktop widget with CPU, GPU, FPS, RAM, network and disk stats.\r\nInstalls to " + Setup.Dir;
        Controls.Add(new Label { Text = desc, ForeColor = Dim, Location = new Point(24, 88), Size = new Size(412, 40) });

        int y = 136;
        if (uninstall) cbSettings = Check("Also remove my settings", false, ref y);
        else
        {
            cbStartup = Check("Start with Windows (also needed for the FPS counter)", true, ref y);
            cbMenu = Check("Start menu shortcut", true, ref y);
            cbDesk = Check("Desktop shortcut", false, ref y);
            cbLaunch = Check("Launch SysWidget when done", true, ref y);
        }

        lblStatus = new Label { ForeColor = Accent, Location = new Point(24, ClientSize.Height - 78), Size = new Size(412, 22) };
        Controls.Add(lblStatus);
        btnCancel = Btn("Cancel", 352, BtnBg); btnCancel.Click += delegate { Close(); };
        btnMain = Btn(uninstall ? "Uninstall" : Setup.Installed ? "Update" : "Install", 258, BtnMain);
        btnMain.Click += delegate { Go(); };
        if (!uninstall && Setup.Installed)
        {
            btnAlt = Btn("Uninstall", 164, BtnBg);
            btnAlt.Click += delegate { using (var f = new SetupForm(true)) f.ShowDialog(this); if (!Setup.Installed) Close(); };
        }
        AcceptButton = btnMain; CancelButton = btnCancel;
    }

    CheckBox Check(string text, bool on, ref int y)
    {
        var c = new CheckBox { Text = text, Checked = on, Location = new Point(26, y), AutoSize = true, ForeColor = Fg };
        Controls.Add(c); y += 27; return c;
    }

    Button Btn(string text, int x, Color bg)
    {
        var b = new Button { Text = text, Location = new Point(x, ClientSize.Height - 48), Size = new Size(86, 30), FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = Fg };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 72, 80);
        Controls.Add(b); return b;
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
                Status("Removing...");
                Setup.Uninstall(cbSettings.Checked);
                Status("SysWidget was removed.");
            }
            else
            {
                Setup.Install(cbStartup.Checked, cbMenu.Checked, cbDesk.Checked, Status);
                if (cbLaunch.Checked) Setup.Launch(cbStartup.Checked);
                Status("Done! SysWidget is installed.");
            }
            btnCancel.Text = "Close";
        }
        catch (Exception ex)
        {
            Status("Something went wrong.");
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            btnMain.Enabled = true;
        }
        finally { Cursor = Cursors.Default; btnCancel.Enabled = true; }
    }
}
