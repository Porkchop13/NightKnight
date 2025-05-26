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
using System.Diagnostics;
using Windows.Data.Xml.Dom;

namespace NightKnight
{
    // Native P/Invoke methods
    internal static partial class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        internal static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;        // x position of upper-left corner
            public int Top;         // y position of upper-left corner
            public int Right;       // x position of lower-right corner
            public int Bottom;      // y position of lower-right corner
        }

        internal const int SW_SHOW = 5;
        internal const int SW_SHOWNORMAL = 1;
    }

    // Configuration loaded from Settings.json (auto-generated on first run).
    public class Config
    {
        [JsonPropertyName("bedtimes")]
        public Dictionary<string, string>? Bedtimes { get; set; }

        [JsonPropertyName("disableFocusStealing")]
        public bool DisableFocusStealing { get; set; } = false;

        [JsonPropertyName("enableAutoLogoff")]
        public bool EnableAutoLogoff { get; set; } = false;

        [JsonPropertyName("graceMinutesAfterLock")]
        public int GraceMinutesAfterLock { get; set; } = 5;

        [JsonPropertyName("toastRepeatMinutes")]
        public int ToastRepeatMinutes { get; set; } = 5;

        [JsonPropertyName("statsFile")]
        public string? StatsFile { get; set; }

        [JsonPropertyName("warningMinutesBefore")]
        public int WarningMinutesBefore { get; set; } = 15;

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
            _ = SetCurrentProcessExplicitAppUserModelID("com.Porkchop13.NightKnight");

            // (Optional) auto-create Start Menu shortcut for proper toast support
            TryCreateShortcut();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ToolbarContext());
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

    internal class ToolbarContext : ApplicationContext
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

        public ToolbarContext()
        {
            _cfg = Config.Load();

            // Tray setup
            _tray = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location) ?? SystemIcons.Shield,
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
            cm.Items.Add(new ToolStripMenuItem("Open config file", null, (_, _) =>
            {
                try
                {
                    // Create the config file if it doesn't exist
                    if (!File.Exists(Config.ConfigPath))
                    {
                        _cfg = Config.Load();
                    }

                    // Open with notepad explicitly
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "notepad.exe",
                        Arguments = Config.ConfigPath,
                        UseShellExecute = true
                    };

                    var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        ShowToast("Failed to start Notepad");
                        Debug.WriteLine("Failed to start Notepad");
                    }
                }
                catch (Exception ex)
                {
                    ShowToast($"Error opening config: {ex.Message}");
                    Debug.WriteLine($"Error opening config: {ex.Message}");
                }
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

            // Force log-off after grace period if enabled
            if (_cfg.EnableAutoLogoff && _locked && (now - _lockTime).TotalMinutes >= _cfg.GraceMinutesAfterLock)
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

        private static bool IsFullScreenApplicationRunning()
        {
            // Get foreground window
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            // Get window rectangle
            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
                return false;

            // Check if it covers the entire screen
            // First ensure the primary screen is available
            if (Screen.PrimaryScreen == null)
                return false;

            var screenWidth = Screen.PrimaryScreen.Bounds.Width;
            var screenHeight = Screen.PrimaryScreen.Bounds.Height;
            var windowWidth = rect.Right - rect.Left;
            var windowHeight = rect.Bottom - rect.Top;

            // If the window covers at least 95% of the screen, consider it full-screen
            return (windowWidth >= screenWidth * 0.95) && (windowHeight >= screenHeight * 0.95);
        }

        private void ShowFallbackNotification(string message, bool enableFocusStealing)
        {
            var fallbackThread = new Thread(() =>
            {
                Form? fallbackForm = null;
                Timer? closeTimer = null;
                try
                {
                    fallbackForm = new Form
                    {
                        TopMost = true,
                        FormBorderStyle = FormBorderStyle.None,
                        StartPosition = FormStartPosition.CenterScreen,
                        Size = new Size(500, 120),
                        BackColor = Color.Black,
                        Opacity = 0.9,
                        ShowInTaskbar = true // Show in taskbar to aid in focus stealing
                    };
                    var label = new Label
                    {
                        Text = message,
                        Dock = DockStyle.Fill,
                        ForeColor = Color.White,
                        TextAlign = ContentAlignment.MiddleCenter,
                        Font = new Font("Segoe UI", 14, FontStyle.Bold)
                    };
                    fallbackForm.Controls.Add(label);

                    closeTimer = new Timer { Interval = 4000 };
                    closeTimer.Tick += (_, __) =>
                    {
                        if (fallbackForm != null && !fallbackForm.IsDisposed)
                        {
                            fallbackForm.Close();
                        }
                    };
                    fallbackForm.Shown += (_, __) =>
                    {
                        closeTimer?.Start();

                        // Force focus to the form if enabled in configuration
                        if (enableFocusStealing)
                        {
                            NativeMethods.ShowWindow(fallbackForm.Handle, NativeMethods.SW_SHOWNORMAL);
                            NativeMethods.BringWindowToTop(fallbackForm.Handle);
                            NativeMethods.SetForegroundWindow(fallbackForm.Handle);

                            // Ensure form is active
                            fallbackForm.Activate();
                            fallbackForm.Focus();
                        }
                    };
                    Application.Run(fallbackForm);
                }
                finally
                {
                    closeTimer?.Stop();
                    closeTimer?.Dispose();
                    fallbackForm?.Dispose();
                }
            });
            fallbackThread.SetApartmentState(ApartmentState.STA);
            fallbackThread.IsBackground = true;
            fallbackThread.Start();
        }

        private void ShowToast(string message)
        {
            try
            {
                // Capture config value for use in the thread
                bool enableFocusStealing = !_cfg.DisableFocusStealing;

                // Check if full-screen app is running and go straight to fallback if so
                if (IsFullScreenApplicationRunning())
                {
                    Debug.WriteLine("Full-screen application detected. Using fallback notification.");
                    ShowFallbackNotification(message, enableFocusStealing);
                    return;
                }

                // Create toast content
                var toastContent = new ToastContentBuilder()
                    .AddText("NightKnight Reminder")
                    .AddText(message)
                    .GetToastContent();

                // Convert to ToastNotification
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(toastContent.GetContent());
                var toast = new ToastNotification(xmlDoc);

                // Register for the Failed event to show fallback notification
                toast.Failed += (sender, args) =>
                {
                    Debug.WriteLine($"Native toast failed. ErrorCode: {args.ErrorCode}");
                    ShowFallbackNotification(message, enableFocusStealing);
                };

                // Show the toast notification
                ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception attempting to show native toast: {ex}");
                // If toast setup fails completely, show fallback notification
                try
                {
                    ShowFallbackNotification(message, !_cfg.DisableFocusStealing);
                }
                catch
                {
                    // Last resort - nothing we can do if both toast and fallback fail
                }
            }
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
