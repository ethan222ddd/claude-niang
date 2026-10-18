// Claude 娘：a desktop companion that sits on the Claude window and shows
// the Claude Code 5-hour and weekly usage limits.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeNiang
{
    // ------------------------------------------------------------------ settings

    public class Settings
    {
        public string Mode = "window";      // "window": rides on the Claude window; "desktop": free on screen
        public double Scale = 1.0;          // 0.6 – 2.5
        public int RelRight = -1, RelBottom = -1;   // window mode: px from the Claude window's right/bottom edge
        public int DeskRight = -1, DeskBottom = -1; // desktop mode: screen px of the widget's bottom-right
        public bool Topmost = false;        // desktop mode only
        public bool Sound = true;
        public double Volume = 0.6;
        public bool CharacterAdvances = false;
        public bool ShowMenuButton = true;

        static string FilePath { get { return System.IO.Path.Combine(App.StateDir, "settings.json"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null) { s.Clamp(); return s; }
                }
            }
            catch { }
            return new Settings();
        }

        public void Clamp()
        {
            if (Mode != "desktop") Mode = "window";
            Scale = Math.Max(0.6, Math.Min(2.5, Scale));
            Volume = Math.Max(0, Math.Min(1, Volume));
        }

        public void Save()
        {
            try { Util.AtomicWrite(FilePath, new JavaScriptSerializer().Serialize(this)); } catch { }
        }
    }

    public static class Util
    {
        public static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            string tmp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        public static object Get(object obj, string key)
        {
            var d = obj as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static double? Num(object v)
        {
            if (v == null) return null;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return null; }
        }
    }

    // ------------------------------------------------------------------ usage

    public class QuotaWindow
    {
        public string Label;
        public double Used;          // percent 0–100
        public DateTimeOffset? ResetsAt;
        public double Remaining { get { return Math.Max(0, Math.Min(100, 100 - Used)); } }
    }

    public class Snapshot
    {
        public List<QuotaWindow> Windows = new List<QuotaWindow>();
        public string Plan;
        public string Error;
        public DateTime CheckedAt;
        public bool Ok { get { return Error == null && Windows.Count > 0; } }
    }

    // Reads the Claude Code login on this machine and asks Anthropic for the
    // subscription usage windows, the same numbers /usage shows. Credentials are
    // only sent to Anthropic and are never logged or displayed.
    public static class UsageClient
    {
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        static readonly string[] TokenUrls = { "https://platform.claude.com/v1/oauth/token", "https://console.anthropic.com/v1/oauth/token" };
        const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        static readonly HttpClient http;
        static readonly SemaphoreSlim credentialLock = new SemaphoreSlim(1, 1);

        static UsageClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("claude-niang/1.0");
        }

        public static string CredentialsPath
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(dir)) dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                return System.IO.Path.Combine(dir, ".credentials.json");
            }
        }

        class Login { public string Access, Refresh, Plan; public long ExpiresAt; }

        static Login ReadLogin()
        {
            if (!File.Exists(CredentialsPath)) return null;
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(CredentialsPath, Encoding.UTF8));
            var o = Util.Get(root, "claudeAiOauth");
            if (o == null) return null;
            var l = new Login();
            l.Access = Util.Get(o, "accessToken") as string;
            l.Refresh = Util.Get(o, "refreshToken") as string;
            l.Plan = Util.Get(o, "subscriptionType") as string;
            l.ExpiresAt = (long)(Util.Num(Util.Get(o, "expiresAt")) ?? 0);
            return l;
        }

        static long NowMs { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }

        // Refreshes an expired access token the same way Claude Code does and
        // writes the new pair back so Claude Code keeps working with it.
        static async Task<Login> RefreshLogin(Login old)
        {
            await credentialLock.WaitAsync();
            try
            {
                var current = ReadLogin();
                if (current == null) return null;
                // Claude Code may have refreshed it in the meantime.
                if (current.Refresh != old.Refresh || current.ExpiresAt > NowMs + 60000) return current;
                if (string.IsNullOrEmpty(current.Refresh)) return null;

                var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "grant_type", "refresh_token" }, { "refresh_token", current.Refresh }, { "client_id", ClientId } });
                foreach (var url in TokenUrls)
                {
                    HttpResponseMessage res;
                    try { res = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json")); }
                    catch { continue; }
                    if (res.StatusCode == HttpStatusCode.NotFound) continue;
                    if (!res.IsSuccessStatusCode) return null;
                    var json = new JavaScriptSerializer().DeserializeObject(await res.Content.ReadAsStringAsync());
                    var access = Util.Get(json, "access_token") as string;
                    if (string.IsNullOrEmpty(access)) return null;
                    var refresh = Util.Get(json, "refresh_token") as string ?? current.Refresh;
                    long expires = NowMs + (long)((Util.Num(Util.Get(json, "expires_in")) ?? 3600) * 1000);

                    // Rewrite only the token fields; everything else in the file is kept.
                    var ser = new JavaScriptSerializer();
                    var root = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(CredentialsPath, Encoding.UTF8));
                    var o = (Dictionary<string, object>)root["claudeAiOauth"];
                    o["accessToken"] = access;
                    o["refreshToken"] = refresh;
                    o["expiresAt"] = expires;
                    Util.AtomicWrite(CredentialsPath, ser.Serialize(root));
                    current.Access = access; current.Refresh = refresh; current.ExpiresAt = expires;
                    return current;
                }
                return null;
            }
            finally { credentialLock.Release(); }
        }

        public static async Task<Snapshot> Fetch()
        {
            var snap = new Snapshot { CheckedAt = DateTime.Now };
            Login login;
            try { login = ReadLogin(); }
            catch { snap.Error = "讀不到 Claude Code 的登入資料"; return snap; }
            if (login == null || string.IsNullOrEmpty(login.Access))
            {
                snap.Error = "找不到 Claude Code 登入。請在終端機執行一次 claude 並登入";
                return snap;
            }
            snap.Plan = login.Plan;
            try
            {
                if (login.ExpiresAt > 0 && login.ExpiresAt < NowMs + 60000)
                {
                    login = await RefreshLogin(login);
                    if (login == null) { snap.Error = "登入已過期。在終端機執行一次 claude 就會更新"; return snap; }
                }
                var res = await Get(login.Access);
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    login = await RefreshLogin(login);
                    if (login == null) { snap.Error = "登入已過期。在終端機執行一次 claude 就會更新"; return snap; }
                    res = await Get(login.Access);
                }
                if (res.StatusCode == HttpStatusCode.Forbidden) { snap.Error = "這個帳號沒有訂閱額度資料（API Key 登入不適用）"; return snap; }
                if ((int)res.StatusCode == 429) { snap.Error = "查詢太頻繁，稍後再試"; return snap; }
                if (!res.IsSuccessStatusCode) { snap.Error = "額度服務暫時無法使用（" + (int)res.StatusCode + "）"; return snap; }
                Parse(await res.Content.ReadAsStringAsync(), snap);
                if (snap.Windows.Count == 0 && snap.Error == null) snap.Error = "目前帳號沒有可顯示的額度窗口";
            }
            catch (TaskCanceledException) { snap.Error = "查詢逾時，稍後重試"; }
            catch (HttpRequestException) { snap.Error = "連不上伺服器，稍後重試"; }
            catch (Exception) { snap.Error = "讀取額度時發生錯誤，稍後重試"; }
            return snap;
        }

        static Task<HttpResponseMessage> Get(string token)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            return http.SendAsync(req);
        }

        public static void Parse(string json, Snapshot snap)
        {
            var root = new JavaScriptSerializer().DeserializeObject(json);
            Add(snap, root, "five_hour", "5 小時");
            Add(snap, root, "seven_day", "每週");
        }

        static void Add(Snapshot snap, object root, string key, string label)
        {
            var w = Util.Get(root, key);
            var used = Util.Num(Util.Get(w, "utilization"));
            if (w == null || used == null) return;
            var q = new QuotaWindow { Label = label, Used = Math.Max(0, Math.Min(100, used.Value)) };
            var reset = Util.Get(w, "resets_at") as string;
            DateTimeOffset at;
            if (reset != null && DateTimeOffset.TryParse(reset, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out at)) q.ResetsAt = at;
            snap.Windows.Add(q);
        }
    }

    // ------------------------------------------------------------------ quotes

    public static class Quotes
    {
        class Item { public string text = ""; public double weight = 1; }
        class FileModel { public int version = 1; public List<Item> quotes = new List<Item>(); }

        static List<Item> items;
        static string last;
        static readonly Random rng = new Random();

        public static string FilePath { get { return System.IO.Path.Combine(App.StateDir, "quotes.json"); } }
        static string BuiltInPath { get { return System.IO.Path.Combine(App.AppDir, "quotes.json"); } }

        public static void Load()
        {
            items = Read(FilePath) ?? Read(BuiltInPath) ?? new List<Item> { new Item { text = "我在這裡，慢慢來就好。" } };
        }

        static List<Item> Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var m = new JavaScriptSerializer().Deserialize<FileModel>(File.ReadAllText(path, Encoding.UTF8));
                var list = m.quotes.Where(q => !string.IsNullOrWhiteSpace(q.text) && q.weight > 0).ToList();
                return list.Count > 0 ? list : null;
            }
            catch { return null; }
        }

        // Copies the built-in list to the user's state folder so it can be edited.
        public static string EnsureEditable()
        {
            if (!File.Exists(FilePath) && File.Exists(BuiltInPath))
            {
                Directory.CreateDirectory(App.StateDir);
                File.Copy(BuiltInPath, FilePath);
            }
            return FilePath;
        }

        public static string Next()
        {
            if (items == null) Load();
            var pool = items.Count > 1 ? items.Where(q => q.text != last).ToList() : items;
            double total = pool.Sum(q => q.weight), r = rng.NextDouble() * total;
            foreach (var q in pool) { r -= q.weight; if (r <= 0) { last = q.text; return q.text; } }
            last = pool[pool.Count - 1].text;
            return last;
        }
    }

    // ------------------------------------------------------------------ sound

    // Short synthesized press/release blips; no audio files are shipped.
    public static class Sfx
    {
        static byte[] Tone(double f0, double f1, double ms, double volume)
        {
            const int rate = 22050;
            int n = (int)(rate * ms / 1000);
            var ms2 = new MemoryStream();
            var w = new BinaryWriter(ms2);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)n;
                double f = f0 + (f1 - f0) * t;
                phase += 2 * Math.PI * f / rate;
                double env = Math.Min(1, i / (rate * 0.004)) * Math.Pow(1 - t, 2.2);
                double s = (Math.Sin(phase) * 0.8 + Math.Sin(phase * 2) * 0.2) * env * volume * 0.55;
                w.Write((short)(s * short.MaxValue));
            }
            return ms2.ToArray();
        }

        static System.Media.SoundPlayer player;

        public static void Play(bool press, Settings s)
        {
            if (!s.Sound || s.Volume <= 0.01) return;
            try
            {
                var data = press ? Tone(640, 470, 70, s.Volume) : Tone(720, 1050, 90, s.Volume);
                if (player != null) player.Stop();
                player = new System.Media.SoundPlayer(new MemoryStream(data));
                player.Play();
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ native

    public static class Native
    {
        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        public const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
        public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 1, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        public const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17;

        static readonly Dictionary<uint, bool> claudePid = new Dictionary<uint, bool>();

        static bool IsClaudeProcess(uint pid)
        {
            bool known;
            if (claudePid.TryGetValue(pid, out known)) return known;
            bool result = false;
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size))
                {
                    string path = sb.ToString();
                    // The desktop app is an MSIX package; the Claude Code CLI shares
                    // the claude.exe name but has no windows of its own.
                    result = path.IndexOf("\\WindowsApps\\Claude_", StringComparison.OrdinalIgnoreCase) >= 0
                        && System.IO.Path.GetFileName(path).Equals("claude.exe", StringComparison.OrdinalIgnoreCase);
                }
                CloseHandle(h);
            }
            if (claudePid.Count > 512) claudePid.Clear();
            claudePid[pid] = result;
            return result;
        }

        public static bool Cloaked(IntPtr h)
        {
            int v;
            return DwmGetWindowAttribute(h, 14, out v, 4) == 0 && v != 0;
        }

        // The Claude main window: a visible, unowned, titled top-level window of the
        // packaged Claude process. The largest one wins over popups and quick entry.
        public static IntPtr FindClaude()
        {
            IntPtr best = IntPtr.Zero; long bestArea = 0;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                if (((long)GetWindowLongPtr(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (!IsClaudeProcess(pid)) return true;
                RECT r; GetWindowRect(h, out r);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if ((r.Right - r.Left) < 300 || (r.Bottom - r.Top) < 300) return true;
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // Visible frame without the invisible resize borders.
        public static RECT Frame(IntPtr h)
        {
            RECT r;
            if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
            return r;
        }

        public static RECT WorkArea(IntPtr h)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
            return mi.rcWork;
        }
    }

    // ------------------------------------------------------------------ view

    public enum BubbleMode { None, Quota, Quote }

    // All drawing lives here so it can be rendered off-screen for checks.
    public class NiangView : Canvas
    {
        static readonly Color Ink = C("#9A4F22"), Stroke = C("#B8612E"), Paper = C("#FFFCF7"), Soft = C("#A86A45"),
            Track = C("#F6E2D2"), Fill = C("#EE8E4E"), Low = C("#DE5A3C"), Hover = C("#FBE9DC");
        public static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public readonly Canvas Root = new Canvas { Width = 350, Height = 350 };
        public readonly Canvas Bubble = new Canvas { Width = 262, Height = 160, Cursor = Cursors.Hand };
        public readonly Ellipse TailFar = new Ellipse { Width = 24, Height = 18 }, TailNear = new Ellipse { Width = 15, Height = 11 };
        public readonly Image Girl = new Image { Width = 214, Height = 214, Cursor = Cursors.Hand };
        public readonly Border MenuButton;
        public readonly TextBlock RefreshButton;
        readonly ScaleTransform facing = new ScaleTransform(), textFacing = new ScaleTransform();
        readonly ScaleTransform bubbleScale = new ScaleTransform(1, 1), farScale = new ScaleTransform(1, 1), nearScale = new ScaleTransform(1, 1);
        public readonly ScaleTransform Press = new ScaleTransform(1, 1), Breath = new ScaleTransform(1, 1);
        readonly Grid textHost = new Grid { Width = 190, Height = 128 };
        readonly StackPanel quotaPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock header, status, quoteText;
        readonly Row[] rows = new Row[2];
        public BubbleMode Mode = BubbleMode.None;

        class Row
        {
            public StackPanel Panel; public TextBlock Label, Left, Reset; public Border Bar; public QuotaWindow Data;
        }

        public NiangView(BitmapSource art)
        {
            Width = 378; Height = 378;
            Children.Add(Root);
            SetLeft(Root, 14); SetTop(Root, 14);
            Root.RenderTransformOrigin = new Point(0.5, 1);
            Root.RenderTransform = facing;

            // Speech bubble: a soft rounded cloud with two trailing dots toward her head.
            var shape = new Border
            {
                Width = 262, Height = 160, CornerRadius = new CornerRadius(80, 80, 72, 80),
                Background = B(Paper), BorderBrush = B(Stroke), BorderThickness = new Thickness(3.5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = C("#7A3E1E"), Opacity = 0.18, BlurRadius = 10, ShadowDepth = 2, Direction = 270 }
            };
            Bubble.Children.Add(shape);
            Bubble.RenderTransformOrigin = new Point(0.78, 0.95);
            Bubble.RenderTransform = bubbleScale;
            SetLeft(Bubble, 4); SetTop(Bubble, 8);

            textHost.RenderTransformOrigin = new Point(0.5, 0.5);
            textHost.RenderTransform = textFacing;
            SetLeft(textHost, 36); SetTop(textHost, 16);
            Bubble.Children.Add(textHost);

            var headerRow = new Grid { Height = 18 };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition());
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            header = Text("剩餘額度", 12.5, Soft, FontWeights.SemiBold);
            header.HorizontalAlignment = HorizontalAlignment.Center;
            header.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(header, 1);
            headerRow.Children.Add(header);
            RefreshButton = Text("↻", 13, Soft, FontWeights.Bold);
            RefreshButton.Cursor = Cursors.Hand;
            RefreshButton.ToolTip = "立即重新整理額度";
            RefreshButton.HorizontalAlignment = HorizontalAlignment.Right;
            RefreshButton.Padding = new Thickness(4, 0, 4, 0);
            RefreshButton.Background = Brushes.Transparent;
            RefreshButton.MouseEnter += (s, e) => RefreshButton.Background = B(Hover);
            RefreshButton.MouseLeave += (s, e) => RefreshButton.Background = Brushes.Transparent;
            Grid.SetColumn(RefreshButton, 2);
            headerRow.Children.Add(RefreshButton);
            quotaPanel.Children.Add(headerRow);

            for (int i = 0; i < 2; i++)
            {
                var r = new Row();
                r.Panel = new StackPanel { Margin = new Thickness(0, i == 0 ? 2 : 5, 0, 0), Background = Brushes.Transparent };
                var g = new Grid { Height = 26 };
                r.Label = Text(i == 0 ? "5 小時" : "每週", 13.5, Soft, FontWeights.Normal);
                r.Label.VerticalAlignment = VerticalAlignment.Center;
                r.Left = Text("—", 24, Ink, FontWeights.Bold);
                r.Left.FontFamily = new FontFamily("Segoe UI");
                r.Left.HorizontalAlignment = HorizontalAlignment.Right;
                r.Left.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(r.Label); g.Children.Add(r.Left);
                r.Panel.Children.Add(g);
                var track = new Border { Height = 5, Background = B(Track), CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, 1, 0, 0) };
                r.Bar = new Border { Background = B(Fill), CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
                track.Child = r.Bar;
                r.Panel.Children.Add(track);
                r.Reset = Text("等待額度資料", 12, Soft, FontWeights.Normal);
                r.Reset.Margin = new Thickness(0, 2, 0, 0);
                r.Panel.Children.Add(r.Reset);
                quotaPanel.Children.Add(r.Panel);
                rows[i] = r;
            }
            status = Text("正在讀取額度…", 13, Ink, FontWeights.SemiBold);
            status.TextWrapping = TextWrapping.Wrap;
            status.TextAlignment = TextAlignment.Center;
            status.Margin = new Thickness(0, 10, 0, 0);
            status.Visibility = Visibility.Collapsed;
            quotaPanel.Children.Add(status);
            textHost.Children.Add(quotaPanel);

            quoteText = Text("", 19, Ink, FontWeights.Bold);
            quoteText.TextWrapping = TextWrapping.Wrap;
            quoteText.TextAlignment = TextAlignment.Center;
            quoteText.HorizontalAlignment = HorizontalAlignment.Center;
            quoteText.VerticalAlignment = VerticalAlignment.Center;
            quoteText.LineHeight = 25;
            quoteText.Visibility = Visibility.Collapsed;
            textHost.Children.Add(quoteText);

            foreach (var t in new[] { TailFar, TailNear })
            {
                t.Fill = B(Paper); t.Stroke = B(Stroke); t.StrokeThickness = 3.5;
                t.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            TailFar.RenderTransform = farScale; TailNear.RenderTransform = nearScale;
            SetLeft(TailFar, 150); SetTop(TailFar, 170);
            SetLeft(TailNear, 136); SetTop(TailNear, 196);

            Girl.Source = art;
            Girl.RenderTransformOrigin = new Point(0.5, 1);
            var tg = new TransformGroup(); tg.Children.Add(Breath); tg.Children.Add(Press);
            Girl.RenderTransform = tg;
            Girl.ToolTip = "點我看額度；點泡泡看語錄，再點收起；拖曳可移動；右鍵設定";
            ToolTipService.SetInitialShowDelay(Girl, 900);
            SetLeft(Girl, 136); SetTop(Girl, 136);

            MenuButton = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = B(Paper),
                BorderBrush = B(Stroke), BorderThickness = new Thickness(2), Cursor = Cursors.Hand, Opacity = 0,
                Child = new TextBlock { Text = "☰", FontSize = 13, Foreground = B(Stroke), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = "Claude 娘設定"
            };
            SetLeft(MenuButton, 318); SetTop(MenuButton, 166);

            Root.Children.Add(Bubble);
            Root.Children.Add(TailFar);
            Root.Children.Add(TailNear);
            Root.Children.Add(Girl);
            Root.Children.Add(MenuButton);
            SetBubbleVisible(false);
        }

        static TextBlock Text(string s, double size, Color color, FontWeight weight)
        {
            return new TextBlock
            {
                Text = s, FontSize = size, Foreground = B(color), FontWeight = weight,
                FontFamily = new FontFamily("Microsoft JhengHei UI, Microsoft JhengHei, Segoe UI"), IsHitTestVisible = false
            };
        }

        public void SetFacingLeft(bool left)
        {
            // On the left half she turns inward; text is mirrored back to stay readable.
            facing.ScaleX = left ? -1 : 1;
            textFacing.ScaleX = left ? -1 : 1;
            ((TextBlock)MenuButton.Child).RenderTransformOrigin = new Point(0.5, 0.5);
            ((TextBlock)MenuButton.Child).RenderTransform = new ScaleTransform(left ? -1 : 1, 1);
        }

        public void SetBubbleVisible(bool on)
        {
            var v = on ? Visibility.Visible : Visibility.Hidden;
            Bubble.Visibility = TailFar.Visibility = TailNear.Visibility = v;
        }

        public void ShowQuota() { Mode = BubbleMode.Quota; quotaPanel.Visibility = Visibility.Visible; quoteText.Visibility = Visibility.Collapsed; }

        public void ShowQuote(string text)
        {
            Mode = BubbleMode.Quote;
            quotaPanel.Visibility = Visibility.Collapsed;
            quoteText.Visibility = Visibility.Visible;
            quoteText.Text = text;
            // Long lines shrink a little, then trim; the full text is in the tooltip.
            quoteText.FontSize = text.Length <= 18 ? 19 : text.Length <= 30 ? 16.5 : 15;
            quoteText.LineHeight = quoteText.FontSize + 6;
            quoteText.MaxHeight = 124;
            quoteText.TextTrimming = TextTrimming.CharacterEllipsis;
            Bubble.ToolTip = text.Length > 30 ? text : null;
        }

        static string PlanName(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            switch (p.ToLowerInvariant())
            {
                case "pro": return "Pro";
                case "max": return "Max";
                case "team": return "Team";
                case "enterprise": return "Enterprise";
                case "free": return "Free";
                default: return p;
            }
        }

        // absolute: false = "2 小時 5 分後重置", true = "今天 18:30 重置".
        public void SetSnapshot(Snapshot snap, bool absolute, bool refreshing)
        {
            string plan = snap != null ? PlanName(snap.Plan) : null;
            header.Text = refreshing ? "正在更新…" : plan != null ? "剩餘額度 · " + plan : "剩餘額度";
            bool has = snap != null && snap.Windows.Count > 0;
            status.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            status.Text = snap == null ? "正在讀取額度…" : snap.Error ?? "沒有額度資料";
            for (int i = 0; i < 2; i++)
            {
                var r = rows[i];
                r.Data = has && i < snap.Windows.Count ? snap.Windows[i] : null;
                r.Panel.Visibility = has ? (r.Data != null ? Visibility.Visible : Visibility.Collapsed) : Visibility.Collapsed;
                if (r.Data == null) continue;
                var w = r.Data;
                r.Label.Text = w.Label;
                r.Left.Text = Math.Round(w.Remaining) + "%";
                r.Bar.Width = 190 * w.Remaining / 100.0;
                r.Bar.Background = B(w.Remaining < 15 ? Low : Fill);
                r.Left.Foreground = B(w.Remaining < 15 ? Low : Ink);
                r.Reset.Text = ResetText(w.ResetsAt, absolute);
                if (has && snap.Error != null && i == 0) r.Reset.Text = snap.Error;
                r.Panel.ToolTip = string.Format("{0}：已用 {1}%，剩餘 {2}%\n重置：{3}\n更新於 {4:HH:mm:ss}",
                    w.Label, Math.Round(w.Used, 1), Math.Round(w.Remaining, 1),
                    w.ResetsAt.HasValue ? w.ResetsAt.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm（zzz）") + "，" + ResetText(w.ResetsAt, false) : "未提供",
                    snap.CheckedAt);
            }
        }

        static string ResetText(DateTimeOffset? at, bool absolute)
        {
            if (!at.HasValue) return "未提供重置時間";
            var local = at.Value.ToLocalTime().DateTime;
            var left = local - DateTime.Now;
            if (left.TotalSeconds <= 0) return "已重置，等待更新";
            if (!absolute)
            {
                if (left.TotalMinutes < 60) return Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + " 分鐘後重置";
                if (left.TotalHours < 24) return (int)left.TotalHours + " 小時 " + left.Minutes + " 分後重置";
                return (int)left.TotalDays + " 天 " + left.Hours + " 小時後重置";
            }
            string day = local.Date == DateTime.Today ? "今天" : local.Date == DateTime.Today.AddDays(1) ? "明天" :
                local.ToString("M/d") + " " + "日一二三四五六"[(int)local.DayOfWeek].ToString().Insert(0, "週");
            return day + " " + local.ToString("HH:mm") + " 重置";
        }

        // Bubble entrance: far dot, near dot, then the bubble pops with a little overshoot.
        public void AnimateOpen(bool animate)
        {
            SetBubbleVisible(true);
            if (!animate)
            {
                foreach (var s in new[] { nearScale, farScale, bubbleScale }) { s.BeginAnimation(ScaleTransform.ScaleXProperty, null); s.BeginAnimation(ScaleTransform.ScaleYProperty, null); s.ScaleX = s.ScaleY = 1; }
                return;
            }
            Pop(nearScale, 0, 110, false);
            Pop(farScale, 70, 120, false);
            Pop(bubbleScale, 140, 260, true);
        }

        public void AnimateClose(Action done)
        {
            var dur = TimeSpan.FromMilliseconds(130);
            foreach (var s in new[] { bubbleScale, farScale, nearScale })
            {
                var a = new DoubleAnimation(0, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
            var t = new DispatcherTimer { Interval = dur };
            t.Tick += (o, e) => { t.Stop(); SetBubbleVisible(false); Mode = BubbleMode.None; if (done != null) done(); };
            t.Start();
        }

        static void Pop(ScaleTransform s, int delay, int ms, bool overshoot)
        {
            var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = overshoot ? (IEasingFunction)new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } : new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            s.ScaleX = s.ScaleY = 0;
            s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // Squash on press (105% wide, 88% tall) and a springy release.
        public void AnimatePress(bool down)
        {
            if (down)
            {
                var d = TimeSpan.FromMilliseconds(90);
                Press.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, d) { EasingFunction = new QuadraticEase() });
                Press.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, d) { EasingFunction = new QuadraticEase() });
            }
            else
            {
                var d = TimeSpan.FromMilliseconds(220);
                var e = new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut };
                Press.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = e });
                Press.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = e });
            }
        }

        public void StartBreathing()
        {
            var a = new DoubleAnimation(1, 1.014, TimeSpan.FromSeconds(1.8))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Breath.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    // ------------------------------------------------------------------ window

    public class WidgetWindow : Window
    {
        readonly App app;
        public readonly NiangView View;
        readonly Viewbox box = new Viewbox { Stretch = Stretch.Uniform };
        IntPtr hwnd, owner;
        DispatcherTimer collapseTimer, phaseTimer;
        bool pressed, dragging, menuOpen;
        Point pressAt; Native.RECT pressRect;
        public bool AllowClose;

        public WidgetWindow(App app, BitmapSource art)
        {
            this.app = app;
            Title = "Claude 娘";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = null;
            ShowInTaskbar = false; ShowActivated = false;
            UseLayoutRounding = true;
            View = new NiangView(art);
            box.Child = View;
            Content = box;
            ApplyScale();
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            View.Girl.MouseLeftButtonDown += (s, e) => { BeginPress(e); e.Handled = true; };
            View.Bubble.MouseLeftButtonUp += (s, e) => { if (!dragging) OnBubbleClick(); e.Handled = true; };
            View.RefreshButton.IsHitTestVisible = true;
            View.RefreshButton.MouseLeftButtonUp += (s, e) => { e.Handled = true; app.RequestRefresh(true); RestartCollapse(); };
            View.MenuButton.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenMenu(View.MenuButton); };
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            LostMouseCapture += (s, e) => { if (pressed) { pressed = false; dragging = false; View.AnimatePress(false); } };
            MouseRightButtonUp += (s, e) => { e.Handled = true; OpenMenu(null); };
            MouseEnter += (s, e) => FadeMenuButton(true);
            MouseLeave += (s, e) => { if (!menuOpen) FadeMenuButton(false); };

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                // Clicking her never steals focus from what you are typing in Claude.
                long ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            };
            View.StartBreathing();
        }

        public IntPtr Handle { get { return hwnd; } }

        public void ApplyScale()
        {
            Width = Height = 378 * app.Settings.Scale;
        }

        void FadeMenuButton(bool on)
        {
            bool show = on && app.Settings.ShowMenuButton;
            View.MenuButton.IsHitTestVisible = show;
            View.MenuButton.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(150)));
        }

        // ---- ownership and placement (physical pixels)

        public void SetOwner(IntPtr h)
        {
            if (hwnd == IntPtr.Zero || owner == h) return;
            owner = h;
            Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, h);
        }

        public Native.RECT Rect
        {
            get { Native.RECT r; Native.GetWindowRect(hwnd, out r); return r; }
        }

        public void MoveTo(int left, int top)
        {
            if (hwnd == IntPtr.Zero) return;
            var r = Rect;
            if (r.Left == left && r.Top == top) return;
            Native.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        public void SetFacing(bool left) { View.SetFacingLeft(left); }

        // ---- pointer

        void BeginPress(MouseButtonEventArgs e)
        {
            pressed = true; dragging = false;
            pressAt = PointToScreen(e.GetPosition(this));
            pressRect = Rect;
            CaptureMouse();
            View.AnimatePress(true);
            Sfx.Play(true, app.Settings);
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (!pressed) return;
            var p = PointToScreen(e.GetPosition(this));
            double dx = p.X - pressAt.X, dy = p.Y - pressAt.Y;
            // 3 px before a press turns into a drag.
            if (!dragging && dx * dx + dy * dy < 9 * app.DpiScale * app.DpiScale) return;
            if (!dragging) { dragging = true; View.AnimatePress(false); }
            var r = app.ClampToArea(pressRect.Left + (int)dx, pressRect.Top + (int)dy, pressRect.Right - pressRect.Left, pressRect.Bottom - pressRect.Top);
            MoveTo(r.X, r.Y);
            app.UpdateFacing();
        }

        void OnUp(object s, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            bool wasDrag = dragging;
            pressed = false; dragging = false;
            ReleaseMouseCapture();
            if (wasDrag) { app.RememberPosition(); return; }
            View.AnimatePress(false);
            Sfx.Play(false, app.Settings);
            OnCharacterClick();
        }

        // ---- bubble state machine: quota → quote → closed

        void OnCharacterClick()
        {
            if (View.Mode == BubbleMode.None) { OpenQuota(true); return; }
            if (app.Settings.CharacterAdvances) { OnBubbleClick(); return; }
            if (View.Mode == BubbleMode.Quote) { OpenQuota(false); return; }
            RestartCollapse(); // quota already showing: keep it a while longer
            app.RequestRefresh(false);
        }

        void OnBubbleClick()
        {
            if (View.Mode == BubbleMode.Quota) { View.ShowQuote(Quotes.Next()); RestartCollapse(); }
            else if (View.Mode == BubbleMode.Quote) Collapse();
        }

        void OpenQuota(bool fresh)
        {
            View.ShowQuota();
            showRelative = true;
            Refresh();
            if (fresh) View.AnimateOpen(true);
            app.RequestRefresh(false);
            RestartCollapse();
        }

        bool showRelative = true;

        public void Refresh()
        {
            View.SetSnapshot(app.Snapshot, !showRelative, app.Refreshing);
        }

        void RestartCollapse()
        {
            if (collapseTimer != null) collapseTimer.Stop();
            if (phaseTimer != null) phaseTimer.Stop();
            showRelative = true;
            Refresh();
            // Quota: 2 s countdown, 3 s clock time, then close. Quote: 5 s.
            phaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            phaseTimer.Tick += (s, e) => { phaseTimer.Stop(); showRelative = false; Refresh(); };
            phaseTimer.Start();
            collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5.2) };
            collapseTimer.Tick += (s, e) => { if (menuOpen || IsMouseOver) return; Collapse(); };
            collapseTimer.Start();
        }

        public void Collapse()
        {
            if (collapseTimer != null) collapseTimer.Stop();
            if (phaseTimer != null) phaseTimer.Stop();
            if (View.Mode != BubbleMode.None) View.AnimateClose(null);
        }

        // ---- menu

        void OpenMenu(UIElement anchor)
        {
            var s = app.Settings;
            var m = new ContextMenu { FontFamily = new FontFamily("Microsoft JhengHei UI"), FontSize = 13 };
            Add(m, "立即更新額度", () => { app.RequestRefresh(true); OpenQuota(View.Mode == BubbleMode.None); });
            m.Items.Add(new Separator());
            Add(m, "顯示在 Claude 視窗上", () => app.SetMode("window"), s.Mode == "window");
            Add(m, "顯示在整個桌面", () => app.SetMode("desktop"), s.Mode == "desktop");
            if (s.Mode == "desktop") Add(m, "桌面置頂", () => { s.Topmost = !s.Topmost; s.Save(); app.ApplyMode(); }, s.Topmost);
            m.Items.Add(new Separator());
            m.Items.Add(SliderItem("大小", 0.6, 2.5, s.Scale, v => v.ToString("0.0") + "×", v => { s.Scale = v; app.Rescale(); }, () => s.Save()));
            var presets = new MenuItem { Header = "大小預設" };
            foreach (var p in new[] { Tuple.Create("小", 0.8), Tuple.Create("中", 1.0), Tuple.Create("大", 1.4), Tuple.Create("特大", 2.0) })
            {
                var v = p.Item2;
                Add(presets, p.Item1 + "（" + v.ToString("0.0") + "×）", () => { s.Scale = v; app.Rescale(); s.Save(); }, Math.Abs(s.Scale - v) < 0.05);
            }
            m.Items.Add(presets);
            m.Items.Add(new Separator());
            Add(m, "音效", () => { s.Sound = !s.Sound; s.Save(); }, s.Sound);
            if (s.Sound) m.Items.Add(SliderItem("音量", 0, 1, s.Volume, v => Math.Round(v * 100) + "%", v => s.Volume = v, () => { s.Save(); Sfx.Play(false, s); }));
            Add(m, "點角色也切換語錄", () => { s.CharacterAdvances = !s.CharacterAdvances; s.Save(); }, s.CharacterAdvances);
            Add(m, "顯示選單按鈕", () => { s.ShowMenuButton = !s.ShowMenuButton; s.Save(); FadeMenuButton(s.ShowMenuButton); }, s.ShowMenuButton);
            Add(m, "編輯語錄…", () => { try { Process.Start("notepad.exe", "\"" + Quotes.EnsureEditable() + "\""); } catch { } });
            Add(m, "重新載入語錄", () => Quotes.Load());
            m.Items.Add(new Separator());
            Add(m, "開機時自動啟動", () => App.SetAutoStart(!App.AutoStart), App.AutoStart);
            Add(m, s.Mode == "window" ? "先隱藏（Claude 重開後再出現）" : "先隱藏（再次開啟 Claude 娘就會出現）", () => app.HideForNow());
            Add(m, "結束 Claude 娘", () => app.Quit());

            menuOpen = true;
            m.Closed += (o, e) => { menuOpen = false; if (!IsMouseOver) FadeMenuButton(false); };
            if (anchor != null) { m.PlacementTarget = anchor; m.Placement = PlacementMode.Bottom; }
            else { m.PlacementTarget = this; m.Placement = PlacementMode.MousePoint; }
            m.IsOpen = true;
        }

        static void Add(ItemsControl parent, string text, Action click, bool check = false)
        {
            var mi = new MenuItem { Header = text, IsChecked = check };
            mi.Click += (s, e) => click();
            parent.Items.Add(mi);
        }

        static MenuItem SliderItem(string label, double min, double max, double value, Func<double, string> fmt, Action<double> change, Action done)
        {
            var text = new TextBlock { Text = label + "　" + fmt(value), Width = 86, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 150, VerticalAlignment = VerticalAlignment.Center, SmallChange = 0.05, LargeChange = 0.2 };
            slider.ValueChanged += (s, e) => { text.Text = label + "　" + fmt(e.NewValue); change(e.NewValue); };
            slider.PreviewMouseLeftButtonUp += (s, e) => done();
            slider.LostMouseCapture += (s, e) => done();
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(text); panel.Children.Add(slider);
            return new MenuItem { Header = panel, StaysOpenOnClick = true };
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Alt+F4 or the owner closing must not end the program; only Quit does.
            if (!AllowClose) { e.Cancel = true; Hide(); }
            base.OnClosing(e);
        }
    }

    // ------------------------------------------------------------------ app

    public class App : Application
    {
        public static string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        public static string StateDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "ClaudeNiang";

        public Settings Settings;
        public Snapshot Snapshot;
        public bool Refreshing;
        public double DpiScale = 1;
        WidgetWindow win;
        BitmapSource art;
        IntPtr host, hook;
        Native.WinEventProc hookProc;
        bool hiddenForNow; IntPtr hiddenHost;
        DateTime lastAttempt = DateTime.MinValue;
        DispatcherTimer tick;
        EventWaitHandle showSignal, exitSignal;

        public static bool AutoStart
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
        }

        public static void SetAutoStart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                else k.DeleteValue(RunName, false);
            }
        }

        public static BitmapSource LoadArt()
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(System.IO.Path.Combine(AppDir, "claude-niang.png"));
            bmp.DecodePixelWidth = 640;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        public void Start(EventWaitHandle show, EventWaitHandle exit)
        {
            showSignal = show; exitSignal = exit;
            Settings = Settings.Load();
            Quotes.Load();
            art = LoadArt();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CreateWindow();
            DpiScale = VisualTreeHelper.GetDpi(win).DpiScaleX;

            // Other launches ask this instance to show itself or to exit.
            ThreadPool.RegisterWaitForSingleObject(showSignal, (o, t) => Dispatcher.BeginInvoke(new Action(() => { hiddenForNow = false; Update(); })), null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(exitSignal, (o, t) => Dispatcher.BeginInvoke(new Action(Quit)), null, -1, true);

            tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            tick.Tick += (s, e) => { Update(); MaybeRefresh(); };
            tick.Start();
            Update();
        }

        void CreateWindow()
        {
            win = new WidgetWindow(this, art);
            win.Left = -10000; win.Top = -10000;
            win.Show(); win.Hide();
            // If Windows tears her down along with a closing Claude window, start a fresh one.
            win.Closed += (s, e) =>
            {
                if (win.AllowClose) return;
                host = IntPtr.Zero;
                if (hook != IntPtr.Zero) { Native.UnhookWinEvent(hook); hook = IntPtr.Zero; }
                Dispatcher.BeginInvoke(new Action(() => { CreateWindow(); Update(); }));
            };
        }

        // ---- host tracking

        void Update()
        {
            if (Settings.Mode == "desktop")
            {
                SetHost(IntPtr.Zero);
                if (hiddenForNow) { if (win.IsVisible) win.Hide(); return; }
                ShowAt(DesktopPosition());
                return;
            }
            if (host == IntPtr.Zero || !Native.IsWindow(host) || !Native.IsWindowVisible(host)) SetHost(Native.FindClaude());
            if (hiddenForNow && hiddenHost != host) hiddenForNow = false;
            bool visible = host != IntPtr.Zero && !Native.IsIconic(host) && !Native.Cloaked(host) && !hiddenForNow;
            if (!visible) { if (win.IsVisible) { win.Collapse(); win.Hide(); } return; }
            ShowAt(HostPosition());
        }

        void SetHost(IntPtr h)
        {
            if (h == host) return;
            if (hook != IntPtr.Zero) { Native.UnhookWinEvent(hook); hook = IntPtr.Zero; }
            host = h;
            win.SetOwner(h);
            if (h == IntPtr.Zero) return;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            hookProc = OnHostEvent;
            // Out-of-context: delivered on this thread's message loop as the window moves.
            hook = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, hookProc, pid, 0, 0);
        }

        void OnHostEvent(IntPtr hk, uint ev, IntPtr h, int idObject, int idChild, uint thread, uint time)
        {
            if (h != host || idObject != 0) return;
            if (ev == Native.EVENT_OBJECT_DESTROY) { SetHost(IntPtr.Zero); Update(); return; }
            if (ev == Native.EVENT_OBJECT_LOCATIONCHANGE || ev == Native.EVENT_OBJECT_SHOW || ev == Native.EVENT_OBJECT_HIDE) Update();
        }

        void ShowAt(Native.RECT? pos)
        {
            if (!pos.HasValue) return;
            if (!win.IsVisible) { win.Show(); }
            win.Topmost = Settings.Mode == "desktop" && Settings.Topmost;
            win.MoveTo(pos.Value.Left, pos.Value.Top);
            UpdateFacing();
        }

        int WinW { get { return (int)Math.Round(378 * Settings.Scale * DpiScale); } }

        Native.RECT? HostPosition()
        {
            var f = Native.Frame(host);
            int w = WinW;
            int rr = Settings.RelRight >= 0 ? Settings.RelRight : (int)(18 * DpiScale);
            int rb = Settings.RelBottom >= 0 ? Settings.RelBottom : (int)(18 * DpiScale);
            var p = Clamp(f.Right - rr - w, f.Bottom - rb - w, w, w, f);
            return new Native.RECT { Left = p.X, Top = p.Y, Right = p.X + w, Bottom = p.Y + w };
        }

        Native.RECT? DesktopPosition()
        {
            var work = SystemParameters.WorkArea;
            int w = WinW;
            int right = Settings.DeskRight >= 0 ? Settings.DeskRight : (int)(work.Right * DpiScale) - (int)(12 * DpiScale);
            int bottom = Settings.DeskBottom >= 0 ? Settings.DeskBottom : (int)(work.Bottom * DpiScale);
            var area = new Native.RECT
            {
                Left = (int)(SystemParameters.VirtualScreenLeft * DpiScale), Top = (int)(SystemParameters.VirtualScreenTop * DpiScale),
                Right = (int)((SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) * DpiScale),
                Bottom = (int)((SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) * DpiScale)
            };
            var p = Clamp(right - w, bottom - w, w, w, area);
            return new Native.RECT { Left = p.X, Top = p.Y, Right = p.X + w, Bottom = p.Y + w };
        }

        // Keep the whole widget inside the area so she can't be lost off-screen.
        static System.Drawing.Point Clamp(int x, int y, int w, int h, Native.RECT a)
        {
            x = Math.Max(a.Left, Math.Min(a.Right - w, x));
            y = Math.Max(a.Top, Math.Min(a.Bottom - h, y));
            return new System.Drawing.Point(x, y);
        }

        public System.Drawing.Point ClampToArea(int x, int y, int w, int h)
        {
            if (Settings.Mode == "window" && host != IntPtr.Zero) return Clamp(x, y, w, h, Native.Frame(host));
            var area = new Native.RECT
            {
                Left = (int)(SystemParameters.VirtualScreenLeft * DpiScale), Top = (int)(SystemParameters.VirtualScreenTop * DpiScale),
                Right = (int)((SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) * DpiScale),
                Bottom = (int)((SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) * DpiScale)
            };
            return Clamp(x, y, w, h, area);
        }

        public void RememberPosition()
        {
            var r = win.Rect;
            if (Settings.Mode == "window" && host != IntPtr.Zero)
            {
                var f = Native.Frame(host);
                Settings.RelRight = Math.Max(0, f.Right - r.Right);
                Settings.RelBottom = Math.Max(0, f.Bottom - r.Bottom);
            }
            else { Settings.DeskRight = r.Right; Settings.DeskBottom = r.Bottom; }
            Settings.Save();
        }

        public void UpdateFacing()
        {
            var r = win.Rect;
            int cx = (r.Left + r.Right) / 2;
            Native.RECT a;
            if (Settings.Mode == "window" && host != IntPtr.Zero) a = Native.Frame(host);
            else a = Native.WorkArea(win.Handle);
            win.SetFacing(cx < (a.Left + a.Right) / 2);
        }

        public void SetMode(string mode)
        {
            if (Settings.Mode == mode) return;
            Settings.Mode = mode;
            Settings.Save();
            ApplyMode();
        }

        public void ApplyMode()
        {
            if (Settings.Mode == "desktop") { SetHost(IntPtr.Zero); win.SetOwner(IntPtr.Zero); }
            hiddenForNow = false;
            Update();
        }

        public void Rescale()
        {
            // Keep the bottom-right corner where it is while resizing.
            win.ApplyScale();
            Update();
        }

        public void HideForNow()
        {
            hiddenForNow = true; hiddenHost = host;
            win.Collapse(); win.Hide();
        }

        public void Quit()
        {
            if (tick != null) tick.Stop();
            if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
            win.AllowClose = true;
            win.Close();
            Shutdown();
        }

        // ---- usage refresh: every minute while she is on screen

        void MaybeRefresh()
        {
            if (!win.IsVisible || Refreshing) return;
            double wait = Snapshot != null && Snapshot.Error != null && Snapshot.Error.Contains("頻繁") ? 300 : 60;
            if ((DateTime.Now - lastAttempt).TotalSeconds >= wait) RequestRefresh(true);
        }

        public async void RequestRefresh(bool force)
        {
            if (Refreshing) return;
            if (!force && (DateTime.Now - lastAttempt).TotalSeconds < 60) return;
            Refreshing = true; lastAttempt = DateTime.Now;
            win.Refresh();
            Snapshot next;
            try { next = await UsageClient.Fetch(); }
            catch { next = new Snapshot { Error = "讀取額度時發生錯誤", CheckedAt = DateTime.Now }; }
            // A failed refresh keeps the last numbers and shows the problem under them.
            if (!next.Ok && Snapshot != null && Snapshot.Windows.Count > 0 && next.Error != null && !next.Error.Contains("登入"))
            {
                Snapshot.Error = next.Error;
            }
            else Snapshot = next;
            Refreshing = false;
            win.Refresh();
        }
    }

    // ------------------------------------------------------------------ entry

    public static class Entry
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--exit")
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting("Local\\ClaudeNiang.Exit", out ev)) ev.Set();
                return 0;
            }
            if (args.Length > 0 && args[0] == "--probe") return Probe();
            if (args.Length > 1 && args[0] == "--render") return Render(args[1]);

            bool created;
            var mutex = new Mutex(true, "Local\\ClaudeNiang", out created);
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ClaudeNiang.Show");
            var exit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ClaudeNiang.Exit");
            if (!created) { show.Set(); return 0; } // already running: just bring her back
            var app = new App();
            app.Startup += (s, e) => app.Start(show, exit);
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        // Prints the usage numbers (never the credentials) for troubleshooting.
        static int Probe()
        {
            var snap = UsageClient.Fetch().GetAwaiter().GetResult();
            var sb = new StringBuilder();
            sb.AppendLine("plan: " + (snap.Plan ?? "?"));
            if (snap.Error != null) sb.AppendLine("error: " + snap.Error);
            foreach (var w in snap.Windows)
                sb.AppendLine(string.Format("{0}: used {1}% remaining {2}% resets {3}", w.Label, w.Used, w.Remaining, w.ResetsAt.HasValue ? w.ResetsAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz") : "-"));
            Directory.CreateDirectory(App.StateDir);
            File.WriteAllText(System.IO.Path.Combine(App.StateDir, "probe.txt"), sb.ToString(), new UTF8Encoding(false));
            return snap.Ok ? 0 : 1;
        }

        // Off-screen render of the main states, for checking the layout.
        static int Render(string dir)
        {
            Directory.CreateDirectory(dir);
            var art = App.LoadArt();
            var snap = new Snapshot { Plan = "pro", CheckedAt = DateTime.Now };
            snap.Windows.Add(new QuotaWindow { Label = "5 小時", Used = 37, ResetsAt = DateTimeOffset.Now.AddMinutes(134) });
            snap.Windows.Add(new QuotaWindow { Label = "每週", Used = 88, ResetsAt = DateTimeOffset.Now.AddDays(3).AddHours(5) });
            var cases = new[] { "idle", "quota", "quota-clock", "quote", "quote-long", "error", "left" };
            foreach (var c in cases)
            {
                var v = new NiangView(art);
                if (c != "idle")
                {
                    v.AnimateOpen(false);
                    if (c.StartsWith("quota") || c == "left") { v.ShowQuota(); v.SetSnapshot(snap, c == "quota-clock", false); }
                    if (c == "quote") v.ShowQuote("先跑通，再慢慢打磨。");
                    if (c == "quote-long") v.ShowQuote("這個 bug 我已經完全看懂了，只是 context 太短寫不下。");
                    if (c == "error") { v.ShowQuota(); v.SetSnapshot(new Snapshot { Error = "登入已過期。在終端機執行一次 claude 就會更新" }, false, false); }
                    if (c == "left") v.SetFacingLeft(true);
                }
                v.Measure(new Size(378, 378)); v.Arrange(new Rect(0, 0, 378, 378)); v.UpdateLayout();
                var rtb = new RenderTargetBitmap(756, 756, 192, 192, PixelFormats.Pbgra32);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(NiangView.C("#262A33")), null, new Rect(0, 0, 378, 378));
                rtb.Render(bg);
                rtb.Render(v);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(System.IO.Path.Combine(dir, c + ".png"))) enc.Save(fs);
            }
            return 0;
        }
    }
}
