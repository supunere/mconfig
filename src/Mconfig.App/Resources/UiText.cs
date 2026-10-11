using System.Globalization;
using System.Resources;

namespace Mconfig.App.Resources;

/// <summary>
/// 新しい画面の表示文（App 所有の .resx）。選択言語 → 既定（ja）→ キーの順に解決し、
/// 文は語順を変えられるプレースホルダーで組み立てる。
/// </summary>
internal static class UiText
{
    private static readonly ResourceManager Manager = new("Mconfig.App.Resources.UiText", typeof(UiText).Assembly);

    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("ja");

    public static string Get(string key)
    {
        try
        {
            return Manager.GetString(key, Culture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    public static string Format(string key, params object[] args)
    {
        string template = Get(key);
        try
        {
            return string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
