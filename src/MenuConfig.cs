using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace RadialMenu
{
    public sealed class MenuEntry
    {
        public string Label = "";
        public string Icon = "";
        public string Action = "";
        public string Param = "";
        public string Args = "";
        public string Hint = "";
        public List<MenuEntry> Items = new List<MenuEntry>();

        public bool HasChildren { get { return Items.Count > 0; } }

        public int Depth
        {
            get
            {
                int d = 0;
                foreach (var c in Items) d = Math.Max(d, c.Depth);
                return d + 1;
            }
        }
    }

    public sealed class MenuSettings
    {
        public string BypassModifier = "Ctrl";
        public bool DisableInFullscreen = true;
        public double Scale = 1.0;
        public HashSet<string> ExcludedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExcludedWindowClasses = new HashSet<string>(StringComparer.Ordinal);
        public Color[] Accent = new[]
        {
            Color.FromRgb(0x22, 0xD3, 0xEE),
            Color.FromRgb(0x8B, 0x5C, 0xF6),
            Color.FromRgb(0xEC, 0x48, 0x99)
        };
    }

    public sealed class MenuConfig
    {
        public const int MaxLevels = 4;

        public MenuSettings Settings = new MenuSettings();
        public List<MenuEntry> Items = new List<MenuEntry>();

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadialMenu"); }
        }

        public static string FilePath { get { return Path.Combine(Folder, "config.json"); } }

        /// <summary>Number of rings needed to show the deepest submenu (capped).</summary>
        public int Levels
        {
            get
            {
                int d = 0;
                foreach (var i in Items) d = Math.Max(d, i.Depth);
                return Math.Max(1, Math.Min(MaxLevels, d));
            }
        }

        public static string DefaultJson()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("default-config.json"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        /// <summary>Loads the user config, writing the default one first if it does not exist.</summary>
        public static MenuConfig LoadOrCreate(out bool created)
        {
            created = false;
            if (!File.Exists(FilePath))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, DefaultJson(), new UTF8Encoding(false));
                created = true;
            }
            return Parse(File.ReadAllText(FilePath, Encoding.UTF8));
        }

        public static void ResetToDefault()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, DefaultJson(), new UTF8Encoding(false));
        }

        public static MenuConfig Parse(string json)
        {
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("Fișierul de configurare trebuie să fie un obiect JSON.");

            var cfg = new MenuConfig();
            var s = root.ContainsKey("settings") ? root["settings"] as Dictionary<string, object> : null;
            if (s != null)
            {
                cfg.Settings.BypassModifier = Str(s, "bypassModifier", cfg.Settings.BypassModifier);
                cfg.Settings.DisableInFullscreen = Bool(s, "disableInFullscreen", cfg.Settings.DisableInFullscreen);
                cfg.Settings.Scale = Math.Max(0.6, Math.Min(2.0, Num(s, "scale", 1.0)));
                foreach (var p in List(s, "excludedProcesses"))
                    cfg.Settings.ExcludedProcesses.Add(Path.GetFileNameWithoutExtension(Convert.ToString(p)));
                foreach (var c in List(s, "excludedWindowClasses"))
                    cfg.Settings.ExcludedWindowClasses.Add(Convert.ToString(c));
                var accent = List(s, "accentColors");
                if (accent.Count > 0)
                {
                    var colors = new List<Color>();
                    foreach (var a in accent) colors.Add((Color)ColorConverter.ConvertFromString(Convert.ToString(a)));
                    while (colors.Count < 3) colors.Add(colors[colors.Count - 1]);
                    cfg.Settings.Accent = colors.ToArray();
                }
            }

            foreach (var o in List(root, "items"))
            {
                var e = ParseEntry(o as Dictionary<string, object>);
                if (e != null) cfg.Items.Add(e);
            }
            if (cfg.Items.Count == 0) throw new FormatException("Meniul nu are niciun element (\"items\" este gol).");
            return cfg;
        }

        static MenuEntry ParseEntry(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var e = new MenuEntry
            {
                Label = Str(d, "label", ""),
                Icon = ParseIcon(Str(d, "icon", "")),
                Action = Str(d, "action", "").ToLowerInvariant(),
                Param = Str(d, "param", ""),
                Args = Str(d, "args", ""),
                Hint = Str(d, "hint", "")
            };
            foreach (var o in List(d, "items"))
            {
                var c = ParseEntry(o as Dictionary<string, object>);
                if (c != null) e.Items.Add(c);
            }
            if (e.Hint == "" && e.Action == "keys") e.Hint = e.Param;
            return e;
        }

        /// <summary>"E8C8" → the Segoe Fluent Icons glyph; anything else (an emoji, a letter) is used as is.</summary>
        static string ParseIcon(string icon)
        {
            if (Regex.IsMatch(icon, "^[0-9A-Fa-f]{4,5}$"))
                return char.ConvertFromUtf32(Convert.ToInt32(icon, 16));
            return icon;
        }

        static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : def;
        }

        static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool ? (bool)v : def;
        }

        static double Num(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        static IList List(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is IList ? (IList)v : new object[0];
        }
    }

    public static class ErrorLog
    {
        public static void Write(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(MenuConfig.Folder);
                File.AppendAllText(Path.Combine(MenuConfig.Folder, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
