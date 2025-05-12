using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Toolkit.Uwp.Notifications;
using Timer = System.Windows.Forms.Timer;

namespace NightKnight
{
    /// <summary>
    /// Configuration mapped from Settings.json next to the EXE.
    /// Changes are picked up live – just save the file.
    /// </summary>
    public class Config
    {
        [JsonPropertyName("bedtimes")] public Dictionary<string, string>? Bedtimes { get; set; }
        [JsonPropertyName("warningMinutesBefore")] public int WarningMinutesBefore { get; set; } = 15;
        [JsonPropertyName("toastRepeatMinutes")] public int ToastRepeatMinutes { get; set; } = 5;
        [JsonPropertyName("graceMinutesAfterLock")] public int GraceMinutesAfterLock { get; set; } = 5;
        [JsonPropertyName("statsFile")] public string? StatsFile { get; set; }

        public static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");

        // Cache JsonSerializerOptions instance
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static Config Load()
        {
            if (!File.Exists(ConfigPath))
            {
                // Create default template
                var def = new Config
                {
                    Bedtimes = new Dictionary<string, string>
                    {
                        ["Monday"] = "22:30",
                        ["Tuesday"] = "22:30",
                        ["Wednesday"] = "22:30",
                        ["Thursday"] = "22:30",
                        ["Friday"] = "23:30",
                        ["Saturday"] = "23:30",
                        ["Sunday"] = "21:30"
                    },
                    StatsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NightKnight", "stats.csv")
                };
                Directory.CreateDirectory(Path.GetDirectoryName(def.StatsFile)!);

                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(def, Options));
                return def;
            }

            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Config>(json)!;
        }
    }

    internal static class Program
    {
        // === IMPORTS === //
        [DllImport("user32.dll")] private static extern bool LockWorkStation();
        [DllImport("user32.dll")] private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BouncerContext());
        }

        private class BouncerContext : ApplicationContext
        {
            private NotifyIcon _tray = null!;
            private readonly Timer _timer = null!;
            private FileSystemWatcher _watcher = null!;
            private Config _cfg = null!;

            private bool _cancelTonight;
            private bool _locked;
            private DateTime _lockTime;
            private DateTime _lastToast;
            private string _todayDate = DateTime.Now.ToString("yyyy-MM-dd");

            public BouncerContext()
            {
                LoadConfig();
                InitTray();
                InitWatcher();

                _timer = new Timer { Interval = 60_000 }; // 1‑min tick
                _timer.Tick += (_, _) => Tick();
                _timer.Start();
            }

            private void InitTray()
            {
                _tray = new NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Exclamation,
                    Visible = true,
                    Text = "Night Knight – running"
                };

                var cm = new ContextMenuStrip();
                var cancelItem = new ToolStripMenuItem("Cancel tonight only");
                cancelItem.Click += (_, _) => { _cancelTonight = true; cancelItem.Enabled = false; _tray.Text = "Night Knight – cancelled for tonight"; Log("CancelTonight", DateTime.Now, "user override"); };
                cm.Items.Add(cancelItem);
                cm.Items.Add(new ToolStripMenuItem("Reload config", null, (_, _) => { LoadConfig(); ShowToast("Config reloaded"); }));
                cm.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));
                _tray.ContextMenuStrip = cm;
            }

            private void InitWatcher()
            {
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(Config.ConfigPath)!)
                {
                    Filter = Path.GetFileName(Config.ConfigPath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Changed += (_, _) => { LoadConfig(); ShowToast("Config reloaded"); };
                _watcher.EnableRaisingEvents = true;
            }

            private void LoadConfig()
            {
                _cfg = Config.Load();
                Directory.CreateDirectory(Path.GetDirectoryName(_cfg.StatsFile!)!);
            }

            private void Tick()
            {
                var now = DateTime.Now;

                // At midnight+ we clear daily state and log previous day if needed
                if (_todayDate != now.ToString("yyyy-MM-dd"))
                {
                    _todayDate = now.ToString("yyyy-MM-dd");
                    _cancelTonight = false;
                    _locked = false;
                    _tray.Text = "Night Knight – running";
                }

                if (_cancelTonight) return;

                // Parse bedtime from config
                var bedtimeStr = _cfg.Bedtimes![now.DayOfWeek.ToString()];
                var bedtimeToday = DateTime.Parse($"{now:yyyy-MM-dd} {bedtimeStr}");
                var minutesToBed = (bedtimeToday - now).TotalMinutes;

                // === WARNING TOASTS === //
                if (minutesToBed <= _cfg.WarningMinutesBefore && minutesToBed > 0)
                {
                    if ((now - _lastToast).TotalMinutes >= _cfg.ToastRepeatMinutes)
                    {
                        ShowToast($"Bedtime in {Math.Ceiling(minutesToBed)} minutes.");
                        _lastToast = now;
                    }
                }
                else if (minutesToBed <= 0 && !_locked)
                {
                    ShowToast("Bedtime reached. Locking workstation now.");
                    LockWorkStation();
                    _locked = true;
                    _lockTime = now;
                    Log("Lock", now, "workstation locked");
                }

                // === FORCE LOG‑OFF AFTER GRACE === //
                if (_locked && (now - _lockTime).TotalMinutes >= _cfg.GraceMinutesAfterLock)
                {
                    ShowToast("Logging off. Good night!");
                    Log("Logoff", now, "forced logoff");
                    ExitWindowsEx(0x00000000, 0);
                }
            }

            private void Log(string type, DateTime when, string note)
            {
                var line = $"{when:yyyy-MM-dd},{when:HH:mm},{type},{note}" + Environment.NewLine;
                File.AppendAllText(_cfg.StatsFile!, line);
            }

            private static void ShowToast(string message)
            {
                // Create and show the toast notification
                new ToastContentBuilder()
                    .AddText("Night Knight")
                    .AddText(message)
                    .Show();
            }

            private void ExitApplication()
            {
                _tray.Visible = false;
                _timer.Stop();
                _watcher.EnableRaisingEvents = false;
                _timer.Dispose();
                _watcher.Dispose();
                Application.Exit();
            }
        }
    }
}
