using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);
}

internal static class DrawingExtensions
{
    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(RectangleF bounds, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath(); var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using (var path = RoundedPath(bounds, radius)) graphics.FillPath(brush, path);
    }
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        using (var path = RoundedPath(bounds, radius)) graphics.DrawPath(pen, path);
    }
}

internal sealed class QuotaWindow
{
    public string Label; public double Used; public double Remaining; public long? ResetsAt;
}

internal sealed class Snapshot
{
    public List<QuotaWindow> Windows = new List<QuotaWindow>();
    public string Plan, Reached, Source, Error; public DateTime Observed;
}

internal sealed class TrayApp : ApplicationContext
{
    private const string StartupValue = "CodexQuotaTray";
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly Timer timer = new Timer { Interval = 15000 };
    private readonly Timer postResetTimer = new Timer { Interval = 5000 };
    private Icon currentIcon;
    private long? displayedResetAt;
    private bool postResetRefreshQueued;

    public TrayApp()
    {
        timer.Tick += delegate { Refresh(); };
        postResetTimer.Tick += delegate { postResetTimer.Stop(); Refresh(); };
        // Windows registers a NotifyIcon more reliably when it has an icon before
        // it is made visible.  Register a known-good fallback first; Refresh()
        // immediately replaces it with the quota indicator.
        tray.Icon = SystemIcons.Application;
        tray.Text = "Codex Quota Tray";
        tray.Visible = true;
        Refresh();
        timer.Start();
    }

    private static string Label(int minutes)
    {
        if (minutes == 300) return "5-hour usage";
        if (minutes == 1440) return "1-day usage";
        if (minutes == 10080) return "7-day usage";
        if (minutes % 1440 == 0) return (minutes / 1440) + "-day usage";
        if (minutes % 60 == 0) return (minutes / 60) + "-hour usage";
        return minutes + "-minute usage";
    }

    private static string Relative(long? unix)
    {
        if (!unix.HasValue) return "--";
        var seconds = (long)(DateTimeOffset.FromUnixTimeSeconds(unix.Value).LocalDateTime - DateTime.Now).TotalSeconds;
        if (seconds <= 0) return "now";
        var days = seconds / 86400; var hours = (seconds % 86400) / 3600; var minutes = (seconds % 3600) / 60;
        if (days > 0) return days + "d" + hours + "h";
        if (hours > 0) return hours + "h" + minutes + "m";
        return Math.Max(1, minutes) + "m";
    }

    private static string ShortRelative(long? unix)
    {
        if (!unix.HasValue) return "--";
        var seconds = (long)(DateTimeOffset.FromUnixTimeSeconds(unix.Value).LocalDateTime - DateTime.Now).TotalSeconds;
        if (seconds <= 0) return "now";
        var days = seconds / 86400;
        if (days > 0) return days + "d";
        return Math.Max(1, seconds / 3600) + "h";
    }

    private static object Get(Dictionary<string, object> value, string key)
    {
        object result; return value != null && value.TryGetValue(key, out result) ? result : null;
    }

    private static double? Number(object value)
    {
        double result; return value != null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out result) ? (double?)result : null;
    }

    private static Snapshot Load()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var files = new List<FileInfo>();
        foreach (var root in new[] { Path.Combine(home, ".codex", "sessions"), Path.Combine(home, ".codex", "archived_sessions") })
            if (Directory.Exists(root)) files.AddRange(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Select(x => new FileInfo(x)));
        if (files.Count == 0) return new Snapshot { Error = "No Codex session logs found." };

        Snapshot best = null; var serializer = new JavaScriptSerializer();
        foreach (var file in files.OrderByDescending(x => x.LastWriteTime).Take(120))
        {
            string text;
            try
            {
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(Math.Max(0, stream.Length - 1048576), SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
                }
            }
            catch { continue; }
            foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                if (!line.Contains("\"token_count\"") || !line.Contains("\"rate_limits\"")) continue;
                try
                {
                    var record = serializer.Deserialize<Dictionary<string, object>>(line);
                    var payload = Get(record, "payload") as Dictionary<string, object>;
                    if (payload == null || Convert.ToString(Get(payload, "type")) != "token_count") continue;
                    var limits = Get(payload, "rate_limits") as Dictionary<string, object>;
                    if (limits == null) continue;
                    var snapshot = new Snapshot { Source = file.FullName, Plan = Convert.ToString(Get(limits, "plan_type")), Reached = Convert.ToString(Get(limits, "rate_limit_reached_type")) };
                    DateTime observed; snapshot.Observed = DateTime.TryParse(Convert.ToString(Get(record, "timestamp")), null, DateTimeStyles.RoundtripKind, out observed) ? observed.ToLocalTime() : DateTime.MinValue;
                    foreach (var pair in limits)
                    {
                        var data = pair.Value as Dictionary<string, object>; if (data == null) continue;
                        var used = Number(Get(data, "used_percent")); var minutes = Number(Get(data, "window_minutes"));
                        if (!used.HasValue || !minutes.HasValue || minutes.Value <= 0) continue;
                        var reset = Number(Get(data, "resets_at"));
                        snapshot.Windows.Add(new QuotaWindow { Label = Label((int)minutes.Value), Used = used.Value, Remaining = Math.Max(0, Math.Min(100, 100 - used.Value)), ResetsAt = reset.HasValue ? (long?)reset.Value : null });
                    }
                    snapshot.Windows = snapshot.Windows.OrderBy(x => x.Label.Contains("5-hour") ? 0 : x.Label.Contains("day") ? 1 : 2).ToList();
                    if (snapshot.Windows.Count > 0 || !String.IsNullOrEmpty(snapshot.Reached))
                    {
                        if (best == null || snapshot.Observed > best.Observed) best = snapshot;
                        break;
                    }
                }
                catch { }
            }
        }
        return best ?? new Snapshot { Error = "No rate limit event found in recent Codex logs." };
    }

    private static Icon DrawIcon(Snapshot snapshot)
    {
        var bitmap = new Bitmap(32, 32); using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var shown = snapshot.Windows.Take(2).ToList();
            using (var outerBack = new Pen(Color.FromArgb(75, 90, 100), 5))
            using (var innerBack = new Pen(Color.FromArgb(75, 90, 100), 5))
            {
                g.DrawArc(outerBack, 2, 2, 28, 28, -90, 359);
                if (shown.Count > 1) g.DrawArc(innerBack, 7, 7, 18, 18, -90, 359);
                for (var i = 0; i < shown.Count; i++)
                {
                    var p = shown[i].Remaining;
                    var color = p >= 40 ? Color.FromArgb(38, 189, 104) : p >= 20 ? Color.FromArgb(245, 180, 35) : Color.FromArgb(239, 68, 68);
                    using (var pen = new Pen(color, 5))
                    {
                        if (i == 0) g.DrawArc(pen, 2, 2, 28, 28, -90, (float)(p * 3.59));
                        else g.DrawArc(pen, 7, 7, 18, 18, -90, (float)(p * 3.59));
                    }
                }
            }
            var resetText = ShortRelative(shown.Count > 1 ? shown[1].ResetsAt : shown.Count == 1 ? shown[0].ResetsAt : null);
            using (var font = new Font("Segoe UI", 13.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.FromArgb(135, 140, 145)))
            {
                var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(resetText, font, brush, new RectangleF(3, 5, 26, 22), format);
            }
        }
        var handle = bitmap.GetHicon();
        try
        {
            // Clone the icon before releasing the native handle.  NotifyIcon needs
            // an owned icon handle; a borrowed Bitmap handle is ignored by some
            // Windows 11 notification-area hosts.
            using (var borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
            bitmap.Dispose();
        }
    }

    private void Refresh()
    {
        var snapshot = Load(); var menu = new ContextMenuStrip();
        QueuePostResetRefresh(snapshot);
        AddDisabled(menu, "Codex Quota Tray");
        if (!String.IsNullOrEmpty(snapshot.Error)) { AddDisabled(menu, snapshot.Error); tray.Text = "Codex quota: unavailable"; }
        else
        {
            if (!String.IsNullOrEmpty(snapshot.Plan)) AddDisabled(menu, "Plan: " + snapshot.Plan);
            if (!String.IsNullOrEmpty(snapshot.Reached)) AddDisabled(menu, "Limit reached: " + snapshot.Reached);
            menu.Items.Add(new ToolStripSeparator()); var tooltip = new List<string>();
            foreach (var window in snapshot.Windows)
            {
                AddDisabled(menu, String.Format("{0}: {1:0}% used / {2:0}% left; resets in {3}", window.Label, window.Used, window.Remaining, Relative(window.ResetsAt)));
                tooltip.Add(window.Label + " " + Math.Round(window.Remaining) + "% left");
            }
            menu.Items.Add(new ToolStripSeparator()); AddDisabled(menu, "Updated: " + snapshot.Observed.ToString("g")); AddDisabled(menu, "Source: " + Path.GetFileName(snapshot.Source));
            tray.Text = String.Join(" / ", tooltip).Substring(0, Math.Min(63, String.Join(" / ", tooltip).Length));
        }
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Refresh now", null, delegate { Refresh(); });
        menu.Items.Add("Open session logs", null, delegate { System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions")); });
        var login = (ToolStripMenuItem)menu.Items.Add("Start at login", null, delegate { SetStartup(!IsStartup()); Refresh(); }); login.Checked = IsStartup();
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Quit", null, delegate { tray.Visible = false; ExitThread(); });
        tray.ContextMenuStrip = menu; var next = DrawIcon(snapshot); tray.Icon = next; if (currentIcon != null) currentIcon.Dispose(); currentIcon = next;
    }

    // A reset is reflected only when Codex writes a new local rate-limit record.
    // The current scan is the first refresh; schedule one more scan five seconds later.
    private void QueuePostResetRefresh(Snapshot snapshot)
    {
        var shown = snapshot.Windows.Take(2).ToList();
        long? resetAt = shown.Count > 1 ? shown[1].ResetsAt : shown.Count == 1 ? shown[0].ResetsAt : null;
        if (!resetAt.HasValue) return;
        if (displayedResetAt != resetAt)
        {
            displayedResetAt = resetAt;
            postResetRefreshQueued = false;
        }
        if (!postResetRefreshQueued && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= resetAt.Value)
        {
            postResetRefreshQueued = true;
            postResetTimer.Stop();
            postResetTimer.Start();
        }
    }

    private static void AddDisabled(ContextMenuStrip menu, string text) { menu.Items.Add(text).Enabled = false; }
    private static bool IsStartup() { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return key != null && key.GetValue(StartupValue) != null; }
    private static void SetStartup(bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            if (enabled) key.SetValue(StartupValue, "\"" + Application.ExecutablePath + "\""); else key.DeleteValue(StartupValue, false);
    }
}

internal static class Program
{
    [STAThread] private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
