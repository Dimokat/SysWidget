// SysWidget - translations, shared by the widget and the installer.
// English (lang/en.txt) is the source. Every other language is a lang/<code>.txt file with the same keys:
//   key = text        ({0}, {1}... are filled in by the program; \n is a line break; # starts a comment)
// Files are built into the exe. A file in a "lang" folder next to the exe overrides the built-in one, and the
// widget keeps translations downloaded from the GitHub repo (main branch) in %APPDATA%\SysWidget\lang.
// The language follows the Windows display language unless one is picked in Settings.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static class Lang
{
    static Dictionary<string, string> en = new Dictionary<string, string>(), cur = en;
    static string code = "en";

    /// <summary>Active language file, e.g. "uk", "pt-BR", "zh-Hans".</summary>
    public static string Code { get { return code; } }

    /// <summary>Numbers follow the Windows regional format (decimal comma or point), like Task Manager.</summary>
    public static readonly CultureInfo Num = CultureInfo.CurrentCulture;

    /// <summary>Where downloaded translations are kept (set by the widget; null in the installer).</summary>
    public static string CacheDir = null;

    static string LocalDir { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang"); } }

    public static string T(string key)
    {
        string v;
        if (cur.TryGetValue(key, out v) || en.TryGetValue(key, out v)) return v;
        return key;
    }

    public static string F(string key, params object[] args)
    {
        try { return string.Format(Num, T(key), args); }
        catch { try { string v; return en.TryGetValue(key, out v) ? string.Format(Num, v, args) : key; } catch { return key; } }
    }

    // ------------------------------------------------------------------ choosing and loading
    /// <summary>Loads the language: choice is a file code ("uk") or "" to follow Windows.</summary>
    public static void Load(string choice)
    {
        en = Parse(Builtin("en") ?? "", null);
        foreach (var c in Candidates(choice))
        {
            string text = Find(c);
            if (text == null) continue;
            cur = c == "en" ? en : Parse(text, en);
            code = c;
            return;
        }
        cur = en; code = "en";
    }

    /// <summary>Language files to try, best first: the exact culture, its script or parent, any regional variant.</summary>
    public static List<string> Candidates(string choice)
    {
        var l = new List<string>();
        if (!string.IsNullOrEmpty(choice)) l.Add(choice);
        CultureInfo ui = CultureInfo.CurrentUICulture;
        var names = new List<string>();
        for (var c = ui; c != null && c.Name != ""; c = c.Parent) names.Add(c.Name);
        foreach (var n in names)
        {
            l.Add(n);
            string lower = n.ToLowerInvariant();
            if (lower.StartsWith("zh"))
            {
                bool trad = lower.Contains("hant") || lower.EndsWith("-tw") || lower.EndsWith("-hk") || lower.EndsWith("-mo");
                l.Add(trad ? "zh-Hant" : "zh-Hans");
            }
        }
        if (names.Count > 0)
        {
            string neutral = names[names.Count - 1];
            foreach (var a in Available()) if (a.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase)) l.Add(a);
        }
        l.Add("en");
        var unique = new List<string>();
        foreach (var c in l) if (!unique.Exists(x => x.Equals(c, StringComparison.OrdinalIgnoreCase))) unique.Add(c);
        return unique;
    }

    /// <summary>The language Windows itself would pick for us ("" choice).</summary>
    public static string AutoCode()
    {
        foreach (var c in Candidates("")) if (Find(c) != null) return c;
        return "en";
    }

    // Override next to the exe, then a downloaded copy, then the built-in file.
    static string Find(string c)
    {
        string f = Match(LocalDir, c); if (f != null) return Read(f);
        if (CacheDir != null) { f = Match(CacheDir, c); if (f != null) return Read(f); }
        return Builtin(c);
    }

    static string Match(string dir, string c)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.GetFiles(dir, "*.txt"))
                if (Path.GetFileNameWithoutExtension(f).Equals(c, StringComparison.OrdinalIgnoreCase)) return f;
        }
        catch { }
        return null;
    }

    static string Read(string f) { try { return File.ReadAllText(f, Encoding.UTF8); } catch { return null; } }

    static string Builtin(string c)
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var n in asm.GetManifestResourceNames())
            if (n.Equals("lang." + c + ".txt", StringComparison.OrdinalIgnoreCase))
                using (var s = asm.GetManifestResourceStream(n)) using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        return null;
    }

    /// <summary>Every language that can be used right now (built in, downloaded or placed next to the exe).</summary>
    public static List<string> Available()
    {
        var l = new List<string>();
        Action<string> add = c => { if (!l.Exists(x => x.Equals(c, StringComparison.OrdinalIgnoreCase))) l.Add(c); };
        foreach (var n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            if (n.StartsWith("lang.", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) add(n.Substring(5, n.Length - 9));
        foreach (var dir in new[] { LocalDir, CacheDir })
            try { if (dir != null && Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.txt")) add(Path.GetFileNameWithoutExtension(f)); } catch { }
        return l;
    }

    /// <summary>The language's own name, e.g. "Українська", "Deutsch", "中文(简体)".</summary>
    public static string NativeName(string c)
    {
        try
        {
            string n = CultureInfo.GetCultureInfo(c).NativeName;
            return n.Length > 0 ? char.ToUpper(n[0], CultureInfo.GetCultureInfo(c)) + n.Substring(1) : c;
        }
        catch { return c; }
    }

    // ------------------------------------------------------------------ parsing (also guards downloaded files)
    static readonly Regex KeyRx = new Regex(@"^[a-z0-9_.]+$"), HoleRx = new Regex(@"\{(\d+)[^}]*\}");

    /// <summary>
    /// Reads "key = text" lines. With a reference (English), only known keys are kept, and a line is dropped
    /// when its {n} placeholders aren't among the English ones or it isn't a valid format string, so a broken
    /// translation can only fall back to English text, never break the program.
    /// </summary>
    public static Dictionary<string, string> Parse(string text, Dictionary<string, string> reference)
    {
        var d = new Dictionary<string, string>();
        if (text == null) return d;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('='); if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
            if (!KeyRx.IsMatch(key) || val.Length == 0 || val.Length > 400) continue;
            if (reference != null)
            {
                string orig; if (!reference.TryGetValue(key, out orig)) continue;
                if (!SafeFormat(val, Holes(orig))) continue;
            }
            d[key] = val;
        }
        return d;
    }

    /// <summary>True when a downloaded file is a usable translation (enough valid lines).</summary>
    public static bool LooksValid(string text)
    {
        var d = Parse(text, en);
        return d.Count >= Math.Min(20, en.Count / 3);
    }

    static HashSet<int> Holes(string s)
    {
        var h = new HashSet<int>();
        foreach (Match m in HoleRx.Matches(s)) h.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return h;
    }

    static bool SafeFormat(string val, HashSet<int> allowed)
    {
        foreach (int i in Holes(val)) if (!allowed.Contains(i)) return false;
        int n = 0; foreach (int i in allowed) n = Math.Max(n, i + 1);
        try { string.Format(CultureInfo.InvariantCulture, val, new object[Math.Max(n, 1)]); return true; }
        catch { return false; }
    }

    // ------------------------------------------------------------------ fonts
    /// <summary>Segoe UI, or the Windows UI font that covers Chinese, Japanese or Korean.</summary>
    public static string Family
    {
        get
        {
            string c = code.ToLowerInvariant();
            if (c.StartsWith("ja")) return "Yu Gothic UI";
            if (c.StartsWith("ko")) return "Malgun Gothic";
            if (c.StartsWith("zh")) return c.Contains("hant") ? "Microsoft JhengHei UI" : "Microsoft YaHei UI";
            return "Segoe UI";
        }
    }

    public static Font MakeFont(float size, bool semibold, GraphicsUnit unit = GraphicsUnit.Point)
    {
        string f = Family;
        if (f == "Segoe UI" || f == "Yu Gothic UI")
            return new Font(semibold ? f + " Semibold" : f, size, FontStyle.Regular, unit);
        return new Font(f, size, semibold ? FontStyle.Bold : FontStyle.Regular, unit);
    }
}
