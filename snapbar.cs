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

        const float T = 1f / 3, TT = 2f / 3;
        public static readonly SnapLayout[] Landscape = {
            new SnapLayout(0,0,.5f,1, .5f,0,.5f,1),
            new SnapLayout(0,0,TT,1, TT,0,T,1),
            new SnapLayout(0,0,T,1, T,0,T,1, TT,0,T,1),
            new SnapLayout(0,0,.5f,1, .5f,0,.5f,.5f, .5f,.5f,.5f,.5f),
            new SnapLayout(0,0,.5f,.5f, .5f,0,.5f,.5f, 0,.5f,.5f,.5f, .5f,.5f,.5f,.5f),
        };
        public static readonly SnapLayout[] Portrait = {
            new SnapLayout(0,0,1,.5f, 0,.5f,1,.5f),
            new SnapLayout(0,0,1,T, 0,T,1,T, 0,TT,1,T),
            new SnapLayout(0,0,1,.5f, 0,.5f,.5f,.5f, .5f,.5f,.5f,.5f),
            new SnapLayout(0,0,.5f,.5f, .5f,0,.5f,.5f, 0,.5f,.5f,.5f, .5f,.5f,.5f,.5f),
        };
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
        public void ShowAt(Rectangle r)
        {
            if (Bounds != r) Bounds = r;
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
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

    class StripForm : OverlayForm
    {
        static readonly Color Back = Color.FromArgb(32, 32, 32), Border = Color.FromArgb(70, 70, 70),
            CardBack = Color.FromArgb(48, 48, 48), ZoneFill = Color.FromArgb(90, 90, 90), Hot = Color.FromArgb(76, 194, 255);

        public SnapLayout[] Layouts;
        public List<Rectangle[]> ZoneScreenRects = new List<Rectangle[]>(); // per card, in screen coords
        public List<Rectangle> CardScreenRects = new List<Rectangle>();
        public int HotCard = -1, HotZone = -1;
        float scale = 1;

        public StripForm() { BackColor = Back; }

        // Lays out cards for a monitor and returns the strip bounds in screen coordinates.
        public Rectangle Build(Screen screen, float dpiScale)
        {
            scale = dpiScale;
            var mon = screen.Bounds;
            bool portrait = mon.Height > mon.Width;
            Layouts = portrait ? SnapLayout.Portrait : SnapLayout.Landscape;
            int cardW = (int)((portrait ? 64 : 112) * scale);
            int cardH = cardW * mon.Height / mon.Width;
            int pad = (int)(10 * scale), inset = (int)(4 * scale), gap = Math.Max(2, (int)(3 * scale));
            int w = Layouts.Length * cardW + (Layouts.Length + 1) * pad, h = cardH + 2 * pad;
            var strip = new Rectangle(mon.Left + (mon.Width - w) / 2, mon.Top + (int)(8 * scale), w, h);

            CardScreenRects.Clear();
            ZoneScreenRects.Clear();
            for (int c = 0; c < Layouts.Length; c++)
            {
                var card = new Rectangle(strip.Left + pad + c * (cardW + pad), strip.Top + pad, cardW, cardH);
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
            HotCard = HotZone = -1;
            using (var path = RoundRect(new Rectangle(0, 0, w, h), (int)(8 * scale))) Region = new Region(path);
            return strip;
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

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Border, Math.Max(1, scale)))
            using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), (int)(8 * scale)))
                g.DrawPath(pen, path);
            for (int c = 0; c < CardScreenRects.Count; c++)
            {
                using (var b = new SolidBrush(CardBack))
                using (var path = RoundRect(ToClient(CardScreenRects[c]), (int)(5 * scale)))
                    g.FillPath(b, path);
                for (int z = 0; z < ZoneScreenRects[c].Length; z++)
                    using (var b = new SolidBrush(c == HotCard && z == HotZone ? Hot : ZoneFill))
                    using (var path = RoundRect(ToClient(ZoneScreenRects[c][z]), (int)(3 * scale)))
                        g.FillPath(b, path);
            }
        }

        Rectangle ToClient(Rectangle r) { return new Rectangle(r.Left - Left, r.Top - Top, r.Width, r.Height); }
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

        void Poll()
        {
            Native.POINT np;
            Native.GetCursorPos(out np);
            var p = new Point(np.X, np.Y);
            var screen = Screen.FromPoint(p);
            float s = DpiScale(p);
            bool nearTop = p.Y <= screen.Bounds.Top + (int)(24 * s);

            if (!strip.Visible || stripScreen == null || !stripScreen.Equals(screen))
            {
                if (!nearTop) { HideOverlays(); return; }
                stripScreen = screen;
                stripBounds = strip.Build(screen, s);
                strip.ShowAt(stripBounds);
                Log.Write("strip " + R(stripBounds) + " cards " + string.Join(" ", strip.ZoneScreenRects.ConvertAll(z => string.Join(",", Array.ConvertAll(z, R)))));
            }
            else if (!nearTop && !Rectangle.Inflate(stripBounds, (int)(24 * s), (int)(24 * s)).Contains(p))
            {
                HideOverlays();
                return;
            }

            if (strip.UpdateHot(p))
            {
                if (strip.HotCard < 0) preview.Hide();
                else
                {
                    preview.ShowAt(ZoneRect(stripScreen, strip.Layouts[strip.HotCard].Zones[strip.HotZone]));
                    strip.ShowAt(stripBounds); // keep the strip above the preview
                }
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
            bool hot = strip.Visible && strip.HotCard >= 0;
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
