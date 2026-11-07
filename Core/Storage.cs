using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimTogether;

public static class Storage
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    public static void Write<T>(string path, T value, bool keepRawBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(true);
            }
            if (keepRawBackup && File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("Пустой файл: " + path);
}

public class Settings
{
    public string GamePath { get; set; } = Directory.Exists(@"D:\RimWorld") ? @"D:\RimWorld" : "";
    public string Username { get; set; } = "Player";
    public string HostAddress { get; set; } = "127.0.0.1";
    public string Password { get; set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
    public string SeedReplay { get; set; } = "";
    public bool IsHost { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public string LastFingerprint { get; set; } = "";
    public string ExpectedFingerprint { get; set; } = "";
}

public static class Contract
{
    // A single cohort prevents a client updating beyond the bundled server protocol.
    public const string Tag = "v0.11.5";
    public const string Version = "0.11.5";
    public const int Protocol = 54;
    public const int Port = 30502;
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimTogether");
    public static string GameData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios");
}
