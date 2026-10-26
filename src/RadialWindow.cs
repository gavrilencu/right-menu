using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace RadialMenu
{
    /// <summary>
    /// Transparent, non-activating, topmost overlay that draws the pie menu around the cursor.
    /// Root items form the inner ring; hovering an item with children fans its submenu out as the
    /// next ring, centred on the parent's angle. Selection is purely angular/radial, so the cursor
    /// can overshoot a ring and still pick the item it points at.
    /// </summary>
    public sealed class RadialWindow : Window
    {
        // Geometry in DIPs, before the user "scale" setting.
        const double CenterR = 48;
        const double OrbitR = 55;
        const double Ring0In = 62;
        const double Ring0Out = 150;
        const double RingGap = 8;
        const double SubRingWidth = 78;
        const double SubArcTarget = 108;   // arc length of a submenu slice, at its mid radius
        const double SliceGap = 4;         // constant-width gap between neighbouring slices
        const double EdgeMargin = 46;         // glow + "keep selecting" zone past the outermost ring

        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Emoji, Segoe UI Symbol");
        static readonly FontFamily TextFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static readonly Brush TextDim = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)));
        static readonly CultureInfo Ro = new CultureInfo("ro-RO");

        public static readonly MenuEntry ClassicEntry = new MenuEntry { Label = "Meniu clasic", Action = "native" };

        /// <summary>A leaf item (or <see cref="ClassicEntry"/>) was chosen. Raised after the menu starts closing.</summary>
        public event Action<MenuEntry> Invoked;
        /// <summary>The menu closed, for any reason.</summary>
        public event Action Dismissed;

        readonly IntPtr hwnd;
        readonly Canvas stage;
        readonly ScaleTransform stageScale = new ScaleTransform(1, 1);
        readonly List<Ring> rings = new List<Ring>();
        readonly object rectLock = new object();

        MenuConfig config;
        int levels = 1;
        double half;       // stage half-size; the menu centre is at (half, half)
        int version;       // bumps on every open/close so stale animation callbacks do nothing
        RECT screenRect;
        bool hasRect;

        Ellipse spotlight, ripple, orbit, pointer, centerRing;
        ScaleTransform rippleScale, menuScale;
        RotateTransform menuRotate, orbitRotate, pointerRotate;
        Canvas menuLayer, ringLayer;
        StackPanel centerStack;
        TranslateTransform centerShift;
        TextBlock centerGlyph, centerTitle, centerSub;
        Image centerIcon;

        Brush sliceFill, sliceStroke, hoverFill, accentStroke;
        Color glowColor;

        bool centerHot;
        MenuEntry hoverEntry;
        string targetName = "";
        ImageSource targetIcon;
        string idleTitle = "", idleSub = "";

        public bool IsOpen { get; private set; }

        public RadialWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            Title = "Radial Menu";
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            stage = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LayoutTransform = stageScale,
                IsHitTestVisible = false
            };
            // Alpha 1 (not 0) so the whole square receives mouse input instead of clicking through.
            var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
            root.Children.Add(stage);
            Content = root;

            MouseMove += (s, e) => { if (IsOpen) UpdateHover(e.GetPosition(stage)); };
            MouseLeave += (s, e) => { if (IsOpen) HoverNothing(); };
            MouseLeftButtonUp += (s, e) =>
            {
                if (!IsOpen) return;
                UpdateHover(e.GetPosition(stage));
                InvokeHovered(true);
            };

            hwnd = new WindowInteropHelper(this).EnsureHandle();
            Native.SetExStyle(hwnd, Native.GetExStyle(hwnd) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        }

        /// <summary>Thread-safe: used by the hook thread to tell clicks on the menu from clicks elsewhere.</summary>
        public bool ContainsScreenPoint(POINT p)
        {
            lock (rectLock) return hasRect && screenRect.Contains(p);
        }

        /// <summary>Screen rectangle of the open menu, in physical pixels.</summary>
        public RECT ScreenRect
        {
            get { lock (rectLock) return screenRect; }
        }

        /// <summary>Hovers a polar position (degrees clockwise from 12 o'clock, radius in DIPs) — used by --snapshot.</summary>
        public void HoverAt(double angle, double radius)
        {
            if (!IsOpen) return;
            double a = angle * Math.PI / 180;
            UpdateHover(new Point(half + radius * Math.Sin(a), half - radius * Math.Cos(a)));
        }

        public void ApplyConfig(MenuConfig cfg)
        {
            if (IsOpen) Dismiss(false);
            config = cfg;
            levels = cfg.Levels;
            half = RingOuter(levels - 1) + EdgeMargin;
            BuildStage();
        }

        // ---- open / close -------------------------------------------------------------------

        public void Open(POINT cursor, string name, ImageSource icon)
        {
            version++;
            targetName = name ?? "";
            targetIcon = icon;
            var now = DateTime.Now;
            idleTitle = now.ToString("HH:mm", Ro);
            idleSub = now.ToString("dddd, d MMM", Ro);

            foreach (var r in rings) ringLayer.Children.Remove(r.Layer);
            rings.Clear();
            hoverEntry = null;
            centerHot = false;
            SetCenterRingHot(false);
            var rootRing = BuildRing(0, config.Items, 0);
            rings.Add(rootRing);
            ringLayer.Children.Add(rootRing.Layer);

            // Start state for the entrance animation, set before the window becomes visible.
            Stop(menuLayer, OpacityProperty, 0);
            Stop(spotlight, OpacityProperty, 0);
            Stop(ripple, OpacityProperty, 0);
            Stop(menuScale, ScaleTransform.ScaleXProperty, 0.45);
            Stop(menuScale, ScaleTransform.ScaleYProperty, 0.45);
            Stop(menuRotate, RotateTransform.AngleProperty, -38);

            SetClickThrough(false);
            Place(cursor);
            IsOpen = true;
            SetCenterContent(null, "", idleTitle, idleSub, true);
            UpdateHover(stage.PointFromScreen(new Point(cursor.X, cursor.Y)));

            var outBack = new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut };
            var outCubic = new CubicEase { EasingMode = EasingMode.EaseOut };
            var outQuint = new QuinticEase { EasingMode = EasingMode.EaseOut };
            Anim(spotlight, OpacityProperty, 1, 260);
            Anim(menuLayer, OpacityProperty, 1, 170);
            Anim(menuScale, ScaleTransform.ScaleXProperty, 1, 380, outBack);
            Anim(menuScale, ScaleTransform.ScaleYProperty, 1, 380, outBack);
            Anim(menuRotate, RotateTransform.AngleProperty, 0, 460, outQuint);
            Anim(ripple, OpacityProperty, 0, 650, outCubic, 0.85);
            Anim(rippleScale, ScaleTransform.ScaleXProperty, 1.3, 650, outCubic, 0.3);
            Anim(rippleScale, ScaleTransform.ScaleYProperty, 1.3, 650, outCubic, 0.3);
            for (int i = 0; i < rootRing.Slices.Count; i++)
                Anim(rootRing.Slices[i].Root, OpacityProperty, 1, 200, null, 0, 30 + i * 28);
            orbitRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(16)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        /// <summary>Closes the menu. <paramref name="chosen"/> plays the "zoom through" exit instead of the shrink.</summary>
        public void Dismiss(bool chosen)
        {
            if (!IsOpen) return;
            IsOpen = false;
            lock (rectLock) hasRect = false;
            // Click-through right away, so a replayed right-click reaches the window underneath.
            SetClickThrough(true);
            int v = ++version;
            var inCubic = new CubicEase { EasingMode = EasingMode.EaseIn };
            double scale = chosen ? 1.08 : 0.86;
            Anim(menuScale, ScaleTransform.ScaleXProperty, scale, 150, inCubic);
            Anim(menuScale, ScaleTransform.ScaleYProperty, scale, 150, inCubic);
            Anim(menuLayer, OpacityProperty, 0, 140);
            Anim(spotlight, OpacityProperty, 0, 170, null, null, 0, (s, e) =>
            {
                if (v != version) return;
                orbitRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                Hide();
            });
            var h = Dismissed;
            if (h != null) h();
        }

        /// <summary>The right button was released after the press that opened the menu ("press, drag, release" selection).</summary>
        public void OnOpeningRelease(POINT p)
        {
            if (!IsOpen) return;
            UpdateHover(stage.PointFromScreen(new Point(p.X, p.Y)));
            if (hoverEntry != null && !hoverEntry.HasChildren && !centerHot) Choose(hoverEntry);
        }

        /// <summary>A full right-click on the open menu behaves like a left-click.</summary>
        public void OnClickAt(POINT p)
        {
            if (!IsOpen) return;
            UpdateHover(stage.PointFromScreen(new Point(p.X, p.Y)));
            InvokeHovered(true);
        }

        void InvokeHovered(bool closeIfNothing)
        {
            if (centerHot) Choose(ClassicEntry);
            else if (hoverEntry == null) { if (closeIfNothing) Dismiss(false); }
            else if (!hoverEntry.HasChildren) Choose(hoverEntry);
        }

        void Choose(MenuEntry entry)
        {
            Dismiss(true);
            var h = Invoked;
            if (h != null) h(entry);
        }

        void Place(POINT cursor)
        {
            IntPtr mon = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            Native.GetMonitorInfo(mon, ref mi);
            uint dx, dy;
            double dpi = Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 ? dx / 96.0 : 1.0;
            PlaceAt(cursor, mi.rcMonitor, dpi);
            if (!IsVisible) Show();
            // Moving to a monitor with another DPI makes WPF rescale the window: settle on WPF's own DPI.
            PlaceAt(cursor, mi.rcMonitor, VisualTreeHelper.GetDpi(this).DpiScaleX);
            UpdateLayout();
        }

        void PlaceAt(POINT cursor, RECT mon, double dpi)
        {
            double s = config.Settings.Scale * dpi;
            int halfPx = (int)Math.Ceiling(half * s);
            int keep = (int)Math.Ceiling((Ring0Out + 16) * s);
            // Near a screen edge, slide the menu inwards so the root ring is always fully visible.
            int cx = Math.Max(mon.Left + keep, Math.Min(mon.Right - keep, cursor.X));
            int cy = Math.Max(mon.Top + keep, Math.Min(mon.Bottom - keep, cursor.Y));
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, cx - halfPx, cy - halfPx, 2 * halfPx, 2 * halfPx, Native.SWP_NOACTIVATE);
            lock (rectLock)
            {
                screenRect = new RECT { Left = cx - halfPx, Top = cy - halfPx, Right = cx + halfPx, Bottom = cy + halfPx };
                hasRect = true;
            }
        }

        void SetClickThrough(bool on)
        {
            int ex = Native.GetExStyle(hwnd);
            Native.SetExStyle(hwnd, on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT);
        }

        // ---- hover --------------------------------------------------------------------------

        void UpdateHover(Point p)
        {
            double dx = p.X - half, dy = p.Y - half;
            double r = Math.Sqrt(dx * dx + dy * dy);
            double ang = Norm(Math.Atan2(dx, -dy) * 180 / Math.PI);
            pointerRotate.Angle = ang;

            if (r <= CenterR + 6) { HoverCenter(); return; }
            pointer.Opacity = 1;

            Ring last = rings[rings.Count - 1];
            if (r > last.ROut + 40) { HoverNothing(); return; }

            // The ring under the cursor wins; if its arc does not cover this angle, fall back to the rings inside it.
            int level = rings.Count - 1;
            while (level > 0 && r < rings[level].RIn - RingGap / 2) level--;
            int idx = -1;
            for (; level >= 0; level--)
            {
                idx = rings[level].HitIndex(ang);
                if (idx >= 0) break;
            }
            if (idx < 0) { HoverNothing(); return; }
            HoverItem(level, idx);
        }

        void HoverCenter()
        {
            pointer.Opacity = 0;
            RemoveRingsAbove(0);
            var root = rings[0];
            if (root.Hovered >= 0) { SetState(root.Slices[root.Hovered], 0); root.Hovered = -1; }
            hoverEntry = null;
            if (!centerHot)
            {
                centerHot = true;
                SetCenterRingHot(true);
            }
            SetCenterContent(targetIcon, targetIcon == null ? char.ConvertFromUtf32(0xE712) : "", targetName, "Meniu clasic", false);
        }

        void HoverNothing()
        {
            if (centerHot) { centerHot = false; SetCenterRingHot(false); }
            foreach (var ring in rings)
                if (ring.Hovered >= 0) SetState(ring.Slices[ring.Hovered], 1);
            hoverEntry = null;
            SetCenterContent(null, "", idleTitle, idleSub, true);
        }

        void HoverItem(int level, int idx)
        {
            if (centerHot) { centerHot = false; SetCenterRingHot(false); }
            Ring ring = rings[level];
            MenuEntry item = ring.Items[idx];

            if (ring.Hovered != idx)
            {
                RemoveRingsAbove(level);
                if (ring.Hovered >= 0) SetState(ring.Slices[ring.Hovered], 0);
                ring.Hovered = idx;
                if (item.HasChildren && level + 1 < levels) OpenRing(level + 1, item, ring.MidAngle(idx));
            }
            else
            {
                RemoveRingsAbove(level + 1);
                if (rings.Count > level + 1)
                {
                    Ring child = rings[level + 1];
                    if (child.Hovered >= 0) { SetState(child.Slices[child.Hovered], 0); child.Hovered = -1; }
                }
            }

            for (int k = 0; k < level; k++) SetState(rings[k].Slices[rings[k].Hovered], 1);
            SetState(ring.Slices[idx], 2);
            hoverEntry = item;

            string sub = item.HasChildren ? item.Items.Count + " opțiuni  ›" : item.Hint;
            SetCenterContent(null, item.Icon, item.Label, sub, false);
        }

        void OpenRing(int level, MenuEntry parent, double parentAngle)
        {
            Ring ring = BuildRing(level, parent.Items, parentAngle);
            rings.Add(ring);
            ringLayer.Children.Add(ring.Layer);

            var outCubic = new CubicEase { EasingMode = EasingMode.EaseOut };
            Anim(ring.Scale, ScaleTransform.ScaleXProperty, 1, 240, outCubic, 0.84);
            Anim(ring.Scale, ScaleTransform.ScaleYProperty, 1, 240, outCubic, 0.84);
            for (int i = 0; i < ring.Slices.Count; i++)
            {
                Slice s = ring.Slices[i];
                // Fan out: every slice starts folded onto the parent's angle and swings into place.
                double from = (Norm(parentAngle - ring.MidAngle(i) + 180) - 180) * 0.7;
                Anim(s.Fan, RotateTransform.AngleProperty, 0, 300, outCubic, from, i * 18);
                Anim(s.Root, OpacityProperty, 1, 170, null, 0, i * 18);
            }
        }

        void RemoveRingsAbove(int level)
        {
            while (rings.Count > level + 1)
            {
                Ring r = rings[rings.Count - 1];
                rings.RemoveAt(rings.Count - 1);
                Canvas layer = r.Layer;
                layer.IsHitTestVisible = false;
                Anim(layer, OpacityProperty, 0, 90, null, null, 0, (s, e) => ringLayer.Children.Remove(layer));
            }
        }

        void SetState(Slice s, int state)
        {
            if (s.State == state) return;
            s.State = state;
            double glow = state == 2 ? 1 : state == 1 ? 0.4 : 0;
            double push = state == 2 ? 6 : state == 1 ? 2.5 : 0;
            var outCubic = new CubicEase { EasingMode = EasingMode.EaseOut };
            Anim(s.Glow, OpacityProperty, glow, 110);
            Anim(s.Push, TranslateTransform.XProperty, s.DirX * push, 170, outCubic);
            Anim(s.Push, TranslateTransform.YProperty, s.DirY * push, 170, outCubic);
            var pop = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut };
            Anim(s.ContentScale, ScaleTransform.ScaleXProperty, state == 2 ? 1.14 : 1, 200, pop);
            Anim(s.ContentScale, ScaleTransform.ScaleYProperty, state == 2 ? 1.14 : 1, 200, pop);
            s.Glow.Effect = state == 2
                ? new DropShadowEffect { Color = glowColor, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.95, RenderingBias = RenderingBias.Performance }
                : null;
            s.Label.Foreground = state > 0 ? Brushes.White : TextDim;
            if (s.Chevron != null) s.Chevron.Opacity = state > 0 ? 0.95 : 0.4;
        }

        void SetCenterRingHot(bool hot)
        {
            Anim(centerRing, OpacityProperty, hot ? 1 : 0.45, 140);
            centerRing.Effect = hot
                ? new DropShadowEffect { Color = glowColor, BlurRadius = 20, ShadowDepth = 0, Opacity = 0.9, RenderingBias = RenderingBias.Performance }
                : null;
        }

        void SetCenterContent(ImageSource icon, string glyph, string title, string sub, bool big)
        {
            if (centerIcon.Source == icon && centerGlyph.Text == glyph && centerTitle.Text == title && centerSub.Text == sub) return;
            centerIcon.Source = icon;
            centerIcon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
            centerGlyph.Text = glyph;
            centerGlyph.Visibility = string.IsNullOrEmpty(glyph) ? Visibility.Collapsed : Visibility.Visible;
            centerTitle.Text = title;
            centerTitle.FontSize = big ? 22 : 12.5;
            centerTitle.FontWeight = big ? FontWeights.Light : FontWeights.SemiBold;
            centerSub.Text = sub ?? "";
            centerSub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
            Anim(centerStack, OpacityProperty, 1, 150, null, 0.2);
            Anim(centerShift, TranslateTransform.YProperty, 0, 170, new CubicEase { EasingMode = EasingMode.EaseOut }, 4);
        }

        // ---- building visuals ---------------------------------------------------------------

        void BuildStage()
        {
            double c = half;
            stage.Children.Clear();
            stage.Width = stage.Height = 2 * half;
            stageScale.ScaleX = stageScale.ScaleY = config.Settings.Scale;

            var a = config.Settings.Accent;
            glowColor = a[1];
            hoverFill = Frozen(new LinearGradientBrush(Stops(a), new Point(0, 0), new Point(1, 1)));
            accentStroke = Frozen(new LinearGradientBrush(Stops(a), new Point(0, 0), new Point(1, 1)));
            sliceStroke = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));
            var fill = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = new Point(c, c),
                GradientOrigin = new Point(c, c),
                RadiusX = RingOuter(levels - 1),
                RadiusY = RingOuter(levels - 1)
            };
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, 0x10, 0x13, 0x1E), 0.25));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0xEE, 0x1F, 0x24, 0x38), 1));
            sliceFill = Frozen(fill);

            // Soft dark vignette that pulls focus to the menu.
            var dim = new RadialGradientBrush();
            dim.GradientStops.Add(new GradientStop(Color.FromArgb(0x8C, 0x04, 0x05, 0x0B), 0));
            dim.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0x04, 0x05, 0x0B), 0.55));
            dim.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x04, 0x05, 0x0B), 1));
            spotlight = new Ellipse { Width = 2 * half, Height = 2 * half, Fill = Frozen(dim), Opacity = 0 };
            stage.Children.Add(spotlight);

            double rr = Ring0Out + 6;
            rippleScale = new ScaleTransform(1, 1, rr, rr);
            ripple = new Ellipse { Width = 2 * rr, Height = 2 * rr, Stroke = accentStroke, StrokeThickness = 2, Opacity = 0, RenderTransform = rippleScale };
            Put(ripple, c - rr, c - rr);
            stage.Children.Add(ripple);

            menuLayer = new Canvas { Width = 2 * half, Height = 2 * half, Opacity = 0 };
            menuScale = new ScaleTransform(1, 1, c, c);
            menuRotate = new RotateTransform(0, c, c);
            var tg = new TransformGroup();
            tg.Children.Add(menuScale);
            tg.Children.Add(menuRotate);
            menuLayer.RenderTransform = tg;
            stage.Children.Add(menuLayer);

            ringLayer = new Canvas();
            menuLayer.Children.Add(ringLayer);
            BuildCenter(c, a);
            rings.Clear();
        }

        void BuildCenter(double c, Color[] accent)
        {
            orbitRotate = new RotateTransform(0, OrbitR, OrbitR);
            orbit = new Ellipse
            {
                Width = 2 * OrbitR,
                Height = 2 * OrbitR,
                Stroke = accentStroke,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 0.1, 4.5 },
                StrokeDashCap = PenLineCap.Round,
                Opacity = 0.8,
                RenderTransform = orbitRotate
            };
            Put(orbit, c - OrbitR, c - OrbitR);
            menuLayer.Children.Add(orbit);

            var diskFill = new RadialGradientBrush();
            diskFill.GradientStops.Add(new GradientStop(Color.FromArgb(0xF7, 0x1D, 0x22, 0x36), 0));
            diskFill.GradientStops.Add(new GradientStop(Color.FromArgb(0xF7, 0x0B, 0x0D, 0x16), 1));
            var disk = new Ellipse { Width = 2 * CenterR, Height = 2 * CenterR, Fill = Frozen(diskFill), Stroke = sliceStroke, StrokeThickness = 1 };
            Put(disk, c - CenterR, c - CenterR);
            menuLayer.Children.Add(disk);

            centerRing = new Ellipse { Width = 2 * CenterR, Height = 2 * CenterR, Stroke = accentStroke, StrokeThickness = 1.6, Opacity = 0.45 };
            Put(centerRing, c - CenterR, c - CenterR);
            menuLayer.Children.Add(centerRing);

            // Little glowing dot on the orbit that points at the cursor.
            pointerRotate = new RotateTransform(0, 4, OrbitR + 4);
            pointer = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Frozen(new SolidColorBrush(accent[0])),
                Opacity = 0,
                RenderTransform = pointerRotate,
                Effect = new DropShadowEffect { Color = accent[0], BlurRadius = 12, ShadowDepth = 0, Opacity = 1 }
            };
            Put(pointer, c - 4, c - OrbitR - 4);
            menuLayer.Children.Add(pointer);

            centerIcon = new Image { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
            RenderOptions.SetBitmapScalingMode(centerIcon, BitmapScalingMode.HighQuality);
            centerGlyph = new TextBlock { FontFamily = IconFont, FontSize = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
            centerTitle = new TextBlock
            {
                FontFamily = TextFont,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 34,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            centerSub = new TextBlock
            {
                FontFamily = TextFont,
                FontSize = 10,
                Foreground = TextDim,
                Opacity = 0.8,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            };
            centerShift = new TranslateTransform();
            centerStack = new StackPanel
            {
                MaxWidth = 2 * CenterR - 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = centerShift
            };
            centerStack.Children.Add(centerIcon);
            centerStack.Children.Add(centerGlyph);
            centerStack.Children.Add(centerTitle);
            centerStack.Children.Add(centerSub);
            var box = new Grid { Width = 2 * CenterR, Height = 2 * CenterR };
            box.Children.Add(centerStack);
            Put(box, c - CenterR, c - CenterR);
            menuLayer.Children.Add(box);
        }

        Ring BuildRing(int level, List<MenuEntry> items, double centerAngle)
        {
            var ring = new Ring { Items = items, RIn = RingInner(level), ROut = RingOuter(level) };
            int n = items.Count;
            double span, start;
            if (level == 0)
            {
                span = 360.0 / n;
                start = -span / 2;     // first item centred at 12 o'clock
            }
            else
            {
                double mid = (ring.RIn + ring.ROut) / 2;
                span = Math.Min(360.0 / n, SubArcTarget / mid * 180 / Math.PI);
                start = centerAngle - span * n / 2;
            }
            ring.Start = new double[n];
            ring.Span = new double[n];
            for (int i = 0; i < n; i++)
            {
                ring.Start[i] = Norm(start + i * span);
                ring.Span[i] = span;
            }

            ring.Scale = new ScaleTransform(1, 1, half, half);
            ring.Layer = new Canvas { RenderTransform = ring.Scale };
            for (int i = 0; i < n; i++)
            {
                Slice s = BuildSlice(ring, i, n == 1 && level == 0);
                ring.Slices.Add(s);
                ring.Layer.Children.Add(s.Root);
            }
            return ring;
        }

        Slice BuildSlice(Ring ring, int i, bool fullCircle)
        {
            MenuEntry item = ring.Items[i];
            double a0 = ring.Start[i], a1 = a0 + ring.Span[i], mid = (a0 + a1) / 2;
            double rIn = ring.RIn, rOut = ring.ROut, rMid = (rIn + rOut) / 2, width = rOut - rIn;
            bool inner = rIn < Ring0Out;

            Geometry g = fullCircle
                ? (Geometry)new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(half, half), rOut, rOut), new EllipseGeometry(new Point(half, half), rIn, rIn))
                : Wedge(rIn, rOut, a0, a1);
            g.Freeze();

            var s = new Slice { Item = item, State = 0 };
            s.Root = new Canvas { Opacity = 0 };
            s.Root.Children.Add(new Path { Data = g, Fill = sliceFill, Stroke = sliceStroke, StrokeThickness = 1 });
            s.Glow = new Path { Data = g, Fill = hoverFill, Opacity = 0 };
            s.Root.Children.Add(s.Glow);

            if (item.HasChildren)
            {
                // Small arc on the outer edge: "this opens a submenu".
                double r = rOut - 7;
                double k = Math.Min(ring.Span[i] * 0.16, 14 / r * 180 / Math.PI);
                var fig = new PathFigure { StartPoint = Polar(r, mid - k), IsFilled = false };
                fig.Segments.Add(new ArcSegment(Polar(r, mid + k), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                var arc = new PathGeometry();
                arc.Figures.Add(fig);
                arc.Freeze();
                s.Chevron = new Path
                {
                    Data = arc,
                    Stroke = Brushes.White,
                    StrokeThickness = 2.2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = 0.4
                };
                s.Root.Children.Add(s.Chevron);
            }

            double chord = 2 * rMid * Math.Sin(Math.Min(ring.Span[i], 180) / 2 * Math.PI / 180);
            double boxW = Math.Max(48, Math.Min(chord, width + 8) - 8);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = item.Icon,
                FontFamily = IconFont,
                FontSize = inner ? 21 : 19,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            double fs = inner ? 11 : 10.5;
            s.Label = new TextBlock
            {
                Text = item.Label,
                FontFamily = TextFont,
                FontSize = fs,
                Foreground = TextDim,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = boxW,
                MaxHeight = fs * 2.75,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            };
            if (item.Label.Length > 0) stack.Children.Add(s.Label);

            s.ContentScale = new ScaleTransform(1, 1);
            var box = new Grid { Width = boxW, Height = width, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = s.ContentScale };
            box.Children.Add(stack);
            Point p = Polar(rMid, mid);
            Put(box, p.X - boxW / 2, p.Y - width / 2);
            s.Root.Children.Add(box);

            double rad = mid * Math.PI / 180;
            s.DirX = Math.Sin(rad);
            s.DirY = -Math.Cos(rad);
            s.Fan = new RotateTransform(0, half, half);
            s.Push = new TranslateTransform();
            var tg = new TransformGroup();
            tg.Children.Add(s.Fan);
            tg.Children.Add(s.Push);
            s.Root.RenderTransform = tg;
            return s;
        }

        /// <summary>Ring slice between two angles (degrees, clockwise from 12 o'clock) with parallel, constant-width gaps.</summary>
        Geometry Wedge(double rIn, double rOut, double a0, double a1)
        {
            double gOut = SliceGap / 2 / rOut * 180 / Math.PI;
            double gIn = SliceGap / 2 / rIn * 180 / Math.PI;
            double o0 = a0 + gOut, o1 = a1 - gOut, i0 = a0 + gIn, i1 = a1 - gIn;
            var fig = new PathFigure { StartPoint = Polar(rOut, o0), IsClosed = true };
            fig.Segments.Add(new ArcSegment(Polar(rOut, o1), new Size(rOut, rOut), 0, o1 - o0 > 180, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(Polar(rIn, i1), true));
            fig.Segments.Add(new ArcSegment(Polar(rIn, i0), new Size(rIn, rIn), 0, i1 - i0 > 180, SweepDirection.Counterclockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            return g;
        }

        Point Polar(double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(half + r * Math.Sin(a), half - r * Math.Cos(a));
        }

        static double RingInner(int level)
        {
            return level == 0 ? Ring0In : Ring0Out + RingGap + (level - 1) * (SubRingWidth + RingGap);
        }

        static double RingOuter(int level)
        {
            return level == 0 ? Ring0Out : RingInner(level) + SubRingWidth;
        }

        // ---- helpers ------------------------------------------------------------------------

        static double Norm(double deg)
        {
            deg %= 360;
            return deg < 0 ? deg + 360 : deg;
        }

        static void Put(UIElement e, double x, double y)
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
        }

        static GradientStopCollection Stops(Color[] a)
        {
            return new GradientStopCollection { new GradientStop(a[0], 0), new GradientStop(a[1], 0.55), new GradientStop(a[2], 1) };
        }

        static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        static void Anim(IAnimatable target, DependencyProperty dp, double to, double ms,
            IEasingFunction ease = null, double? from = null, double delayMs = 0, EventHandler completed = null)
        {
            var a = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = ease,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            if (from.HasValue) a.From = from.Value;
            if (completed != null) a.Completed += completed;
            target.BeginAnimation(dp, a);
        }

        /// <summary>Cancels any running animation on the property and pins it to a value.</summary>
        static void Stop(IAnimatable target, DependencyProperty dp, double value)
        {
            target.BeginAnimation(dp, null);
            ((DependencyObject)target).SetValue(dp, value);
        }

        sealed class Ring
        {
            public List<MenuEntry> Items;
            public double RIn, ROut;
            public double[] Start, Span;
            public Canvas Layer;
            public ScaleTransform Scale;
            public readonly List<Slice> Slices = new List<Slice>();
            public int Hovered = -1;

            public int HitIndex(double ang)
            {
                for (int i = 0; i < Items.Count; i++)
                    if (Norm(ang - Start[i]) < Span[i]) return i;
                return -1;
            }

            public double MidAngle(int i) { return Start[i] + Span[i] / 2; }
        }

        sealed class Slice
        {
            public MenuEntry Item;
            public Canvas Root;
            public Path Glow, Chevron;
            public TextBlock Label;
            public RotateTransform Fan;
            public TranslateTransform Push;
            public ScaleTransform ContentScale;
            public double DirX, DirY;
            public int State;
        }
    }
}
