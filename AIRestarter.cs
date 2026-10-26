using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("AI 工具重启器")]
[assembly: System.Reflection.AssemblyDescription("一键彻底重启 AI 编程桌面工具")]
[assembly: System.Reflection.AssemblyProduct("AI 工具重启器")]
[assembly: System.Reflection.AssemblyVersion("1.5.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.5.0.0")]

namespace AIRestarterForms
{
    internal static class Program
    {
        internal const string WindowTitle = "AI 工具重启器";
        private const string MutexName = "Local\\AIRestarter.SingleInstance";

        [STAThread]
        private static void Main()
        {
            CleanHostEnvironment();
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    ActivateExisting();
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }

        // 快捷方式启动走 ShellExecute，子进程只能继承本进程环境，
        // 所以必须在启动应用之前把宿主终端注入的身份变量清掉，否则会被认成别的产品。
        private static void CleanHostEnvironment()
        {
            var names = new List<string>();
            foreach (string name in Environment.GetEnvironmentVariables().Keys) names.Add(name);
            foreach (string name in names)
            {
                if (name.StartsWith("QODER", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "NODE_OPTIONS", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable(name, null);
                }
            }
        }

        // 托盘隐藏时窗口还在，只是不可见且不在任务栏；叫出来要让程序自己恢复状态，
        // 光靠 ShowWindow 会把 ShowInTaskbar=false 的窗口还原成一个没有任务栏按钮的怪东西。
        internal static readonly int ShowFromTrayMessage = RegisterWindowMessage("AIRestarter.ShowFromTray");

        private static void ActivateExisting()
        {
            for (int i = 0; i < 20; i++)
            {
                IntPtr h = FindWindow(null, WindowTitle);
                if (h != IntPtr.Zero)
                {
                    // 只通知程序自己恢复（含 ShowInTaskbar、Show、Activate）。
                    // 这里再调 ShowWindow 会把它刚恢复好的状态再改一遍，反而还原不出来。
                    PostMessage(h, ShowFromTrayMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                Thread.Sleep(200);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    internal sealed class MainForm : Form
    {
        private const string LogDirName = "AIRestarter";
        private static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LogDirName);
        private static readonly string LogPath = Path.Combine(LogDir, "log.txt");
        private static readonly string OrderPath = Path.Combine(LogDir, "order.txt");
        private static readonly object LogLock = new object();

        // C:\Windows\System32\ —— 用来认出"快捷方式指向的是系统宿主进程"而不是应用本体
        private static readonly string SystemDirectoryPrefix =
            Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\";

        // ---- 暖白基调。层次靠"背景渐变 + 卡片阴影 + 描边浓淡"拉开，不引入新色相 ----
        // 列表几何常量：BuildLayout 与拖动换位判定共用同一组值，
        // 否则改了行高却漏改阈值，拖拽会在错误的位置提前换位
        private const float ListTopDip = 124f;
        private const float RowHeightDip = 70f;
        private const float RowStepDip = 78f;

        // 图标托盘边长（DIP），BuildLayout 与 PrewarmIcons 共用，改一处即可
        private const float IconTileDip = 40f;

        // 面板尺寸与列表/页脚边界：后四个都从前两个推出来，
        // 免得改了窗口高度后列表底边和页脚分隔线各走各路
        private const float ClientWidthDip = 580f;
        private const float ClientHeightDip = 640f;
        private const float ListWidthDip = ClientWidthDip - 48f;          // 532 = 580 - 20 左边距 - 28 右侧留位
        private const float FooterDividerDip = ClientHeightDip - 54f;     // 586
        private const float ListHeightDip = FooterDividerDip - ListTopDip; // 462
        private const float FooterRowDip = FooterDividerDip + 10f;        // 596
        // 页脚说明文字右侧让开的宽度：20 边距 + 36 版本热区 + 56 日志热区 + 38 间隔
        private const float FooterStatusGutterDip = 150f;

        private static readonly Color BgColor = Color.FromArgb(250, 249, 246);
        private static readonly Color BgTopColor = Color.FromArgb(253, 252, 250);
        private static readonly Color BgBottomColor = Color.FromArgb(244, 242, 236);
        private static readonly Color CardColor = Color.White;
        // 未运行的卡片往背景里收一点，运行中的才"浮"出来
        private static readonly Color CardIdleColor = Color.FromArgb(251, 250, 248);
        private static readonly Color BorderColor = Color.FromArgb(234, 230, 221);
        private static readonly Color BorderIdleColor = Color.FromArgb(241, 238, 232);
        private static readonly Color BorderStrongColor = Color.FromArgb(225, 220, 210);
        private static readonly Color ShadowColor = Color.FromArgb(26, 78, 62, 44);
        private static readonly Color TextColor = Color.FromArgb(32, 29, 23);
        private static readonly Color TextIdleColor = Color.FromArgb(128, 122, 111);
        private static readonly Color SubColor = Color.FromArgb(124, 118, 107);
        private static readonly Color FaintColor = Color.FromArgb(157, 151, 140);
        private static readonly Color FaintIdleColor = Color.FromArgb(184, 179, 170);
        private static readonly Color AccentColor = Color.FromArgb(232, 98, 42);
        private static readonly Color AccentDarkColor = Color.FromArgb(211, 82, 30);
        private static readonly Color AccentTopColor = Color.FromArgb(243, 122, 66);
        private static readonly Color AccentHoverTopColor = Color.FromArgb(224, 88, 36);
        private static readonly Color AccentHoverBottomColor = Color.FromArgb(196, 72, 24);
        private static readonly Color GreenColor = Color.FromArgb(43, 154, 101);
        private static readonly Color GreenHaloColor = Color.FromArgb(46, 43, 154, 101);
        private static readonly Color IdleDotColor = Color.FromArgb(190, 184, 173);
        private static readonly Color ErrorColor = Color.FromArgb(189, 67, 39);
        private static readonly Color RestartTextColor = Color.FromArgb(201, 81, 29);
        private static readonly Color RestartBorderColor = Color.FromArgb(240, 209, 191);
        private static readonly Color RestartBgColor = Color.FromArgb(253, 244, 237);
        private static readonly Color RestartHoverBgColor = Color.FromArgb(250, 233, 221);
        private static readonly Color OpenTextColor = Color.FromArgb(120, 114, 103);
        private static readonly Color ButtonBorderColor = Color.FromArgb(226, 221, 211);
        private static readonly Color CloseTextColor = Color.FromArgb(138, 131, 120);
        private static readonly Color CloseHoverColor = Color.FromArgb(178, 74, 42);
        private static readonly Color HoverBgColor = Color.FromArgb(255, 252, 247);
        private static readonly Color HoverBorderColor = Color.FromArgb(233, 220, 205);
        private static readonly Color DragBgColor = Color.FromArgb(255, 244, 236);
        private static readonly Color DisabledBgColor = Color.FromArgb(234, 229, 219);
        private static readonly Color DisabledTextColor = Color.FromArgb(164, 157, 145);
        private static readonly Color ScrollTrackColor = Color.FromArgb(241, 238, 232);
        private static readonly Color ScrollThumbColor = Color.FromArgb(199, 191, 177);
        private static readonly Color HeaderHoverColor = Color.FromArgb(239, 235, 227);
        private static readonly Color HeaderCloseHoverColor = Color.FromArgb(243, 227, 219);
        private static readonly Color RowCloseHoverBgColor = Color.FromArgb(251, 238, 231);
        private static readonly Color RowActionHoverBgColor = Color.FromArgb(247, 241, 233);
        private static readonly Color DisabledBorderColor = Color.FromArgb(232, 227, 219);
        private static readonly Color GripColor = Color.FromArgb(196, 189, 177);
        private static readonly Color GripHoverColor = Color.FromArgb(158, 150, 137);
        // 真实应用图标统一放在浅色圆角托盘里，避免各家图标自带白底时和卡片糊在一起
        private static readonly Color IconTileColor = Color.FromArgb(255, 255, 255);
        private static readonly Color IconTileIdleColor = Color.FromArgb(247, 246, 243);
        private static readonly Color IconTileBorderColor = Color.FromArgb(233, 229, 220);
        // 未运行图标的去饱和矩阵：约 30% 灰化 + alpha 0.85，保留品牌色但退到后面。
        // GDI+ 按 [R G B A 1] × matrix 计算，行是输入通道、列是输出通道，
        // 写成它的转置不会报错，只会把近中性色往绿里推（ChatGPT 的蓝黑标就是这样变绿的）
        private static readonly System.Drawing.Imaging.ColorMatrix IdleIconMatrix = new System.Drawing.Imaging.ColorMatrix(new float[][] {
            new float[] { 0.790f, 0.090f, 0.034f, 0f, 0f },
            new float[] { 0.176f, 0.876f, 0.176f, 0f, 0f },
            new float[] { 0.034f, 0.034f, 0.734f, 0f, 0f },
            new float[] { 0f, 0f, 0f, 0.85f, 0f },
            new float[] { 0f, 0f, 0f, 0f, 1f }
        });

        private readonly List<AppDef> _catalog = new List<AppDef>();
        private readonly List<AppVM> _apps = new List<AppVM>();
        private readonly List<AppVM> _rowOrder = new List<AppVM>();
        private readonly List<RowLayout> _layouts = new List<RowLayout>();
        private readonly Dictionary<int, SolidBrush> _brushes = new Dictionary<int, SolidBrush>();
        // 图标按 "来源|索引|尺寸" 缓存；值为 null 表示已尝试过且失败，避免每次重绘都再走一遍 IO
        private readonly Dictionary<string, IconEntry> _iconCache = new Dictionary<string, IconEntry>(StringComparer.OrdinalIgnoreCase);
        private List<string> _processNames = new List<string>();
        private Dictionary<string, string> _appIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Font _fontTitle;
        private Font _fontSub;
        private Font _fontName;
        private Font _fontPath;
        private Font _fontButton;
        private Font _fontPrimary;
        private Font _fontBadge;
        private Font _fontIcon;

        private int _dpi = 96;
        private float _scale = 1f;
        private System.Windows.Forms.Timer _timer;

        private Rectangle _headerDragRect;
        private Rectangle _refreshRect;
        private Rectangle _minRect;
        private Rectangle _closeRect;
        private Rectangle _primaryRect;
        private Rectangle _logRect;
        private Rectangle _versionRect;
        private Rectangle _listRect;
        private Rectangle _scrollTrackRect;
        private Rectangle _scrollThumbRect;
        private bool _needsScroll;
        private int _scrollRow;
        private int _maxScrollRow;
        private bool _scrollDragging;
        private int _scrollDragY;
        private int _scrollDragStartRow;

        private Hit _hover;
        private Hit _pressed;
        private bool _dragArmed;
        private bool _dragging;
        private bool _orderChanged;
        private AppVM _dragVm;
        private Point _dragStart;

        private bool _busy;
        private AppVM _busyVm;
        private string _busyText;
        private NotifyIcon _tray;
        private bool _reallyExit;
        private bool _trayHintShown;
        private string _statusText = "就绪";
        private Color _statusColor = SubColor;
        private DateTime _statusUntil = DateTime.MinValue;
        private bool _trimmed;

        public MainForm()
        {
            Text = Program.WindowTitle;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            MaximizeBox = false;
            MinimizeBox = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = BgColor;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            BuildCatalog();
            LoadOrder();
            Log("启动");
            RescanApps(true);
            CreateTimer();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                cp.Style |= 0x00020000 | 0x00080000; // WS_MINIMIZEBOX | WS_SYSMENU，保证任务栏点击能最小化
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _dpi = (int)GetDpiForWindow(Handle);
            if (_dpi <= 0) _dpi = 96;
            _scale = _dpi / 96f;
            CreateFonts();
            ApplyCorners();
            RestoreClientSize();
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
            // 图标提取走 shell，放在首帧之前预热，绘制时只查表不做 IO
            PrewarmIcons();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
            {
                if (!_trimmed)
                {
                    _trimmed = true;
                    TrimWorkingSet();
                }
            }
            else
            {
                _trimmed = false;
                RefreshStatuses();
                Invalidate();
            }
        }

        private static void TrimWorkingSet()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using (Process current = Process.GetCurrentProcess())
                {
                    SetProcessWorkingSetSize(current.Handle, -1, -1);
                }
            }
            catch { }
        }

        // ---- 托盘 ----

        private void HideToTray()
        {
            EnsureTray();
            Hide();
            ShowInTaskbar = false;
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                if (_tray != null)
                {
                    _tray.BalloonTipTitle = Program.WindowTitle;
                    _tray.BalloonTipText = "已收进托盘。右键托盘图标可以完全退出。";
                    try { _tray.ShowBalloonTip(3000); } catch { }
                }
            }
        }

        private void ShowFromTray()
        {
            // 隐藏时 ShowInTaskbar 被置 false。WinForms 对这个属性有缓存，
            // 光调 Show() 有时不会真正把窗口拿回来，所以显式走一遍原生 ShowWindow。
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            if (!Visible) Show();
            ShowWindow(Handle, SW_SHOW);
            ShowWindow(Handle, SW_RESTORE);
            // SW_RESTORE 会把窗口还原成系统的"还原尺寸"，那个尺寸带着边框余量，
            // 比本程序自己算的 ClientSize 更大 —— 这就是从托盘回来时"下巴变长"的原因。
            // 恢复后必须把客户区尺寸重新钉回去，界面才对得上。
            RestoreClientSize();
            SetForegroundWindow(Handle);
            Activate();
            BringToFront();
            BuildLayout();
            Invalidate();
            Refresh();
            Log("面板已从托盘恢复");
        }

        // RestoreClientSize 是启动、从托盘恢复和 DPI 变更三处共用的尺寸逻辑：
        // 三处必须走同一段代码，否则两边算出来的界面高度会不一致。
        private void RestoreClientSize()
        {
            int w = Px(ClientWidthDip);
            int h = Px(ClientHeightDip);
            if (ClientSize.Width != w || ClientSize.Height != h)
            {
                ClientSize = new Size(w, h);
            }
        }

        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        private void EnsureTray()
        {
            if (_tray != null) return;
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开面板", null, delegate { ShowFromTray(); });
            menu.Items.Add("重启全部工具", null, delegate { ShowFromTray(); OnRestartAll(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("完全退出", null, delegate { ExitApp(); });

            _tray = new NotifyIcon();
            _tray.Text = Program.WindowTitle;
            _tray.Icon = LoadAppIcon();
            _tray.ContextMenuStrip = menu;
            // 左键单击 = 打开面板（Windows 托盘习惯）
            _tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowFromTray();
            };
            _tray.Visible = true;
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static Icon LoadAppIcon()
        {
            try
            {
                string exe = Application.ExecutablePath;
                Icon icon = Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
            catch { }
            return SystemIcons.Application;
        }

        private void ExitApp()
        {
            _reallyExit = true;
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            Close();
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 用户点 × / Alt+F4 / 任务栏关闭：一律收进托盘，除非明确要退出。
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            DisposeFonts();
            ClearIconCache();
            foreach (SolidBrush brush in _brushes.Values) brush.Dispose();
            _brushes.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.ShowFromTrayMessage)
            {
                Log("收到唤出消息 msg=" + m.Msg);
                ShowFromTray();
                m.Result = IntPtr.Zero;
                return;
            }
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED)
            {
                int newDpi = (int)((long)m.WParam >> 16) & 0xFFFF;
                if (newDpi > 0)
                {
                    _dpi = newDpi;
                    _scale = _dpi / 96f;
                    CreateFonts();
                    // 图标是按物理像素缓存的，跨屏后尺寸变了必须重取
                    ClearIconCache();
                }
                RECT suggested = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                Location = new Point(suggested.Left, suggested.Top);
                RestoreClientSize();
                ApplyCorners();
                BuildLayout();
                PrewarmIcons();
                Invalidate();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BgColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            BuildLayout();

            using (GraphicsPath border = RoundRect(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Px(10)))
            {
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, ClientSize.Width), Math.Max(2, ClientSize.Height)),
                           BgTopColor, BgBottomColor, LinearGradientMode.Vertical))
                {
                    g.FillPath(bg, border);
                }
                using (var pen = new Pen(BorderStrongColor, Math.Max(1f, Px(1))))
                {
                    g.DrawPath(pen, border);
                }
            }

            DrawHeader(g);
            DrawActionRow(g);
            DrawRows(g);
            DrawFooter(g);
        }

        private void DrawHeader(Graphics g)
        {
            Rectangle icon = new Rectangle(Px(20), Px(14), Px(32), Px(32));
            FillRoundGradient(g, icon, Px(10), AccentTopColor, AccentColor);
            TextRenderer.DrawText(g, "↻", _fontIcon, icon, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, "AI 工具重启器", _fontTitle, new Rectangle(Px(62), Px(11), Px(300), Px(26)), TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, "更新或改完配置后，一键彻底重启", _fontSub, new Rectangle(Px(62), Px(38), Px(320), Px(18)), SubColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            DrawHeaderButton(g, _refreshRect, "刷新", HitKind.Refresh);
            DrawIconButton(g, _minRect, false, HitKind.Min);
            DrawIconButton(g, _closeRect, true, HitKind.Close);
        }

        private void DrawHeaderButton(Graphics g, Rectangle rect, string text, HitKind kind)
        {
            Rectangle pill = new Rectangle(rect.X + Px(4), rect.Y + Px(1), rect.Width - Px(8), rect.Height - Px(2));
            if (IsHover(kind, null)) FillRound(g, pill, Px(7), HeaderHoverColor);
            TextRenderer.DrawText(g, text, _fontSub, pill, SubColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        private void DrawIconButton(Graphics g, Rectangle rect, bool close, HitKind kind)
        {
            Rectangle pill = new Rectangle(rect.X + Px(1), rect.Y, rect.Width - Px(2), rect.Height);
            if (IsHover(kind, null))
            {
                Color hover = close ? HeaderCloseHoverColor : HeaderHoverColor;
                FillRound(g, pill, Px(7), hover);
            }
            using (var pen = new Pen(close && IsHover(kind, null) ? CloseHoverColor : FaintColor, Math.Max(1f, Px(1.1f))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int cx = pill.X + pill.Width / 2;
                int cy = pill.Y + pill.Height / 2;
                int r = Px(5);
                if (close)
                {
                    g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                    g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
                }
                else
                {
                    g.DrawLine(pen, cx - r, cy, cx + r, cy);
                }
            }
        }

        private void DrawActionRow(Graphics g)
        {
            int running = _apps.Count(a => a.Installed && a.ProcessCount > 0);
            bool enabled = !_busy && running > 0;
            bool hover = enabled && IsHover(HitKind.Primary, null);
            if (enabled)
            {
                FillRoundGradient(g, _primaryRect, Px(9), hover ? AccentHoverTopColor : AccentTopColor,
                    hover ? AccentHoverBottomColor : AccentDarkColor);
            }
            else
            {
                FillRound(g, _primaryRect, Px(9), DisabledBgColor);
            }
            TextRenderer.DrawText(g, "重启全部运行中的", _fontPrimary, _primaryRect, enabled ? Color.White : DisabledTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            Rectangle statusRect = new Rectangle(_primaryRect.Right + Px(14), _primaryRect.Y, ClientSize.Width - _primaryRect.Right - Px(34), _primaryRect.Height);
            TextRenderer.DrawText(g, _statusText, _fontSub, statusRect, _statusColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        private void DrawRows(Graphics g)
        {
            if (_rowOrder.Count == 0)
            {
                TextRenderer.DrawText(g, "没有检测到支持的 AI 编程工具", _fontName,
                    new Rectangle(_listRect.X, _listRect.Y + Px(70), _listRect.Width, Px(24)), SubColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, "支持 ChatGPT / Claude / Qoder / DSH / WorkBuddy / ZCode / 千问办公 / OpenCode / Cursor / Windsurf / Trae / Kiro / Zed / Antigravity",
                    _fontPath, new Rectangle(_listRect.X + Px(20), _listRect.Y + Px(102), _listRect.Width - Px(40), Px(36)), FaintColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return;
            }

            GraphicsState state = g.Save();
            // 裁剪区比列表区四周各让出一点，否则卡片投影会在边界上被切一刀
            Rectangle clip = new Rectangle(_listRect.X - Px(8), _listRect.Y - Px(6), _listRect.Width + Px(16), _listRect.Height + Px(12));
            g.SetClip(clip);
            foreach (RowLayout row in _layouts) DrawRow(g, row);
            g.Restore(state);
            if (_needsScroll) DrawScrollbar(g);
        }

        private void DrawRow(Graphics g, RowLayout layout)
        {
            AppVM vm = layout.VM;
            bool hover = IsHover(HitKind.RowDrag, vm) || IsHover(HitKind.RowAction, vm) || IsHover(HitKind.RowClose, vm);
            bool dragging = _dragging && _dragVm == vm;
            bool busyRow = _busy && _busyVm == vm;
            bool running = vm.ProcessCount > 0;

            Color bg = dragging ? DragBgColor : (hover ? HoverBgColor : (running ? CardColor : CardIdleColor));
            Color border = dragging ? AccentColor : (hover ? HoverBorderColor : (running ? BorderColor : BorderIdleColor));

            // 只有运行中的行浮起来，未运行的贴在背景上，不用读文字就知道哪几个在跑
            if (running && !dragging && !hover) DrawCardShadow(g, layout.Rect);

            FillRound(g, layout.Rect, Px(12), bg);
            StrokeRound(g, layout.Rect, Px(12), border, dragging ? 1.6f : 1f);

            DrawAppIcon(g, layout.IconRect, vm, running);

            DrawGrip(g, layout.GripRect, vm);

            int textX = layout.Rect.X + Px(82);
            int textRight = (running ? layout.CloseRect.X : layout.ActionRect.X) - Px(10);
            int textWidth = Math.Max(Px(60), textRight - textX);
            Rectangle nameRect = new Rectangle(textX, layout.Rect.Y + Px(9), textWidth, Px(21));
            TextRenderer.DrawText(g, vm.Def.Name, _fontName, nameRect, running ? TextColor : TextIdleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            Rectangle statusRect = new Rectangle(textX, layout.Rect.Y + Px(31), textWidth, Px(15));
            if (running && !busyRow)
            {
                using (var halo = new SolidBrush(GreenHaloColor))
                {
                    g.FillEllipse(halo, statusRect.X - Px(2), statusRect.Y + Px(2.5f), Px(10), Px(10));
                }
            }
            Color dotColor = busyRow ? AccentColor : (running ? GreenColor : IdleDotColor);
            using (var brush = new SolidBrush(dotColor))
            {
                g.FillEllipse(brush, new Rectangle(statusRect.X, statusRect.Y + Px(4.5f), Px(6), Px(6)));
            }
            string status = busyRow ? (_busyText ?? "正在重启…") : (running ? "运行中" : "未运行");
            TextRenderer.DrawText(g, status, _fontSub, new Rectangle(statusRect.X + Px(12), statusRect.Y, statusRect.Width - Px(12), statusRect.Height),
                busyRow ? AccentColor : (running ? SubColor : FaintIdleColor),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            Rectangle pathRect = new Rectangle(textX, layout.Rect.Y + Px(47), textWidth, Px(14));
            TextRenderer.DrawText(g, vm.Launch.Display ?? string.Empty, _fontPath, pathRect, running ? FaintColor : FaintIdleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            if (running)
            {
                DrawRowButton(g, layout.CloseRect, "关闭", HitKind.RowClose, vm, false, !_busy);
            }
            string actionText = busyRow ? "…" : (running ? "重启" : "打开");
            DrawRowButton(g, layout.ActionRect, actionText, HitKind.RowAction, vm, running, !_busy);
        }

        private void DrawCardShadow(Graphics g, Rectangle rect)
        {
            int spread = Math.Max(2, Px(5));
            Rectangle outer = new Rectangle(rect.X - spread, rect.Y - spread + Px(2), rect.Width + spread * 2, rect.Height + spread * 2);
            using (GraphicsPath path = RoundRect(outer, Px(12) + spread))
            using (var brush = new PathGradientBrush(path))
            {
                brush.CenterColor = ShadowColor;
                brush.SurroundColors = new[] { Color.FromArgb(0, ShadowColor) };
                brush.CenterPoint = new PointF(rect.X + rect.Width / 2f, rect.Y + Px(2) + rect.Height / 2f);
                g.FillPath(brush, path);
            }
        }

        // 真实图标统一放在 40 DIP 的圆角托盘里。未运行的图标做轻度去饱和 + 降不透明度，
        // 和文字一起退到后面；自带底板的图标铺满托盘，不再另画白底描边。
        private void DrawAppIcon(Graphics g, Rectangle tile, AppVM vm, bool running)
        {
            IconEntry entry = GetIconEntry(vm, tile.Width);
            if (entry == null)
            {
                // 兜底：AppX 没有可解析路径、或提取失败时退回原来的字母色块
                Color brand = ColorFromHex(vm.Def.BadgeColor);
                if (!running) brand = Blend(brand, Color.FromArgb(228, 226, 221), 0.45f);
                FillRound(g, tile, Px(11), brand);
                TextRenderer.DrawText(g, vm.Def.Badge, _fontBadge, tile, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                return;
            }

            GraphicsState state = g.Save();
            using (GraphicsPath clip = RoundRect(tile, Px(11)))
            {
                g.SetClip(clip);
                if (entry.Opaque)
                {
                    DrawIconImage(g, entry.Image, tile, running);
                }
                else
                {
                    FillRound(g, tile, Px(11), running ? IconTileColor : IconTileIdleColor);
                    int inset = Math.Max(1, Px(3));
                    var inner = new Rectangle(tile.X + inset, tile.Y + inset, tile.Width - inset * 2, tile.Height - inset * 2);
                    DrawIconImage(g, entry.Image, inner, running);
                    StrokeRound(g, tile, Px(11), running ? IconTileBorderColor : BorderIdleColor, 1f);
                }
            }
            g.Restore(state);
        }

        private void DrawIconImage(Graphics g, Bitmap icon, Rectangle dst, bool running)
        {
            if (running)
            {
                g.DrawImage(icon, dst, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel);
                return;
            }
            using (var attrs = new System.Drawing.Imaging.ImageAttributes())
            {
                attrs.SetColorMatrix(IdleIconMatrix);
                g.DrawImage(icon, dst, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attrs);
            }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        private void DrawGrip(Graphics g, Rectangle rect, AppVM vm)
        {
            Color color = IsHover(HitKind.RowDrag, vm) ? GripHoverColor : GripColor;
            int dot = Math.Max(2, Px(2.6f));
            int gap = Math.Max(3, Px(6.5f));
            int left = rect.X + (rect.Width - (gap + dot)) / 2;
            int top = rect.Y + (rect.Height - (gap * 2 + dot)) / 2;
            using (var brush = new SolidBrush(color))
            {
                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 2; col++)
                    {
                        g.FillEllipse(brush, left + col * gap, top + row * gap, dot, dot);
                    }
                }
            }
        }

        private void DrawRowButton(Graphics g, Rectangle rect, string text, HitKind kind, AppVM vm, bool primary, bool enabled)
        {
            bool hover = enabled && IsHover(kind, vm);
            bool close = kind == HitKind.RowClose;
            Color border = close ? ButtonBorderColor : (primary ? RestartBorderColor : ButtonBorderColor);
            Color fg = close ? CloseTextColor : (primary ? RestartTextColor : OpenTextColor);
            // 运行中的"重启"是该行的主操作，给一层极浅的暖底；未运行的"打开"保持描边样式
            if (primary && enabled && !hover) FillRound(g, rect, Px(8), RestartBgColor);
            if (hover)
            {
                if (close)
                {
                    border = RestartBorderColor;
                    fg = CloseHoverColor;
                    FillRound(g, rect, Px(8), RowCloseHoverBgColor);
                }
                else
                {
                    FillRound(g, rect, Px(8), primary ? RestartHoverBgColor : RowActionHoverBgColor);
                }
            }
            if (!enabled)
            {
                border = DisabledBorderColor;
                fg = DisabledTextColor;
            }
            StrokeRound(g, rect, Px(8), border, 1f);
            TextRenderer.DrawText(g, text, _fontButton, rect, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        private void DrawScrollbar(Graphics g)
        {
            FillRound(g, _scrollTrackRect, Px(3), ScrollTrackColor);
            FillRound(g, _scrollThumbRect, Px(3), ScrollThumbColor);
        }

        private void DrawFooter(Graphics g)
        {
            using (var pen = new Pen(BorderColor, Math.Max(1f, Px(1))))
            {
                g.DrawLine(pen, Px(20), Px(FooterDividerDip), ClientSize.Width - Px(20), Px(FooterDividerDip));
            }
            TextRenderer.DrawText(g, "只管理桌面应用 · 拖动左侧圆点可排序 · 命令行版新开会话生效", _fontPath,
                new Rectangle(Px(20), Px(FooterRowDip), ClientSize.Width - Px(FooterStatusGutterDip), Px(22)), FaintColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, "日志", _fontPath, _logRect, IsHover(HitKind.Log, null) ? AccentDarkColor : FaintColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, "v1.5", _fontPath, _versionRect, FaintIdleColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        private void CreateTimer()
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 2500;
            _timer.Tick += delegate
            {
                if (_busy || _dragging || WindowState == FormWindowState.Minimized) return;
                RefreshStatuses();
                if (DateTime.Now >= _statusUntil) UpdateIdleStatus();
            };
            _timer.Start();
        }

        private void BuildCatalog()
        {
            _catalog.Clear();
            _catalog.Add(new AppDef
            {
                Key = "codex", Name = "ChatGPT", Badge = "C", BadgeColor = "#1D1D1F",
                AppIdPrefix = "OpenAI.Codex_", Names = new[] { "ChatGPT" }, Force = true
            });
            _catalog.Add(new AppDef
            {
                Key = "claude", Name = "Claude Desktop", Badge = "C", BadgeColor = "#C96442",
                AppIdPrefix = "Claude_", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%USERPROFILE%\Desktop\Claude.lnk",
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Claude.lnk"
                },
                Names = new[] { "claude" },
                PathPrefixes = new[]
                {
                    @"%ProgramFiles%\WindowsApps\Claude_",
                    @"%LOCALAPPDATA%\AnthropicClaude\",
                    @"%LOCALAPPDATA%\Programs\Claude\"
                },
                RequirePath = true, Force = true
            });
            _catalog.Add(new AppDef
            {
                Key = "qoder", Name = "Qoder CN", Badge = "Q", BadgeColor = "#4D6BFE",
                AppIdPrefix = "com.qodercn.app", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Qoder CN.lnk",
                    @"%USERPROFILE%\Desktop\Qoder CN.lnk"
                },
                Exes = new[] { @"%LOCALAPPDATA%\Qoder CN\Qoder CN Launcher\Qoder CN Launcher.exe" },
                Names = new[] { "Qoder CN" }, PathPrefixes = new[] { @"D:\QoderCN\Qoder CN\" }, Force = true
            });
            _catalog.Add(new AppDef
            {
                Key = "qoder-intl", Name = "Qoder", Badge = "Q", BadgeColor = "#8A5CF6",
                AppIdPrefix = "com.qoder.app", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Qoder.lnk",
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Qoder\Qoder.lnk",
                    @"%USERPROFILE%\Desktop\Qoder.lnk"
                },
                Exes = new[] { @"%LOCALAPPDATA%\Qoder\Qoder Launcher\Qoder Launcher.exe", @"D:\Qoder\Qoder\Qoder.exe" },
                Names = new[] { "Qoder" }, PathPrefixes = new[] { @"D:\Qoder\Qoder\" }, RequirePath = true, Force = true
            });
            _catalog.Add(new AppDef
            {
                Key = "qoder-ide", Name = "Qoder CN IDE", Badge = "Q", BadgeColor = "#6B7CFF",
                AppIdPrefix = "AlibabaCloud.QoderCN", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Qoder CN IDE.lnk",
                    @"%USERPROFILE%\Desktop\Qoder CN IDE.lnk"
                },
                Exes = new[] { @"D:\Qoder CN IDE\Qoder CN IDE\Qoder CN IDE.exe" },
                Names = new[] { "Qoder CN IDE", "QoderCN", "Qoder CN" }, PathPrefixes = new[] { @"D:\Qoder CN IDE\" }, PathOnly = true
            });
            _catalog.Add(new AppDef
            {
                Key = "dsh", Name = "DeepSeek Harness", Badge = "D", BadgeColor = "#3B5BDB",
                AppIdPrefix = "com.deepseek.dsh", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\DeepSeek Harness.lnk",
                    @"%USERPROFILE%\Desktop\DeepSeek Harness.lnk"
                },
                Exes = new[] { @"D:\DSH\DeepSeek Harness.exe", @"%LOCALAPPDATA%\Programs\DeepSeek Harness\DeepSeek Harness.exe" },
                Names = new[] { "DeepSeek Harness" }, PathPrefixes = new[] { @"D:\DSH\" }, Force = true
            });
            _catalog.Add(new AppDef
            {
                Key = "workbuddy", Name = "WorkBuddy", Badge = "W", BadgeColor = "#1F9D8F",
                AppIdPrefix = "WorkBuddy.WorkBuddy", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\WorkBuddy.lnk",
                    @"%USERPROFILE%\Desktop\WorkBuddy.lnk"
                },
                Exes = new[] { @"D:\WorkBuddy\WorkBuddy.exe", @"%LOCALAPPDATA%\Programs\WorkBuddy\WorkBuddy.exe" },
                Names = new[] { "WorkBuddy" }, PathPrefixes = new[] { @"D:\WorkBuddy\" }, Force = true
            });
            _catalog.Add(new AppDef
            {
                // ZCode：Electron 客户端，设置里 closeToTrayOnWindows=true，点关闭只是收进托盘，
                // 正常关闭永远退不出去，所以按托盘型强制结束整棵进程树再重开。
                Key = "zcode", Name = "ZCode", Badge = "Z", BadgeColor = "#6B21A8", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\ZCode.lnk",
                    @"%USERPROFILE%\Desktop\ZCode.lnk"
                },
                Exes = new[] { @"D:\ZCode\ZCode.exe", @"%LOCALAPPDATA%\Programs\ZCode\ZCode.exe" },
                Names = new[] { "ZCode" }, PathPrefixes = new[] { @"D:\ZCode\" }, Force = true
            });
            _catalog.Add(new AppDef
            {
                // 千问办公：启动器在 Launcher.exe，真正干活的是 QwenWorkCN.exe（多进程）。
                // 关掉时要把 qwenworkhelper 一起带走，否则残留进程会让"重启"变成空转。
                Key = "qwenwork", Name = "千问办公", Badge = "Q", BadgeColor = "#615CED",
                AppIdPrefix = "QwenWorkCN_", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\千问办公.lnk",
                    @"%USERPROFILE%\Desktop\千问办公.lnk"
                },
                Exes = new[] { @"D:\千问办公\QwenWorkCN\Launcher.exe" },
                Names = new[] { "QwenWorkCN", "qwenworkhelper" },
                PathPrefixes = new[] { @"D:\千问办公\QwenWorkCN\" }, Force = true
            });
            _catalog.Add(new AppDef
            {
                Key = "opencode", Name = "OpenCode", Badge = "O", BadgeColor = "#0F172A",
                AppIdPrefix = "ai.opencode.desktop", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\OpenCode.lnk",
                    @"%USERPROFILE%\Desktop\OpenCode.lnk"
                },
                Exes = new[]
                {
                    @"D:\opencode\OpenCode.exe",
                    @"D:\OpenCode\OpenCode.exe",
                    @"%LOCALAPPDATA%\Programs\OpenCode\OpenCode.exe"
                },
                Names = new[] { "OpenCode" }, PathPrefixes = new[] { @"D:\opencode\" }, RequirePath = true
            });
            // Cursor 是 PathOnly：运行状态只按安装目录前缀认，前缀来源见 ResolveLaunch
            // （清单里的候选 exe 目录 + 快捷方式指向的 exe 目录）。少了这一层，
            // 快捷方式能显示出来，但运行状态永远是 0，按钮一直显示"打开"。
            _catalog.Add(new AppDef
            {
                Key = "cursor", Name = "Cursor", Badge = "C", BadgeColor = "#151515", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Cursor\Cursor.lnk",
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Cursor.lnk",
                    @"%USERPROFILE%\Desktop\Cursor.lnk"
                },
                Exes = new[]
                {
                    @"D:\cursor\Cursor.exe",
                    @"%LOCALAPPDATA%\Programs\cursor\Cursor.exe",
                    @"%ProgramFiles%\Cursor\Cursor.exe"
                },
                Names = new[] { "Cursor" }, PathPrefixes = new[] { @"D:\cursor\" }, PathOnly = true
            });
            AddEditor("windsurf", "Windsurf", "W", "#0E9F9F", "Windsurf", new[] { @"%LOCALAPPDATA%\Programs\Windsurf\Windsurf.exe", @"%ProgramFiles%\Windsurf\Windsurf.exe" });
            // Trae CN 的进程名带空格（"Trae CN"）、快捷方式放在开始菜单的 TRAE CN\ 子目录下，
            // 只按 "Trae" 单名字 + 默认路径写会一项都匹配不上，等于装了也不显示。
            _catalog.Add(new AppDef
            {
                Key = "trae", Name = "Trae", Badge = "T", BadgeColor = "#2E6BFF", PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\TRAE CN\TRAE CN.lnk",
                    @"%USERPROFILE%\Desktop\TRAE CN.lnk",
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Trae\Trae.lnk",
                    @"%USERPROFILE%\Desktop\Trae.lnk"
                },
                Exes = new[]
                {
                    @"D:\Trae CN\Trae CN.exe",
                    @"%LOCALAPPDATA%\Programs\Trae CN\Trae CN.exe",
                    @"%LOCALAPPDATA%\Programs\Trae\Trae.exe",
                    @"%ProgramFiles%\Trae\Trae.exe"
                },
                Names = new[] { "Trae CN", "Trae" },
                PathPrefixes = new[] { @"D:\Trae CN\", @"%LOCALAPPDATA%\Programs\Trae CN\" },
                PathOnly = true
            });
            AddEditor("kiro", "Kiro", "K", "#7C3AED", "Kiro", new[] { @"%LOCALAPPDATA%\Programs\Kiro\Kiro.exe", @"%ProgramFiles%\Kiro\Kiro.exe" });
            AddEditor("zed", "Zed", "Z", "#2D2A26", "zed", new[] { @"%LOCALAPPDATA%\Programs\Zed\zed.exe", @"%ProgramFiles%\Zed\zed.exe" });
            AddEditor("antigravity", "Antigravity", "A", "#D99A2B", "Antigravity", new[] { @"%LOCALAPPDATA%\Programs\Antigravity\Antigravity.exe", @"%ProgramFiles%\Antigravity\Antigravity.exe" });

            _processNames = _catalog.SelectMany(d => d.Names ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void AddEditor(string key, string name, string badge, string color, string processName, string[] exes)
        {
            _catalog.Add(new AppDef
            {
                Key = key, Name = name, Badge = badge, BadgeColor = color, PreferShortcut = true,
                Shortcuts = new[]
                {
                    @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\" + name + @"\" + name + ".lnk",
                    @"%USERPROFILE%\Desktop\" + name + ".lnk"
                },
                Exes = exes, Names = new[] { processName }, PathOnly = true
            });
        }

        private void LoadOrder()
        {
            try
            {
                if (!File.Exists(OrderPath)) return;
                List<string> keys = File.ReadAllLines(OrderPath).Select(l => l.Trim()).Where(l => l.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var byKey = new Dictionary<string, AppDef>(StringComparer.OrdinalIgnoreCase);
                foreach (AppDef def in _catalog) byKey[def.Key] = def;
                var ordered = new List<AppDef>();
                foreach (string key in keys)
                {
                    AppDef def;
                    if (byKey.TryGetValue(key, out def) && !ordered.Contains(def)) ordered.Add(def);
                }
                foreach (AppDef def in _catalog) if (!ordered.Contains(def)) ordered.Add(def);
                _catalog.Clear();
                _catalog.AddRange(ordered);
            }
            catch (Exception ex) { Log("载入顺序失败: " + ex.Message); }
        }

        private void SaveOrder()
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.WriteAllLines(OrderPath, _apps.Select(a => a.Def.Key).ToArray(), new UTF8Encoding(false));
                Log("已保存顺序: " + string.Join(",", _apps.Select(a => a.Def.Key).ToArray()));
            }
            catch (Exception ex) { Log("保存顺序失败: " + ex.Message); }
        }

        private void ApplyOrder()
        {
            var visible = new List<AppVM>(_rowOrder);
            var rest = _apps.Where(a => !visible.Contains(a)).ToList();
            _apps.Clear();
            _apps.AddRange(visible);
            _apps.AddRange(rest);

            var byKey = new Dictionary<string, AppDef>(StringComparer.OrdinalIgnoreCase);
            foreach (AppDef def in _catalog) byKey[def.Key] = def;
            var orderedDefs = new List<AppDef>();
            foreach (AppVM vm in _apps)
            {
                AppDef def;
                if (byKey.TryGetValue(vm.Def.Key, out def)) orderedDefs.Add(def);
            }
            _catalog.Clear();
            _catalog.AddRange(orderedDefs);
            SaveOrder();
            _rowOrder.Clear();
            _rowOrder.AddRange(_apps.Where(a => a.Installed));
        }

        private void RescanApps(bool initial)
        {
            // 每次重新扫描都清空重取：安装路径或图标变了不会留旧图，缓存条目数也不会随刷新累积
            ClearIconCache();
            _appIds = AppxApps.Read();
            foreach (AppDef def in _catalog)
            {
                AppVM vm = _apps.FirstOrDefault(a => string.Equals(a.Def.Key, def.Key, StringComparison.OrdinalIgnoreCase));
                if (vm == null)
                {
                    vm = new AppVM();
                    vm.Def = def;
                    _apps.Add(vm);
                }
                vm.Launch = ResolveLaunch(def);
                vm.Installed = vm.Launch.Installed;
                vm.Prefixes = BuildPrefixes(def, vm.Launch);
                vm.ProcessCount = 0;
            }
            _rowOrder.Clear();
            _rowOrder.AddRange(_apps.Where(a => a.Installed));
            RefreshStatuses();
            foreach (AppVM vm in _apps)
            {
                if (vm.Installed) Log("检测到 " + vm.Def.Name + " -> " + vm.Launch.Display);
            }
            if (!initial) SetStatus("已刷新", SubColor, 3);
            // 构造函数里的首次扫描发生在句柄创建之前，那时 DPI 还是 96，预热了也是白预热
            if (IsHandleCreated) PrewarmIcons();
            Invalidate();
        }

        private LaunchInfo ResolveLaunch(AppDef def)
        {
            var info = new LaunchInfo();
            string appId = string.IsNullOrEmpty(def.AppIdPrefix) ? null : FindAppId(def.AppIdPrefix);
            string shortcut = FirstExisting(def.Shortcuts);
            string exe = FirstExisting(def.Exes);

            if (def.PreferShortcut && shortcut != null)
            {
                info.Kind = "lnk"; info.Target = shortcut;
                info.Display = "快捷方式 · " + Path.GetFileName(shortcut);
            }
            else if (!string.IsNullOrEmpty(appId))
            {
                info.Kind = "aumid"; info.Target = appId;
                info.Display = "AppX 应用 · " + AppShortName(appId);
            }
            else if (shortcut != null)
            {
                info.Kind = "lnk"; info.Target = shortcut;
                info.Display = "快捷方式 · " + Path.GetFileName(shortcut);
            }
            else if (exe != null)
            {
                info.Kind = "exe"; info.Target = exe; info.Display = exe;
            }
            info.Installed = info.Kind != null;
            info.ExePath = exe;
            info.ExtraPathPrefixes = new List<string>();
            if (exe != null) info.ExtraPathPrefixes.Add(DirectoryPrefix(Path.GetDirectoryName(exe)));
            // 快捷方式指向的 exe 目录也算一个前缀：装在哪个盘由用户自己的快捷方式决定，
            // 不靠清单里写死的安装位置（PathOnly 的应用只认路径，缺这条会一直显示未运行）
            if (string.Equals(info.Kind, "lnk", StringComparison.OrdinalIgnoreCase))
            {
                string targetExe = LnkTargetOf(info.Target);
                string targetPrefix = targetExe == null ? null : DirectoryPrefix(Path.GetDirectoryName(targetExe));
                // 指向 wscript.exe / rundll32.exe 这类系统宿主的快捷方式不算安装位置：
                // 那只是个包装脚本，真正的应用目录另有其处
                if (targetPrefix != null && !targetPrefix.StartsWith(SystemDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
                    && !info.ExtraPathPrefixes.Contains(targetPrefix, StringComparer.OrdinalIgnoreCase))
                    info.ExtraPathPrefixes.Add(targetPrefix);
            }
            return info;
        }

        private List<string> BuildPrefixes(AppDef def, LaunchInfo launch)
        {
            var prefixes = new List<string>();
            if (def.PathPrefixes != null)
            {
                foreach (string raw in def.PathPrefixes)
                {
                    string p = Environment.ExpandEnvironmentVariables(raw);
                    if (!string.IsNullOrEmpty(p)) prefixes.Add(p);
                }
            }
            if (launch != null && launch.ExtraPathPrefixes != null) prefixes.AddRange(launch.ExtraPathPrefixes);
            return prefixes.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string DirectoryPrefix(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            return dir.TrimEnd('\\', '/') + "\\";
        }

        private static string AppShortName(string appId)
        {
            int bang = appId.IndexOf('!');
            string value = bang > 0 ? appId.Substring(0, bang) : appId;
            int underscore = value.IndexOf('_');
            if (underscore > 0) value = value.Substring(0, underscore);
            return value;
        }

        private string FindAppId(string prefix)
        {
            foreach (KeyValuePair<string, string> pair in _appIds)
                if (pair.Value != null && pair.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }

        private static string FirstExisting(string[] patterns)
        {
            if (patterns == null) return null;
            foreach (string raw in patterns)
            {
                string p = Environment.ExpandEnvironmentVariables(raw);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private void BuildLayout()
        {
            int w = ClientSize.Width;
            _closeRect = new Rectangle(w - Px(12) - Px(30), Px(16), Px(30), Px(26));
            _minRect = new Rectangle(_closeRect.X - Px(30), Px(16), Px(30), Px(26));
            _refreshRect = new Rectangle(_minRect.X - Px(48), Px(17), Px(48), Px(24));
            _headerDragRect = new Rectangle(Px(20), Px(14), Math.Max(0, _refreshRect.X - Px(28)), Px(44));
            _primaryRect = new Rectangle(Px(20), Px(78), Px(160), Px(36));
            _versionRect = new Rectangle(w - Px(20) - Px(36), Px(FooterRowDip), Px(36), Px(22));
            _logRect = new Rectangle(_versionRect.X - Px(56), Px(FooterRowDip), Px(56), Px(22));
            _listRect = new Rectangle(Px(20), Px(ListTopDip), Px(ListWidthDip), Px(ListHeightDip));

            int step = Px(RowStepDip);
            int visibleRows = Math.Max(1, (_listRect.Height + Px(8)) / step);
            _maxScrollRow = Math.Max(0, _rowOrder.Count - visibleRows);
            _scrollRow = Math.Max(0, Math.Min(_scrollRow, _maxScrollRow));
            _needsScroll = _maxScrollRow > 0;

            _layouts.Clear();
            int lastRow = Math.Min(_rowOrder.Count, _scrollRow + visibleRows);
            for (int i = _scrollRow; i < lastRow; i++)
            {
                int top = _listRect.Y + (i - _scrollRow) * step;
                var row = new Rectangle(Px(20), top, Px(ListWidthDip), Px(RowHeightDip));
                var layout = new RowLayout();
                layout.VM = _rowOrder[i];
                layout.Rect = row;
                layout.IconRect = new Rectangle(row.X + Px(30), row.Y + Px(15), Px(IconTileDip), Px(IconTileDip));
                layout.ActionRect = new Rectangle(row.Right - Px(12) - Px(64), row.Y + Px(21), Px(64), Px(28));
                layout.CloseRect = new Rectangle(layout.ActionRect.X - Px(7) - Px(52), row.Y + Px(21), Px(52), Px(28));
                layout.GripRect = new Rectangle(row.X + Px(11), row.Y + Px(26), Px(12), Px(18));
                _layouts.Add(layout);
            }

            if (_needsScroll)
            {
                _scrollTrackRect = new Rectangle(_listRect.Right + Px(6), _listRect.Y, Px(5), _listRect.Height);
                int shown = Math.Max(1, _rowOrder.Count - _maxScrollRow);
                int thumbH = Math.Max(Px(34), (int)(_listRect.Height * (shown / (float)_rowOrder.Count)));
                int travel = _listRect.Height - thumbH;
                int thumbY = _listRect.Y + (travel <= 0 ? 0 : (int)(travel * (_scrollRow / (float)_maxScrollRow)));
                _scrollThumbRect = new Rectangle(_scrollTrackRect.X, thumbY, Px(5), thumbH);
            }
            else
            {
                _scrollTrackRect = Rectangle.Empty;
                _scrollThumbRect = Rectangle.Empty;
            }
        }

        private Hit HitTest(Point p)
        {
            if (_closeRect.Contains(p)) return new Hit(HitKind.Close, null);
            if (_minRect.Contains(p)) return new Hit(HitKind.Min, null);
            if (_refreshRect.Contains(p)) return new Hit(HitKind.Refresh, null);
            if (_primaryRect.Contains(p) && !_busy) return new Hit(HitKind.Primary, null);
            if (_logRect.Contains(p)) return new Hit(HitKind.Log, null);
            if (_needsScroll && _scrollThumbRect.Contains(p)) return new Hit(HitKind.ScrollThumb, null);
            if (_listRect.Contains(p))
            {
                for (int i = _layouts.Count - 1; i >= 0; i--)
                {
                    RowLayout row = _layouts[i];
                    bool running = row.VM.ProcessCount > 0;
                    if (running && row.CloseRect.Contains(p)) return new Hit(HitKind.RowClose, row.VM);
                    if (row.ActionRect.Contains(p)) return new Hit(HitKind.RowAction, row.VM);
                    if (row.Rect.Contains(p)) return new Hit(HitKind.RowDrag, row.VM);
                }
            }
            if (_headerDragRect.Contains(p)) return new Hit(HitKind.HeaderDrag, null);
            return new Hit(HitKind.None, null);
        }

        private void UpdateHover(Point p)
        {
            Hit hit = HitTest(p);
            string oldKey = _hover == null ? string.Empty : _hover.Key;
            if (oldKey != hit.Key)
            {
                _hover = hit;
                if (hit.Kind == HitKind.RowDrag)
                {
                    RowLayout layout = _layouts.FirstOrDefault(r => r.VM == hit.VM);
                    Cursor = (layout != null && layout.GripRect.Contains(p)) ? Cursors.SizeAll : Cursors.Default;
                }
                else if (hit.Kind == HitKind.None || hit.Kind == HitKind.HeaderDrag) Cursor = Cursors.Default;
                else Cursor = Cursors.Hand;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            BuildLayout();
            Hit hit = HitTest(e.Location);
            if (hit.Kind == HitKind.HeaderDrag)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                return;
            }
            if (hit.Kind == HitKind.ScrollThumb)
            {
                _scrollDragging = true;
                _scrollDragY = e.Y;
                _scrollDragStartRow = _scrollRow;
                Capture = true;
                return;
            }
            if (hit.Kind == HitKind.RowDrag && !_busy)
            {
                _dragArmed = true;
                _dragVm = hit.VM;
                _dragStart = e.Location;
                return;
            }
            if (hit.Kind == HitKind.RowClose || hit.Kind == HitKind.RowAction || hit.Kind == HitKind.Refresh ||
                hit.Kind == HitKind.Min || hit.Kind == HitKind.Close || hit.Kind == HitKind.Primary || hit.Kind == HitKind.Log)
            {
                _pressed = hit;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_scrollDragging)
            {
                if (e.Button != MouseButtons.Left) { _scrollDragging = false; Capture = false; return; }
                int travel = _listRect.Height - _scrollThumbRect.Height;
                if (travel > 0)
                {
                    int delta = e.Y - _scrollDragY;
                    int row = Math.Max(0, Math.Min(_maxScrollRow, _scrollDragStartRow + (int)Math.Round(delta * (_maxScrollRow / (float)travel))));
                    if (row != _scrollRow) { _scrollRow = row; Invalidate(); }
                }
                return;
            }
            if (_dragArmed || _dragging)
            {
                if (e.Button != MouseButtons.Left) { FinishDrag(true); return; }
                if (!_dragging)
                {
                    int dx = e.X - _dragStart.X;
                    int dy = e.Y - _dragStart.Y;
                    if (dx * dx + dy * dy < Px(5) * Px(5)) return;
                    _dragging = true;
                    _orderChanged = false;
                    Capture = true;
                    Cursor = Cursors.SizeAll;
                }
                MoveDraggedRow(e.Y);
                AutoScrollRows(e.Y);
                Invalidate();
                return;
            }
            UpdateHover(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_scrollDragging) { _scrollDragging = false; Capture = false; return; }
            if (_dragging) { FinishDrag(true); return; }
            if (_pressed != null)
            {
                BuildLayout();
                Hit hit = HitTest(e.Location);
                if (hit.Key == _pressed.Key) Execute(hit);
                _pressed = null;
                Invalidate();
                return;
            }
            _dragArmed = false;
            _dragVm = null;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_needsScroll || !_listRect.Contains(e.Location)) return;
            _scrollRow = Math.Max(0, Math.Min(_maxScrollRow, _scrollRow - Math.Sign(e.Delta)));
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_dragging && !_scrollDragging && _hover != null)
            {
                _hover = null;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        private void Execute(Hit hit)
        {
            switch (hit.Kind)
            {
                case HitKind.Refresh:
                    if (_busy || _dragging) return;
                    RescanApps(false);
                    break;
                case HitKind.Min:
                    WindowState = FormWindowState.Minimized;
                    break;
                case HitKind.Close:
                    // 点 × 只收进托盘，不退出进程；真正退出走托盘右键菜单。
                    HideToTray();
                    break;
                case HitKind.Primary:
                    OnRestartAll();
                    break;
                case HitKind.Log:
                    OpenLog();
                    break;
                case HitKind.RowClose:
                    if (hit.VM != null) OnCloseApp(hit.VM);
                    break;
                case HitKind.RowAction:
                    if (hit.VM != null) OnRowAction(hit.VM);
                    break;
            }
        }

        private void FinishDrag(bool save)
        {
            bool wasDragging = _dragging;
            bool changed = _orderChanged;
            _dragging = false;
            _dragArmed = false;
            _orderChanged = false;
            Capture = false;
            Cursor = Cursors.Default;
            if (wasDragging && save && changed)
            {
                ApplyOrder();
                SetStatus("顺序已保存", SubColor, 3);
            }
            _dragVm = null;
            Invalidate();
        }

        private void MoveDraggedRow(int mouseY)
        {
            if (_dragVm == null) return;
            int index = _rowOrder.IndexOf(_dragVm);
            if (index < 0) return;
            int rowH = Px(RowHeightDip);
            int step = Px(RowStepDip);
            if (index > 0)
            {
                int prevTop = Px(ListTopDip) + (index - 1 - _scrollRow) * step;
                if (mouseY < prevTop + rowH / 2) { SwapRows(index, index - 1); return; }
            }
            if (index < _rowOrder.Count - 1)
            {
                int nextTop = Px(ListTopDip) + (index + 1 - _scrollRow) * step;
                if (mouseY > nextTop + rowH / 2) SwapRows(index, index + 1);
            }
        }

        private void SwapRows(int from, int to)
        {
            if (from == to) return;
            AppVM vm = _rowOrder[from];
            _rowOrder.RemoveAt(from);
            _rowOrder.Insert(to, vm);
            _orderChanged = true;
            if (_dragVm != null)
            {
                int idx = _rowOrder.IndexOf(_dragVm);
                int shown = Math.Max(1, _rowOrder.Count - _maxScrollRow);
                if (idx < _scrollRow) _scrollRow = idx;
                else if (idx >= _scrollRow + shown) _scrollRow = idx - shown + 1;
                _scrollRow = Math.Max(0, Math.Min(_scrollRow, _maxScrollRow));
            }
            BuildLayout();
        }

        private void AutoScrollRows(int mouseY)
        {
            if (!_needsScroll) return;
            int target = _scrollRow;
            if (mouseY < _listRect.Top + Px(24)) target = Math.Max(0, _scrollRow - 1);
            else if (mouseY > _listRect.Bottom - Px(24)) target = Math.Min(_maxScrollRow, _scrollRow + 1);
            else return;
            if (target != _scrollRow)
            {
                _scrollRow = target;
                BuildLayout();
            }
        }

        private void OnRowAction(AppVM vm)
        {
            if (_busy || _dragging || !vm.Installed) return;
            bool wasRunning = vm.ProcessCount > 0;
            RunBusy(delegate
            {
                SetBusy(vm, wasRunning ? "正在重启…" : "正在打开…");
                SetStatus((wasRunning ? "正在重启 " : "正在打开 ") + vm.Def.Name + "…", AccentColor, 0);
                bool ok = RestartApp(vm, wasRunning);
                SetBusy(null, null);
                SetStatus(vm.LastMessage, ok ? (vm.Warning ? AccentColor : SubColor) : ErrorColor, ok ? 12 : 18);
            });
        }

        private void OnCloseApp(AppVM vm)
        {
            if (_busy || _dragging || !vm.Installed) return;
            if (vm.ProcessCount == 0) { SetStatus(vm.Def.Name + " 未在运行", SubColor, 3); return; }
            vm.Warning = false;
            vm.LastMessage = null;
            RunBusy(delegate
            {
                SetBusy(vm, "正在关闭…");
                SetStatus("正在关闭 " + vm.Def.Name + "…", AccentColor, 0);
                bool ok = StopApp(vm);
                SetBusy(null, null);
                if (ok) SetStatus("已关闭 " + vm.Def.Name, SubColor, 12);
                else SetStatus(vm.LastMessage, ErrorColor, 18);
            });
        }

        private void OnRestartAll()
        {
            if (_busy || _dragging) return;
            List<AppVM> running = _apps.Where(a => a.Installed && a.ProcessCount > 0).ToList();
            if (running.Count == 0) { SetStatus("没有正在运行的应用", SubColor, 3); return; }
            string names = string.Join("\r\n", running.Select(a => "· " + a.Def.Name).ToArray());
            DialogResult answer = MessageBox.Show(this,
                "将彻底关闭并重新打开：\r\n\r\n" + names + "\r\n\r\n未保存的内容可能丢失，确定继续？",
                "重启全部", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;

            RunBusy(delegate
            {
                int total = running.Count;
                int ok = 0;
                int fail = 0;
                for (int i = 0; i < running.Count; i++)
                {
                    AppVM vm = running[i];
                    int index = i + 1;
                    SetBusy(vm, "正在重启…");
                    SetStatus("正在重启 " + vm.Def.Name + "（" + index + "/" + total + "）…", AccentColor, 0);
                    bool success = RestartApp(vm, true);
                    if (success) ok++; else fail++;
                    SetBusy(null, null);
                }
                if (fail == 0) SetStatus("全部完成：成功重启 " + ok + " 个应用", SubColor, 18);
                else SetStatus("完成：成功 " + ok + " 个，未完成 " + fail + " 个；详情见日志", ErrorColor, 22);
            });
        }

        private void SetBusy(AppVM vm, string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action(delegate { SetBusy(vm, text); })); return; }
            _busyVm = vm;
            _busyText = text;
            Invalidate();
        }

        private void RunBusy(Action work)
        {
            if (_busy || _dragging) return;
            _busy = true;
            Invalidate();
            var thread = new Thread(new ThreadStart(delegate
            {
                try { work(); }
                catch (Exception ex)
                {
                    Log("ERROR " + ex);
                    SetStatus("操作失败：" + ex.Message, ErrorColor, 20);
                }
                finally
                {
                    UI(delegate
                    {
                        _busy = false;
                        _busyVm = null;
                        _busyText = null;
                        RefreshStatuses();
                        Invalidate();
                    });
                }
            }));
            thread.IsBackground = true;
            thread.Start();
        }

        private bool RestartApp(AppVM vm, bool wasRunning)
        {
            vm.Warning = false;
            vm.LastMessage = null;
            if (wasRunning)
            {
                SetStatus("正在关闭 " + vm.Def.Name + "…", AccentColor, 0);
                if (!StopApp(vm)) return false;
            }
            SetStatus("正在打开 " + vm.Def.Name + "…", AccentColor, 0);
            if (!StartApp(vm)) return false;
            SetStatus("等待 " + vm.Def.Name + " 启动…", AccentColor, 0);
            if (!WaitUntilRunning(vm, 12000))
            {
                vm.Warning = true;
                vm.LastMessage = "已发出启动指令，但暂未检测到 " + vm.Def.Name + " 进程";
                Log(vm.Def.Name + " 启动后未检测到进程");
                return true;
            }
            vm.LastMessage = (wasRunning ? "已重启 " : "已打开 ") + vm.Def.Name;
            Log(vm.Def.Name + (wasRunning ? " 已重启" : " 已打开"));
            return true;
        }

        private bool StopApp(AppVM vm)
        {
            List<ProcInfo> snapshot = Snapshot();
            List<int> pids = snapshot.Where(p => IsMatch(vm, p)).Select(p => p.Pid).Distinct().ToList();
            if (pids.Count == 0) return true;

            if (vm.Def.Force)
            {
                foreach (int pid in pids) KillTree(pid);
                if (WaitUntilStopped(vm, 8000))
                {
                    Log(vm.Def.Name + " 已结束 (" + string.Join(",", pids.Select(p => p.ToString()).ToArray()) + ")");
                    return true;
                }
                vm.LastMessage = "无法结束 " + vm.Def.Name + " 的进程，请检查权限后重试";
                Log(vm.Def.Name + " 强制结束失败");
                return false;
            }

            bool windowFound = false;
            foreach (int pid in pids)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) { windowFound = true; p.CloseMainWindow(); }
                    }
                }
                catch { }
            }
            if (windowFound)
            {
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(200);
                    if (CountRunning(vm) == 0) { Log(vm.Def.Name + " 已正常退出"); return true; }
                }
                List<ProcInfo> remaining = Snapshot().Where(p => IsMatch(vm, p)).ToList();
                bool mainStill = false;
                foreach (ProcInfo info in remaining)
                {
                    try
                    {
                        using (Process p = Process.GetProcessById(info.Pid))
                        {
                            if (p.MainWindowHandle != IntPtr.Zero) { mainStill = true; break; }
                        }
                    }
                    catch { }
                }
                if (mainStill)
                {
                    vm.LastMessage = vm.Def.Name + " 没有退出，可能弹出了保存确认；已取消重启";
                    Log(vm.Def.Name + " 未退出，取消重启");
                    return false;
                }
            }
            foreach (ProcInfo info in Snapshot().Where(p => IsMatch(vm, p)).ToList()) KillTree(info.Pid);
            if (WaitUntilStopped(vm, 5000))
            {
                Log(vm.Def.Name + " 已结束残留进程");
                return true;
            }
            vm.LastMessage = "无法结束 " + vm.Def.Name + " 的残留进程";
            Log(vm.Def.Name + " 残留进程结束失败");
            return false;
        }

        private static void KillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
                psi.Arguments = "/PID " + pid + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi)) { if (p != null) p.WaitForExit(5000); }
            }
            catch (Exception ex) { Log("taskkill " + pid + " 失败: " + ex.Message); }
        }

        private bool WaitUntilStopped(AppVM vm, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (CountRunning(vm) == 0) return true;
                Thread.Sleep(200);
                waited += 200;
            }
            return CountRunning(vm) == 0;
        }

        private bool WaitUntilRunning(AppVM vm, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (CountRunning(vm) > 0) return true;
                Thread.Sleep(250);
                waited += 250;
            }
            return CountRunning(vm) > 0;
        }

        private int CountRunning(AppVM vm)
        {
            return Snapshot().Count(p => IsMatch(vm, p));
        }

        private bool StartApp(AppVM vm)
        {
            try
            {
                var psi = new ProcessStartInfo();
                if (vm.Launch.Kind == "aumid")
                {
                    psi.FileName = "explorer.exe";
                    psi.Arguments = "shell:AppsFolder\\" + vm.Launch.Target;
                    psi.UseShellExecute = true;
                }
                else if (vm.Launch.Kind == "lnk")
                {
                    psi.FileName = vm.Launch.Target;
                    psi.UseShellExecute = true;
                }
                else
                {
                    psi.FileName = vm.Launch.Target;
                    psi.UseShellExecute = true;
                    string dir = Path.GetDirectoryName(vm.Launch.Target);
                    if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
                }
                Process.Start(psi);
                Log("启动 " + vm.Def.Name + " -> " + vm.Launch.Target);
                return true;
            }
            catch (Exception ex)
            {
                vm.LastMessage = "打开 " + vm.Def.Name + " 失败：" + ex.Message;
                Log(vm.Def.Name + " 启动失败: " + ex);
                return false;
            }
        }

        private bool IsMatch(AppVM vm, ProcInfo info)
        {
            bool nameOk = vm.Def.Names == null || vm.Def.Names.Length == 0 ||
                          vm.Def.Names.Any(n => string.Equals(n, info.Name, StringComparison.OrdinalIgnoreCase));
            bool pathOk = info.Path != null && vm.Prefixes != null &&
                          vm.Prefixes.Any(prefix => !string.IsNullOrEmpty(prefix) && info.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (vm.Def.PathOnly) return pathOk;
            if (vm.Def.RequirePath) return nameOk && pathOk;
            return nameOk || pathOk;
        }

        private List<ProcInfo> Snapshot()
        {
            var seen = new HashSet<int>();
            var result = new List<ProcInfo>();
            foreach (string name in _processNames)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }
                foreach (Process p in processes)
                {
                    if (seen.Add(p.Id))
                    {
                        string path = null;
                        try { path = p.MainModule.FileName; } catch { }
                        result.Add(new ProcInfo { Pid = p.Id, Name = p.ProcessName, Path = path });
                    }
                    p.Dispose();
                }
            }
            return result;
        }

        private void RefreshStatuses()
        {
            if (_busy) return;
            List<ProcInfo> snapshot = Snapshot();
            bool changed = false;
            foreach (AppVM vm in _apps)
            {
                int count = vm.Installed ? snapshot.Count(p => IsMatch(vm, p)) : 0;
                if (count != vm.ProcessCount) { vm.ProcessCount = count; changed = true; }
            }
            if (changed)
            {
                BuildLayout();
                Invalidate();
            }
            if (DateTime.Now >= _statusUntil && !_busy) UpdateIdleStatus();
        }

        private void UpdateIdleStatus()
        {
            int running = _apps.Count(a => a.Installed && a.ProcessCount > 0);
            string text = running > 0 ? (running + " 个应用正在运行") : "没有正在运行的应用";
            if (text != _statusText || _statusColor != SubColor)
            {
                _statusText = text;
                _statusColor = SubColor;
                Invalidate();
            }
        }

        private void SetStatus(string text, Color color, int holdSeconds)
        {
            if (InvokeRequired) { BeginInvoke(new Action(delegate { SetStatus(text, color, holdSeconds); })); return; }
            _statusText = text ?? string.Empty;
            _statusColor = color;
            _statusUntil = holdSeconds > 0 ? DateTime.Now.AddSeconds(holdSeconds) : DateTime.MinValue;
            Invalidate();
        }

        private void UI(Action action)
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }

        private void OpenLog()
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                if (File.Exists(LogPath)) Process.Start("explorer.exe", "/select,\"" + LogPath + "\"");
                else Process.Start("explorer.exe", "\"" + LogDir + "\"");
            }
            catch (Exception ex) { SetStatus("打开日志失败：" + ex.Message, ErrorColor, 8); }
        }

        // ---- 应用图标 ----
        // 走 PrivateExtractIcons 直接要 256px，再按当前 DPI 缩到托盘尺寸。
        // 不用 SHGetImageList：它只有 comctl32 v6 才导出，实测本进程里加载到的仍是 v5.85，
        //   清单声明也救不回来（SxS 重定向只在该 DLL 首次加载时生效）。
        // 不用 Icon.ExtractAssociatedIcon：那个固定 32px，200% 屏上放大会糊。
        // 图标源优先用快捷方式自带的 ICONLOCATION（Qoder、Claude 的图标其实是独立的 .ico），
        // 其次退回目录里已解析出的 exe 路径。

        private void PrewarmIcons()
        {
            int size = Px(IconTileDip);
            foreach (AppVM vm in _rowOrder) GetIconEntry(vm, size);
        }

        private void ClearIconCache()
        {
            foreach (IconEntry entry in _iconCache.Values)
            {
                if (entry != null && entry.Image != null) entry.Image.Dispose();
            }
            _iconCache.Clear();
        }

        private IconEntry GetIconEntry(AppVM vm, int pxSize)
        {
            int index;
            string source = ResolveIconSource(vm, out index);
            if (string.IsNullOrEmpty(source)) return null;
            string key = source + "|" + index + "|" + pxSize;
            IconEntry cached;
            if (_iconCache.TryGetValue(key, out cached)) return cached;
            IconEntry loaded = LoadIconBitmap(source, index, pxSize);
            _iconCache[key] = loaded;
            return loaded;
        }

        private static string ResolveIconSource(AppVM vm, out int index)
        {
            index = 0;
            if (vm == null || vm.Launch == null || string.IsNullOrEmpty(vm.Launch.Target)) return null;
            string target = vm.Launch.Target;

            if (string.Equals(vm.Launch.Kind, "aumid", StringComparison.OrdinalIgnoreCase))
            {
                // 查清单比取快捷方式贵，每个 LaunchInfo 只查一次，别每帧重查
                if (!vm.Launch.AppxIconProbed)
                {
                    vm.Launch.AppxIconProbed = true;
                    vm.Launch.AppxIconPath = ResolveAppxIconPath(target);
                }
                return string.IsNullOrEmpty(vm.Launch.AppxIconPath) ? null : vm.Launch.AppxIconPath;
            }

            if (string.Equals(vm.Launch.Kind, "lnk", StringComparison.OrdinalIgnoreCase))
            {
                string loc = IconLocationOf(target);
                if (!string.IsNullOrEmpty(loc))
                {
                    int comma = loc.LastIndexOf(',');
                    if (comma > 1)
                    {
                        int parsed;
                        if (int.TryParse(loc.Substring(comma + 1), out parsed))
                        {
                            index = parsed;
                            loc = loc.Substring(0, comma);
                        }
                    }
                    loc = Environment.ExpandEnvironmentVariables(loc.Trim().Trim('"'));
                    if (File.Exists(loc)) return loc;
                }
                if (!string.IsNullOrEmpty(vm.Launch.ExePath) && File.Exists(vm.Launch.ExePath)) return vm.Launch.ExePath;
                // .lnk 本身喂给 PrivateExtractIcons 取不到东西，直接退回字母块
                return null;
            }
            return File.Exists(target) ? target : null;
        }

        private static string IconLocationOf(string lnkPath)
        {
            try
            {
                var info = new SHFILEINFO();
                if (SHGetFileInfo(lnkPath, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICONLOCATION) == IntPtr.Zero) return null;
                return info.szDisplayName;
            }
            catch { return null; }
        }

        // 读 .lnk 指向的真实 exe。只在扫描时调（不在绘制路径上），走 COM 不心疼
        private static string LnkTargetOf(string lnkPath)
        {
            object shell = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                string target = (string)((dynamic)shell).CreateShortcut(lnkPath).TargetPath;
                return string.IsNullOrEmpty(target) || !File.Exists(target) ? null : target;
            }
            catch { return null; }
            finally
            {
                try { if (shell != null) Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        // AUMID 形如 <包家族名>!<应用 Id>。装包目录不能靠枚举 WindowsApps 拿到
        // （普通用户对它没有列目录权限），从 HKCU 的 AppModel 包仓库按包名取出 PackageRootFolder，
        // 再读清单里该应用的 Executable，那个 exe 的内嵌图标跟任务栏上是同一个。
        private const string AppxRepositoryPath =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        private static string ResolveAppxIconPath(string aumid)
        {
            try
            {
                int bang = aumid.IndexOf('!');
                if (bang <= 0) return null;
                string familyName = aumid.Substring(0, bang);
                int familyUnderscore = familyName.LastIndexOf('_');
                if (familyUnderscore <= 0) return null;
                string packageName = familyName.Substring(0, familyUnderscore);
                string publisherHash = familyName.Substring(familyUnderscore + 1);
                string applicationId = aumid.Substring(bang + 1);

                using (RegistryKey packages = Registry.CurrentUser.OpenSubKey(AppxRepositoryPath))
                {
                    if (packages == null) return null;
                    foreach (string packageId in packages.GetSubKeyNames())
                    {
                        // 仓库里的键是包全名：包名_版本_架构__发布者哈希，同族的资源包也会命中，
                        // 它没有 Application 节点，会在下一步返回 null 被跳过
                        if (!packageId.StartsWith(packageName + "_", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!packageId.EndsWith("_" + publisherHash, StringComparison.OrdinalIgnoreCase)) continue;
                        using (RegistryKey package = packages.OpenSubKey(packageId))
                        {
                            string root = package == null ? null : package.GetValue("PackageRootFolder") as string;
                            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                            string exe = AppxExecutableOf(root, applicationId);
                            if (exe != null) return exe;
                        }
                    }
                }
                return null;
            }
            catch { return null; }
        }

        // 纯 UWP 包清单里写的是 $targetnametoken$.exe，磁盘上不存在，返回 null 让字母块兜底
        private static string AppxExecutableOf(string packageDir, string applicationId)
        {
            try
            {
                string manifest = Path.Combine(packageDir, "AppxManifest.xml");
                if (!File.Exists(manifest)) return null;
                var doc = new XmlDocument();
                doc.Load(manifest);
                // 清单带默认命名空间，用 local-name() 匹配，省得再维护一套前缀
                XmlNodeList apps = doc.SelectNodes(
                    "/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']");
                if (apps == null) return null;
                string anyOther = null;
                foreach (XmlNode node in apps)
                {
                    var app = node as XmlElement;
                    if (app == null) continue;
                    string executable = app.GetAttribute("Executable");
                    if (string.IsNullOrEmpty(executable)) continue;
                    string path = Path.Combine(packageDir, executable.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path)) continue;
                    if (string.Equals(app.GetAttribute("Id"), applicationId, StringComparison.OrdinalIgnoreCase)) return path;
                    if (anyOther == null) anyOther = path;
                }
                return anyOther;
            }
            catch { return null; }
        }

        private static IconEntry LoadIconBitmap(string path, int index, int pxSize)
        {
            IntPtr hicon = IntPtr.Zero;
            try
            {
                var handles = new IntPtr[1];
                var ids = new int[1];
                uint got = PrivateExtractIcons(path, index, 256, 256, handles, ids, 1, 0);
                if (got == 0 || handles[0] == IntPtr.Zero) return null;
                hicon = handles[0];
                using (Icon icon = Icon.FromHandle(hicon))
                using (Bitmap raw = icon.ToBitmap())
                {
                    if (raw == null || raw.Width <= 0) return null;
                    Bitmap scaled = ScaleToSize(raw, pxSize);
                    if (scaled == null) return null;
                    var entry = new IconEntry();
                    entry.Image = scaled;
                    entry.Opaque = IsMostlyOpaque(scaled);
                    return entry;
                }
            }
            catch { return null; }
            finally
            {
                if (hicon != IntPtr.Zero) DestroyIcon(hicon);
            }
        }

        // 覆盖率高的图标（千问办公、Trae 这种自带圆角底板的）直接铺满托盘，
        // 再套一层白底就会变成"框中框"；覆盖率低的（鲸鱼、字母标）才需要底板托一下。
        private static bool IsMostlyOpaque(Bitmap bmp)
        {
            int step = 3;
            int total = 0;
            int opaque = 0;
            for (int y = 0; y < bmp.Height; y += step)
            {
                for (int x = 0; x < bmp.Width; x += step)
                {
                    total++;
                    if (bmp.GetPixel(x, y).A > 220) opaque++;
                }
            }
            return total > 0 && opaque / (float)total > 0.80f;
        }

        private static Bitmap ScaleToSize(Bitmap source, int pxSize)
        {
            if (source == null || pxSize <= 0) return null;
            var target = new Bitmap(pxSize, pxSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(target))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                // SourceCopy：直接写入带 alpha 的像素，避免透明区域被填成黑底
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, 0, 0, pxSize, pxSize);
            }
            return target;
        }

        private void CreateFonts()
        {
            DisposeFonts();
            _fontTitle = new Font("Microsoft YaHei UI", F(17), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontSub = new Font("Microsoft YaHei UI", F(11), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontName = new Font("Microsoft YaHei UI", F(13.5f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontPath = new Font("Microsoft YaHei UI", F(9.5f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontButton = new Font("Microsoft YaHei UI", F(11), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontPrimary = new Font("Microsoft YaHei UI", F(12.5f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontBadge = new Font("Microsoft YaHei UI", F(15), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontIcon = new Font("Segoe UI Symbol", F(16), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            if (_fontTitle != null) { _fontTitle.Dispose(); _fontTitle = null; }
            if (_fontSub != null) { _fontSub.Dispose(); _fontSub = null; }
            if (_fontName != null) { _fontName.Dispose(); _fontName = null; }
            if (_fontPath != null) { _fontPath.Dispose(); _fontPath = null; }
            if (_fontButton != null) { _fontButton.Dispose(); _fontButton = null; }
            if (_fontPrimary != null) { _fontPrimary.Dispose(); _fontPrimary = null; }
            if (_fontBadge != null) { _fontBadge.Dispose(); _fontBadge = null; }
            if (_fontIcon != null) { _fontIcon.Dispose(); _fontIcon = null; }
        }

        private float F(float dip) { return Math.Max(6f, dip * _dpi / 96f); }
        private int Px(float dip) { return (int)Math.Round(dip * _dpi / 96f); }

        private SolidBrush Br(Color color)
        {
            int key = color.ToArgb();
            SolidBrush brush;
            if (!_brushes.TryGetValue(key, out brush))
            {
                brush = new SolidBrush(color);
                _brushes[key] = brush;
            }
            return brush;
        }

        private void FillRound(Graphics g, Rectangle rect, int radius, Color color)
        {
            using (GraphicsPath path = RoundRect(rect, radius)) g.FillPath(Br(color), path);
        }

        private void FillRoundGradient(Graphics g, Rectangle rect, int radius, Color top, Color bottom)
        {
            using (GraphicsPath path = RoundRect(rect, radius))
            using (var brush = new LinearGradientBrush(new Rectangle(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(2, rect.Height)),
                       top, bottom, LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);
            }
        }

        private void StrokeRound(Graphics g, Rectangle rect, int radius, Color color, float width)
        {
            using (GraphicsPath path = RoundRect(rect, radius))
            using (var pen = new Pen(color, Math.Max(1f, width * _scale)))
            {
                g.DrawPath(pen, path);
            }
        }

        private static GraphicsPath RoundRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(rect); return path; }
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d - 1, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d - 1, rect.Bottom - d - 1, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d - 1, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color ColorFromHex(string hex)
        {
            try { return ColorTranslator.FromHtml(hex); }
            catch { return Color.Gray; }
        }

        private static bool IsHoverHit(Hit hover, HitKind kind, AppVM vm)
        {
            if (hover == null || hover.Kind != kind) return false;
            if (vm == null) return hover.VM == null;
            return hover.VM == vm;
        }

        private bool IsHover(HitKind kind, AppVM vm) { return IsHoverHit(_hover, kind, vm); }

        private void ApplyCorners()
        {
            if (Handle == IntPtr.Zero) return;
            int preference = 2;
            int hr = DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
            if (hr != 0)
            {
                int radius = Px(10);
                IntPtr region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, radius * 2, radius * 2);
                SetWindowRgn(Handle, region, true);
            }
        }

        private static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(LogDir);
                    try
                    {
                        var info = new FileInfo(LogPath);
                        if (info.Exists && info.Length > 512 * 1024)
                        {
                            File.Copy(LogPath, LogPath + ".old", true);
                            File.WriteAllText(LogPath, string.Empty, new UTF8Encoding(false));
                        }
                    }
                    catch { }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n";
                    File.AppendAllText(LogPath, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        private sealed class Hit
        {
            public HitKind Kind;
            public AppVM VM;
            public Hit(HitKind kind, AppVM vm) { Kind = kind; VM = vm; }
            public string Key { get { return Kind + "|" + (VM != null ? VM.Def.Key : string.Empty); } }
        }

        private enum HitKind
        {
            None, HeaderDrag, Refresh, Min, Close, Primary, Log, RowClose, RowAction, RowDrag, ScrollThumb
        }

        private sealed class IconEntry
        {
            public Bitmap Image;
            public bool Opaque;
        }

        private sealed class RowLayout
        {
            public AppVM VM;
            public Rectangle Rect;
            public Rectangle IconRect;
            public Rectangle CloseRect;
            public Rectangle ActionRect;
            public Rectangle GripRect;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, int minSize, int maxSize);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIcons(string lpszFilename, int nIconIndex, int cxIcon, int cyIcon, [Out] IntPtr[] phicon, [Out] int[] piconid, int nIcons, uint flags);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICONLOCATION = 0x000001000;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
    }

    internal sealed class AppDef
    {
        public string Key;
        public string Name;
        public string Badge;
        public string BadgeColor;
        public string AppIdPrefix;
        public bool PreferShortcut;
        public string[] Shortcuts;
        public string[] Exes;
        public string[] Names;
        public string[] PathPrefixes;
        public bool PathOnly;
        public bool RequirePath;
        public bool Force;
    }

    internal sealed class AppVM
    {
        public AppDef Def;
        public LaunchInfo Launch;
        public List<string> Prefixes;
        public bool Installed;
        public int ProcessCount;
        public string LastMessage;
        public bool Warning;
    }

    internal sealed class LaunchInfo
    {
        public string Kind;
        public string Target;
        public string Display;
        public bool Installed;
        public List<string> ExtraPathPrefixes;
        // 目录里解析出的真实 exe，用于快捷方式没带图标时取应用图标
        public string ExePath;
        // AppX 的图标源要查装包清单才能定位，解析一次记在这里，刷新时随 LaunchInfo 一起作废
        public bool AppxIconProbed;
        public string AppxIconPath;
    }

    internal sealed class ProcInfo
    {
        public int Pid;
        public string Name;
        public string Path;
    }

    internal static class AppxApps
    {
        public static Dictionary<string, string> Read()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            object shell = null;
            object folder = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return map;
                shell = Activator.CreateInstance(shellType);
                dynamic sh = shell;
                folder = sh.Namespace("shell:AppsFolder");
                if (folder == null) return map;
                dynamic f = folder;
                foreach (dynamic item in f.Items())
                {
                    try
                    {
                        string name = (string)item.Name;
                        string path = (string)item.Path;
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path) && !map.ContainsKey(name)) map[name] = path;
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                try { if (folder != null) Marshal.ReleaseComObject(folder); } catch { }
                try { if (shell != null) Marshal.ReleaseComObject(shell); } catch { }
            }
            return map;
        }
    }
}
