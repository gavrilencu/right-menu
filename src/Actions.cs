using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RadialMenu
{
    /// <summary>Synthesizes keyboard/mouse input. Every event carries <see cref="Marker"/> so our own hooks let it through.</summary>
    public static class InputSim
    {
        public static readonly IntPtr Marker = new IntPtr(0x52414449); // "RADI"

        static readonly Dictionary<string, ushort> Named = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            { "ctrl", 0x11 }, { "control", 0x11 }, { "shift", 0x10 }, { "alt", 0x12 }, { "win", 0x5B }, { "windows", 0x5B },
            { "esc", 0x1B }, { "escape", 0x1B }, { "tab", 0x09 }, { "enter", 0x0D }, { "return", 0x0D }, { "space", 0x20 },
            { "backspace", 0x08 }, { "delete", 0x2E }, { "del", 0x2E }, { "insert", 0x2D }, { "ins", 0x2D },
            { "home", 0x24 }, { "end", 0x23 }, { "pageup", 0x21 }, { "pgup", 0x21 }, { "pagedown", 0x22 }, { "pgdn", 0x22 },
            { "left", 0x25 }, { "up", 0x26 }, { "right", 0x27 }, { "down", 0x28 },
            { "printscreen", 0x2C }, { "prtsc", 0x2C }, { "apps", 0x5D }, { "menu", 0x5D },
            { ".", 0xBE }, { ",", 0xBC }, { "-", 0xBD }, { "=", 0xBB }, { ";", 0xBA }, { "/", 0xBF }, { "`", 0xC0 },
            { "[", 0xDB }, { "\\", 0xDC }, { "]", 0xDD }, { "'", 0xDE },
            { "mediaplaypause", 0xB3 }, { "playpause", 0xB3 }, { "medianext", 0xB0 }, { "mediaprev", 0xB1 }, { "mediastop", 0xB2 },
            { "volumeup", 0xAF }, { "volumedown", 0xAE }, { "volumemute", 0xAD }, { "mute", 0xAD },
            { "browserback", 0xA6 }, { "browserforward", 0xA7 }, { "browserrefresh", 0xA8 }, { "browserhome", 0xAC }
        };

        static readonly HashSet<ushort> Extended = new HashSet<ushort>
        {
            0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5,
            0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7
        };

        public static ushort ParseKey(string name)
        {
            name = name.Trim();
            ushort vk;
            if (Named.TryGetValue(name, out vk)) return vk;
            if (name.Length == 1 && char.IsLetterOrDigit(name[0])) return (ushort)char.ToUpperInvariant(name[0]);
            var f = Regex.Match(name, "^[Ff]([0-9]{1,2})$");
            if (f.Success)
            {
                int n = int.Parse(f.Groups[1].Value);
                if (n >= 1 && n <= 24) return (ushort)(0x70 + n - 1);
            }
            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt16(name.Substring(2), 16);
            throw new FormatException("Tastă necunoscută: \"" + name + "\"");
        }

        /// <summary>Presses a combination like "Ctrl+Shift+Esc" or "Win+.".</summary>
        public static void SendCombo(string combo)
        {
            var keys = new List<ushort>();
            foreach (var part in combo.Split('+'))
                if (part.Trim().Length > 0) keys.Add(ParseKey(part));
            if (keys.Count == 0) return;

            var inputs = new List<INPUT>();
            foreach (var k in keys) inputs.Add(Key(k, false));
            for (int i = keys.Count - 1; i >= 0; i--) inputs.Add(Key(keys[i], true));
            Send(inputs);
        }

        /// <summary>Types arbitrary Unicode text into the focused control.</summary>
        public static void SendText(string text)
        {
            var inputs = new List<INPUT>();
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); continue; }
                inputs.Add(Unicode(c, false));
                inputs.Add(Unicode(c, true));
            }
            Send(inputs);
        }

        /// <summary>Clicks the right button at a screen point (physical pixels) — used to open the original context menu.</summary>
        public static void RightClick(POINT p)
        {
            Native.SetCursorPos(p.X, p.Y);
            var inputs = new List<INPUT>
            {
                Mouse(Native.MOUSEEVENTF_RIGHTDOWN),
                Mouse(Native.MOUSEEVENTF_RIGHTUP)
            };
            Send(inputs);
        }

        static INPUT Key(ushort vk, bool up)
        {
            uint flags = up ? Native.KEYEVENTF_KEYUP : 0;
            if (Extended.Contains(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            var i = new INPUT { type = Native.INPUT_KEYBOARD };
            i.u.ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)Native.MapVirtualKey(vk, 0), dwFlags = flags, dwExtraInfo = Marker };
            return i;
        }

        static INPUT Unicode(char c, bool up)
        {
            var i = new INPUT { type = Native.INPUT_KEYBOARD };
            i.u.ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = Native.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = Marker
            };
            return i;
        }

        static INPUT Mouse(uint flags)
        {
            var i = new INPUT { type = Native.INPUT_MOUSE };
            i.u.mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = Marker };
            return i;
        }

        static void Send(List<INPUT> inputs)
        {
            if (inputs.Count == 0) return;
            Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }
    }

    /// <summary>What was right-clicked: the window under the cursor and the original click point.</summary>
    public sealed class ActionContext
    {
        public IntPtr Target;
        public string TargetClass = "";
        public string TargetName = "";
        public POINT Origin;
    }

    public static class ActionRunner
    {
        /// <summary>Raised with a user-facing message when an action cannot be completed.</summary>
        public static event Action<string> Failed;

        public static async void Run(MenuEntry e, ActionContext ctx)
        {
            try
            {
                switch (e.Action)
                {
                    case "native":
                        await Task.Delay(15);
                        InputSim.RightClick(ctx.Origin);
                        break;
                    case "keys":
                        await Focus(ctx);
                        InputSim.SendCombo(e.Param);
                        break;
                    case "type":
                        await Focus(ctx);
                        InputSim.SendText(Expand(e.Param, ctx));
                        break;
                    case "run":
                        RunCommand(e, ctx);
                        break;
                    case "window":
                        WindowOp(ctx, e.Param.ToLowerInvariant());
                        break;
                    case "system":
                        SystemOp(e.Param.ToLowerInvariant());
                        break;
                    case "search":
                        {
                            string q = await CopySelection(ctx);
                            OpenUrl(e.Param.Replace("{q}", Uri.EscapeDataString(q)));
                            break;
                        }
                    case "openlink":
                        {
                            string text = await CopySelection(ctx);
                            if (text.Length == 0) { Fail("Selectează mai întâi un link."); break; }
                            if (Regex.IsMatch(text, @"^[a-z][a-z0-9+.-]*://\S+$", RegexOptions.IgnoreCase)) OpenUrl(text);
                            else if (Regex.IsMatch(text, @"^(www\.)?[\w-]+(\.[\w-]+)+(/\S*)?$")) OpenUrl("https://" + text);
                            else OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(text));
                            break;
                        }
                    case "":
                        break;
                    default:
                        Fail("Acțiune necunoscută: \"" + e.Action + "\"");
                        break;
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                Fail(e.Label + ": " + ex.Message);
            }
        }

        static void Fail(string message)
        {
            var h = Failed;
            if (h != null) h(message);
        }

        /// <summary>Gives keyboard focus back to the right-clicked window before sending keys to it.</summary>
        static async Task Focus(ActionContext ctx)
        {
            Native.ForceForeground(ctx.Target);
            await Task.Delay(40);
        }

        static string Expand(string text, ActionContext ctx)
        {
            var now = DateTime.Now;
            text = text.Replace("{date}", now.ToString("dd.MM.yyyy"))
                       .Replace("{time}", now.ToString("HH:mm"));
            if (text.Contains("{folder}")) text = text.Replace("{folder}", CurrentFolder(ctx));
            return text;
        }

        // ---- run ----------------------------------------------------------------------------

        /// <summary>
        /// "param" may list alternatives separated by "|" ("wt.exe -d .|powershell.exe"); the first one that
        /// starts wins. Programs start in the folder that was right-clicked when it is an Explorer window or the desktop.
        /// </summary>
        static void RunCommand(MenuEntry e, ActionContext ctx)
        {
            string workDir = CurrentFolder(ctx);
            foreach (var alt in e.Param.Split('|'))
            {
                string file, args;
                SplitCommand(alt.Trim(), out file, out args);
                if (file.Length == 0) continue;
                if (e.Args.Length > 0) args = (args + " " + e.Args).Trim();
                args = args.Replace("{folder}", workDir);
                try
                {
                    var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(file), args)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = workDir
                    };
                    Process.Start(psi);
                    return;
                }
                catch (Exception ex)
                {
                    ErrorLog.Write(ex);
                }
            }
            Fail("Nu pot porni: " + e.Param);
        }

        static void SplitCommand(string cmd, out string file, out string args)
        {
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end < 0) { file = cmd.Trim('"'); args = ""; return; }
                file = cmd.Substring(1, end - 1);
                args = cmd.Substring(end + 1).Trim();
                return;
            }
            // URLs and shell URIs ("ms-settings:") never take arguments.
            if (cmd.Contains("://") || Regex.IsMatch(cmd, @"^[a-z][a-z0-9+.-]*:\S*$", RegexOptions.IgnoreCase) && !Regex.IsMatch(cmd, @"^[a-z]:\\", RegexOptions.IgnoreCase))
            {
                file = cmd; args = ""; return;
            }
            int sp = cmd.IndexOf(' ');
            file = sp < 0 ? cmd : cmd.Substring(0, sp);
            args = sp < 0 ? "" : cmd.Substring(sp + 1).Trim();
        }

        static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        /// <summary>The folder shown by the right-clicked Explorer window, the desktop folder, or the user profile.</summary>
        static string CurrentFolder(ActionContext ctx)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (ctx.TargetClass == "Progman" || ctx.TargetClass == "WorkerW")
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (ctx.TargetClass != "CabinetWClass") return home;
            try
            {
                string title = Native.WindowText(ctx.Target);
                string fallback = null;
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                foreach (dynamic w in shell.Windows())
                {
                    if (new IntPtr(Convert.ToInt64(w.HWND)) != ctx.Target) continue;
                    string path = w.Document.Folder.Self.Path;
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                    // Explorer tabs share one HWND; the window title names the active tab.
                    string name = w.LocationName;
                    if (title.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return path;
                    if (fallback == null) fallback = path;
                }
                if (fallback != null) return fallback;
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
            return home;
        }

        // ---- window / system ----------------------------------------------------------------

        static void WindowOp(ActionContext ctx, string op)
        {
            IntPtr h = ctx.Target;
            if (h == IntPtr.Zero || Native.IsShellWindow(ctx.TargetClass)) return;
            switch (op)
            {
                case "minimize":
                    Native.ShowWindow(h, Native.SW_MINIMIZE);
                    break;
                case "maximize":
                    Native.ShowWindow(h, Native.IsZoomed(h) ? Native.SW_RESTORE : Native.SW_MAXIMIZE);
                    break;
                case "close":
                    Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    break;
                case "topmost":
                    bool top = (Native.GetExStyle(h) & Native.WS_EX_TOPMOST) != 0;
                    Native.SetWindowPos(h, top ? Native.HWND_NOTOPMOST : Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    break;
                default:
                    Fail("Operație de fereastră necunoscută: \"" + op + "\"");
                    break;
            }
        }

        static void SystemOp(string op)
        {
            switch (op)
            {
                case "lock":
                    Native.LockWorkStation();
                    break;
                case "sleep":
                    Task.Run(() => Native.SetSuspendState(false, false, false));
                    break;
                default:
                    Fail("Operație de sistem necunoscută: \"" + op + "\"");
                    break;
            }
        }

        // ---- clipboard ----------------------------------------------------------------------

        /// <summary>Copies the current selection of the target window and returns it ("" when nothing was copied).</summary>
        static async Task<string> CopySelection(ActionContext ctx)
        {
            uint before = Native.GetClipboardSequenceNumber();
            await Focus(ctx);
            InputSim.SendCombo("Ctrl+C");
            bool changed = false;
            for (int i = 0; i < 20 && !changed; i++)
            {
                await Task.Delay(25);
                changed = Native.GetClipboardSequenceNumber() != before;
            }
            if (!changed) return "";

            for (int attempt = 0; attempt < 6; attempt++)
            {
                string text = null;
                try
                {
                    text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
                }
                catch (ExternalException) { }
                if (text != null)
                {
                    text = Regex.Replace(text, @"\s+", " ").Trim();
                    return text.Length > 1000 ? text.Substring(0, 1000) : text;
                }
                await Task.Delay(40);
            }
            return "";
        }
    }
}
