using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

namespace Mconfig.Application.History;

public enum ContentChangeKind
{
    SpecAxisAdded,
    SpecAxisRemoved,
    /// <summary>仕様の軸の値（または値の順序）が変わった。</summary>
    SpecValuesChanged,
    SpecAxesReordered,
    SchemaAdded,
    SchemaRemoved,
    SchemaGroupAdded,
    SchemaGroupRemoved,
    SchemaGroupsReordered,
    SchemaRowsChanged,
    SchemaColumnsChanged,
    CategoryAdded,
    CategoryRemoved,
    PresetAdded,
    PresetRemoved,
    PresetsReordered,
    /// <summary>グループの有無・未使用（Unused）の切り替え。Items に "absent"/"unused"/"used"。</summary>
    GroupStateChanged,
    /// <summary>理解できない構造の行が変わった。Items に保存表現。</summary>
    RowChanged,
    CellChanged,
    AssignmentAdded,
    AssignmentRemoved,
    /// <summary>同じ位置の割り当て行の条件または割り当て先が変わった（順序の変更もここに現れる）。</summary>
    AssignmentChanged,
    /// <summary>仕様・スキーマの表示名または単位が変わった（キーは同じ）。</summary>
    DisplayNamesChanged,
    /// <summary>未知プロパティや壊れたカテゴリなど、上の分類で説明できない差。</summary>
    OtherDataChanged,
}

/// <summary>変更の位置。識別キーと定義ファイルの相対パス。表示名・単位は変更側の版から Items に含める。</summary>
public sealed record ChangeLocation(
    string? SpecKey = null,
    string? Schema = null,
    string? Category = null,
    string? Preset = null,
    string? Group = null,
    string? Row = null,
    string? Column = null,
    int? Index = null,
    string? DefinitionPath = null);

public sealed record AssignmentSnapshot(IReadOnlyDictionary<string, string> Conditions, string? Preset);

/// <summary>意味単位の変更。種類に応じて Cell・Items・Assignment のいずれかに前後の値を持つ。</summary>
public sealed record ContentChange(ContentChangeKind Kind, ChangeLocation Location)
{
    public CellValue? CellBefore { get; init; }
    public CellValue? CellAfter { get; init; }
    public IReadOnlyList<string>? ItemsBefore { get; init; }
    public IReadOnlyList<string>? ItemsAfter { get; init; }
    public AssignmentSnapshot? AssignmentBefore { get; init; }
    public AssignmentSnapshot? AssignmentAfter { get; init; }
}

/// <summary>二つのワークスペース内容の意味差分。それぞれ自身の仕様・スキーマの定義で比較する。</summary>
public static class WorkspaceDiff
{
    public static WorkspaceContent Empty { get; } = new(new SettingsData(), SpecDefinition.Empty, []);

    public static IReadOnlyList<ContentChange> Compare(WorkspaceContent before, WorkspaceContent after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        List<ContentChange> changes = [];
        CompareSpec(changes, before.Spec, after.Spec);
        CompareSchemas(changes, before.Schemas, after.Schemas);
        CompareSettings(changes, before.Settings, after.Settings);
        CompareDefinitionSources(changes, before, after);

        // 分類できなかった差も、差がないとは言わない
        if (changes.Count == 0 && WorkspaceFingerprints.Of(before) != WorkspaceFingerprints.Of(after))
            changes.Add(new ContentChange(ContentChangeKind.OtherDataChanged, new ChangeLocation()));
        return changes;
    }

    private static void CompareDefinitionSources(List<ContentChange> changes, WorkspaceContent before, WorkspaceContent after)
    {
        foreach (string path in before.DefinitionSources.Keys.Concat(after.DefinitionSources.Keys)
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            bool existed = before.DefinitionSources.ContainsKey(path);
            bool exists = after.DefinitionSources.ContainsKey(path);
            ChangeLocation location = new(DefinitionPath: path);
            if (existed != exists)
            {
                changes.Add(new(ContentChangeKind.OtherDataChanged, location)
                {
                    ItemsBefore = existed ? [path] : [],
                    ItemsAfter = exists ? [path] : [],
                });
                continue;
            }
            string oldProperties = before.DefinitionUnknownProperties.GetValueOrDefault(path) ?? "{}";
            string newProperties = after.DefinitionUnknownProperties.GetValueOrDefault(path) ?? "{}";
            if (oldProperties != newProperties)
                changes.Add(new(ContentChangeKind.OtherDataChanged, location)
                {
                    ItemsBefore = [oldProperties],
                    ItemsAfter = [newProperties],
                });
        }
    }

    private static void CompareSpec(List<ContentChange> changes, SpecDefinition before, SpecDefinition after)
    {
        Dictionary<string, SpecAxis> old = before.Axes.ToDictionary(a => a.Key);
        Dictionary<string, SpecAxis> now = after.Axes.ToDictionary(a => a.Key);
        foreach (SpecAxis axis in before.Axes.Where(a => !now.ContainsKey(a.Key)))
            changes.Add(new(ContentChangeKind.SpecAxisRemoved, new(SpecKey: axis.Key)) { ItemsBefore = axis.Values });
        foreach (SpecAxis axis in after.Axes)
        {
            if (!old.TryGetValue(axis.Key, out SpecAxis? previous))
                changes.Add(new(ContentChangeKind.SpecAxisAdded, new(SpecKey: axis.Key)) { ItemsAfter = axis.Values });
            else
            {
                if (!previous.Values.SequenceEqual(axis.Values))
                    changes.Add(new(ContentChangeKind.SpecValuesChanged, new(SpecKey: axis.Key)) { ItemsBefore = previous.Values, ItemsAfter = axis.Values });
                if (previous.DisplayName.Canonical != axis.DisplayName.Canonical)
                    AddDisplayChange(changes, new(SpecKey: axis.Key), previous.DisplayName, axis.DisplayName);
            }
        }
        AddReorder(changes, ContentChangeKind.SpecAxesReordered, new(),
            before.Axes.Select(a => a.Key).ToArray(), after.Axes.Select(a => a.Key).ToArray());
    }

    private static void CompareSchemas(List<ContentChange> changes, IReadOnlyList<SchemaDefinition> before, IReadOnlyList<SchemaDefinition> after)
    {
        Dictionary<string, SchemaDefinition> old = before.ToDictionary(s => s.Name);
        Dictionary<string, SchemaDefinition> now = after.ToDictionary(s => s.Name);
        foreach (SchemaDefinition schema in before.Where(s => !now.ContainsKey(s.Name)).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            changes.Add(new(ContentChangeKind.SchemaRemoved, new(Schema: schema.Name))
                { ItemsBefore = [DefinitionItem(schema.Name, schema.DisplayName, null)] });
            foreach (SchemaGroupDefinition group in schema.Groups)
                DescribeSchemaGroup(changes, schema.Name, group, added: false);
        }
        foreach (SchemaDefinition schema in after.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            if (!old.TryGetValue(schema.Name, out SchemaDefinition? previous))
            {
                changes.Add(new(ContentChangeKind.SchemaAdded, new(Schema: schema.Name))
                    { ItemsAfter = [DefinitionItem(schema.Name, schema.DisplayName, null)] });
                foreach (SchemaGroupDefinition group in schema.Groups)
                    DescribeSchemaGroup(changes, schema.Name, group, added: true);
                continue;
            }
            if (previous.DisplayName.Canonical != schema.DisplayName.Canonical)
                AddDisplayChange(changes, new(Schema: schema.Name), previous.DisplayName, schema.DisplayName);
            Dictionary<string, SchemaGroupDefinition> oldGroups = previous.Groups.ToDictionary(g => g.Key);
            Dictionary<string, SchemaGroupDefinition> newGroups = schema.Groups.ToDictionary(g => g.Key);
            foreach (SchemaGroupDefinition group in previous.Groups.Where(g => !newGroups.ContainsKey(g.Key)))
                DescribeSchemaGroup(changes, schema.Name, group, added: false);
            foreach (SchemaGroupDefinition group in schema.Groups)
            {
                if (!oldGroups.TryGetValue(group.Key, out SchemaGroupDefinition? previousGroup))
                {
                    DescribeSchemaGroup(changes, schema.Name, group, added: true);
                    continue;
                }
                if (!previousGroup.Rows.SequenceEqual(group.Rows))
                    changes.Add(new(ContentChangeKind.SchemaRowsChanged, new(Schema: schema.Name, Group: group.Key))
                        {
                            ItemsBefore = previousGroup.Rows.Select(r => DefinitionItem(r, previousGroup.Labels.Row(r), null)).ToArray(),
                            ItemsAfter = group.Rows.Select(r => DefinitionItem(r, group.Labels.Row(r), null)).ToArray(),
                        });
                if (!previousGroup.Columns.SequenceEqual(group.Columns))
                    changes.Add(new(ContentChangeKind.SchemaColumnsChanged, new(Schema: schema.Name, Group: group.Key))
                        {
                            ItemsBefore = previousGroup.Columns.Select(c => DefinitionItem(c, previousGroup.Labels.Column(c), previousGroup.Labels.Unit(c))).ToArray(),
                            ItemsAfter = group.Columns.Select(c => DefinitionItem(c, group.Labels.Column(c), group.Labels.Unit(c))).ToArray(),
                        });
                CompareLabels(changes, schema.Name, previousGroup, group);
            }
            AddReorder(changes, ContentChangeKind.SchemaGroupsReordered, new(Schema: schema.Name),
                previous.Groups.Select(g => g.Key).ToArray(), schema.Groups.Select(g => g.Key).ToArray());
        }
    }

    /// <summary>追加・削除されたグループと行・列を、その版の表示名・単位で示す。</summary>
    private static void DescribeSchemaGroup(List<ContentChange> changes, string schema, SchemaGroupDefinition group, bool added)
    {
        ChangeLocation location = new(Schema: schema, Group: group.Key);
        string[] items = [DefinitionItem(group.Key, group.Labels.Group, null)];
        changes.Add(new(added ? ContentChangeKind.SchemaGroupAdded : ContentChangeKind.SchemaGroupRemoved, location)
        {
            ItemsBefore = added ? [] : items,
            ItemsAfter = added ? items : [],
        });
        if (group.Rows.Count > 0)
        {
            string[] rows = group.Rows.Select(r => DefinitionItem(r, group.Labels.Row(r), null)).ToArray();
            changes.Add(new(ContentChangeKind.SchemaRowsChanged, location)
                { ItemsBefore = added ? [] : rows, ItemsAfter = added ? rows : [] });
        }
        if (group.Columns.Count > 0)
        {
            string[] columns = group.Columns.Select(c => DefinitionItem(c, group.Labels.Column(c), group.Labels.Unit(c))).ToArray();
            changes.Add(new(ContentChangeKind.SchemaColumnsChanged, location)
                { ItemsBefore = added ? [] : columns, ItemsAfter = added ? columns : [] });
        }
    }

    private static void CompareSettings(List<ContentChange> changes, SettingsData before, SettingsData after)
    {
        foreach (string name in before.Categories.Keys.Where(k => !after.Categories.ContainsKey(k)).Order(StringComparer.Ordinal))
            changes.Add(new(ContentChangeKind.CategoryRemoved, new(Category: name)));
        foreach ((string name, CategoryData category) in after.Categories.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (!before.Categories.TryGetValue(name, out CategoryData? previous))
            {
                changes.Add(new(ContentChangeKind.CategoryAdded, new(Category: name)));
                // 追加されたカテゴリも、空のカテゴリとの差として中身（プリセットの値・割り当て）を示す。壊れたカテゴリは解釈しない
                if (!category.IsDiagnosticOnly) CompareCategory(changes, name, new CategoryData(), category);
            }
            else
                CompareCategory(changes, name, previous, category);
        }
        if (!SameExtensions(before.Extensions, after.Extensions))
            changes.Add(new(ContentChangeKind.OtherDataChanged, new()));
    }

    private static void CompareCategory(List<ContentChange> changes, string name, CategoryData before, CategoryData after)
    {
        // 壊れたカテゴリは内容を解釈できないので、差があることだけを示す
        if (before.IsDiagnosticOnly || after.IsDiagnosticOnly)
        {
            if (before.StoredRepresentation != after.StoredRepresentation || before.IsDiagnosticOnly != after.IsDiagnosticOnly)
                changes.Add(new(ContentChangeKind.OtherDataChanged, new(Category: name)));
            return;
        }

        Dictionary<string, Preset> old = before.Presets.ToDictionary(p => p.Name);
        Dictionary<string, Preset> now = after.Presets.ToDictionary(p => p.Name);
        foreach (Preset preset in before.Presets.Where(p => !now.ContainsKey(p.Name)))
            changes.Add(new(ContentChangeKind.PresetRemoved, new(Category: name, Preset: preset.Name)));
        foreach (Preset preset in after.Presets)
        {
            if (!old.TryGetValue(preset.Name, out Preset? previous))
            {
                changes.Add(new(ContentChangeKind.PresetAdded, new(Category: name, Preset: preset.Name)));
                // 追加されたプリセットも、値のないプリセットとの差としてグループの使用状態とセルの値を示す
                ComparePreset(changes, name, new Preset { Name = preset.Name }, preset);
            }
            else
                ComparePreset(changes, name, previous, preset);
        }
        AddReorder(changes, ContentChangeKind.PresetsReordered, new(Category: name),
            before.Presets.Select(p => p.Name).ToArray(), after.Presets.Select(p => p.Name).ToArray());

        int count = Math.Max(before.Assignments.Count, after.Assignments.Count);
        for (int i = 0; i < count; i++)
        {
            Assignment? a = i < before.Assignments.Count ? before.Assignments[i] : null;
            Assignment? b = i < after.Assignments.Count ? after.Assignments[i] : null;
            ChangeLocation location = new(Category: name, Index: i);
            if (a is null)
                changes.Add(new(ContentChangeKind.AssignmentAdded, location) { AssignmentAfter = Snapshot(b!) });
            else if (b is null)
                changes.Add(new(ContentChangeKind.AssignmentRemoved, location) { AssignmentBefore = Snapshot(a) });
            else if (a.Preset != b.Preset || !SameConditions(a.Specs, b.Specs))
                changes.Add(new(ContentChangeKind.AssignmentChanged, location) { AssignmentBefore = Snapshot(a), AssignmentAfter = Snapshot(b) });
            else if (!SameExtensions(a.Extensions, b.Extensions))
                changes.Add(new(ContentChangeKind.OtherDataChanged, location));
        }

        if (!SameExtensions(before.Extensions, after.Extensions))
            changes.Add(new(ContentChangeKind.OtherDataChanged, new(Category: name)));
    }

    private static void ComparePreset(List<ContentChange> changes, string category, Preset before, Preset after)
    {
        PresetContent old = before.Content!;
        PresetContent now = after.Content!;
        IEnumerable<string> groups = old.Groups.Select(g => g.Key).Concat(now.Groups.Select(g => g.Key)).Distinct();
        foreach (string groupKey in groups)
        {
            PresetGroup? a = old.FindGroup(groupKey);
            PresetGroup? b = now.FindGroup(groupKey);
            ChangeLocation groupLocation = new(Category: category, Preset: after.Name, Group: groupKey);
            if (State(a) != State(b))
                changes.Add(new(ContentChangeKind.GroupStateChanged, groupLocation) { ItemsBefore = [State(a)], ItemsAfter = [State(b)] });

            IReadOnlyList<KeyValuePair<string, PresetRow>> oldRows = a is { IsUnused: false } ? a.Rows : [];
            IReadOnlyList<KeyValuePair<string, PresetRow>> newRows = b is { IsUnused: false } ? b.Rows : [];
            foreach (string rowKey in oldRows.Select(r => r.Key).Concat(newRows.Select(r => r.Key)).Distinct())
            {
                PresetRow? rowA = a is { IsUnused: false } ? a.FindRow(rowKey) : null;
                PresetRow? rowB = b is { IsUnused: false } ? b.FindRow(rowKey) : null;
                ChangeLocation rowLocation = groupLocation with { Row = rowKey };
                if (rowA is { IsSupported: false } || rowB is { IsSupported: false })
                {
                    if (rowA?.UnsupportedRepresentation != rowB?.UnsupportedRepresentation || rowA?.IsSupported != rowB?.IsSupported)
                        changes.Add(new(ContentChangeKind.RowChanged, rowLocation)
                        {
                            ItemsBefore = rowA is null ? [] : [rowA.UnsupportedRepresentation ?? "(row)"],
                            ItemsAfter = rowB is null ? [] : [rowB.UnsupportedRepresentation ?? "(row)"],
                        });
                    continue;
                }
                IEnumerable<string> columns = (rowA?.Cells.Select(c => c.Key) ?? []).Concat(rowB?.Cells.Select(c => c.Key) ?? []).Distinct();
                foreach (string column in columns)
                {
                    CellValue x = rowA?.GetCell(column) ?? CellValue.Missing;
                    CellValue y = rowB?.GetCell(column) ?? CellValue.Missing;
                    if (x != y)
                        changes.Add(new(ContentChangeKind.CellChanged, rowLocation with { Column = column }) { CellBefore = x, CellAfter = y });
                }
            }
        }
        if (!SameExtensions(before.Extensions, after.Extensions))
            changes.Add(new(ContentChangeKind.OtherDataChanged, new(Category: category, Preset: after.Name)));
    }

    private static void CompareLabels(List<ContentChange> changes, string schema,
        SchemaGroupDefinition before, SchemaGroupDefinition after)
    {
        ChangeLocation group = new(Schema: schema, Group: after.Key);
        if (before.Labels.Group.Canonical != after.Labels.Group.Canonical)
            AddDisplayChange(changes, group, before.Labels.Group, after.Labels.Group);
        foreach (string row in before.Rows.Intersect(after.Rows, StringComparer.Ordinal))
            if (before.Labels.Row(row).Canonical != after.Labels.Row(row).Canonical)
                AddDisplayChange(changes, group with { Row = row }, before.Labels.Row(row), after.Labels.Row(row));
        foreach (string column in before.Columns.Intersect(after.Columns, StringComparer.Ordinal))
        {
            LocalizedText oldName = before.Labels.Column(column);
            LocalizedText newName = after.Labels.Column(column);
            string? oldUnit = before.Labels.Unit(column);
            string? newUnit = after.Labels.Unit(column);
            if (oldName.Canonical != newName.Canonical || oldUnit != newUnit)
                changes.Add(new(ContentChangeKind.DisplayNamesChanged, group with { Column = column })
                {
                    ItemsBefore = [DisplayValue(oldName, oldUnit)],
                    ItemsAfter = [DisplayValue(newName, newUnit)],
                });
        }
    }

    private static void AddDisplayChange(List<ContentChange> changes, ChangeLocation location,
        LocalizedText before, LocalizedText after) =>
        changes.Add(new(ContentChangeKind.DisplayNamesChanged, location)
        {
            ItemsBefore = [DisplayValue(before, null)],
            ItemsAfter = [DisplayValue(after, null)],
        });

    private static string DisplayValue(LocalizedText name, string? unit) =>
        $"displayName: {(name.Canonical.Length == 0 ? "(none)" : name.Canonical.Replace('\n', ';'))}; " +
        $"unit: {(unit is null ? "(none)" : unit)}";

    private static string DefinitionItem(string key, LocalizedText name, string? unit) =>
        $"{key} ({DisplayValue(name, unit)})";

    private static string State(PresetGroup? group) => group is null ? "absent" : group.IsUnused ? "unused" : "used";

    private static void AddReorder(List<ContentChange> changes, ContentChangeKind kind, ChangeLocation location, string[] before, string[] after)
    {
        string[] commonBefore = before.Where(after.Contains).ToArray();
        string[] commonAfter = after.Where(before.Contains).ToArray();
        if (!commonBefore.SequenceEqual(commonAfter))
            changes.Add(new(kind, location) { ItemsBefore = before, ItemsAfter = after });
    }

    private static AssignmentSnapshot Snapshot(Assignment assignment) =>
        new(new Dictionary<string, string>(assignment.Specs, StringComparer.Ordinal), assignment.Preset);

    private static bool SameConditions(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out string? v) && v == p.Value);

    private static bool SameExtensions(ExtensionProperties a, ExtensionProperties b) =>
        a.Items.Count == b.Items.Count &&
        a.Items.OrderBy(i => i.Key, StringComparer.Ordinal).SequenceEqual(b.Items.OrderBy(i => i.Key, StringComparer.Ordinal));
}
