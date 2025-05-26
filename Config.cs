// Config.cs - Manages application configuration settings for NightKnight.
// Handles loading and saving settings from/to a JSON file.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightKnight
{
    public class Config
    {
        private const int MaxReadRetries = 5;
        private const int ReadRetryDelayMilliseconds = 250;
        [JsonPropertyName("bedtimes")]
        public Dictionary<string, string>? Bedtimes { get; set; }

        [JsonPropertyName("disableFocusStealing")]
        public bool DisableFocusStealing { get; set; } = false;

        [JsonPropertyName("disableOneMinuteWarning")]
        public bool DisableOneMinuteWarning { get; set; } = false;

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

        public static readonly string ConfigPath;

        static Config() // Static constructor to initialize ConfigPath
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string companyName = "Porkchop13"; // Or your actual company/developer name
            string appName = "NightKnight";
            ConfigPath = Path.Combine(appDataPath, companyName, appName, "Settings.json");

            // Ensure the directory exists so the file can be created
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        }

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static Config Load()
        {
            if (!File.Exists(ConfigPath))
            {
                var def = new Config
                {
                    Bedtimes = new Dictionary<string, string>
                    {
                        ["Monday"] = "22:20",
                        ["Tuesday"] = "22:20",
                        ["Wednesday"] = "22:20",
                        ["Thursday"] = "22:20",
                        ["Friday"] = "23:20",
                        ["Saturday"] = "23:20",
                        ["Sunday"] = "22:20"
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

            string json = string.Empty;
            for (int i = 0; i < MaxReadRetries; i++)
            {
                try
                {
                    json = File.ReadAllText(ConfigPath);
                    break; // Successfully read the file
                }
                catch (IOException)
                {
                    if (i < MaxReadRetries - 1)
                    {
                        Thread.Sleep(ReadRetryDelayMilliseconds); // Wait before retrying
                    }
                    else
                    {
                        throw; // Rethrow the exception if all retries fail
                    }
                }
            }
            return JsonSerializer.Deserialize<Config>(json)!;
        }
    }
}
