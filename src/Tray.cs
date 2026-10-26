using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using Forms = System.Windows.Forms;

namespace RadialMenu
{
    sealed class Tray : IDisposable
    {
        static readonly Color Accent = Color.FromArgb(0x22, 0xD3, 0xEE);

        readonly Controller controller;
        readonly Forms.NotifyIcon icon;
        readonly Forms.ToolStripMenuItem enabledItem;
        readonly Forms.ToolStripMenuItem startupItem;

        public Tray(Controller controller)
        {
            this.controller = controller;
            Forms.Application.EnableVisualStyles();

            var menu = new Forms.ContextMenuStrip
            {
                Renderer = new DarkRenderer(),
                Font = new Font("Segoe UI", 9.75f),
                ForeColor = Color.White,
                BackColor = DarkColors.Bg,
                Padding = new Forms.Padding(2, 4, 2, 4)
            };
            menu.Items.Add(new Forms.ToolStripLabel("Radial Menu")
            {
                Font = new Font("Segoe UI Semibold", 10f),
                ForeColor = Accent,
                Margin = new Forms.Padding(8, 2, 8, 4)
            });
            enabledItem = Item("Activ", (s, e) => { controller.Enabled = !controller.Enabled; Sync(); });
            menu.Items.Add(enabledItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item("Editează meniul…", (s, e) => controller.EditConfig()));
            menu.Items.Add(Item("Deschide folderul configurației", (s, e) => controller.OpenConfigFolder()));
            menu.Items.Add(Item("Reîncarcă meniul", (s, e) => controller.Reload(true)));
            menu.Items.Add(Item("Resetează la meniul implicit…", (s, e) => ConfirmReset()));
            menu.Items.Add(new Forms.ToolStripSeparator());
            startupItem = Item("Pornește cu Windows", (s, e) => { controller.StartWithWindows = !controller.StartWithWindows; Sync(); });
            menu.Items.Add(startupItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item("Ieșire", (s, e) => controller.Exit()));
            menu.Opening += (s, e) => Sync();

            icon = new Forms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Radial Menu",
                ContextMenuStrip = menu,
                Visible = true
            };
            // Left click opens the same menu (NotifyIcon only does that for right click by itself).
            icon.MouseUp += (s, e) =>
            {
                if (e.Button != Forms.MouseButtons.Left) return;
                var show = typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (show != null) show.Invoke(icon, null);
            };
            Sync();
        }

        public void Notify(string text, bool warning)
        {
            icon.ShowBalloonTip(4000, "Radial Menu", text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        }

        void Sync()
        {
            enabledItem.Checked = controller.Enabled;
            startupItem.Checked = controller.StartWithWindows;
            icon.Text = controller.Enabled ? "Radial Menu" : "Radial Menu (oprit)";
        }

        void ConfirmReset()
        {
            var answer = Forms.MessageBox.Show(
                "Înlocuiesc config.json cu meniul implicit? Modificările tale se vor pierde.",
                "Radial Menu", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question);
            if (answer == Forms.DialogResult.Yes) controller.ResetConfig();
        }

        static Forms.ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            return new Forms.ToolStripMenuItem(text, null, onClick) { ForeColor = Color.White, Padding = new Forms.Padding(4, 3, 4, 3) };
        }

        static Icon LoadIcon()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    return new Icon(s, Forms.SystemInformation.SmallIconSize);
            }
            catch { return SystemIcons.Application; }
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }

        sealed class DarkColors : Forms.ProfessionalColorTable
        {
            public static readonly Color Bg = Color.FromArgb(0x16, 0x19, 0x24);
            static readonly Color Border = Color.FromArgb(0x2E, 0x34, 0x48);
            static readonly Color Selected = Color.FromArgb(0x2A, 0x30, 0x48);

            public override Color ToolStripDropDownBackground { get { return Bg; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemBorder { get { return Selected; } }
            public override Color MenuItemSelected { get { return Selected; } }
            public override Color MenuItemSelectedGradientBegin { get { return Selected; } }
            public override Color MenuItemSelectedGradientEnd { get { return Selected; } }
            public override Color ImageMarginGradientBegin { get { return Bg; } }
            public override Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Color ImageMarginGradientEnd { get { return Bg; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Bg; } }
            public override Color CheckBackground { get { return Bg; } }
            public override Color CheckSelectedBackground { get { return Selected; } }
            public override Color CheckPressedBackground { get { return Selected; } }
        }

        sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
            {
                Rectangle r = e.ImageRectangle;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
                        new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
                        new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f)
                    });
                }
            }
        }
    }
}
