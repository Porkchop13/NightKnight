// NightKnight – Tray utility with dynamic JSON config, daily stats log, reliable toasts & fullscreen popup fallback

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;
using Timer = System.Windows.Forms.Timer;
using System.Diagnostics;
using Windows.Data.Xml.Dom;
using Microsoft.Win32; // Added for SystemEvents

namespace NightKnight
{
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
                // For single-file app, Assembly.Location is empty. Use AppContext.BaseDirectory and the process name.
                string exeName = Path.GetFileName(Environment.ProcessPath!); // Get the actual exe name
                string appPath = Path.Combine(AppContext.BaseDirectory, exeName);
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
                        shortcut.WorkingDirectory = AppContext.BaseDirectory; // BaseDirectory is the working directory
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
        private NotifyIcon? _tray;
        private readonly Timer _timer;
        private FileSystemWatcher? _watcher;
        private Config _cfg;

        private bool _cancelTonight;
        private bool _locked;
        private DateTime _lockTime;
        private DateTime _lastToast;
        private string _todayDate = DateTime.Now.ToString(DateFormatString);
        private bool _oneMinuteWarningShownToday = false;

        // Constants
        private const string TrayTextRunning = "NightKnight – running";
        private const string TrayTextCancelled = "NightKnight – cancelled for tonight";
        private const string DateFormatString = "yyyy-MM-dd";
        private const int TimerIntervalMilliseconds = 60_000;

        public ToolbarContext()
        {
            _cfg = Config.Load();

            InitializeTrayAndMenu();
            InitializeConfigWatcher();

            // 1-min tick timer
            _timer = new Timer { Interval = TimerIntervalMilliseconds };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();

            // Subscribe to PowerModeChanged event to handle system resume
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        }

        private void InitializeTrayAndMenu()
        {
            // Tray setup
            Icon? trayIcon = null;
            string directIconPath = Path.Combine(AppContext.BaseDirectory, "NightKnight.ico");

            if (File.Exists(directIconPath))
            {
                try
                {
                    trayIcon = new Icon(directIconPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error loading icon from {directIconPath}: {ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine($"NightKnight.ico not found at {directIconPath}. Ensure 'Copy to Output Directory' is set. Attempting fallbacks.");
            }

            // Fallback to extracting from entry assembly if direct load failed
            if (trayIcon == null)
            {
                string? assemblyLocation = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(assemblyLocation) && File.Exists(assemblyLocation))
                {
                    try
                    {
                        trayIcon = Icon.ExtractAssociatedIcon(assemblyLocation);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error extracting icon from assembly {assemblyLocation}: {ex.Message}");
                    }
                }
                else
                {
                    // This case is expected for single-file apps, so logging might be too noisy unless it's a true unexpected failure.
                    // Only log if assemblyLocation was expected to be valid but wasn't found.
                    if (!string.IsNullOrEmpty(assemblyLocation) && !File.Exists(assemblyLocation))
                    {
                        Debug.WriteLine($"Assembly for icon extraction not found at expected location: {assemblyLocation}");
                    }
                    // If assemblyLocation is null/empty (single-file app), this is normal, no specific log needed here for that case.
                }
            }

            _tray = new NotifyIcon
            {
                Icon = trayIcon ?? SystemIcons.Application, // Use loaded/extracted icon or fallback
                Visible = true,
                Text = TrayTextRunning
            };
            var cm = new ContextMenuStrip();
            var cancelItem = new ToolStripMenuItem("Cancel tonight only");
            cancelItem.Click += (_, _) =>
            {
                _cancelTonight = true;
                cancelItem.Enabled = false;
                if (_tray != null) _tray.Text = TrayTextCancelled;
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
                    // Ensure the config file exists before trying to open it.
                    // Config.Load() might create it or load defaults if it doesn't.
                    if (!File.Exists(Config.ConfigPath))
                    {
                        Config.Load(); // This might create/populate the file
                    }
                    // If it still doesn't exist after attempting to load/create, handle error or let Process.Start fail.
                    if (!File.Exists(Config.ConfigPath))
                    {
                        ShowToast($"Config file not found at {Config.ConfigPath} and could not be created.");
                        Debug.WriteLine($"Config file not found at {Config.ConfigPath} and could not be created.");
                        return;
                    }

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = Config.ConfigPath, // Use the config path directly
                        UseShellExecute = true       // This tells the OS to use the default handler
                    };
                    Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    ShowToast($"Error opening config: {ex.Message}");
                    Debug.WriteLine($"Error opening config: {ex.Message}");
                }
            }));
            cm.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));
            _tray.ContextMenuStrip = cm;
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                Debug.WriteLine("System resumed. Checking for daily reset.");
                HandleDailyReset(DateTime.Now);
            }
        }

        private void InitializeConfigWatcher()
        {
            // Watch for JSON config edits
            // Ensure DirectoryName is not null before using it.
            var configDir = Path.GetDirectoryName(Config.ConfigPath);
            if (string.IsNullOrEmpty(configDir))
            {
                Debug.WriteLine($"Could not determine directory for config path: {Config.ConfigPath}");
                ShowToast("Error: Config path directory not found.");
                return;
            }

            _watcher = new FileSystemWatcher(configDir)
            {
                Filter = Path.GetFileName(Config.ConfigPath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) =>
            {
                _cfg = Config.Load();
                ShowToast("Config reloaded");
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
                _tray?.Dispose();
                _watcher?.Dispose();

                // Unsubscribe from PowerModeChanged event
                SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            }
            base.Dispose(disposing);
        }

        private void Tick()
        {
            var now = DateTime.Now;
            HandleDailyReset(now);
            if (_cancelTonight) return;
            ProcessBedtimeChecks(now);
        }

        private void HandleDailyReset(DateTime now)
        {
            // Daily reset at midnight
            if (_todayDate != now.ToString(DateFormatString)) // Use constant
            {
                _todayDate = now.ToString(DateFormatString); // Use constant
                _cancelTonight = false;
                _locked = false;
                _oneMinuteWarningShownToday = false; // Reset the flag daily
                if (_tray != null) _tray.Text = TrayTextRunning;
            }
        }

        private void ProcessBedtimeChecks(DateTime now)
        {
            if (!TryCalculateMinutesToBedtime(now, out double minutesToBed))
            {
                return; // Error handled in TryCalculateMinutesToBedtime
            }

            HandleBedtimeWarning(now, minutesToBed);
            HandleBedtimeLock(now, minutesToBed);
            HandleAutoLogoff(now); // Depends on _locked state, not directly minutesToBed
        }

        private bool TryCalculateMinutesToBedtime(DateTime now, out double minutesToBed)
        {
            minutesToBed = 0;
            if (_cfg.Bedtimes == null || !_cfg.Bedtimes.TryGetValue(now.DayOfWeek.ToString(), out var bedtimeStr) || string.IsNullOrEmpty(bedtimeStr))
            {
                Debug.WriteLine($"Bedtime not configured or invalid for {now.DayOfWeek}.");
                return false;
            }

            DateTime bedtimeToday;
            try
            {
                bedtimeToday = DateTime.Parse($"{now.ToString(DateFormatString)} {bedtimeStr}");
            }
            catch (FormatException ex)
            {
                Debug.WriteLine($"Invalid bedtime format for {now.DayOfWeek}: {bedtimeStr}. Error: {ex.Message}");
                ShowToast($"Error: Invalid bedtime format for {now.DayOfWeek}.");
                return false;
            }

            minutesToBed = (bedtimeToday - now).TotalMinutes;
            return true;
        }

        private void HandleBedtimeWarning(DateTime now, double minutesToBed)
        {
            // One-minute specific warning
            if (!_cfg.DisableOneMinuteWarning && minutesToBed > 0 && minutesToBed <= 1 && !_oneMinuteWarningShownToday)
            {
                ShowToast("Bedtime in 1 minute!");
                _lastToast = now; // Update lastToast to prevent immediate repeat by general warning
                _oneMinuteWarningShownToday = true;
                return; // One-minute warning shown, skip general warning for this tick
            }

            // General warning
            // Ensure minutesToBed > 0 to avoid warning after bedtime, and also check !_oneMinuteWarningShownToday to prevent re-warning if 1-min warning is also the configured WarningMinutesBefore
            if (minutesToBed <= _cfg.WarningMinutesBefore && minutesToBed > 0 && !_oneMinuteWarningShownToday)
            {
                // Check if enough time has passed since the last toast
                if ((now - _lastToast).TotalMinutes >= _cfg.ToastRepeatMinutes)
                {
                    ShowToast($"Bedtime in {Math.Ceiling(minutesToBed)} minutes.");
                    _lastToast = now;
                }
            }
        }

        private void HandleBedtimeLock(DateTime now, double minutesToBed)
        {
            if (minutesToBed <= 0 && !_locked)
            {
                ShowToast("Bedtime reached. Locking workstation now.");
                NativeMethods.LockWorkStation();
                _locked = true;
                _lockTime = now;
                Log("Lock", now, "workstation locked");
            }
        }

        private void HandleAutoLogoff(DateTime now)
        {
            if (_cfg.EnableAutoLogoff && _locked && (now - _lockTime).TotalMinutes >= _cfg.GraceMinutesAfterLock)
            {
                ShowToast("Grace period expired. Logging off now.");
                Log("Logoff", now, "auto logoff");
                NativeMethods.ExitWindowsEx(0, 0); //EWX_LOGOFF = 0
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

        private static void ShowFallbackNotification(string message, bool enableFocusStealing)
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
                string iconPath = Path.Combine(AppContext.BaseDirectory, "NightKnight.ico");
                Uri iconUri = new($"file:///{iconPath}");
                Debug.WriteLine($"Toast Icon URI: {iconUri}");

                var toastContent = new ToastContentBuilder()
                    .AddAppLogoOverride(iconUri)
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
                    // If _cfg is null, DisableFocusStealing defaults to false, so enableFocusStealing becomes true.
                    ShowFallbackNotification(message, !(_cfg?.DisableFocusStealing ?? false));
                }
                catch
                {
                    // Last resort - nothing we can do if both toast and fallback fail
                }
            }
        }

        private void ExitApplication()
        {
            if (_tray != null) _tray.Visible = false;
            _timer?.Stop(); // Timer is readonly, initialized, but good practice with ?. if other disposables are handled this way.
            if (_watcher != null) _watcher.EnableRaisingEvents = false;
            // _timer, _tray, and _watcher will be disposed by the overridden Dispose method when ApplicationContext disposes.
            Application.Exit();
        }
    }
}
