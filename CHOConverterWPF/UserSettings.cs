using System;
using System.IO;
using System.Text.Json;

namespace CHOConverterWPF
{
    internal sealed class UserSettings
    {
        public string UiCulture { get; set; } = "de-DE";

        public static string SettingsPath
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CHOConverterWPF");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.json");
            }
        }

        public static UserSettings Load()
        {
            try
            {
                var path = SettingsPath;
                if (!File.Exists(path)) return new UserSettings();

                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<UserSettings>(json);
                return settings ?? new UserSettings();
            }
            catch
            {
                return new UserSettings();
            }
        }

        public void Save()
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
    }
}
