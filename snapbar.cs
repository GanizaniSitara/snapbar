// SnapBar: Windows 11-style snap bar for Windows 10.
// Drag a window toward the top edge of a monitor and a strip of layouts drops down;
// hover a zone to preview it and release to place the window there. Esc, resizes and
// drops elsewhere are left to Windows. One process, a tray icon, no services.
// Run with --log (or SNAPBAR_LOG=1) to log geometry and cursor positions to %LOCALAPPDATA%\snapbar\snapbar.log.
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
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
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

        public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint tid);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern uint GetTickCount();

        public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_ASYNCWINDOWPOS = 0x4000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const int GWL_STYLE = -16, WS_THICKFRAME = 0x40000, SW_RESTORE = 9;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    }

    static class Log
    {
        static readonly string path = Environment.GetEnvironmentVariable("SNAPBAR_LOG") == "1"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "snapbar", "snapbar.log") : null;

        static readonly System.Text.StringBuilder buffer = new System.Text.StringBuilder();

        public static bool Enabled { get { return path != null; } }

        // Buffered in memory; file writes happen only on Flush (drag end), never mid-drag.
        public static void Write(string msg)
        {
            if (path == null) return;
            buffer.Append(DateTime.Now.ToString("HH:mm:ss.fff ")).Append(msg).Append(Environment.NewLine);
        }

        // A line that already carries its own timestamp.
        public static void WriteRaw(string line)
        {
            if (path == null) return;
            buffer.Append(line).Append(Environment.NewLine);
        }

        static readonly System.Collections.Concurrent.ConcurrentQueue<string> pending = new System.Collections.Concurrent.ConcurrentQueue<string>();
        static readonly object fileLock = new object();

        // Writes on a worker thread so disk or antivirus latency never reaches the UI thread;
        // chunks are queued and drained in order under one lock.
        public static void Flush()
        {
            if (path == null || buffer.Length == 0) return;
            pending.Enqueue(buffer.ToString());
            buffer.Clear();
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (fileLock)
                {
                    string text;
                    while (pending.TryDequeue(out text))
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            File.AppendAllText(path, text);
                        }
                        catch (IOException) { }
                    }
                }
            });
        }
    }

    // Debug (--log only): records raw mouse input during a drag, to tell a paused hand from late input.
    // The low-level hook runs on its own thread so a busy UI thread can never delay system input.
    // Each line: event position, the input's own timestamp, and how late the hook received it.
    static class RawMouse
    {
        public static volatile bool Recording;
        static readonly System.Collections.Concurrent.ConcurrentQueue<string> lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        static Native.HookProc proc; // kept alive for the hook
        static IntPtr hook;

        public static void Start()
        {
            var t = new Thread(delegate ()
            {
                proc = Callback;
                hook = Native.SetWindowsHookEx(14 /* WH_MOUSE_LL */, proc, Native.GetModuleHandle(null), 0);
                Application.Run();
            });
            t.IsBackground = true;
            t.Start();
        }

        static IntPtr Callback(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && Recording && w.ToInt32() == 0x200 /* WM_MOUSEMOVE */)
            {
                var m = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(Native.MSLLHOOKSTRUCT));
                lines.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff ") + "raw " + m.pt.X + "," + m.pt.Y + " t=" + m.time + " late=" + unchecked((int)(Native.GetTickCount() - m.time)) + "ms");
            }
            return Native.CallNextHookEx(hook, code, w, l);
        }

        // Moves the recorded lines into the log (called on the UI thread at drag end).
        public static void Drain()
        {
            string s;
            while (lines.TryDequeue(out s)) Log.WriteRaw(s);
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
        static readonly Dictionary<string, SnapLayout> Named = new Dictionary<string, SnapLayout>(StringComparer.OrdinalIgnoreCase) {
            { "2x2",     new SnapLayout(0,0,.5f,.5f, .5f,0,.5f,.5f, 0,.5f,.5f,.5f, .5f,.5f,.5f,.5f) },
            { "halves",  new SnapLayout(0,0,.5f,1, .5f,0,.5f,1) },
            { "rows",    new SnapLayout(0,0,1,.5f, 0,.5f,1,.5f) },
            { "thirds",  new SnapLayout(0,0,T,1, T,0,T,1, TT,0,T,1) },
            { "2/3+1/3", new SnapLayout(0,0,TT,1, TT,0,T,1) },
            { "1/3+2/3", new SnapLayout(0,0,T,1, T,0,TT,1) },
        };

        // The layouts shown, in order, on every monitor: from snapbar.ini next to the exe
        // ("layout = NAME" lines), or just 2x2 when the file is missing or names nothing known.
        public static SnapLayout[] Landscape = { Named["2x2"] };
        public static SnapLayout[] Portrait { get { return Landscape; } }

        public static void Load()
        {
            var path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "snapbar.ini");
            if (!File.Exists(path)) return;
            var list = new List<SnapLayout>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq < 0 || !line.Substring(0, eq).Trim().Equals("layout", StringComparison.OrdinalIgnoreCase)) continue;
                var name = line.Substring(eq + 1).Trim();
                SnapLayout l;
                if (Named.TryGetValue(name, out l)) list.Add(l);
                else Log.Write("snapbar.ini: unknown layout '" + name + "' (known: " + string.Join(", ", Named.Keys) + ")");
            }
            if (list.Count > 0) Landscape = list.ToArray();
            Log.Write("layouts from snapbar.ini: " + list.Count);
        }
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
        // Debug: time painting, the only work done outside the poll tick.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg != 0x000F || !Log.Enabled) { base.WndProc(ref m); return; }
            var sw = Stopwatch.StartNew();
            base.WndProc(ref m);
            if (sw.ElapsedMilliseconds > 15) Log.Write("paint " + GetType().Name + " " + sw.ElapsedMilliseconds + "ms");
        }

        // Replaces the window shape, freeing the previous region's GDI handle.
        protected void SetShape(GraphicsPath path)
        {
            var old = Region;
            Region = new Region(path);
            if (old != null) old.Dispose();
        }

        // Shows at r directly below `above` in z-order (or at the top of the topmost band), in one call.
        public static bool NoUi; // debug: --no-ui runs the logic and logging without showing anything

        public void ShowAt(Rectangle r, IntPtr above)
        {
            if (NoUi) return;
            if (!IsHandleCreated) CreateHandle();
            if (Bounds == r && Visible) return;
            Native.SetWindowPos(Handle, above == IntPtr.Zero ? Native.HWND_TOPMOST : above, r.Left, r.Top, r.Width, r.Height,
                Native.SWP_NOACTIVATE | 0x40 /* SWP_SHOWWINDOW */);
            if (!Visible) Visible = true;
        }
    }

    // Snap preview: a translucent rounded rectangle slightly inset from the target zone.
    class PreviewForm : OverlayForm
    {
        public PreviewForm()
        {
            BackColor = Color.FromArgb(0, 120, 212);
            Opacity = 0.25;
        }
        public void ShowZone(Rectangle zone, float scale, IntPtr above)
        {
            var r = Rectangle.Inflate(zone, -(int)(8 * scale), -(int)(8 * scale));
            if (r.Size != Size)
                using (var path = Shapes.Round(new Rectangle(0, 0, r.Width, r.Height), (int)(8 * scale))) SetShape(path);
            ShowAt(r, above);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.White, 2))
            using (var path = Shapes.Round(new Rectangle(1, 1, Width - 3, Height - 3), 8))
                e.Graphics.DrawPath(pen, path);
        }
    }

    static class Shapes
    {
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            return Round(r, radius, true, true, true, true);
        }

        // Rounds only the chosen corners (top-left, top-right, bottom-right, bottom-left).
        public static GraphicsPath Round(Rectangle r, int radius, bool tl, bool tr, bool br, bool bl)
        {
            var p = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (tl) p.AddArc(r.Left, r.Top, d, d, 180, 90); else p.AddLine(r.Left, r.Top, r.Left, r.Top);
            if (tr) p.AddArc(r.Right - d, r.Top, d, d, 270, 90); else p.AddLine(r.Right, r.Top, r.Right, r.Top);
            if (br) p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); else p.AddLine(r.Right, r.Bottom, r.Right, r.Bottom);
            if (bl) p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); else p.AddLine(r.Left, r.Bottom, r.Left, r.Bottom);
            p.CloseFigure();
            return p;
        }
    }

    // Windows 11-style snap bar: a compact panel of layouts hanging from the top edge of the screen.
    // Colours are sampled from Microsoft's own screenshot of the Windows 11 snap bar (light theme).
    class StripForm : OverlayForm
    {
        static readonly Color Back = Color.FromArgb(238, 239, 240), Border = Color.FromArgb(200, 203, 206), Grip = Color.FromArgb(131, 138, 143),
            ZoneFill = Color.FromArgb(212, 216, 220), ZoneEdge = Color.FromArgb(131, 138, 143),
            Hot = Color.FromArgb(0, 120, 212), HotEdge = Color.FromArgb(0, 90, 170);

        public SnapLayout[] Layouts;
        public List<Rectangle[]> ZoneScreenRects = new List<Rectangle[]>(); // per card, in screen coords
        public List<Rectangle> CardScreenRects = new List<Rectangle>();
        public int HotCard = -1, HotZone = -1;
        public bool Expanded;
        int pad;
        float scale = 1;

        public StripForm() { BackColor = Back; }

        // Lays out the pill or the panel for a monitor; returns its bounds in screen coordinates.
        // Centred two thirds across the monitor, clear of remote-session toolbars at the centre.
        public Rectangle Build(Screen screen, float dpiScale, bool expanded)
        {
            scale = dpiScale;
            Expanded = expanded;
            var mon = screen.Bounds;
            CardScreenRects.Clear();
            ZoneScreenRects.Clear();
            HotCard = HotZone = -1;
            pad = (int)(8 * scale);
            int cardW = 0, cardH = 0, w, h;
            if (expanded)
            {
                bool portrait = mon.Height > mon.Width;
                Layouts = portrait ? SnapLayout.Portrait : SnapLayout.Landscape;
                cardW = (int)((portrait ? 56 : 96) * scale);
                cardH = cardW * mon.Height / mon.Width;
                w = Layouts.Length * cardW + (Layouts.Length + 1) * pad;
                h = cardH + 2 * pad;
            }
            else
            {
                w = (int)(96 * scale);
                h = (int)(10 * scale);
            }
            int left = Math.Max(mon.Left, Math.Min(mon.Right - w, mon.Left + mon.Width * 2 / 3 - w / 2));
            var bounds = new Rectangle(left, mon.Top, w, h); // glued to the top edge

            int gap = Math.Max(2, (int)(3 * scale));
            for (int c = 0; expanded && c < Layouts.Length; c++)
            {
                var card = new Rectangle(bounds.Left + pad + c * (cardW + pad), bounds.Top + pad, cardW, cardH);
                CardScreenRects.Add(card);
                var zones = new Rectangle[Layouts[c].Zones.Length];
                for (int z = 0; z < zones.Length; z++)
                {
                    var f = Layouts[c].Zones[z];
                    int x0 = card.Left + (int)Math.Round(f.X * card.Width), x1 = card.Left + (int)Math.Round((f.X + f.Width) * card.Width);
                    int y0 = card.Top + (int)Math.Round(f.Y * card.Height), y1 = card.Top + (int)Math.Round((f.Y + f.Height) * card.Height);
                    zones[z] = Rectangle.FromLTRB(x0 + gap / 2, y0 + gap / 2, x1 - (gap - gap / 2), y1 - (gap - gap / 2));
                }
                ZoneScreenRects.Add(zones);
            }
            if (Bounds != bounds) Bounds = bounds;
            using (var path = Shapes.Round(new Rectangle(0, 0, w, h), (int)((expanded ? 8 : 5) * scale), false, false, true, true)) SetShape(path);
            Invalidate();
            return bounds;
        }

        // Anywhere over a card (including its padding) picks the nearest zone, so there are no
        // dead gaps that make the preview flicker. Returns true when the hovered zone changed.
        public bool UpdateHot(Point cursor)
        {
            int hc = -1, hz = -1;
            for (int c = 0; c < CardScreenRects.Count && hc < 0; c++)
            {
                if (!Rectangle.Inflate(CardScreenRects[c], pad, pad).Contains(cursor)) continue;
                double best = double.MaxValue;
                for (int z = 0; z < ZoneScreenRects[c].Length; z++)
                {
                    var r = ZoneScreenRects[c][z];
                    double dx = Math.Max(0, Math.Max(r.Left - cursor.X, cursor.X - r.Right));
                    double dy = Math.Max(0, Math.Max(r.Top - cursor.Y, cursor.Y - r.Bottom));
                    double d = dx * dx + dy * dy;
                    if (d < best) { best = d; hc = c; hz = z; }
                }
            }
            if (hc == HotCard && hz == HotZone) return false;
            HotCard = hc; HotZone = hz;
            Invalidate();
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Border, Math.Max(1, scale)))
            using (var path = Shapes.Round(new Rectangle(0, -2, Width - 1, Height + 1), (int)((Expanded ? 8 : 5) * scale), false, false, true, true))
                g.DrawPath(pen, path);
            if (!Expanded)
            {
                int gw = (int)(32 * scale), gh = Math.Max(2, (int)(2 * scale));
                using (var b = new SolidBrush(Grip)) g.FillRectangle(b, (Width - gw) / 2, (Height - gh) / 2, gw, gh);
                return;
            }
            // As on Windows 11, each tile rounds only the corners on the outside of its layout.
            int radius = (int)(4 * scale);
            for (int c = 0; c < CardScreenRects.Count; c++)
            {
                var card = CardScreenRects[c];
                int slack = (int)(3 * scale);
                for (int z = 0; z < ZoneScreenRects[c].Length; z++)
                {
                    var zr = ZoneScreenRects[c][z];
                    bool left = zr.Left - card.Left <= slack, right = card.Right - zr.Right <= slack;
                    bool top = zr.Top - card.Top <= slack, bottom = card.Bottom - zr.Bottom <= slack;
                    bool hot = c == HotCard && z == HotZone;
                    var r = ToClient(zr);
                    r.Width -= 1; r.Height -= 1;
                    using (var b = new SolidBrush(hot ? Hot : ZoneFill))
                    using (var pen = new Pen(hot ? HotEdge : ZoneEdge, Math.Max(1, scale)))
                    using (var path = Shapes.Round(r, radius, top && left, top && right, bottom && right, bottom && left))
                    {
                        g.FillPath(b, path);
                        g.DrawPath(pen, path);
                    }
                }
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
            WarmUp();
            Log.Write("started, hook=" + hook); Log.Flush();
        }

        // Create, lay out and paint both overlays once, off-screen, so the first drag opens instantly.
        void WarmUp()
        {
            var sw = Stopwatch.StartNew();
            var screen = Screen.PrimaryScreen;
            var far = new Point(-32000, -32000);
            foreach (bool expanded in new[] { false, true })
            {
                var r = strip.Build(screen, 1, expanded);
                strip.ShowAt(new Rectangle(far, r.Size), IntPtr.Zero);
                strip.Update();
            }
            strip.Hide();
            preview.ShowZone(new Rectangle(far, new Size(400, 300)), 1, IntPtr.Zero);
            preview.Update();
            preview.Hide();
            Log.Write("warm-up " + sw.ElapsedMilliseconds + "ms");
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
                if (IsSnappableMove(root)) { dragged = root; armed = false; dragStart = Cursor.Position; RawMouse.Recording = Log.Enabled; tick.Reset(); poll.Start(); Log.Write("drag start " + root); }
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

        Point lastCursor;
        float lastScale = 1;
        bool armed;
        Point dragStart;

        void ShowStrip(bool expanded, float s)
        {
            stripBounds = strip.Build(stripScreen, s, expanded);
            strip.ShowAt(stripBounds, IntPtr.Zero);
            if (expanded)
                Log.Write("strip " + R(stripBounds) + " cards " + string.Join(" ", strip.ZoneScreenRects.ConvertAll(z => string.Join(",", Array.ConvertAll(z, R)))));
        }

        readonly Stopwatch tick = new Stopwatch();

        void Poll()
        {
            // Debug: record when this thread was blocked or a poll ran slow.
            long since = tick.ElapsedMilliseconds;
            if (tick.IsRunning && since > 120) Log.Write("stall " + since + "ms");
            tick.Restart();
            PollOnce();
            long spent = tick.ElapsedMilliseconds;
            if (spent > 30) Log.Write("slow poll " + spent + "ms");
        }

        void PollOnce()
        {
            Native.POINT np;
            Native.GetCursorPos(out np);
            var p = new Point(np.X, np.Y);
            var screen = Screen.FromPoint(p);
            float s = DpiScale(p);
            lastScale = s;

            // Nothing is shown (or logged) until the drag comes up near the top edge: an overlay
            // appearing mid-drag makes every window drag on the desktop feel sluggish. Once there,
            // the full panel opens straight away, anywhere across the screen. The hide depth is
            // larger than the show depth so the panel does not flap on the boundary.
            // A drag that starts inside the band (a title bar near the top) stays quiet until it
            // has been below the band once, or has been pushed up to the top edge from where it
            // was grabbed, so grabbing a top-row window never pops the panel by itself.
            if (p != lastCursor) { Log.Write("cur " + p.X + "," + p.Y); lastCursor = p; }
            bool shown = stripScreen != null;
            if (p.Y > screen.Bounds.Top + (shown ? 280 : 200) * s)
            {
                armed = true;
                if (shown) { HideOverlays(); Log.Write("strip hidden"); }
                return;
            }
            if (!armed && p.Y <= screen.Bounds.Top + 40 * s &&
                Math.Abs(p.X - dragStart.X) + Math.Abs(p.Y - dragStart.Y) >= 40 * s) armed = true;
            if (!armed) return;

            // The panel follows the drag onto whichever monitor holds the cursor.
            if (stripScreen == null || !stripScreen.Equals(screen))
            {
                HideOverlays();
                stripScreen = screen;
                ShowStrip(true, s);
            }
            if (strip.UpdateHot(p))
            {
                if (strip.HotCard < 0) preview.Hide();
                else preview.ShowZone(ZoneRect(stripScreen, strip.Layouts[strip.HotCard].Zones[strip.HotZone]), s, strip.Handle);
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
            RawMouse.Recording = false;
            RawMouse.Drain();
            var hwnd = dragged;
            dragged = IntPtr.Zero;
            bool hot = (strip.Visible || OverlayForm.NoUi) && strip.Expanded && strip.HotCard >= 0;
            Rectangle target = hot ? ZoneRect(stripScreen, strip.Layouts[strip.HotCard].Zones[strip.HotZone]) : Rectangle.Empty;
            HideOverlays();
            // Esc ends the move loop while the button is still held; leave that to Windows.
            bool cancelled = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
            Log.Write("drag end " + hwnd + " target " + (hot ? R(target) : "none") + (cancelled ? " cancelled" : "")); Log.Flush();
            if (!hot || cancelled) return;
            var t = new System.Windows.Forms.Timer { Interval = 40 };
            var ended = Stopwatch.StartNew();
            t.Tick += delegate { t.Stop(); t.Dispose(); Log.Write("place after " + ended.ElapsedMilliseconds + "ms"); Place(hwnd, target); };
            t.Start();
        }

        // Positions the visible frame exactly on the target, compensating for DWM's invisible borders.
        // Never blocks on the target application: the invisible-border insets are measured first,
        // then restore and move are posted to the window's own thread (asynchronous requests).
        static void Place(IntPtr hwnd, Rectangle r)
        {
            var sw = Stopwatch.StartNew();
            if (!Native.IsWindow(hwnd)) return;
            int l = 0, t = 0, rr = 0, b = 0;
            Native.RECT win, frame;
            if (!Native.IsZoomed(hwnd) && Native.GetWindowRect(hwnd, out win) &&
                Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf(typeof(Native.RECT))) == 0)
            {
                l = frame.Left - win.Left; t = frame.Top - win.Top; rr = win.Right - frame.Right; b = win.Bottom - frame.Bottom;
            }
            else
            {
                Native.ShowWindowAsync(hwnd, Native.SW_RESTORE);
                l = rr = b = (int)(7 * DpiScale(r.Location)); // standard Windows 10 invisible borders
            }
            Native.SetWindowPos(hwnd, IntPtr.Zero, r.Left - l, r.Top - t, r.Width + l + rr, r.Height + t + b,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
            Log.Write("placed " + hwnd + " at " + R(r) + " in " + sw.ElapsedMilliseconds + "ms"); Log.Flush();
        }

        static string R(Rectangle r) { return r.Left + "," + r.Top + "," + r.Width + "x" + r.Height; }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (Array.IndexOf(args, "--log") >= 0) Environment.SetEnvironmentVariable("SNAPBAR_LOG", "1");
            OverlayForm.NoUi = Array.IndexOf(args, "--no-ui") >= 0;
            SnapLayout.Load();
            bool first;
            using (new Mutex(true, "Local\\SnapBar", out first))
            {
                if (!first) return;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
                Application.EnableVisualStyles();
                if (Environment.GetEnvironmentVariable("SNAPBAR_LOG") == "1") RawMouse.Start();
                Application.Run(new SnapBarContext());
            }
        }
    }
}
