// Renders the coop window and the layout status line with example data only (no network, no real names).
// Compiled by render-doc-screenshots.ps1 via Add-Type (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using LiveSplit.Coop;

public static class DocScreenshots {
    const string ExampleServer = "wss://livesplit-coop-relay.example.workers.dev/coop";
    static WindowsFormsSynchronizationContext uiContext;
    static readonly List<string> uiErrors = new List<string>();

    sealed class DocSession : ICoopSession {
        public string role, server = ExampleServer;
        public LobbyState lobby;
        public bool connected;
        public int viewers = -1;
        public string profile;
        public DocSession(string role) { this.role = role; }
        public string Role { get { return role; } }
        public string Server { get { return server; } set { server = value; } }
        public LobbyState Lobby { get { return lobby; } }
        public bool Connected { get { return connected; } }
        public int Viewers { get { return viewers; } }
        public string StopReason { get { return null; } }
        public string StatusLine {
            get {
                if (lobby == null) return Texts.T("status.notConnected");
                if (!connected) return lobby.name + " · " + Texts.T("status.connecting");
                if (role == "host") return lobby.name + " · " + Texts.T("status.host") + (viewers >= 0 ? " · " + Texts.T("status.online", viewers) : "");
                return lobby.name + " · " + Texts.T("status.following");
            }
        }
        public string SavedPassword { get { return null; } }
        public string JoinProblem() { return null; }
        public string ShareText { get { return lobby == null || role != "host" ? null : CoopWindow.Info(lobby.name, ""); } }
        public bool ShareIsInvite { get { return false; } }
        public string LastLobbyName { get { return null; } }
        public void ReopenLast() { }
        public string LastFailure { get { return null; } }
        public string Note { get { return null; } }
        public string ProfileText { get { return profile; } }
        public string SaveWarning { get { return null; } }
        public string UseLobby(LobbyGrant grant, string password) {
            lobby = LobbyState.From(grant, role, password);
            connected = true; viewers = 2; profile = "Duo / Any%";
            return null;
        }
        public bool JoinInvite(string invite) { return false; }
        public void Reconnect() { }
        public void Leave() { lobby = null; connected = false; }
        public void ExportDiagnostics(IWin32Window owner) { }
    }

    static LobbyGrant FakeCreate(string server, string name, string password, bool isPublic) {
        return new LobbyGrant { server = server, name = name, roomId = LobbyKey.RoomId(name), hostKey = new string('h', 43), viewerKey = new string('v', 43), isPublic = isPublic, locked = password.Length > 0 };
    }
    static List<LobbyInfo> FakeList(string server) {
        return new List<LobbyInfo> {
            new LobbyInfo { name = "team-night", locked = true, hostOnline = true, viewers = 2, label = "Duo / Any%" },
            new LobbyInfo { name = "speedrun-sunday", locked = false, hostOnline = true, viewers = 0, label = "Trio / Any%" },
            new LobbyInfo { name = "practice-room", locked = true, hostOnline = false, viewers = 0, label = "Duo / 100%" }
        };
    }

    static void Pump(int ms) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); }
    }
    static bool Until(Func<bool> ok, int ms) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!ok() && clock.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); }
        return ok();
    }
    static T Find<T>(Form f, string name) where T : Control {
        var found = f.Controls.Find(name, true);
        if (found.Length == 0) throw new Exception("control not found: " + name);
        return (T)found[0];
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    // Visible window rectangle without the invisible resize/shadow border Windows 10/11 adds to Form.Bounds.
    static Rectangle VisibleBounds(Form f) {
        RECT r;
        if (DwmGetWindowAttribute(f.Handle, 9, out r, System.Runtime.InteropServices.Marshal.SizeOf(typeof(RECT))) == 0)
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return f.Bounds;
    }

    // Shows the form top-left on screen and copies its bounds from the screen (DrawToBitmap leaves ListView rows empty).
    static void Capture(Form f, string path) {
        f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.TopMost = true;
        f.Show(); f.Activate();
        Pump(900);
        Cursor.Position = new Point(Screen.PrimaryScreen.Bounds.Width - 5, Screen.PrimaryScreen.Bounds.Height - 5);
        Pump(500);
        var r = VisibleBounds(f);
        using (var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bmp.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("saved " + path + " " + r.Width + "x" + r.Height);
    }
    static void Done(Form f) { f.Close(); f.Dispose(); Pump(200); }

    static void ExpandAdvanced(Form f) {
        var toggle = Find<LinkLabel>(f, "advancedToggle");
        var m = typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.NonPublic | BindingFlags.Instance);
        m.Invoke(toggle, new object[] { new LinkLabelLinkClickedEventArgs(toggle.Links[0]) });
        Pump(300);
    }

    static CoopWindow Open(DocSession session) {
        var f = new CoopWindow(session, FakeCreate, FakeList);
        f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.TopMost = true;
        return f;
    }

    static void Shots(string dir, bool german) {
        Texts.German = german;
        string outDir = Path.Combine(dir, german ? "de" : "en");

        // host window, nothing created yet
        var host = new DocSession("host");
        var f = Open(host);
        f.Show(); Pump(300);
        Find<TextBox>(f, "lobbyName").Text = "team-night";
        Find<CheckBox>(f, "lobbyPublic").Checked = true;
        Capture(f, Path.Combine(outDir, "coop-host-create.png"));
        Done(f);

        // host window with the advanced section open
        host = new DocSession("host");
        f = Open(host);
        f.Show(); Pump(300);
        ExpandAdvanced(f);
        Capture(f, Path.Combine(outDir, "coop-advanced.png"));
        Done(f);

        // connected host: really create the lobby through the window
        host = new DocSession("host");
        f = Open(host);
        f.Show(); Pump(300);
        Find<TextBox>(f, "lobbyName").Text = "team-night";
        Find<CheckBox>(f, "lobbyPublic").Checked = true;
        Find<Button>(f, "create").PerformClick();
        if (!Until(() => host.lobby != null, 5000)) throw new Exception("lobby was not created");
        Pump(500);
        // Creating a lobby copies name/password to the clipboard and reports it; clipboard access needs an STA thread
        // and the confirmation text is not interesting here, so show the plain connected state.
        Find<Label>(f, "message").Text = "";
        Pump(200);
        Capture(f, Path.Combine(outDir, "coop-connected.png"));
        Done(f);

        // viewer with the public list loaded and team-night selected
        var viewer = new DocSession("viewer");
        f = Open(viewer);
        f.Show();
        var list = Find<ListView>(f, "lobbyList");
        if (!Until(() => list.Items.Count == 3, 5000)) throw new Exception("public list did not load");
        list.Items[0].Selected = true; list.Items[0].Focused = true;
        Pump(300);
        Capture(f, Path.Combine(outDir, "coop-viewer-join.png"));
        Done(f);

        StatusLine(Path.Combine(outDir, "status-line.png"));
    }

    // The real CoopComponent needs a LiveSplitState and a live relay connection to reach the connected state, so this
    // draws what CoopComponent.Draw draws (same text, colours, font, offsets) on LiveSplit's dark background.
    static void StatusLine(string path) {
        string left = "Coop: team-night · " + Texts.T("status.host") + " · " + Texts.T("status.online", 2);
        string right = "Duo / Any%";
        const int width = 340, height = 22;
        using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("Segoe UI", 9F))
        using (var brush = new SolidBrush(Color.MediumAquamarine))
        using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) {
            g.Clear(Color.FromArgb(21, 21, 21));
            float badge = Math.Min(width / 2f, g.MeasureString(right, font).Width + 12);
            g.DrawString(left, font, brush, new RectangleF(4, 2, Math.Max(0, width - badge - 8), height - 2), format);
            format.Alignment = StringAlignment.Far;
            brush.Color = Color.FromArgb(88, 214, 207);
            g.DrawString(right, font, brush, new RectangleF(width - badge, 2, badge - 4, height - 2), format);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bmp.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("saved " + path + " " + width + "x" + height);
    }

    public static void Run(string imagesDir) {
        // Never show .NET error dialogs; fail the run instead. Give up after two minutes.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.ThreadException += (o, a) => { lock (uiErrors) uiErrors.Add(a.Exception.ToString()); };
        var watchdog = new System.Threading.Timer(_ => { Console.Error.WriteLine("TIMEOUT after 2 minutes"); Environment.Exit(2); }, null, 120000, Timeout.Infinite);
        if (uiContext == null) uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        try {
            Shots(imagesDir, false);
            Shots(imagesDir, true);
            lock (uiErrors) if (uiErrors.Count > 0) throw new Exception("UI thread exception: " + uiErrors[0]);
        } finally { Texts.German = false; watchdog.Dispose(); }
    }
}
