using System.Collections.ObjectModel;

namespace Mconfig.Domain.Definitions;

/// <summary>スキーマのグループ。行と列のキー（順序付き）と、それぞれの表示名・単位。</summary>
public sealed record SchemaGroupDefinition
{
    public SchemaGroupDefinition(string key, IEnumerable<string> rows, IEnumerable<string> columns, SchemaGroupLabels? labels = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Rows = Array.AsReadOnly(rows?.ToArray() ?? throw new ArgumentNullException(nameof(rows)));
        Columns = Array.AsReadOnly(columns?.ToArray() ?? throw new ArgumentNullException(nameof(columns)));
        Labels = labels ?? SchemaGroupLabels.None;
    }

    public string Key { get; }
    public IReadOnlyList<string> Rows { get; }
    public IReadOnlyList<string> Columns { get; }

    /// <summary>グループ・行・列の表示名と列の単位。</summary>
    public SchemaGroupLabels Labels { get; }
}

/// <summary>設定カテゴリのスキーマ。Name はカテゴリ名に対応する。</summary>
public sealed record SchemaDefinition
{
    public SchemaDefinition(string name, IEnumerable<SchemaGroupDefinition> groups, LocalizedText? displayName = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Groups = Array.AsReadOnly(groups?.ToArray() ?? throw new ArgumentNullException(nameof(groups)));
        DisplayName = displayName ?? LocalizedText.None;
    }

    public string Name { get; }
    public IReadOnlyList<SchemaGroupDefinition> Groups { get; }

    /// <summary>カテゴリの表示名。</summary>
    public LocalizedText DisplayName { get; }

    /// <summary>スキーマにある位置（グループ・行・列）か。スキーマにない位置（orphan）には値を入力させない。</summary>
    public bool Defines(string group, string row, string column) =>
        Groups.FirstOrDefault(g => g.Key == group) is { } definition &&
        definition.Rows.Contains(row) && definition.Columns.Contains(column);
}

/// <summary>スキーマのグループ・行・列の表示名と、列の単位（なければキーで表示し、単位なし）。</summary>
public sealed class SchemaGroupLabels
{
    public static SchemaGroupLabels None { get; } = new(LocalizedText.None, new Dictionary<string, LocalizedText>(),
        new Dictionary<string, LocalizedText>(), new Dictionary<string, string>());

    public SchemaGroupLabels(
        LocalizedText group,
        IReadOnlyDictionary<string, LocalizedText> rows,
        IReadOnlyDictionary<string, LocalizedText> columns,
        IReadOnlyDictionary<string, string> units)
    {
        Group = group ?? throw new ArgumentNullException(nameof(group));
        Rows = Copy(rows ?? throw new ArgumentNullException(nameof(rows)));
        Columns = Copy(columns ?? throw new ArgumentNullException(nameof(columns)));
        Units = Copy(units ?? throw new ArgumentNullException(nameof(units)));
    }

    public LocalizedText Group { get; }
    public IReadOnlyDictionary<string, LocalizedText> Rows { get; }
    public IReadOnlyDictionary<string, LocalizedText> Columns { get; }
    public IReadOnlyDictionary<string, string> Units { get; }

    public LocalizedText Row(string key) => Rows.GetValueOrDefault(key) ?? LocalizedText.None;
    public LocalizedText Column(string key) => Columns.GetValueOrDefault(key) ?? LocalizedText.None;
    public string? Unit(string key) => Units.GetValueOrDefault(key);

    // 渡された辞書を後で変えても表示名が変わらないよう複製する
    private static IReadOnlyDictionary<string, T> Copy<T>(IReadOnlyDictionary<string, T> source) =>
        new ReadOnlyDictionary<string, T>(source.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
}
