using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Offbeat;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "Offbeat.SingleInstance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Offbeat.Show");
        if (!first) { showSignal.Set(); return; } // bring the running window forward instead
        ApplicationConfiguration.Initialize();
        var form = new MainForm(args.Contains("--tray"));
        new Thread(() =>
        {
            while (showSignal.WaitOne())
                try { form.BeginInvoke(form.ShowMain); } catch { }
        }) { IsBackground = true }.Start();
        Application.Run(form);
    }
}

sealed class Settings
{
    public string? Folder { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public string FilePath { get; set; } = "";

    public static Settings Load(string path)
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new(); } catch { s = new(); }
        s.FilePath = path;
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); } catch { }
    }
}

sealed class MainForm : Form
{
    const string Origin = "https://offbeat.app.local";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly HashSet<string> AudioExt = [".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".oga", ".opus", ".weba", ".webm"];

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Offbeat");
    readonly Settings settings;
    readonly NotifyIcon tray = new();
    readonly ToolStripMenuItem nowItem = new("Nothing playing") { Enabled = false };
    readonly ToolStripMenuItem playItem = new("Play");
    readonly System.Windows.Forms.Timer rescanTimer = new() { Interval = 3000 };
    FileSystemWatcher? watcher;
    CoreWebView2? core;
    bool quitting, startHidden, mini, fullscreen;
    Rectangle savedBounds;
    FormWindowState savedState;
    FormBorderStyle fsBorder;
    FormWindowState fsState;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainForm(bool hidden)
    {
        Directory.CreateDirectory(root);
        settings = Settings.Load(Path.Combine(root, "settings.json"));
        startHidden = hidden;
        Text = "Offbeat";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = Color.Black;
        MinimumSize = new Size(760, 560);
        Size = new Size(1320, 840);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(web);
        if (hidden) Opacity = 0;
        BuildTray();
        rescanTimer.Tick += (_, _) => { rescanTimer.Stop(); SendFiles(); };
        Load += async (_, _) => await StartAsync();
        Shown += (_, _) => { if (startHidden) { Hide(); Opacity = 1; } };
        FormClosing += OnClosing;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar();
    }

    void DarkTitleBar()
    {
        int dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    // ---------- tray ----------
    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        playItem.Click += (_, _) => Cmd("toggle");
        menu.Items.Add(nowItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(playItem);
        menu.Items.Add("Next", null, (_, _) => Cmd("next"));
        menu.Items.Add("Previous", null, (_, _) => Cmd("prev"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show Offbeat", null, (_, _) => ShowMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        tray.ContextMenuStrip = menu;
        tray.Icon = Icon;
        tray.Text = "Offbeat";
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
    }

    public void ShowMain()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void Quit()
    {
        quitting = true;
        tray.Visible = false;
        Close();
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!quitting && settings.CloseToTray && e.CloseReason is CloseReason.UserClosing or CloseReason.None)
        {
            e.Cancel = true;
            Hide();
            if (!settings.TrayHintShown)
            {
                tray.ShowBalloonTip(4000, "Offbeat is still playing", "It's in the tray (bottom-right). Right-click the icon to quit.", ToolTipIcon.None);
                settings.TrayHintShown = true;
                settings.Save();
            }
            return;
        }
        tray.Visible = false;
    }

    void Cmd(string c) => Post(new { type = "cmd", cmd = c });

    void Post(object o) => core?.PostWebMessageAsJson(JsonSerializer.Serialize(o));

    // ---------- web view ----------
    async Task StartAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "data"));
        await web.EnsureCoreWebView2Async(env);
        core = web.CoreWebView2;

        var s = core.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;

        // Safety: the app can only ever show its own page, served from inside the .exe.
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRequest;
        core.NavigationStarting += (_, a) =>
        {
            if (!a.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) a.Cancel = true;
        };
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.PermissionRequested += (_, a) => a.State = CoreWebView2PermissionState.Deny;
        core.DocumentTitleChanged += (_, _) => Text = core.DocumentTitle;
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(core.ContainsFullScreenElement);
        core.WebMessageReceived += OnMessage;

        core.Navigate(Origin + "/index.html");
        StartWatcher();
    }

    void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs a)
    {
        var uri = new Uri(a.Request.Uri);
        var env = core!.Environment;
        if (uri.AbsolutePath is "/" or "/index.html")
        {
            var ms = new MemoryStream();
            using (var r = typeof(MainForm).Assembly.GetManifestResourceStream("index.html")!) r.CopyTo(ms);
            ms.Position = 0;
            a.Response = env.CreateWebResourceResponse(ms, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            return;
        }
        if (uri.AbsolutePath == "/__file" && uri.Query.StartsWith("?p="))
        {
            var path = MusicPath(Uri.UnescapeDataString(uri.Query[3..]));
            if (path != null)
            {
                try
                {
                    var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    a.Response = env.CreateWebResourceResponse(fs, 200, "OK", $"Content-Type: {Mime(path)}\r\nCache-Control: no-store");
                    return;
                }
                catch { }
            }
        }
        a.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    // Only music and lyrics files inside the chosen music folder can be read.
    string? MusicPath(string p)
    {
        if (settings.Folder == null) return null;
        try
        {
            var full = Path.GetFullPath(p);
            var dir = Path.GetFullPath(settings.Folder).TrimEnd('\\') + "\\";
            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return null;
            if (!AudioExt.Contains(ext) && ext != ".lrc") return null;
            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".aac" => "audio/mp4",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".weba" or ".webm" => "audio/webm",
        ".lrc" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs a)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(a.WebMessageAsJson); } catch { return; }
        if (m == null) return;
        bool on = m["on"]?.GetValue<bool>() == true;
        switch (m["type"]?.GetValue<string>())
        {
            case "ready":
                SendState();
                SendFiles();
                break;
            case "pickFolder":
                PickFolder();
                break;
            case "scan":
                SendFiles();
                break;
            case "unwatch":
                settings.Folder = null;
                settings.Save();
                StartWatcher();
                SendState();
                break;
            case "setStartup":
                SetStartup(on);
                SendState();
                break;
            case "setTray":
                settings.CloseToTray = on;
                settings.Save();
                SendState();
                break;
            case "mini":
                SetMini(on);
                break;
            case "now":
                var title = m["title"]?.GetValue<string>() ?? "";
                var artist = m["artist"]?.GetValue<string>() ?? "";
                var playing = m["playing"]?.GetValue<bool>() == true;
                nowItem.Text = Clip($"{title} · {artist}", 60);
                playItem.Text = playing ? "Pause" : "Play";
                tray.Text = Clip($"Offbeat — {title}", 120);
                break;
        }
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    void SendState() => Post(new { type = "state", folder = settings.Folder, startup = IsStartup(), tray = settings.CloseToTray });

    // ---------- music folder ----------
    void PickFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose your music folder",
            UseDescriptionForTitle = true,
            SelectedPath = settings.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        settings.Folder = dlg.SelectedPath;
        settings.Save();
        StartWatcher();
        SendState();
        SendFiles();
    }

    void SendFiles()
    {
        var folder = settings.Folder;
        if (folder == null || !Directory.Exists(folder)) return; // e.g. a USB drive that's unplugged: keep the library as is
        Task.Run(() =>
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var list = new List<object>();
            foreach (var f in Directory.EnumerateFiles(folder, "*", opts))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (!AudioExt.Contains(ext) && ext != ".lrc") continue;
                try { list.Add(new { p = f, s = new FileInfo(f).Length }); } catch { }
            }
            return list;
        }).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && settings.Folder == folder) Post(new { type = "files", files = t.Result });
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void StartWatcher()
    {
        watcher?.Dispose();
        watcher = null;
        if (settings.Folder == null || !Directory.Exists(settings.Folder)) return;
        watcher = new FileSystemWatcher(settings.Folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            SynchronizingObject = this
        };
        void Changed(object? s, FileSystemEventArgs e) { rescanTimer.Stop(); rescanTimer.Start(); }
        watcher.Created += Changed;
        watcher.Deleted += Changed;
        watcher.Changed += Changed;
        watcher.Renamed += Changed;
        watcher.EnableRaisingEvents = true;
    }

    // ---------- start with Windows ----------
    static bool IsStartup()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("Offbeat") != null;
    }

    static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("Offbeat", $"\"{Application.ExecutablePath}\" --tray");
        else k.DeleteValue("Offbeat", false);
    }

    // ---------- mini player & full screen ----------
    void SetMini(bool on)
    {
        if (on == mini || fullscreen) return;
        mini = on;
        if (on)
        {
            savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            savedState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimumSize = new Size(330, 190);
            TopMost = true;
            var wa = Screen.FromControl(this).WorkingArea;
            Bounds = new Rectangle(wa.Right - 440, wa.Bottom - 250, 420, 230);
        }
        else
        {
            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(760, 560);
            Bounds = savedBounds;
            WindowState = savedState;
        }
        DarkTitleBar();
        ShowMain();
    }

    void SetFullscreen(bool on)
    {
        if (on == fullscreen) return;
        fullscreen = on;
        if (on)
        {
            fsBorder = FormBorderStyle;
            fsState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = fsBorder;
            WindowState = fsState;
            DarkTitleBar();
        }
    }
}
