using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace RadialMenu
{
    /// <summary>
    /// Developer tool: <c>RadialMenu.exe --snapshot out.png [angle@radius,...]</c> opens the menu in the middle
    /// of the primary screen, hovers each polar position in turn, then saves a screen capture of the menu.
    /// </summary>
    static class Snapshot
    {
        public static void Run(string outPath, string path)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (s, e) =>
            {
                var menu = new RadialWindow();
                menu.ApplyConfig(MenuConfig.Parse(MenuConfig.DefaultJson()));
                var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                menu.Open(new POINT(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2), "Visual Studio Code", null);

                string[] steps = path.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                int next = 0;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                timer.Tick += (t, a) =>
                {
                    if (next < steps.Length)
                    {
                        string[] p = steps[next++].Split('@');
                        menu.HoverAt(double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
                        return;
                    }
                    timer.Stop();
                    Capture(menu.ScreenRect, outPath);
                    app.Shutdown();
                };
                timer.Start();
            };
            app.Run();
        }

        static void Capture(RECT r, string outPath)
        {
            const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000; // CAPTUREBLT includes layered windows
            using (var bmp = new System.Drawing.Bitmap(r.Width, r.Height))
            {
                IntPtr screen = GetDC(IntPtr.Zero);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    BitBlt(hdc, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT);
                    g.ReleaseHdc(hdc);
                }
                ReleaseDC(IntPtr.Zero, screen);
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    }
}
