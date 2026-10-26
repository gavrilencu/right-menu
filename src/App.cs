using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Radial Menu")]
[assembly: AssemblyProduct("Radial Menu")]
[assembly: AssemblyDescription("Meniu radial pentru click dreapta")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
// Tells WPF it may use the per-monitor DPI support added in .NET 4.6.2.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace RadialMenu
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--snapshot")
            {
                Snapshot.Run(args[1], args.Length > 2 ? args[2] : "");
                return;
            }

            bool isNew;
            using (var mutex = new Mutex(true, @"Local\RadialMenu.Instance", out isNew))
            {
                if (!isNew)
                {
                    MessageBox.Show("Radial Menu rulează deja — caută iconița în system tray.", "Radial Menu",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { ErrorLog.Write(e.Exception); e.Handled = true; };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ErrorLog.Write(e.ExceptionObject as Exception);

                Controller controller = null;
                app.Startup += (s, e) =>
                {
                    try { controller = new Controller(); }
                    catch (Exception ex)
                    {
                        ErrorLog.Write(ex);
                        MessageBox.Show("Radial Menu nu a putut porni:\n\n" + ex.Message, "Radial Menu",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        app.Shutdown(1);
                    }
                };
                app.Exit += (s, e) => { if (controller != null) controller.Dispose(); };
                app.Run();
            }
        }
    }

    /// <summary>
    /// Runs the low-level hooks on a dedicated thread with its own message loop, so a busy UI thread
    /// (starting a program, reading the clipboard) can never delay system-wide mouse input or make
    /// Windows drop the hooks for timing out.
    /// </summary>
    sealed class HookThread : IDisposable
    {
        const uint WM_REINSTALL = Native.WM_APP + 1;

        readonly Hooks hooks;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        Thread thread;
        uint threadId;
        Exception startError;

        public HookThread(Hooks.MouseHandler mouse, Hooks.KeyHandler key)
        {
            hooks = new Hooks(mouse, key);
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "RadialMenu hooks", Priority = ThreadPriority.Highest };
            thread.Start();
            ready.WaitOne();
            if (startError != null) throw startError;
        }

        void Run()
        {
            threadId = Native.GetCurrentThreadId();
            MSG msg;
            Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE); // creates the thread's message queue
            try { hooks.Install(); }
            catch (Exception ex) { startError = ex; ready.Set(); return; }
            ready.Set();

            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_REINSTALL)
                {
                    try { hooks.Install(); }
                    catch (Exception ex) { ErrorLog.Write(ex); }
                    continue;
                }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            hooks.Uninstall();
        }

        /// <summary>Re-registers the hooks (Windows can silently drop them, e.g. across sleep).</summary>
        public void Reinstall()
        {
            Native.PostThreadMessage(threadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            if (thread == null) return;
            Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(1000);
            thread = null;
        }
    }

    sealed class Controller : IDisposable
    {
        const int VK_ESCAPE = 0x1B;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "RadialMenu";
        const string AppKey = @"Software\RadialMenu";
        const string StartupSetValue = "StartupConfigured";

        readonly Dispatcher ui;
        readonly RadialWindow menu;
        readonly HookThread hookThread;
        readonly Tray tray;
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        // Automated UI tests drive the menu with synthesized input, which is normally ignored.
        readonly bool acceptInjected = Environment.GetEnvironmentVariable("RADIALMENU_ACCEPT_INJECTED") == "1";
        readonly Dictionary<string, string> nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        FileSystemWatcher watcher;
        DispatcherTimer reloadTimer;
        ActionContext ctx = new ActionContext();

        // Shared with the hook thread.
        volatile MenuSettings settings;
        volatile bool enabled = true;
        volatile bool menuOpen;
        int pendingOpens;

        // Hook thread only.
        bool swallowRightUp;
        bool openingPress;

        public Controller()
        {
            ui = Dispatcher.CurrentDispatcher;

            bool created = false;
            string loadError = null;
            MenuConfig config;
            try { config = MenuConfig.LoadOrCreate(out created); }
            catch (Exception ex)
            {
                loadError = ex.Message;
                config = MenuConfig.Parse(MenuConfig.DefaultJson());
            }
            settings = config.Settings;

            menu = new RadialWindow();
            menu.ApplyConfig(config);
            menu.Invoked += entry => ActionRunner.Run(entry, ctx);
            menu.Dismissed += () => { if (Thread.VolatileRead(ref pendingOpens) == 0) menuOpen = false; };

            tray = new Tray(this);
            ActionRunner.Failed += message => tray.Notify(message, true);

            hookThread = new HookThread(OnMouse, OnKey);
            hookThread.Start();

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            WatchConfig();
            bool startupEnabled = InitStartup();

            if (loadError != null)
                tray.Notify("config.json are o eroare, folosesc meniul implicit:\n" + loadError, true);
            else if (created || startupEnabled)
                tray.Notify("Click dreapta oriunde pentru meniul radial.\nCentrul cercului sau Ctrl + click dreapta = meniul clasic." +
                    (startupEnabled ? "\nPornește automat cu Windows (se poate opri din tray)." : ""), false);
        }

        // The first launch turns "start with Windows" on; after that only the tray toggle changes it.
        // Returns true when it was just turned on.
        bool InitStartup()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(AppKey))
                {
                    if (k.GetValue(StartupSetValue) == null)
                    {
                        StartWithWindows = true;
                        k.SetValue(StartupSetValue, 1);
                        return true;
                    }
                }
                if (StartWithWindows) StartWithWindows = true; // refresh the stored path if the exe moved
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
            return false;
        }

        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; }
        }

        // ---- hooks (run on the hook thread) -----------------------------------------------

        bool OnMouse(int msg, ref MSLLHOOKSTRUCT d)
        {
            // Our own synthesized clicks, and input injected by other tools, pass straight through.
            if (d.dwExtraInfo == InputSim.Marker || (!acceptInjected && (d.flags & Native.LLMHF_INJECTED) != 0)) return false;
            POINT pt = d.pt;

            switch (msg)
            {
                case Native.WM_RBUTTONDOWN:
                    if (menuOpen)
                    {
                        if (menu.ContainsScreenPoint(pt))
                        {
                            swallowRightUp = true;
                            openingPress = false;
                            return true;
                        }
                        // Right-click elsewhere: close this menu, then treat it as a fresh right-click.
                        menuOpen = false;
                        ui.BeginInvoke(new Action(() => menu.Dismiss(false)));
                    }
                    if (!enabled || ShouldBypass(pt))
                    {
                        swallowRightUp = false;
                        return false;
                    }
                    Interlocked.Increment(ref pendingOpens);
                    menuOpen = true;
                    swallowRightUp = true;
                    openingPress = true;
                    ui.BeginInvoke(new Action(() => OpenMenu(pt)));
                    return true;

                case Native.WM_RBUTTONUP:
                    if (!swallowRightUp) return false;
                    swallowRightUp = false;
                    bool opening = openingPress;
                    openingPress = false;
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (opening) menu.OnOpeningRelease(pt);
                        else menu.OnClickAt(pt);
                    }));
                    return true;

                case Native.WM_LBUTTONDOWN:
                case Native.WM_MBUTTONDOWN:
                case Native.WM_XBUTTONDOWN:
                    if (menuOpen && !menu.ContainsScreenPoint(pt))
                    {
                        menuOpen = false;
                        ui.BeginInvoke(new Action(() => menu.Dismiss(false)));
                    }
                    return false;
            }
            return false;
        }

        bool OnKey(int msg, ref KBDLLHOOKSTRUCT k)
        {
            if (!menuOpen || k.dwExtraInfo == InputSim.Marker) return false;
            if (k.vkCode == VK_ESCAPE && (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN))
            {
                menuOpen = false;
                ui.BeginInvoke(new Action(() => menu.Dismiss(false)));
                return true;
            }
            return false;
        }

        /// <summary>True when this right-click should reach Windows untouched.</summary>
        bool ShouldBypass(POINT pt)
        {
            MenuSettings s = settings;
            if (ModifierHeld(s.BypassModifier)) return true;

            IntPtr root = Native.RootAt(pt);
            if (root == IntPtr.Zero) return true;
            if (Native.ProcessIdOf(root) == ownPid) return true;

            string cls = Native.ClassOf(root);
            if (s.ExcludedWindowClasses.Contains(cls)) return true;
            if (s.ExcludedProcesses.Count > 0)
            {
                string path = Native.ProcessPathOf(root);
                if (path != null && s.ExcludedProcesses.Contains(Path.GetFileNameWithoutExtension(path))) return true;
            }
            return s.DisableInFullscreen && IsFullscreen(root, cls);
        }

        static bool ModifierHeld(string modifier)
        {
            switch ((modifier ?? "").ToLowerInvariant())
            {
                case "ctrl":
                case "control": return Native.IsKeyDown(0x11);
                case "shift": return Native.IsKeyDown(0x10);
                case "alt": return Native.IsKeyDown(0x12);
                case "win": return Native.IsKeyDown(0x5B) || Native.IsKeyDown(0x5C);
                default: return false;
            }
        }

        /// <summary>A borderless window covering its whole monitor — games, videos, presentations.</summary>
        static bool IsFullscreen(IntPtr root, string cls)
        {
            if (Native.IsShellWindow(cls) || Native.IsZoomed(root) || Native.GetForegroundWindow() != root) return false;
            RECT w;
            if (!Native.GetWindowRect(root, out w)) return false;
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(root, Native.MONITOR_DEFAULTTONEAREST), ref mi)) return false;
            RECT m = mi.rcMonitor;
            return w.Left <= m.Left && w.Top <= m.Top && w.Right >= m.Right && w.Bottom >= m.Bottom;
        }

        // ---- menu (UI thread) -------------------------------------------------------------

        void OpenMenu(POINT pt)
        {
            try
            {
                IntPtr root = Native.RootAt(pt);
                string cls = Native.ClassOf(root);
                string path = Native.ProcessPathOf(root);
                var c = new ActionContext { Target = root, TargetClass = cls, Origin = pt };
                c.TargetName = Describe(root, cls, path);
                ctx = c;
                // A real right-click activates the window it lands on; keep that behaviour so
                // keyboard actions (Ctrl+C, …) go to the window that was clicked.
                Native.ForceForeground(root);
                menu.Open(pt, c.TargetName, IconOf(path));
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
            finally
            {
                Interlocked.Decrement(ref pendingOpens);
                menuOpen = menu.IsOpen || Thread.VolatileRead(ref pendingOpens) > 0;
            }
        }

        string Describe(IntPtr root, string cls, string path)
        {
            if (cls == "Progman" || cls == "WorkerW") return "Desktop";
            if (cls == "CabinetWClass") return "Explorer";
            if (path == null) return "Aplicație";
            if (Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                string title = Native.WindowText(root);
                return title.Length > 0 ? title : "Aplicație";
            }
            string name;
            if (nameCache.TryGetValue(path, out name)) return name;
            try { name = (FileVersionInfo.GetVersionInfo(path).FileDescription ?? "").Trim(); }
            catch { name = ""; }
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);
            nameCache[path] = name;
            return name;
        }

        ImageSource IconOf(string path)
        {
            if (path == null) return null;
            ImageSource src;
            if (iconCache.TryGetValue(path, out src)) return src;
            try
            {
                using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    src = bmp;
                }
            }
            catch { src = null; }
            iconCache[path] = src;
            return src;
        }

        // ---- configuration ----------------------------------------------------------------

        void WatchConfig()
        {
            Directory.CreateDirectory(MenuConfig.Folder);
            reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); Reload(true); };

            // Editors often save via temp file + rename, so listen for every kind of change.
            watcher = new FileSystemWatcher(MenuConfig.Folder, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            FileSystemEventHandler changed = (s, e) => ui.BeginInvoke(new Action(() => { reloadTimer.Stop(); reloadTimer.Start(); }));
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (s, e) => changed(s, e);
            watcher.EnableRaisingEvents = true;
        }

        public void Reload(bool notify)
        {
            MenuConfig cfg;
            try { cfg = MenuConfig.Parse(ReadConfigText()); }
            catch (Exception ex)
            {
                tray.Notify("config.json are o eroare:\n" + ex.Message, true);
                return;
            }
            settings = cfg.Settings;
            menu.ApplyConfig(cfg);
            if (notify) tray.Notify("Meniul a fost reîncărcat.", false);
        }

        static string ReadConfigText()
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return File.ReadAllText(MenuConfig.FilePath, Encoding.UTF8); }
                catch (IOException)
                {
                    if (attempt >= 5) throw;
                    Thread.Sleep(80); // the editor may still be writing the file
                }
            }
        }

        public void EditConfig()
        {
            if (!File.Exists(MenuConfig.FilePath)) MenuConfig.ResetToDefault();
            try { Process.Start(new ProcessStartInfo(MenuConfig.FilePath) { UseShellExecute = true }); }
            catch { Process.Start("notepad.exe", "\"" + MenuConfig.FilePath + "\""); }
        }

        public void OpenConfigFolder()
        {
            Process.Start("explorer.exe", "\"" + MenuConfig.Folder + "\"");
        }

        public void ResetConfig()
        {
            MenuConfig.ResetToDefault(); // the file watcher reloads it
        }

        public bool StartWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunValue, "\"" + Assembly.GetEntryAssembly().Location + "\"");
                    else k.DeleteValue(RunValue, false);
                }
            }
        }

        public void Exit()
        {
            Application.Current.Shutdown();
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock) hookThread.Reinstall();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) hookThread.Reinstall();
        }

        public void Dispose()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            hookThread.Dispose();
            if (watcher != null) watcher.Dispose();
            tray.Dispose();
        }
    }
}
