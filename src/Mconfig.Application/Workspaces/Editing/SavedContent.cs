using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Workspaces.Editing;

/// <summary>
/// 一時保存 W の時点の設定。プリセットの値と割り当て行は不変なので、
/// カテゴリごとの一覧を写すだけで、その後の編集の影響を受けない。診断専用のカテゴリは編集できないので持たない。
/// </summary>
internal sealed class SavedContent
{
    private readonly Dictionary<string, SavedCategory> _categories;

    private SavedContent(Dictionary<string, SavedCategory> categories) => _categories = categories;

    public static SavedContent Capture(WorkspaceContent content) => new(content.Settings.Categories
        .Where(c => !c.Value.IsDiagnosticOnly)
        .ToDictionary(
            c => c.Key,
            c => new SavedCategory(
                c.Value.Presets.ToDictionary(p => p.Name, p => p.Content!, StringComparer.Ordinal),
                c.Value.Assignments.ToArray()),
            StringComparer.Ordinal));

    /// <summary>
    /// プリセットの W の時点の値。同じ名前がなければ、名前だけを変えたもの（値が同じ参照）を探す。W になかったプリセットは null。
    /// </summary>
    public PresetContent? SavedPreset(string category, string preset, PresetContent current)
    {
        if (!_categories.TryGetValue(category, out SavedCategory? saved)) return null;
        if (saved.Presets.TryGetValue(preset, out PresetContent? content)) return content;
        return saved.Presets.Values.FirstOrDefault(c => ReferenceEquals(c, current));
    }

    /// <summary>W の時点のセルの値。E と同じなら null（未保存の変更ではない）。W になかったプリセットは Missing と比べる。</summary>
    public static CellValue? SavedValueIfChanged(PresetContent? saved, string group, string row, string column, CellValue current)
    {
        CellValue before = saved?.FindGroup(group)?.FindRow(row)?.GetCell(column) ?? CellValue.Missing;
        return before == current ? null : before;
    }

    /// <summary>
    /// W から E までの変更の件数。セルは 1 つずつ、プリセットの追加・削除・名前の変更は 1 件ずつ、
    /// 割り当ては変わった行（追加・削除を含む）を数え、並びだけが変わったカテゴリは 1 件とする。
    /// 名前の変更は値を共有したまま名前だけが変わるので（業務操作は値を作り直さない）、値が同じ参照の削除と追加の組を名前の変更とみなし、
    /// それに追従した割り当て行は数えない。
    /// </summary>
    public int CountChanges(WorkspaceContent content) =>
        content.Settings.Categories.Sum(c => CountChanges(c.Key, c.Value));

    /// <summary>
    /// W の時点の割り当て行（上から優先）。W の後に名前を変えたプリセットは、いまの名前に読み替える（名前の変更だけで行が変わったとしない）。
    /// W になかったカテゴリは空。
    /// </summary>
    public IReadOnlyList<Assignment> SavedAssignments(string name, CategoryData category)
    {
        if (!_categories.TryGetValue(name, out SavedCategory? saved)) return [];
        IReadOnlyDictionary<string, string> renamed = Renames(saved, category, out _);
        return saved.Assignments
            .Select(a => a.Preset is { } p && renamed.TryGetValue(p, out string? to) ? new Assignment { Specs = a.Specs, Preset = to } : a)
            .ToArray();
    }

    /// <summary>
    /// いまの割り当て行のうち、W になかった行（追加した行と、条件か割り当て先を変えた行）の位置。
    /// 同じ行が複数あるときは、W にあった数だけを W の行とみなす。
    /// </summary>
    public IReadOnlySet<int> UnsavedAssignmentRows(string name, CategoryData category)
    {
        List<Assignment> remaining = [.. SavedAssignments(name, category)];
        HashSet<int> unsaved = [];
        for (int i = 0; i < category.Assignments.Count; i++)
        {
            int index = remaining.FindIndex(r => SameRule(r, category.Assignments[i]));
            if (index < 0) unsaved.Add(i);
            else remaining.RemoveAt(index);
        }
        return unsaved;
    }

    /// <summary>カテゴリ 1 つ分の変更の件数（数え方は <see cref="CountChanges(WorkspaceContent)"/> と同じ）。</summary>
    public int CountChanges(string name, CategoryData category)
    {
        if (category.IsDiagnosticOnly) return 0;
        _categories.TryGetValue(name, out SavedCategory? saved);
        IReadOnlyDictionary<string, PresetContent> before = saved?.Presets ?? new Dictionary<string, PresetContent>();
        Renames(saved, category, out int removed);

        int count = removed;
        foreach (Preset preset in category.Presets)
        {
            // W になかった名前は、追加か名前の変更で 1 件
            count += before.TryGetValue(preset.Name, out PresetContent? old) ? CountCellChanges(old, preset.Content!) : 1;
        }
        return count + CountAssignmentChanges(SavedAssignments(name, category), category.Assignments);
    }

    /// <summary>W にあって E にない名前のうち、値が同じ参照のまま E の新しい名前になったもの（旧 → 新）。removed はそれ以外に消えたプリセットの数。</summary>
    private static IReadOnlyDictionary<string, string> Renames(SavedCategory? saved, CategoryData category, out int removed)
    {
        IReadOnlyDictionary<string, PresetContent> before = saved?.Presets ?? new Dictionary<string, PresetContent>();
        List<string> gone = before.Keys.Where(p => category.Presets.All(x => x.Name != p)).ToList();
        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        foreach (Preset preset in category.Presets.Where(p => !before.ContainsKey(p.Name)))
        {
            int source = gone.FindIndex(r => ReferenceEquals(before[r], preset.Content));
            if (source < 0) continue;
            renamed[gone[source]] = preset.Name;
            gone.RemoveAt(source);
        }
        removed = gone.Count;
        return renamed;
    }

    private static int CountCellChanges(PresetContent before, PresetContent after)
    {
        if (ReferenceEquals(before, after)) return 0;
        int count = 0;
        foreach (string groupKey in Keys(before.Groups, after.Groups))
        {
            PresetGroup? b = before.FindGroup(groupKey), a = after.FindGroup(groupKey);
            if (b is null || a is null || b.IsUnused != a.IsUnused)
            {
                count++;
                continue;
            }
            foreach (string rowKey in Keys(b.Rows, a.Rows))
            {
                PresetRow? br = b.FindRow(rowKey), ar = a.FindRow(rowKey);
                if (br is { IsSupported: false } || ar is { IsSupported: false })
                {
                    if (br?.UnsupportedRepresentation != ar?.UnsupportedRepresentation) count++;
                    continue;
                }
                // 行の有無だけの違い（値のない行を作った）は数えず、セルの違いで数える
                IEnumerable<KeyValuePair<string, CellValue>> none = [];
                foreach (string column in Keys(br?.Cells ?? none, ar?.Cells ?? none))
                {
                    CellValue bv = br?.GetCell(column) ?? CellValue.Missing, av = ar?.GetCell(column) ?? CellValue.Missing;
                    if (bv != av) count++;
                }
            }
        }
        return count;
    }

    private static int CountAssignmentChanges(IReadOnlyList<Assignment> before, IReadOnlyList<Assignment> after)
    {
        List<Assignment> remaining = [.. before];
        int added = 0;
        foreach (Assignment row in after)
        {
            int index = remaining.FindIndex(r => SameRule(r, row));
            if (index < 0) added++;
            else remaining.RemoveAt(index);
        }
        int changed = added + remaining.Count;
        if (changed > 0) return changed;
        // 同じ行が並び替えられただけでも、優先順が変わるので変更として数える
        return before.Count == after.Count && before.Zip(after).All(p => SameRule(p.First, p.Second)) ? 0 : 1;
    }

    private static bool SameRule(Assignment a, Assignment b) =>
        a.Preset == b.Preset && a.Specs.Count == b.Specs.Count &&
        a.Specs.All(s => b.Specs.TryGetValue(s.Key, out string? v) && v == s.Value);

    private static IEnumerable<string> Keys<T>(IEnumerable<KeyValuePair<string, T>> first, IEnumerable<KeyValuePair<string, T>> second) =>
        first.Select(e => e.Key).Concat(second.Select(e => e.Key)).Distinct(StringComparer.Ordinal);

    private sealed record SavedCategory(IReadOnlyDictionary<string, PresetContent> Presets, IReadOnlyList<Assignment> Assignments);
}
