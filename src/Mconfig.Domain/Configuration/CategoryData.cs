using System.Diagnostics.CodeAnalysis;
using Mconfig.Domain.Definitions;

namespace Mconfig.Domain.Configuration;

/// <summary>
/// カテゴリ。プリセットと順序付きの割り当てを所有し、
/// 業務操作を通じてだけ変更する。
/// 保存内容が壊れていたカテゴリは診断専用（<see cref="IsDiagnosticOnly"/>）で、編集できず、業務の一覧（Presets・Assignments）は空になる。
/// 壊れた元の内容は <see cref="Broken"/> から、欠落や null を含みうる型で読む（利用側が個別の null 判定を忘れて異常終了しないようにする）。
/// </summary>
public sealed class CategoryData
{
    // Corrupt persisted content may omit the lists entirely, so the fields stay nullable
    // until ValidateSnapshot proves otherwise.
    private readonly List<Preset>? _presets;
    private List<Assignment>? _assignments;
    private readonly bool _diagnosticOnly;

    /// <summary>業務で使えるプリセット。診断専用のカテゴリでは空（null や null の要素を含まない）。</summary>
    public IReadOnlyList<Preset> Presets { get; }

    /// <summary>業務で使える割り当て（上から優先）。診断専用のカテゴリでは空（null や null の要素を含まない）。</summary>
    public IReadOnlyList<Assignment> Assignments { get; private set; }

    /// <summary>保存表現にあるが業務モデルが解釈しないプロパティ。</summary>
    public ExtensionProperties Extensions { get; private init; } = ExtensionProperties.None;

    /// <summary>
    /// 業務モデルへ正しく写せなかった元の保存表現（診断用のカテゴリだけが持つ）。診断用のカテゴリは編集できないので、
    /// 保存側はこれをそのまま書き戻し、壊れた内容を失わない。
    /// </summary>
    public string? StoredRepresentation { get; private init; }

    /// <summary>保存内容が壊れているため、編集・評価に使えないカテゴリ。これだけで判断し、一覧の null 判定はしない。</summary>
    public bool IsDiagnosticOnly => _diagnosticOnly;

    /// <summary>診断専用のカテゴリの、壊れたまま読んだ内容（検証の報告や指紋に使う）。使えるカテゴリでは null。</summary>
    public BrokenCategoryContent? Broken { get; }

    public CategoryData() : this(Array.Empty<Preset>(), Array.Empty<Assignment>()) { }

    public CategoryData(IEnumerable<Preset> presets, IEnumerable<Assignment> assignments)
        : this(presets, assignments, diagnosticOnly: false)
    {
        ValidateSnapshot(_presets, _assignments);
    }

    private CategoryData(IEnumerable<Preset>? presets, IEnumerable<Assignment>? assignments, bool diagnosticOnly)
    {
        _presets = presets?.ToList();
        _assignments = assignments?.ToList();
        _diagnosticOnly = diagnosticOnly;
        if (diagnosticOnly)
        {
            Presets = Array.Empty<Preset>();
            Assignments = Array.Empty<Assignment>();
            Broken = new BrokenCategoryContent(
                _presets is null ? null : Array.AsReadOnly(_presets.ToArray()),
                _assignments is null ? null : Array.AsReadOnly(_assignments.ToArray()));
        }
        else
        {
            Presets = (_presets?.AsReadOnly())!;
            Assignments = (_assignments?.AsReadOnly())!;
        }
    }

    // Corrupt persisted content remains available to diagnostics, but cannot be edited.
    public static CategoryData ForDiagnostics(
        IEnumerable<Preset>? presets, IEnumerable<Assignment>? assignments, string? storedRepresentation = null) =>
        new(presets, assignments, diagnosticOnly: true) { StoredRepresentation = storedRepresentation };

    /// <summary>
    /// 保存されていた内容から作る。壊れていれば編集できない診断用のカテゴリにし、元の保存表現を持たせる。
    /// 壊れていなければ保存表現は持たない（書き戻しは業務モデルから行う）。
    /// </summary>
    public static CategoryData FromStored(
        IEnumerable<Preset>? presets, IEnumerable<Assignment>? assignments,
        ExtensionProperties? extensions = null, string? storedRepresentation = null)
    {
        CategoryData category = new(presets, assignments, diagnosticOnly: false) { Extensions = extensions ?? ExtensionProperties.None };
        try
        {
            ValidateSnapshot(category._presets, category._assignments);
            return category;
        }
        catch (PresetOperationException)
        {
            return new CategoryData(category._presets, category._assignments, diagnosticOnly: true)
            {
                Extensions = category.Extensions,
                StoredRepresentation = storedRepresentation,
            };
        }
    }

    /// <summary>
    /// いまの内容の写し（参照用）。プリセットの値と割り当て行は不変なので一覧だけを写し、
    /// 元のカテゴリをその後に編集しても写しは変わらない。診断専用のカテゴリは壊れた内容と保存表現を引き継ぐ。
    /// </summary>
    public CategoryData Snapshot() =>
        new(_presets, _assignments, _diagnosticOnly) { Extensions = Extensions, StoredRepresentation = StoredRepresentation };

    private void EnsureEditable()
    {
        if (_diagnosticOnly)
            throw new PresetOperationException(PresetOperationFailure.InvalidCategory);
    }

    // Returning normally proves both lists are present, so callers may use them unguarded.
    private static HashSet<string> ValidateSnapshot(
        [NotNull] IReadOnlyList<Preset>? presets, [NotNull] IReadOnlyList<Assignment>? assignments)
    {
        if (presets == null || assignments == null)
            throw new PresetOperationException(PresetOperationFailure.InvalidCategory);

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Preset preset in presets)
        {
            if (preset == null || preset.Content == null ||
                string.IsNullOrWhiteSpace(preset.Name) || !names.Add(preset.Name))
                throw new PresetOperationException(PresetOperationFailure.InvalidCategory);
        }
        if (assignments.Any(a => a == null || a.Specs == null ||
            (a.Preset != null && !names.Contains(a.Preset))))
            throw new PresetOperationException(PresetOperationFailure.InvalidCategory);
        return names;
    }

    public void AddPreset(Preset preset)
    {
        EnsureEditable();
        HashSet<string> names = ValidateSnapshot(_presets, _assignments);
        string name = preset?.Name?.Trim() ?? "";
        if (preset == null || name.Length == 0)
            throw new PresetOperationException(PresetOperationFailure.InvalidName);
        if (names.Contains(name))
            throw new PresetOperationException(PresetOperationFailure.DuplicateName);
        if (preset.Content == null)
            throw new PresetOperationException(PresetOperationFailure.InvalidCategory);

        // 値は不変なので共有しても元のプリセットには影響しない
        _presets.Add(new Preset { Name = name, Content = preset.Content, Extensions = preset.Extensions });
    }

    /// <summary>
    /// 既存のプリセットを別名で複製する。値は不変なので、複製を編集しても複製元は変わらない。
    /// 名前の扱いと拒否条件は作成（AddPreset）と同じ。未知プロパティは元の要素のものなので複製しない。
    /// </summary>
    public void DuplicatePreset(string sourceName, string? newName)
    {
        EnsureEditable();
        ValidateSnapshot(_presets, _assignments);
        Preset source = _presets.Find(p => string.Equals(p.Name, sourceName, StringComparison.Ordinal))
            ?? throw new PresetOperationException(PresetOperationFailure.MissingPreset);

        AddPreset(new Preset { Name = newName ?? "", Content = source.Content });
    }

    /// <summary>プリセットを削除する。割り当てから参照されている間は削除しない。</summary>
    public void DeletePreset(string name)
    {
        EnsureEditable();
        ValidateSnapshot(_presets, _assignments);
        int index = _presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (index < 0)
            throw new PresetOperationException(PresetOperationFailure.MissingPreset);
        if (_assignments.Any(a => string.Equals(a.Preset, name, StringComparison.Ordinal)))
            throw new PresetOperationException(PresetOperationFailure.InUse);

        _presets.RemoveAt(index);
    }

    /// <summary>
    /// プリセットのセルの値を変える。行がまだなければ作る。グループが未使用、
    /// または行・セルが編集できない構造なら変更しない（CellNotEditable）。
    /// </summary>
    public void ChangeCell(string presetName, string groupKey, string rowKey, string columnKey, CellValue value) =>
        ChangeCells([new CellChange(presetName, groupKey, rowKey, columnKey, value)]);

    /// <summary>
    /// 複数のセルの値をまとめて変える。すべての変更後の内容を作ってから差し替えるので、
    /// どれか一つでも変更できなければ何も変えない。
    /// </summary>
    public void ChangeCells(IReadOnlyList<CellChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        EnsureEditable();
        ValidateSnapshot(_presets, _assignments);

        Dictionary<int, PresetContent> changed = [];
        foreach (CellChange change in changes)
        {
            int index = _presets.FindIndex(p => string.Equals(p.Name, change.Preset, StringComparison.Ordinal));
            if (index < 0)
                throw new PresetOperationException(PresetOperationFailure.MissingPreset);
            PresetContent current = changed.TryGetValue(index, out PresetContent? pending) ? pending : _presets[index].Content!;
            changed[index] = current.WithCell(change.Group, change.Row, change.Column, change.Value);
        }

        foreach ((int index, PresetContent content) in changed)
        {
            Preset target = _presets[index];
            _presets[index] = new Preset { Name = target.Name, Content = content, Extensions = target.Extensions };
        }
    }

    /// <summary>
    /// 割り当て行を末尾（最も低い優先順位）に加える。条件は仕様と照合し、仕様にないキー・値（未知キーの any を含む）は
    /// 拒否する。割り当て先は存在するプリセットか null（Disabled）でなければならない。
    /// </summary>
    public void AddAssignment(IReadOnlyDictionary<string, string> conditions, string? preset, SpecDefinition spec)
    {
        EnsureEditable();
        HashSet<string> names = ValidateSnapshot(_presets, _assignments);
        Assignment added = NewAssignment(conditions, preset, ExtensionProperties.None, spec, names);
        SetAssignments([.. _assignments, added]);
    }

    /// <summary>
    /// 割り当て行の条件と割り当て先を変える。規則は追加と同じ。保存表現の未知プロパティは同じ行のものとして引き継ぐ。
    /// </summary>
    public void ChangeAssignment(int index, IReadOnlyDictionary<string, string> conditions, string? preset, SpecDefinition spec)
    {
        EnsureEditable();
        HashSet<string> names = ValidateSnapshot(_presets, _assignments);
        EnsureAssignmentIndex(_assignments, index);
        List<Assignment> rows = [.. _assignments];
        rows[index] = NewAssignment(conditions, preset, _assignments[index].Extensions, spec, names);
        SetAssignments(rows);
    }

    public void RemoveAssignment(int index)
    {
        EnsureEditable();
        ValidateSnapshot(_presets, _assignments);
        EnsureAssignmentIndex(_assignments, index);
        List<Assignment> rows = [.. _assignments];
        rows.RemoveAt(index);
        SetAssignments(rows);
    }

    /// <summary>割り当て行の優先順位を変える（from の行を to の位置へ移す）。</summary>
    public void MoveAssignment(int from, int to)
    {
        EnsureEditable();
        ValidateSnapshot(_presets, _assignments);
        EnsureAssignmentIndex(_assignments, from);
        EnsureAssignmentIndex(_assignments, to);
        List<Assignment> rows = [.. _assignments];
        Assignment moved = rows[from];
        rows.RemoveAt(from);
        rows.Insert(to, moved);
        SetAssignments(rows);
    }

    private static Assignment NewAssignment(
        IReadOnlyDictionary<string, string> conditions, string? preset, ExtensionProperties extensions,
        SpecDefinition spec, HashSet<string> presetNames)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (conditions is null || conditions.Any(c => c.Key is null || c.Value is null))
            throw new PresetOperationException(PresetOperationFailure.InvalidCondition);
        if (AssignmentConditions.Problems(spec, conditions).Any())
            throw new PresetOperationException(PresetOperationFailure.InvalidCondition);
        if (preset != null && !presetNames.Contains(preset))
            throw new PresetOperationException(PresetOperationFailure.MissingPreset);
        return new Assignment { Specs = conditions, Preset = preset, Extensions = extensions };
    }

    private static void EnsureAssignmentIndex(IReadOnlyList<Assignment> rows, int index)
    {
        if (index < 0 || index >= rows.Count)
            throw new PresetOperationException(PresetOperationFailure.MissingAssignment);
    }

    // Assignment は不変なので、行はそのまま保持してよい
    private void SetAssignments(List<Assignment> rows)
    {
        _assignments = rows;
        Assignments = rows.AsReadOnly();
    }

    [MemberNotNull(nameof(_presets), nameof(_assignments))]
    public PresetRenameResult PreviewRenamePreset(string oldName, string? proposedName)
    {
        EnsureEditable();
        string newName = proposedName?.Trim() ?? "";
        if (newName.Length == 0)
            throw new PresetOperationException(PresetOperationFailure.InvalidName);

        HashSet<string> names = ValidateSnapshot(_presets, _assignments);
        if (!names.Contains(oldName))
            throw new PresetOperationException(PresetOperationFailure.MissingPreset);
        if (!string.Equals(oldName, newName, StringComparison.Ordinal) && names.Contains(newName))
            throw new PresetOperationException(PresetOperationFailure.DuplicateName);

        int referenceCount = _assignments.Count(a =>
            string.Equals(a.Preset, oldName, StringComparison.Ordinal));
        return new PresetRenameResult(
            !string.Equals(oldName, newName, StringComparison.Ordinal), newName, referenceCount);
    }

    public PresetRenameResult RenamePreset(string oldName, string? proposedName)
    {
        PresetRenameResult assessment = PreviewRenamePreset(oldName, proposedName);
        if (!assessment.Changed) return assessment;

        int targetIndex = _presets.FindIndex(p => string.Equals(p.Name, oldName, StringComparison.Ordinal));
        Preset target = _presets[targetIndex];
        Preset renamed = new() { Name = assessment.NewName, Content = target.Content, Extensions = target.Extensions };
        (int index, Assignment replacement)[] references = _assignments
            .Select((assignment, index) => (assignment, index))
            .Where(item => string.Equals(item.assignment.Preset, oldName, StringComparison.Ordinal))
            .Select(item => (item.index, new Assignment
            {
                Specs = item.assignment.Specs,
                Preset = assessment.NewName,
                Extensions = item.assignment.Extensions,
            }))
            .ToArray();

        // Construct every replacement before modifying the category.
        _presets[targetIndex] = renamed;
        foreach ((int index, Assignment replacement) in references)
            _assignments[index] = replacement;
        return assessment;
    }
}
