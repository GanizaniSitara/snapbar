// SnapBar: Windows 11-style snap bar for Windows 10.
// Drag a window toward the top edge of a monitor and a strip of layouts drops down;
// hover a zone to preview it and release to place the window there. Esc, resizes and
// drops elsewhere are left to Windows. One process, a tray icon, no services.
// Set SNAPBAR_LOG=1 to log geometry to %LOCALAPPDATA%\snapbar\snapbar.log.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SnapBar
{
    static class Native
    {
        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT r, int size);

        public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const int GWL_STYLE = -16, WS_THICKFRAME = 0x40000, SW_RESTORE = 9;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    }

    static class Log
    {
        static readonly string path = Environment.GetEnvironmentVariable("SNAPBAR_LOG") == "1"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "snapbar", "snapbar.log") : null;

        public static void Write(string msg)
        {
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }

    // A layout is a set of zones in fractions of the monitor work area.
    class SnapLayout
    {
        public RectangleF[] Zones;
        public SnapLayout(params float[] v)
        {
            Zones = new RectangleF[v.Length / 4];
            for (int i = 0; i < Zones.Length; i++) Zones[i] = new RectangleF(v[i * 4], v[i * 4 + 1], v[i * 4 + 2], v[i * 4 + 3]);
        }

        // Four-up only, on every monitor.
        public static readonly SnapLayout[] Landscape = {
            new SnapLayout(0,0,.5f,.5f, .5f,0,.5f,.5f, 0,.5f,.5f,.5f, .5f,.5f,.5f,.5f),
        };
        public static readonly SnapLayout[] Portrait = Landscape;
    }

    // Borderless, click-through, never-activating topmost window.
    class OverlayForm : Form
    {
        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x20 | 0x8; // TOOLWINDOW | NOACTIVATE | TRANSPARENT | TOPMOST
                return cp;
            }
        }
        // Shows at r directly below `above` in z-order (or at the top of the topmost band), in one call.
        public void ShowAt(Rectangle r, IntPtr above)
        {
            if (!IsHandleCreated) CreateHandle();
            if (Bounds == r && Visible) return;
            Native.SetWindowPos(Handle, above == IntPtr.Zero ? Native.HWND_TOPMOST : above, r.Left, r.Top, r.Width, r.Height,
                Native.SWP_NOACTIVATE | 0x40 /* SWP_SHOWWINDOW */);
            if (!Visible) Visible = true;
        }
    }

    class PreviewForm : OverlayForm
    {
        public PreviewForm()
        {
            BackColor = Color.FromArgb(76, 194, 255);
            Opacity = 0.30;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var pen = new Pen(Color.White, 3)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
    }

    // Windows 11-style bar hanging from the top edge: a small pill while dragging, expanding
    // into the layout panel when the cursor comes up to it.
    class StripForm : OverlayForm
    {
        static readonly Color Back = Color.FromArgb(44, 44, 44), Border = Color.FromArgb(78, 78, 78), Grip = Color.FromArgb(150, 150, 150),
            CardBack = Color.FromArgb(58, 58, 58), ZoneFill = Color.FromArgb(98, 98, 98), Hot = Color.FromArgb(76, 194, 255);

        public SnapLayout[] Layouts;
        public List<Rectangle[]> ZoneScreenRects = new List<Rectangle[]>(); // per card, in screen coords
        public List<Rectangle> CardScreenRects = new List<Rectangle>();
        public int HotCard = -1, HotZone = -1;
        public bool Expanded;
        Rectangle final;
        float scale = 1;

        public StripForm() { BackColor = Back; }

        // Lays out the pill or the panel for a monitor; returns its resting bounds in screen coordinates.
        // It is centred two thirds across the monitor, clear of remote-session toolbars at the centre.
        public Rectangle Build(Screen screen, float dpiScale, bool expanded)
        {
            scale = dpiScale;
            Expanded = expanded;
            var mon = screen.Bounds;
            CardScreenRects.Clear();
            ZoneScreenRects.Clear();
            HotCard = HotZone = -1;
            int cardW = 0, cardH = 0, pad = (int)(12 * scale), w, h;
            if (expanded)
            {
                bool portrait = mon.Height > mon.Width;
                Layouts = portrait ? SnapLayout.Portrait : SnapLayout.Landscape;
                cardW = (int)((portrait ? 120 : 200) * scale);
                cardH = cardW * mon.Height / mon.Width;
                w = Layouts.Length * cardW + (Layouts.Length + 1) * pad;
                h = cardH + 2 * pad;
            }
            else
            {
                w = (int)(120 * scale);
                h = (int)(14 * scale);
            }
            int left = Math.Max(mon.Left, Math.Min(mon.Right - w, mon.Left + mon.Width * 2 / 3 - w / 2));
            final = new Rectangle(left, mon.Top, w, h);

            int inset = (int)(4 * scale), gap = Math.Max(2, (int)(4 * scale));
            for (int c = 0; expanded && c < Layouts.Length; c++)
            {
                var card = new Rectangle(final.Left + pad + c * (cardW + pad), final.Top + pad, cardW, cardH);
                CardScreenRects.Add(card);
                var area = Rectangle.Inflate(card, -inset, -inset);
                var zones = new Rectangle[Layouts[c].Zones.Length];
                for (int z = 0; z < zones.Length; z++)
                {
                    var f = Layouts[c].Zones[z];
                    int x0 = area.Left + (int)Math.Round(f.X * area.Width), x1 = area.Left + (int)Math.Round((f.X + f.Width) * area.Width);
                    int y0 = area.Top + (int)Math.Round(f.Y * area.Height), y1 = area.Top + (int)Math.Round((f.Y + f.Height) * area.Height);
                    zones[z] = Rectangle.FromLTRB(x0 + gap / 2, y0 + gap / 2, x1 - (gap - gap / 2), y1 - (gap - gap / 2));
                }
                ZoneScreenRects.Add(zones);
            }
            using (var path = RoundBottom(new Rectangle(0, 0, w, h), (int)((expanded ? 10 : 7) * scale))) Region = new Region(path);
            Invalidate();
            return final;
        }

        // Returns true when the hovered zone changed.
        public bool UpdateHot(Point cursor)
        {
            int hc = -1, hz = -1;
            for (int c = 0; c < ZoneScreenRects.Count && hc < 0; c++)
                for (int z = 0; z < ZoneScreenRects[c].Length; z++)
                    if (ZoneScreenRects[c][z].Contains(cursor)) { hc = c; hz = z; break; }
            if (hc == HotCard && hz == HotZone) return false;
            HotCard = hc; HotZone = hz;
            Invalidate();
            return true;
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Square top edge (flush with the screen edge), rounded bottom corners.
        static GraphicsPath RoundBottom(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddLine(r.Left, r.Top, r.Right, r.Top);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Border, Math.Max(1, scale)))
            using (var path = RoundBottom(new Rectangle(0, -1, Width - 1, Height), (int)((Expanded ? 10 : 7) * scale)))
                g.DrawPath(pen, path);
            if (!Expanded)
            {
                int gw = (int)(36 * scale), gh = Math.Max(2, (int)(3 * scale));
                using (var b = new SolidBrush(Grip))
                using (var path = RoundRect(new Rectangle((Width - gw) / 2, (Height - gh) / 2, gw, gh), Math.Max(1, gh / 2)))
                    g.FillPath(b, path);
                return;
            }
            for (int c = 0; c < CardScreenRects.Count; c++)
            {
                using (var b = new SolidBrush(CardBack))
                using (var path = RoundRect(ToClient(CardScreenRects[c]), (int)(6 * scale)))
                    g.FillPath(b, path);
                for (int z = 0; z < ZoneScreenRects[c].Length; z++)
                    using (var b = new SolidBrush(c == HotCard && z == HotZone ? Hot : ZoneFill))
                    using (var path = RoundRect(ToClient(ZoneScreenRects[c][z]), (int)(4 * scale)))
                        g.FillPath(b, path);
            }
        }

        // Relative to the resting position, so content slides with the window.
        Rectangle ToClient(Rectangle r) { return new Rectangle(r.Left - final.Left, r.Top - final.Top, r.Width, r.Height); }
    }

    class SnapBarContext : ApplicationContext
    {
        readonly Native.WinEventProc proc; // kept alive for the hook
        readonly IntPtr hook;
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 15 };
        readonly StripForm strip = new StripForm();
        readonly PreviewForm preview = new PreviewForm();
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;

        IntPtr dragged = IntPtr.Zero;
        Screen stripScreen;
        Rectangle stripBounds;

        public SnapBarContext()
        {
            proc = OnWinEvent;
            hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, proc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            poll.Tick += delegate { Poll(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Exit SnapBar", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Icon = MakeIcon(), Text = "SnapBar", ContextMenuStrip = menu, Visible = true };
            Log.Write("started, hook=" + hook);
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var b = new SolidBrush(Color.FromArgb(76, 194, 255)))
                {
                    g.Clear(Color.Transparent);
                    g.FillRectangle(b, 2, 4, 13, 24);
                    g.FillRectangle(b, 17, 4, 13, 11);
                    g.FillRectangle(b, 17, 17, 13, 11);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        protected override void ExitThreadCore()
        {
            Native.UnhookWinEvent(hook);
            tray.Visible = false;
            base.ExitThreadCore();
        }

        void OnWinEvent(IntPtr h, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || hwnd == IntPtr.Zero) return;
            if (evt == Native.EVENT_SYSTEM_MOVESIZESTART)
            {
                var root = Native.GetAncestor(hwnd, 2 /* GA_ROOT */);
                if (IsSnappableMove(root)) { dragged = root; poll.Start(); Log.Write("drag start " + root); }
            }
            else if (evt == Native.EVENT_SYSTEM_MOVESIZEEND && dragged != IntPtr.Zero)
            {
                EndDrag();
            }
        }

        bool IsSnappableMove(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == ownPid || (Native.GetWindowLong(hwnd, Native.GWL_STYLE) & Native.WS_THICKFRAME) == 0) return false;
            // Resizes start on a border (HTLEFT..HTBOTTOMRIGHT = 10..17); anything else is a move.
            Native.POINT p;
            Native.GetCursorPos(out p);
            IntPtr hit;
            var lp = new IntPtr((p.Y << 16) | (p.X & 0xFFFF));
            if (Native.SendMessageTimeout(hwnd, 0x84 /* WM_NCHITTEST */, IntPtr.Zero, lp, 2 /* ABORTIFHUNG */, 100, out hit) == IntPtr.Zero)
                return true;
            int code = hit.ToInt32();
            return code < 10 || code > 17;
        }

        static float DpiScale(Point p)
        {
            var mon = Native.MonitorFromPoint(new Native.POINT { X = p.X, Y = p.Y }, 2);
            uint dx, dy;
            return Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 ? dx / 96f : 1f;
        }

        const int SlideSteps = 6; // 6 x 15 ms drop-down
        int slideStep = SlideSteps;

        void ShowStrip(bool expanded, float s)
        {
            stripBounds = strip.Build(stripScreen, s, expanded);
            slideStep = expanded ? 1 : SlideSteps;
            strip.ShowAt(expanded ? Slid(stripBounds, slideStep) : stripBounds, IntPtr.Zero);
            if (expanded)
                Log.Write("strip " + R(stripBounds) + " cards " + string.Join(" ", strip.ZoneScreenRects.ConvertAll(z => string.Join(",", Array.ConvertAll(z, R)))));
        }

        static Rectangle Slid(Rectangle r, int step)
        {
            return new Rectangle(r.Left, r.Top - r.Height + r.Height * step / SlideSteps, r.Width, r.Height);
        }

        void Poll()
        {
            Native.POINT np;
            Native.GetCursorPos(out np);
            var p = new Point(np.X, np.Y);
            var screen = Screen.FromPoint(p);
            float s = DpiScale(p);

            // The pill follows the drag onto whichever monitor holds the cursor.
            if (stripScreen == null || !stripScreen.Equals(screen))
            {
                HideOverlays();
                stripScreen = screen;
                ShowStrip(false, s);
            }

            // Expand before the cursor reaches the very edge, so the Windows top-edge
            // maximize preview never starts competing with the panel.
            bool nearTop = p.Y <= screen.Bounds.Top + (int)(48 * s);
            if (!strip.Expanded)
            {
                if (nearTop || Rectangle.Inflate(stripBounds, (int)(80 * s), (int)(60 * s)).Contains(p)) ShowStrip(true, s);
                return;
            }
            if (slideStep < SlideSteps)
            {
                strip.ShowAt(Slid(stripBounds, ++slideStep), IntPtr.Zero);
                if (slideStep < SlideSteps) return;
            }
            if (!nearTop && !Rectangle.Inflate(stripBounds, (int)(40 * s), (int)(40 * s)).Contains(p))
            {
                if (preview.Visible) preview.Hide();
                ShowStrip(false, s);
                return;
            }
            if (strip.UpdateHot(p))
            {
                if (strip.HotCard < 0) preview.Hide();
                else preview.ShowAt(ZoneRect(stripScreen, strip.Layouts[strip.HotCard].Zones[strip.HotZone]), strip.Handle);
            }
        }

        static Rectangle ZoneRect(Screen screen, RectangleF f)
        {
            var wa = screen.WorkingArea;
            int x0 = wa.Left + (int)Math.Round(f.X * wa.Width), x1 = wa.Left + (int)Math.Round((f.X + f.Width) * wa.Width);
            int y0 = wa.Top + (int)Math.Round(f.Y * wa.Height), y1 = wa.Top + (int)Math.Round((f.Y + f.Height) * wa.Height);
            return Rectangle.FromLTRB(x0, y0, x1, y1);
        }

        void HideOverlays()
        {
            if (strip.Visible) strip.Hide();
            if (preview.Visible) preview.Hide();
            strip.HotCard = strip.HotZone = -1;
            stripScreen = null;
        }

        void EndDrag()
        {
            poll.Stop();
            var hwnd = dragged;
            dragged = IntPtr.Zero;
            bool hot = strip.Visible && strip.Expanded && strip.HotCard >= 0;
            Rectangle target = hot ? ZoneRect(stripScreen, strip.Layouts[strip.HotCard].Zones[strip.HotZone]) : Rectangle.Empty;
            HideOverlays();
            // Esc ends the move loop while the button is still held; leave that to Windows.
            bool cancelled = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
            Log.Write("drag end " + hwnd + " target " + (hot ? R(target) : "none") + (cancelled ? " cancelled" : ""));
            if (!hot || cancelled) return;
            var t = new System.Windows.Forms.Timer { Interval = 40 };
            t.Tick += delegate { t.Stop(); t.Dispose(); Place(hwnd, target); };
            t.Start();
        }

        // Positions the visible frame exactly on the target, compensating for DWM's invisible borders.
        static void Place(IntPtr hwnd, Rectangle r)
        {
            if (!Native.IsWindow(hwnd)) return;
            if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
            const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
            Native.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, flags);
            Native.RECT win, frame;
            if (Native.GetWindowRect(hwnd, out win) &&
                Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf(typeof(Native.RECT))) == 0)
            {
                int l = frame.Left - win.Left, t = frame.Top - win.Top, rr = win.Right - frame.Right, b = win.Bottom - frame.Bottom;
                Native.SetWindowPos(hwnd, IntPtr.Zero, r.Left - l, r.Top - t, r.Width + l + rr, r.Height + t + b, flags);
            }
            Log.Write("placed " + hwnd + " at " + R(r));
        }

        static string R(Rectangle r) { return r.Left + "," + r.Top + "," + r.Width + "x" + r.Height; }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (new Mutex(true, "Local\\SnapBar", out first))
            {
                if (!first) return;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
                Application.EnableVisualStyles();
                Application.Run(new SnapBarContext());
            }
        }
    }
}
