using System.Text.Json;
using System.Text.Json.Serialization;

namespace XMInstaller;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
sealed partial class AppJsonContext : JsonSerializerContext;

sealed class AppSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 34567;
    public string User { get; set; } = "admin";
    public string LastCfg { get; set; } = "";

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XMInstaller", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new AppSettings { Host = Host, Port = Port, User = User, LastCfg = LastCfg }, AppJsonContext.Default.AppSettings);
        File.WriteAllText(FilePath, json);
    }
}
