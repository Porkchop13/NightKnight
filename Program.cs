// NightKnight – Tray utility with dynamic JSON config, daily stats log, reliable toasts & fullscreen popup fallback
// Build with: dotnet new winforms -n NightKnight -f net9.0-windows10.0.17763.0
// Add NuGet: dotnet add package Microsoft.Toolkit.Uwp.Notifications
// Add COM/NuGet ref: IWshRuntimeLibrary (for shortcut creator, if desired)
// Replace Program.cs with this file, then:
// dotnet publish -c Release -p:PublishSingleFile=true -p:SelfContained=true -r win-x64

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;
using Timer = System.Windows.Forms.Timer;

namespace NightKnight
{
    // Native P/Invoke methods
    internal static partial class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        internal static extern bool ExitWindowsEx(uint uFlags, uint dwReason);
    }

    // Configuration loaded from Settings.json (auto-generated on first run).
    public class Config
    {
        [JsonPropertyName("bedtimes")]
        public Dictionary<string, string>? Bedtimes { get; set; }

        [JsonPropertyName("warningMinutesBefore")]
        public int WarningMinutesBefore { get; set; } = 15;

        [JsonPropertyName("toastRepeatMinutes")]
        public int ToastRepeatMinutes { get; set; } = 5;

        [JsonPropertyName("graceMinutesAfterLock")]
        public int GraceMinutesAfterLock { get; set; } = 5;

        [JsonPropertyName("statsFile")]
        public string? StatsFile { get; set; }

        public static string ConfigPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static Config Load()
        {
            if (!File.Exists(ConfigPath))
            {
                var def = new Config
                {
                    Bedtimes = new Dictionary<string, string>
                    {
                        ["Monday"] = "22:15",
                        ["Tuesday"] = "22:15",
                        ["Wednesday"] = "22:15",
                        ["Thursday"] = "22:15",
                        ["Friday"] = "23:15",
                        ["Saturday"] = "23:15",
                        ["Sunday"] = "22:15"
                    },
                    StatsFile = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "NightKnight",
                        "stats.csv"
                    )
                };
                Directory.CreateDirectory(Path.GetDirectoryName(def.StatsFile!)!);
                File.WriteAllText(
                    ConfigPath,
                    JsonSerializer.Serialize(def, Options)
                );
                return def;
            }

            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Config>(json)!;
        }
    }

    internal static class Program
    {
        // Windows AppUserModelID for toasts
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

        [STAThread]
        private static void Main()
        {
            // Ensure Windows toasts will appear as popups
            _ = SetCurrentProcessExplicitAppUserModelID("com.yourcompany.NightKnight");

            // (Optional) auto-create Start Menu shortcut for proper toast support
            TryCreateShortcut();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BouncerContext());
        }

        private static void TryCreateShortcut()
        {
            try
            {
                string shortcutName = "NightKnight";
                string appPath = Assembly.GetExecutingAssembly().Location;
                string startMenuDir =
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                        "Programs",
                        shortcutName
                    );
                string linkPath = Path.Combine(startMenuDir, $"{shortcutName}.lnk");

                if (!File.Exists(linkPath))
                {
                    Directory.CreateDirectory(startMenuDir);

                    // Create COM-based shortcut
                    Type? wscriptShellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (wscriptShellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(wscriptShellType)!;
                        dynamic shortcut = shell.CreateShortcut(linkPath);
                        shortcut.TargetPath = appPath;
                        shortcut.WorkingDirectory = Path.GetDirectoryName(appPath);
                        shortcut.IconLocation = appPath;
                        shortcut.Description = "NightKnight – your bedtime enforcer";
                        shortcut.Save();
                    }
                }
            }
            catch
            {
                // If shortcut creation fails, silently continue
            }
        }
    }

    internal class BouncerContext : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly Timer _timer;
        private readonly FileSystemWatcher _watcher;
        private Config _cfg;

        private bool _cancelTonight;
        private bool _locked;
        private DateTime _lockTime;
        private DateTime _lastToast;
        private string _todayDate = DateTime.Now.ToString("yyyy-MM-dd");

        public BouncerContext()
        {
            _cfg = Config.Load();

            // Tray setup
            _tray = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Exclamation,
                Visible = true,
                Text = "NightKnight – running"
            };
            var cm = new ContextMenuStrip();
            var cancelItem = new ToolStripMenuItem("Cancel tonight only");
            cancelItem.Click += (_, _) =>
            {
                _cancelTonight = true;
                cancelItem.Enabled = false;
                _tray.Text = "NightKnight – cancelled for tonight";
                Log("CancelTonight", DateTime.Now, "user override");
            };
            cm.Items.Add(cancelItem);
            cm.Items.Add(new ToolStripMenuItem("Reload config", null, (_, _) =>
            {
                _cfg = Config.Load();
                ShowToast("Config reloaded");
            }));
            cm.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));
            _tray.ContextMenuStrip = cm;

            // Watch for JSON config edits
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(Config.ConfigPath)!)
            {
                Filter = Path.GetFileName(Config.ConfigPath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += (_, _) =>
            {
                _cfg = Config.Load();
                ShowToast("Config reloaded");
            };
            _watcher.EnableRaisingEvents = true;

            // 1-min tick timer
            _timer = new Timer { Interval = 60_000 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private void Tick()
        {
            var now = DateTime.Now;

            // Daily reset at midnight
            if (_todayDate != now.ToString("yyyy-MM-dd"))
            {
                _todayDate = now.ToString("yyyy-MM-dd");
                _cancelTonight = false;
                _locked = false;
                _tray.Text = "NightKnight – running";
            }

            if (_cancelTonight) return;

            // Compute minutes to bedtime
            string bedtimeStr = _cfg.Bedtimes![now.DayOfWeek.ToString()];
            DateTime bedtimeToday = DateTime.Parse($"{now:yyyy-MM-dd} {bedtimeStr}");
            double minutesToBed = (bedtimeToday - now).TotalMinutes;

            // Warning toasts
            if (minutesToBed <= _cfg.WarningMinutesBefore && minutesToBed > 0)
            {
                if ((now - _lastToast).TotalMinutes >= _cfg.ToastRepeatMinutes)
                {
                    ShowToast($"Bedtime in {Math.Ceiling(minutesToBed)} minutes.");
                    _lastToast = now;
                }
            }
            // Lock at bedtime
            else if (minutesToBed <= 0 && !_locked)
            {
                ShowToast("Bedtime reached. Locking workstation now.");
                NativeMethods.LockWorkStation();
                _locked = true;
                _lockTime = now;
                Log("Lock", now, "workstation locked");
            }

            // Force log-off after grace period
            if (_locked && (now - _lockTime).TotalMinutes >= _cfg.GraceMinutesAfterLock)
            {
                ShowToast("Logging off. Good night!");
                Log("Logoff", now, "forced logoff");
                NativeMethods.ExitWindowsEx(0x00000000, 0);
            }
        }

        private void Log(string type, DateTime when, string note)
        {
            var line = $"{when:yyyy-MM-dd},{when:HH:mm},{type},{note}{Environment.NewLine}";
            File.AppendAllText(_cfg.StatsFile!, line);
        }

        private static void ShowToast(string message)
        {
            // Native Windows toast
            var content = new ToastContentBuilder()
                .AddText("NightKnight")
                .AddText(message)
                .GetToastContent();

            var toast = new ToastNotification(content.GetXml());
            ToastNotificationManager
                .CreateToastNotifier("com.yourcompany.NightKnight")
                .Show(toast);

            // Fallback popup for fullscreen apps
            var thread = new Thread(() =>
            {
                var form = new Form
                {
                    TopMost = true,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.CenterScreen,
                    Size = new Size(500, 120),
                    BackColor = Color.Black,
                    Opacity = 0.9,
                    ShowInTaskbar = false
                };
                var label = new Label
                {
                    Text = message,
                    Dock = DockStyle.Fill,
                    ForeColor = Color.White,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 14, FontStyle.Bold)
                };
                form.Controls.Add(label);
                var closeTimer = new Timer { Interval = 4000, Enabled = true };
                closeTimer.Tick += (_, __) =>
                {
                    closeTimer.Stop();
                    form.Close();
                };
                form.Shown += (_, __) => closeTimer.Start();
                Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
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
