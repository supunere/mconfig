namespace Mconfig.Domain.Configuration;

/// <summary>ワークスペースの設定。カテゴリ名 → カテゴリ。</summary>
public sealed class SettingsData
{
    public IReadOnlyDictionary<string, CategoryData> Categories { get; }

    public SettingsData() : this(new Dictionary<string, CategoryData>()) { }

    public SettingsData(IDictionary<string, CategoryData> categories, ExtensionProperties? extensions = null)
    {
        Dictionary<string, CategoryData> copy = new(categories, StringComparer.Ordinal);
        Categories = new System.Collections.ObjectModel.ReadOnlyDictionary<string, CategoryData>(copy);
        Extensions = extensions ?? ExtensionProperties.None;
    }

    /// <summary>最上位にあるカテゴリ以外のプロパティ（保存表現の未知情報）。</summary>
    public ExtensionProperties Extensions { get; }
}
