using Mconfig.Domain.Definitions;

namespace Mconfig.Presentation.Common;

/// <summary>
/// 定義の表示名。表示言語 → ja → キーの順で解決し、定義にないキーはそのまま示す。
/// 画面ごとに同じ解決を書かないよう、ここにまとめる。
/// </summary>
public static class DefinitionLabels
{
    public static SchemaGroupDefinition? FindGroup(SchemaDefinition? schema, string? group) =>
        group is null ? null : schema?.Groups.FirstOrDefault(g => g.Key == group);

    public static string Category(IReadOnlyList<SchemaDefinition> schemas, string category, string language) =>
        schemas.FirstOrDefault(s => s.Name == category)?.DisplayName.Resolve(language, category) ?? category;

    public static string Group(SchemaGroupDefinition? group, string key, string language) =>
        group?.Labels.Group.Resolve(language, key) ?? key;

    public static string Row(SchemaGroupDefinition? group, string key, string language) =>
        group?.Labels.Row(key).Resolve(language, key) ?? key;

    public static string Column(SchemaGroupDefinition? group, string key, string language) =>
        group?.Labels.Column(key).Resolve(language, key) ?? key;

    /// <summary>「カテゴリ / プリセット / グループ / 行 / 列」の位置。null の部分は省く（category が null ならカテゴリも省く）。</summary>
    public static string Location(
        IReadOnlyList<SchemaDefinition> schemas, string language,
        string? category, string? preset, string? group, string? row, string? column)
    {
        SchemaDefinition? schema = category is null ? null : schemas.FirstOrDefault(s => s.Name == category);
        SchemaGroupDefinition? definition = FindGroup(schema, group);
        return string.Join(" / ", new[]
        {
            category is null ? null : Category(schemas, category, language),
            preset,
            group is null ? null : Group(definition, group, language),
            row is null ? null : Row(definition, row, language),
            column is null ? null : Column(definition, column, language),
        }.OfType<string>());
    }
}
