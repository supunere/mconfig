using System.Text.Json;

namespace Mconfig.App.Composition;

/// <summary>
/// 利用者ごとのアプリの設定（%LOCALAPPDATA%\mconfig\app-settings.json）。ワークスペースの場所と画面の配置を、文字列の値で記憶する。
/// 書くときは既存の値に重ねるので、別の項目を消さない。読めない・書けないときは既定のまま続ける。
/// </summary>
internal static class AppSettingsFile
{
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mconfig");

    private static string FilePath => Path.Combine(AppDataDirectory, "app-settings.json");

    public static string? Get(string key) => ReadAll().GetValueOrDefault(key);

    public static void Set(IReadOnlyDictionary<string, string> values)
    {
        try
        {
            Dictionary<string, string> settings = ReadAll();
            foreach ((string key, string value) in values) settings[key] = value;
            Directory.CreateDirectory(AppDataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
        }
        catch (IOException)
        {
            // 記憶できなくても今回の起動は続ける
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, string> ReadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
