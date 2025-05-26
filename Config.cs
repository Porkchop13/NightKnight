// Config.cs - Manages application configuration settings for NightKnight.
// Handles loading and saving settings from/to a JSON file.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading; // Required for Thread.Sleep

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
