using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

namespace Mconfig.Application.Presets.Queries;

/// <summary>比較表の 1 行が表すセルの位置。</summary>
public sealed record CellKey(string Group, string Row, string Column);

/// <summary>カテゴリ内のプリセットを、スキーマの行の順に並べて比較する。</summary>
public sealed record GetPresetComparisonQuery(string Category);

public enum ComparisonCellState
{
    /// <summary>グループが未使用。</summary>
    Unused,
    /// <summary>値を編集できる（未入力を含む）。</summary>
    Editable,
    /// <summary>行やセルが理解できない構造なので編集できない。</summary>
    Uneditable,
}

public sealed record ComparisonCell(ComparisonCellState State, CellValue Value)
{
    /// <summary>一時保存 W の時点の値。W から変わっていなければ null（未保存の変更ではない）。</summary>
    public CellValue? SavedValue { get; init; }
}

public sealed record PresetComparisonRow(CellKey Key, IReadOnlyList<ComparisonCell> Cells);

/// <summary>
/// 比較表のリードモデル。読み取り専用の値だけで構成し、ドメインの変更可能な参照を含めない。
/// Version は投影元の E の版で、画面は古い版の結果を破棄できる。
/// カテゴリやスキーマがないか、カテゴリが壊れていれば Available は false。
/// </summary>
public sealed record PresetComparisonReadModel(
    string Category,
    SessionVersion Version,
    bool Available,
    IReadOnlyList<string> Presets,
    IReadOnlyList<PresetComparisonRow> Rows)
{
    /// <summary>同じ時点のカテゴリのスキーマ（不変）。表示名と単位の解決に使う。</summary>
    public SchemaDefinition? Schema { get; init; }
}

public sealed class GetPresetComparisonHandler(EditSession session)
{
    public PresetComparisonReadModel Handle(GetPresetComparisonQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        // 一覧と不変のプリセット値だけを写す。セルの投影はセッションロックを保持しない。
        SessionSnapshot snapshot = session.Capture();
        return Project(snapshot.Content, snapshot.Saved, query.Category) with { Version = snapshot.Status.Version };
    }

    private static PresetComparisonReadModel Project(WorkspaceContent content, SavedContent saved, string categoryName)
    {
        SchemaDefinition? schema = content.Schemas.FirstOrDefault(s => s.Name == categoryName);
        if (schema is null ||
            !content.Settings.Categories.TryGetValue(categoryName, out CategoryData? category) ||
            category.IsDiagnosticOnly)
            return new PresetComparisonReadModel(categoryName, SessionVersion.Initial, false, [], []);

        Preset[] presets = category.Presets.ToArray();
        PresetContent?[] savedPresets = presets.Select(p => saved.SavedPreset(categoryName, p.Name, p.Content!)).ToArray();
        PresetComparisonRow[] rows = schema.Groups
            .SelectMany(g => g.Rows.SelectMany(r => g.Columns.Select(c => new CellKey(g.Key, r, c))))
            .Select(key => new PresetComparisonRow(key, new ComparisonCells(presets, savedPresets, key)))
            .ToArray();
        return new PresetComparisonReadModel(
            categoryName, SessionVersion.Initial, true, presets.Select(p => p.Name).ToArray(), rows) { Schema = schema };
    }

    // 非表示の列のセルを生成しない。不変の内容なので後からセッションを編集しても結果は混ざらない。
    private sealed class ComparisonCells(Preset[] presets, PresetContent?[] savedPresets, CellKey key) : IReadOnlyList<ComparisonCell>
    {
        public int Count => presets.Length;
        public ComparisonCell this[int index]
        {
            get
            {
                ComparisonCell cell = CellOf(presets[index].Content!, key);
                return cell.State == ComparisonCellState.Editable
                    ? cell with { SavedValue = SavedContent.SavedValueIfChanged(savedPresets[index], key.Group, key.Row, key.Column, cell.Value) }
                    : cell;
            }
        }
        public IEnumerator<ComparisonCell> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // 入力できるかの規則は Domain（PresetContent.CellAt）が持つ。理解できない値は表示しない
    private static ComparisonCell CellOf(PresetContent content, CellKey key)
    {
        CellSlot cell = content.CellAt(key.Group, key.Row, key.Column);
        return cell.Editability switch
        {
            CellEditability.Editable => new ComparisonCell(ComparisonCellState.Editable, cell.Value),
            CellEditability.Unused => new ComparisonCell(ComparisonCellState.Unused, CellValue.Missing),
            _ => new ComparisonCell(ComparisonCellState.Uneditable, cell.Value.Kind == CellValueKind.Unsupported ? cell.Value : CellValue.Missing),
        };
    }
}
