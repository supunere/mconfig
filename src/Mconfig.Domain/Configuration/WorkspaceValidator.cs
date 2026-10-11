using Mconfig.Domain.Definitions;

namespace Mconfig.Domain.Configuration;

/// <summary>検証結果。Args は表示文の組み立てに使う値（表示文そのものは App が作る）。</summary>
public sealed record ValidationIssue(ValidationCode Code, ValidationTarget Target, IReadOnlyList<string> Args);

/// <summary>
/// ワークスペース全体の検証規則。起動時・編集時・保存時・出力時で同じ規則を使い、
/// 操作ごとの重大度は <see cref="ValidationPolicy"/> で決める。
/// </summary>
public static class WorkspaceValidator
{
    /// <summary>検証規則の版。規則を変えたら上げる。警告の承認はこの版に紐付ける。</summary>
    public const string RuleVersion = "2026-09-30.1";

    public static IReadOnlyList<ValidationIssue> Validate(
        SettingsData settings, SpecDefinition spec, IReadOnlyList<SchemaDefinition> schemas)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(schemas);

        List<ValidationIssue> issues = [];
        ValidateSpec(issues, spec);
        HashSet<string> invalidSchemas = ValidateSchemas(issues, schemas);
        Dictionary<string, SchemaDefinition> schemaByName = schemas
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach ((string categoryName, CategoryData category) in settings.Categories)
        {
            int reported = issues.Count;
            ValidateCategory(issues, categoryName, category, schemaByName, invalidSchemas, spec);
            // 壊れたカテゴリは、詳しい理由が分からなくても必ずエラーとして示す（編集できないため）
            if (category.IsDiagnosticOnly && !issues.Skip(reported).Any(i => ValidationPolicy.SeverityFor(i.Code, ValidationOperation.Edit) == ValidationSeverity.Error))
                Add(issues, ValidationCode.InvalidStructure, new(categoryName));
        }

        // スキーマがあるのに設定にカテゴリがなければ、そのカテゴリの設定がまるごと欠けている
        foreach (SchemaDefinition schema in schemas)
        {
            if (!settings.Categories.ContainsKey(schema.Name))
                Add(issues, ValidationCode.MissingCategory, new(schema.Name));
        }

        return issues;
    }

    /// <summary>グループ・行・列のキーは参照先なので、重複を集約せず曖昧な定義として報告する。</summary>
    private static HashSet<string> ValidateSchemas(List<ValidationIssue> issues, IReadOnlyList<SchemaDefinition> schemas)
    {
        HashSet<string> invalid = new(StringComparer.Ordinal);
        foreach (SchemaDefinition schema in schemas)
        {
            foreach (string key in Duplicates(schema.Groups.Select(g => g.Key)))
            {
                Add(issues, ValidationCode.DuplicateSchemaGroup, new(schema.Name, Group: key), key);
                invalid.Add(schema.Name);
            }
            foreach (SchemaGroupDefinition group in schema.Groups)
            {
                foreach (string key in Duplicates(group.Rows))
                {
                    Add(issues, ValidationCode.DuplicateSchemaRow, new(schema.Name, Group: group.Key, Row: key), key);
                    invalid.Add(schema.Name);
                }
                foreach (string key in Duplicates(group.Columns))
                {
                    Add(issues, ValidationCode.DuplicateSchemaColumn, new(schema.Name, Group: group.Key, Column: key), key);
                    invalid.Add(schema.Name);
                }
            }
        }
        return invalid;
    }

    private static IEnumerable<string> Duplicates(IEnumerable<string> keys) => keys
        .GroupBy(k => k, StringComparer.Ordinal)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key);

    /// <summary>
    /// 仕様値の集合。空の集合は組み合わせの意味を変えるので、軸を黙って除かずエラーにする。
    /// 重複した値は曖昧な参照、any は任意値の予約値と衝突するのでエラー。仕様の誤りはカテゴリに属さない（Category は空）。
    /// </summary>
    private static void ValidateSpec(List<ValidationIssue> issues, SpecDefinition spec)
    {
        foreach (SpecAxis axis in spec.Axes)
        {
            if (axis.Values.Count == 0)
                Add(issues, ValidationCode.EmptySpecValues, new(""), axis.Key);
            HashSet<string> values = new(StringComparer.Ordinal);
            foreach (string value in axis.Values)
            {
                if (value == AssignmentEvaluation.AnyValue)
                    Add(issues, ValidationCode.ReservedSpecValue, new(""), axis.Key, value);
                else if (!values.Add(value))
                    Add(issues, ValidationCode.DuplicateSpecValue, new(""), axis.Key, value);
            }
        }
    }

    private static void ValidateCategory(
        List<ValidationIssue> issues, string categoryName, CategoryData category, Dictionary<string, SchemaDefinition> schemaByName,
        HashSet<string> invalidSchemas, SpecDefinition spec)
    {
        // 定義一式との対応はカテゴリ内部の破損とは独立して検証する。
        if (!schemaByName.TryGetValue(categoryName, out SchemaDefinition? schema))
        {
            Add(issues, ValidationCode.MissingSchema, new(categoryName));
            return;
        }
        if (invalidSchemas.Contains(categoryName)) return;

        // 壊れたカテゴリは、読めた範囲の内容で理由を詳しく示す。一覧や要素が欠けていれば構造の誤り
        IReadOnlyList<Preset?>? storedPresets = category.IsDiagnosticOnly ? category.Broken?.Presets : category.Presets;
        IReadOnlyList<Assignment?>? storedAssignments = category.IsDiagnosticOnly ? category.Broken?.Assignments : category.Assignments;
        if (storedPresets is null || storedAssignments is null ||
            storedPresets.Any(p => p?.Content is null) || storedAssignments.Any(a => a?.Specs is null))
        {
            Add(issues, ValidationCode.InvalidStructure, new(categoryName));
            return;
        }
        Preset[] presets = storedPresets.Select(p => p!).ToArray();
        Assignment[] assignments = storedAssignments.Select(a => a!).ToArray();

        HashSet<string> presetNames = new(StringComparer.Ordinal);
        foreach (Preset preset in presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name) || !presetNames.Add(preset.Name))
                Add(issues, ValidationCode.InvalidPresetName, new(categoryName, preset.Name), preset.Name);
        }

        foreach (Assignment assignment in assignments)
        {
            foreach ((ValidationCode code, string key, string? value) in AssignmentConditions.Problems(spec, assignment.Specs))
            {
                if (value is null) Add(issues, code, new(categoryName), key);
                else Add(issues, code, new(categoryName), key, value);
            }
            if (assignment.Preset != null && !presetNames.Contains(assignment.Preset))
                Add(issues, ValidationCode.MissingPreset, new(categoryName, assignment.Preset), assignment.Preset);
        }

        Dictionary<string, SchemaGroupDefinition> schemaGroups = schema.Groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        foreach (Preset preset in presets)
            ValidatePreset(issues, categoryName, preset, schemaGroups);
    }

    /// <summary>
    /// 割り当ての被覆。カテゴリごとに未カバーの組み合わせの件数と、
    /// 採用されない行を確認付き警告として返す。構造が壊れたカテゴリは Validate の側で扱うので除く。
    /// </summary>
    /// <remarks>
    /// 進捗は調べた組み合わせの数（カテゴリごとに数え、全体は組み合わせの数 × カテゴリの数）で、およそ
    /// <see cref="CoverageProgress.ReportInterval"/> 件ごとに知らせる。取消されたら途中の結果を返さず OperationCanceledException を送出する
    /// （途中取消は「検証未完了」であり、完了した検証と扱わない）。
    /// </remarks>
    public static IReadOnlyList<ValidationIssue> ValidateCoverage(
        SettingsData settings, SpecDefinition spec, CancellationToken cancellationToken = default, IProgress<CoverageProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(spec);

        // 組み合わせは保持せず、カテゴリごとに求め直す
        CombinationSpace space = CombinationSpace.Of(spec);
        int usable = settings.Categories.Values.Count(c => !c.IsDiagnosticOnly);
        CoverageCounter? counter = progress is null ? null : new CoverageCounter(progress, space.Count * usable);
        List<ValidationIssue> issues = [];
        foreach ((string categoryName, CategoryData category) in settings.Categories)
        {
            if (category.IsDiagnosticOnly) continue;

            IEnumerable<IReadOnlyDictionary<string, string>> combinations = space.Enumerate(cancellationToken);
            AssignmentCoverageReport report = AssignmentCoverage.Analyze(category.Assignments,
                counter is null ? combinations : counter.Count(combinations), cancellationToken);
            if (report.UncoveredCount > 0)
                Add(issues, ValidationCode.UncoveredCombinations, new(categoryName),
                    report.UncoveredCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (int row in report.UnreachableRows)
                Add(issues, ValidationCode.UnreachableAssignment, new(categoryName),
                    row.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        counter?.Complete();
        return issues;
    }

    /// <summary>調べた組み合わせを数え、一定の件数ごとに進捗を知らせる（全カテゴリで通して数える）。</summary>
    private sealed class CoverageCounter
    {
        private readonly IProgress<CoverageProgress> _progress;
        private readonly long _total;
        private long _checked;

        public CoverageCounter(IProgress<CoverageProgress> progress, System.Numerics.BigInteger total)
        {
            _progress = progress;
            _total = total > long.MaxValue ? long.MaxValue : (long)total;
            _progress.Report(new CoverageProgress(0, _total));
        }

        public IEnumerable<IReadOnlyDictionary<string, string>> Count(IEnumerable<IReadOnlyDictionary<string, string>> combinations)
        {
            foreach (IReadOnlyDictionary<string, string> combination in combinations)
            {
                yield return combination;
                if (++_checked % CoverageProgress.ReportInterval == 0) _progress.Report(new CoverageProgress(_checked, _total));
            }
        }

        public void Complete() => _progress.Report(new CoverageProgress(_total, _total));
    }

    private static void ValidatePreset(
        List<ValidationIssue> issues, string categoryName, Preset preset, Dictionary<string, SchemaGroupDefinition> schemaGroups)
    {
        foreach ((string groupKey, PresetGroup group) in preset.Content!.Groups)
        {
            if (!schemaGroups.TryGetValue(groupKey, out SchemaGroupDefinition? schemaGroup))
            {
                Add(issues, ValidationCode.OrphanGroup, new(categoryName, preset.Name, groupKey));
                continue;
            }
            if (group.IsUnused) continue;

            HashSet<string> schemaRows = schemaGroup.Rows.ToHashSet();
            HashSet<string> schemaColumns = schemaGroup.Columns.ToHashSet();

            foreach ((string rowKey, PresetRow row) in group.Rows)
            {
                if (!schemaRows.Contains(rowKey))
                {
                    Add(issues, ValidationCode.OrphanRow, new(categoryName, preset.Name, groupKey, rowKey));
                    continue;
                }
                if (!row.IsSupported)
                {
                    Add(issues, ValidationCode.UnsupportedRow, new(categoryName, preset.Name, groupKey, rowKey));
                    continue;
                }
                foreach (string columnKey in row.Cells.Select(c => c.Key))
                {
                    if (!schemaColumns.Contains(columnKey))
                        Add(issues, ValidationCode.OrphanColumn, new(categoryName, preset.Name, groupKey, rowKey, columnKey));
                }
                foreach (string columnKey in schemaColumns)
                {
                    CellEntryState state = CellEntry.Classify(groupUnused: false, row.GetCell(columnKey));
                    if (state == CellEntryState.Unsupported)
                        Add(issues, ValidationCode.UnsupportedCell, new(categoryName, preset.Name, groupKey, rowKey, columnKey));
                    else if (state == CellEntryState.Blank)
                        Add(issues, ValidationCode.BlankCell, new(categoryName, preset.Name, groupKey, rowKey, columnKey));
                }
            }

            HashSet<string> presentRows = group.Rows.Select(r => r.Key).ToHashSet();
            foreach (string rowKey in schemaRows)
            {
                if (!presentRows.Contains(rowKey))
                    Add(issues, ValidationCode.BlankRow, new(categoryName, preset.Name, groupKey, rowKey));
            }
        }

        // 必要なグループの欠落は、明示的な未使用（null）と区別する
        foreach (string groupKey in schemaGroups.Keys)
        {
            if (preset.Content.FindGroup(groupKey) is null)
                Add(issues, ValidationCode.MissingGroup, new(categoryName, preset.Name, groupKey));
        }
    }

    private static void Add(List<ValidationIssue> issues, ValidationCode code, ValidationTarget target, params string[] args) =>
        issues.Add(new ValidationIssue(code, target, args));
}
