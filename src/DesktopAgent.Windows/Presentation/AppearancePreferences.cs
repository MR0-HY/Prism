using System.IO;
using System.Text.Json;

namespace DesktopAgent.Windows.Presentation;

internal sealed record AppearancePreferences(string Theme = "system", double? HudX = null, double? HudY = null)
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prism", "appearance.json");
    internal static AppearancePreferences Read()
    { try { return JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(FilePath)) ?? new(); } catch { return new(); } }
    internal void Save()
    { try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this)); File.Move(FilePath + ".tmp", FilePath, true); } catch { } }
}
