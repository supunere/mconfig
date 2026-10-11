namespace Mconfig.Domain.Configuration;

/// <summary>プリセット。名前と値（不変）を持つ。値の変更はカテゴリの業務操作で行う。</summary>
public sealed class Preset
{
    public string Name { get; init; } = "";

    /// <summary>グループ → 行 → 列の値。null は読み込んだデータが壊れていた場合だけ（診断用）。</summary>
    public PresetContent? Content { get; init; } = PresetContent.Empty;

    private readonly ExtensionProperties _extensions = ExtensionProperties.None;

    /// <summary>保存表現にあるが業務モデルが解釈しないプロパティ。業務操作で引き継ぐ。</summary>
    public ExtensionProperties Extensions
    {
        get => _extensions;
        init => _extensions = value ?? ExtensionProperties.None;
    }

    /// <summary>セルの値。グループ・行・セルがなければ Missing。</summary>
    public CellValue GetCell(string groupKey, string rowKey, string columnKey) =>
        Content?.FindGroup(groupKey)?.FindRow(rowKey)?.GetCell(columnKey) ?? CellValue.Missing;

    public bool IsGroupUnused(string groupKey) => Content?.FindGroup(groupKey)?.IsUnused == true;

    /// <summary>指定したグループと行を持つ、値が未入力の新しいプリセットを作る。</summary>
    public static Preset CreateBlank(string name, IEnumerable<KeyValuePair<string, IEnumerable<string>>> groupRows) => new()
    {
        Name = name,
        Content = new PresetContent(groupRows.Select(g => KeyValuePair.Create(g.Key,
            PresetGroup.WithRows(g.Value.Select(row => KeyValuePair.Create(row, PresetRow.WithCells([]))))))),
    };
}
