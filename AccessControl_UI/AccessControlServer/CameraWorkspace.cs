using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccessControlServer;

public sealed class CameraWorkspace
{
    public string?[] Slots { get; set; } = new string?[] { "CAM-01", null, null, null, null, null, null, null, null };
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccessControlSystem", "server-camera-layout.json");
    public static CameraWorkspace Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<CameraWorkspace>(File.ReadAllText(FilePath));
            if (value?.Slots is { Length: 9 })
            {
                bool used = false;
                for (int i = 0; i < 9; i++)
                {
                    if (value.Slots[i] != "CAM-01" || used) value.Slots[i] = null;
                    else used = true;
                }
                return value;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
