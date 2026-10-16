using Mconfig.Application.Assignments.Commands;
using Mconfig.Application.Assignments.Queries;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Application.Workspaces.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.Combinations;
using Mconfig.Presentation.Common;

namespace Mconfig.Presentation.Assignments;

/// <summary>条件の列（仕様の軸）。Values は仕様値で、任意（any）は含めない（View が別の文言で示す）。</summary>
public sealed record ConditionColumnDisplay(string Key, string Label, IReadOnlyList<string> Values);

/// <summary>
/// 割り当て行。Conditions は条件の列と同じ順の値（省略は any）。Preset が null の行は無効（Disabled）。
/// Unreachable は上位の行に覆われて使われない行（全組み合わせの評価による）。
/// AppliedCount はこの行が使われる組み合わせの数、Unsaved は一時保存の時点になかった（追加・変更した）行。
/// </summary>
public sealed record AssignmentRowDisplay(IReadOnlyList<string> Conditions, string? Preset, bool Unreachable)
{
    /// <summary>組み合わせが多すぎて数えていなければ null。</summary>
    public int? AppliedCount { get; init; }
    public bool Unsaved { get; init; }
}

/// <summary>カテゴリの一覧の 1 行。問題（未割当・使われない行）と未保存の変更を示す。Available が false なら壊れていて表示できない。</summary>
/// <remarks>Unassigned と UnreachableRows は、組み合わせが多すぎて数えていなければ null。</remarks>
public sealed record CategoryItemDisplay(string Key, string Label, bool Available, int? Unassigned, int? UnreachableRows, bool Unsaved);

/// <summary>
/// 割り当て先の変化の 1 行（一時保存の時点 → いま）。Values は条件の列の順、DecidingRow はいまの結果を決めた行の順位（1 始まり）。
/// </summary>
public sealed record AssignmentChangeDisplay(IReadOnlyList<string> Values, OutcomeDisplay Before, OutcomeDisplay After, int? DecidingRow, bool Changed);

/// <summary>カテゴリの割り当て結果の集計。Total は組み合わせの数。</summary>
public sealed record AssignmentTotalsDisplay(int Total, int Assigned, int Disabled, int Unassigned, int UnreachableRows);

public sealed record AssignmentTableDisplay(
    SessionVersion Version,
    string Category,
    bool Available,
    IReadOnlyList<ConditionColumnDisplay> Columns,
    IReadOnlyList<string> Presets,
    IReadOnlyList<AssignmentRowDisplay> Rows,
    bool Editable,
    int? SelectedRow)
{
    public string CategoryLabel { get; init; } = Category;
    public bool RuleNavigation { get; init; }
    public bool RuleNotFound { get; init; }

    /// <summary>変わる組み合わせの一覧。ShowAllChanges なら全組み合わせ、そうでなければ変わるものだけ。</summary>
    public IReadOnlyList<AssignmentChangeDisplay> Changes { get; init; } = [];
    public bool ShowAllChanges { get; init; }
    public int ChangedCount { get; init; }
    public AssignmentTotalsDisplay Totals { get; init; } = new(0, 0, 0, 0, 0);

    /// <summary>組み合わせが多すぎるため、適用件数・変わる組み合わせ・集計を求めていない（全件の確認は検証で行う）。</summary>
    public bool ImpactSkipped { get; init; }

    /// <summary>組み合わせの数（表示用の文字列）。</summary>
    public string CombinationCount { get; init; } = "0";
}

/// <summary>変わる組み合わせの表示範囲の切り替え。ShowAll なら全組み合わせ。</summary>
public sealed class ChangeScopeEventArgs(bool showAll) : EventArgs
{
    public bool ShowAll { get; } = showAll;
}

public sealed class ConditionChangedEventArgs(int row, string key, string value) : EventArgs
{
    public int Row { get; } = row;
    public string Key { get; } = key;
    public string Value { get; } = value;
}

/// <summary>割り当て先の変更。Preset が null なら無効にする。</summary>
public sealed class AssignmentPresetChangedEventArgs(int row, string? preset) : EventArgs
{
    public int Row { get; } = row;
    public string? Preset { get; } = preset;
}

public sealed class AssignmentRowEventArgs(int row) : EventArgs
{
    public int Row { get; } = row;
}

/// <summary>優先順位の変更。From の行を To の位置へ移す。</summary>
public sealed class AssignmentMovedEventArgs(int from, int to) : EventArgs
{
    public int From { get; } = from;
    public int To { get; } = to;
}

/// <summary>割り当て画面の View 契約。WinForms の型を公開しない。</summary>
public interface IAssignmentView
{
    event EventHandler<CategorySelectedEventArgs>? CategorySelected;
    event EventHandler<ConditionChangedEventArgs>? ConditionChanged;
    event EventHandler<AssignmentPresetChangedEventArgs>? PresetChanged;
    event EventHandler? RowAdded;
    event EventHandler<AssignmentRowEventArgs>? RowRemoved;
    event EventHandler<AssignmentMovedEventArgs>? RowMoved;

    void ShowCategories(IReadOnlyList<CategoryItemDisplay> categories, string? selected);
    void ShowAssignments(AssignmentTableDisplay table);
    void ShowEditFeedback(EditFeedback feedback);
}

/// <summary>
/// 割り当て画面。条件・割り当て先・優先順位の変更と行の追加・削除を、表示中の版を期待版とする Command にする。
/// any（任意）・無効・未割当を区別し、上位の行に覆われる行を示す。成功すれば変更通知で読み直し、失敗すれば表示を戻して理由を示す。
/// カテゴリの一覧に各カテゴリの問題と未保存の変更を、行ごとに適用される組み合わせの数を示し、
/// 一時保存の時点と比べて割り当て先が変わる組み合わせを、変更前・変更後・決め手の行とともに示す。
/// </summary>
public sealed class AssignmentPresenter : QueryPresenter<AssignmentListReadModel>
{
    private readonly IAssignmentView _view;
    private readonly GetAssignmentsHandler _assignments;
    private readonly GetWorkspaceStatusHandler _status;
    private readonly AssignmentCommandHandlers _commands;
    private string? _category;
    private int? _selectedRow;
    private IReadOnlyDictionary<string, string>? _navigationCombination;
    private readonly TimeSpan _changeDelay;

    public AssignmentPresenter(IAssignmentView view, EditSession session, IQueryRunner runner, IUiDispatcher ui, DisplayLanguage language,
        TimeSpan? changeDelay = null)
        : base(session, runner, ui, language)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _changeDelay = changeDelay ?? TimeSpan.FromMilliseconds(300);
        _assignments = new GetAssignmentsHandler(session);
        _status = new GetWorkspaceStatusHandler(session);
        _commands = new AssignmentCommandHandlers(session);
        _view.CategorySelected += OnCategorySelected;
        _view.ConditionChanged += OnConditionChanged;
        _view.PresetChanged += OnPresetChanged;
        _view.RowAdded += OnRowAdded;
        _view.RowRemoved += OnRowRemoved;
        _view.RowMoved += OnRowMoved;
    }

    public Task SelectAsync(string? category)
    {
        _category = category;
        _selectedRow = null;
        _navigationCombination = null;
        return RefreshAsync();
    }

    /// <summary>画面切り替え時の保存・破棄が済んだ最新のルールから、該当行を探す。</summary>
    public Task SelectForCombinationAsync(string category, IReadOnlyDictionary<string, string> combination)
    {
        _category = category;
        _selectedRow = null;
        _navigationCombination = combination;
        return RefreshAsync();
    }

    protected override TimeSpan ChangeDelay => _changeDelay;

    protected override AssignmentListReadModel Query() => Query(CancellationToken.None);

    protected override AssignmentListReadModel Query(CancellationToken cancellationToken)
        => _assignments.Handle(new GetAssignmentsQuery(_category ?? "", SelectFirstIfMissing: true), cancellationToken);

    protected override SessionVersion VersionOf(AssignmentListReadModel model) => model.Version;

    protected override void Render(AssignmentListReadModel model)
    {
        bool navigating = _navigationCombination is not null;
        bool ruleNotFound = false;
        if (_navigationCombination is { } combination)
        {
            int matchingRow = model.Available && model.Category == _category
                ? AssignmentEvaluation.FindFirstMatchingRow(model.Rows.Select(r =>
                    new Assignment { Specs = r.Conditions, Preset = r.Preset }).ToArray(), combination)
                : -1;
            _selectedRow = matchingRow >= 0 ? matchingRow : null;
            ruleNotFound = matchingRow < 0;
            _navigationCombination = null;
        }
        if (_category != model.Category)
        {
            _category = model.Category;
            _selectedRow = null;
        }
        _view.ShowCategories(model.Categories
            .Select(c =>
            {
                AssignmentCategorySummary? summary = model.Summaries.FirstOrDefault(x => x.Category == c);
                return new CategoryItemDisplay(c, LabelOf(model, c), summary?.Available ?? false,
                    summary?.Unassigned, summary?.UnreachableRows, summary?.HasUnsavedChanges ?? false);
            })
            .ToArray(), model.Categories.Contains(model.Category) ? model.Category : null);

        ConditionColumnDisplay[] columns = model.Spec.Axes
            .Select(a => new ConditionColumnDisplay(a.Key, a.DisplayName.Resolve(Language.Code, a.Key), a.Values))
            .ToArray();
        int? selected = _selectedRow is int row && row < model.Rows.Count ? row : null;
        _view.ShowAssignments(new AssignmentTableDisplay(
            model.Version,
            model.Category,
            model.Available,
            columns,
            model.Presets,
            model.Rows.Select(r => new AssignmentRowDisplay(
                columns.Select(c => r.Conditions.GetValueOrDefault(c.Key, AssignmentEvaluation.AnyValue)).ToArray(),
                r.Preset,
                r.Unreachable)
            {
                AppliedCount = r.AppliedCount,
                Unsaved = r.Unsaved,
            }).ToArray(),
            model.Available && Editing.IsAllowed(_status.Handle()),
            selected)
        {
            CategoryLabel = LabelOf(model, model.Category),
            RuleNavigation = navigating,
            RuleNotFound = ruleNotFound,
            Changes = new ChangeRows(model.Changes, columns, false),
            ShowAllChanges = false,
            ChangedCount = model.Changes.Count(c => c.Changed),
            Totals = new AssignmentTotalsDisplay(model.Changes.Count,
                model.Totals.Assigned, model.Totals.Disabled, model.Totals.Unassigned, model.Totals.UnreachableRows),
            ImpactSkipped = model.ImpactSkipped,
            CombinationCount = model.CombinationCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    // 影響一覧も表示を求められた行だけ翻訳する。50,000 件分の表示用配列を UI 上で作らない。
    private sealed class ChangeRows(IReadOnlyList<AssignmentChange> changes, IReadOnlyList<ConditionColumnDisplay> columns, bool all)
        : IReadOnlyList<AssignmentChangeDisplay>
    {
        private readonly int[]? _indexes = all ? null : Enumerable.Range(0, changes.Count).Where(i => changes[i].Changed).ToArray();
        public int Count => _indexes?.Length ?? changes.Count;
        public AssignmentChangeDisplay this[int index]
        {
            get
            {
                AssignmentChange c = changes[_indexes is null ? index : _indexes[index]];
                return new AssignmentChangeDisplay(columns.Select(x => c.Combination.GetValueOrDefault(x.Key, "")).ToArray(),
                    Outcome(c.Before), Outcome(c.After), c.DecidingRow + 1, c.Changed);
            }
        }
        public IEnumerator<AssignmentChangeDisplay> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private string LabelOf(AssignmentListReadModel model, string category) =>
        DefinitionLabels.Category(model.Schemas, category, Language.Code);

    private static OutcomeDisplay Outcome(AssignmentOutcome outcome) => outcome.Kind switch
    {
        AssignmentOutcomeKind.Assigned => new OutcomeDisplay(OutcomeDisplayKind.Assigned, outcome.PresetName),
        AssignmentOutcomeKind.Disabled => new OutcomeDisplay(OutcomeDisplayKind.Disabled, null),
        _ => new OutcomeDisplay(OutcomeDisplayKind.Unassigned, null),
    };

    private void OnCategorySelected(object? sender, CategorySelectedEventArgs e) => _ = SelectAsync(e.Category);

    /// <summary>条件を変える。もともと省略していた条件を任意にするときは省略のまま（保存表現を変えない）。</summary>
    private void OnConditionChanged(object? sender, ConditionChangedEventArgs e) => Change(e.Row, row =>
    {
        Dictionary<string, string> conditions = new(row.Conditions, StringComparer.Ordinal);
        if (e.Value == AssignmentEvaluation.AnyValue && !conditions.ContainsKey(e.Key)) return null;
        conditions[e.Key] = e.Value;
        return new AssignmentRow(conditions, row.Preset);
    });

    private void OnPresetChanged(object? sender, AssignmentPresetChangedEventArgs e) =>
        Change(e.Row, row => new AssignmentRow(row.Conditions, e.Preset));

    private void Change(int index, Func<AssignmentListRow, AssignmentRow?> change)
    {
        if (Current is not { Available: true } model || index < 0 || index >= model.Rows.Count) return;
        _selectedRow = index;
        AssignmentRow? row = change(model.Rows[index]);
        if (row is null)
        {
            Render(model);
            return;
        }
        Execute(model, _commands.Handle(new ChangeAssignmentCommand(model.Category, index, row, model.Version)));
    }

    /// <summary>行を末尾に追加する。条件はすべて任意、割り当て先は無効で作り、利用者が選び直す。</summary>
    private void OnRowAdded(object? sender, EventArgs e)
    {
        if (Current is not { Available: true } model) return;
        Dictionary<string, string> conditions = model.Spec.Axes.ToDictionary(a => a.Key, _ => AssignmentEvaluation.AnyValue, StringComparer.Ordinal);
        _selectedRow = model.Rows.Count;
        Execute(model, _commands.Handle(new AddAssignmentCommand(model.Category, new AssignmentRow(conditions, null), model.Version)));
    }

    private void OnRowRemoved(object? sender, AssignmentRowEventArgs e)
    {
        if (Current is not { Available: true } model) return;
        _selectedRow = e.Row > 0 ? e.Row - 1 : null;
        Execute(model, _commands.Handle(new RemoveAssignmentCommand(model.Category, e.Row, model.Version)));
    }

    private void OnRowMoved(object? sender, AssignmentMovedEventArgs e)
    {
        if (Current is not { Available: true } model) return;
        if (e.To < 0 || e.To >= model.Rows.Count) return;
        _selectedRow = e.To;
        Execute(model, _commands.Handle(new MoveAssignmentCommand(model.Category, e.From, e.To, model.Version)));
    }

    private void Execute(AssignmentListReadModel model, CommandResult result)
    {
        if (result.Status == CommandStatus.Applied) return;
        // 変わらなかった・失敗したので、選んだ値を表示から消して元に戻す
        Render(model);
        if (!Editing.Succeeded(result)) _view.ShowEditFeedback(EditFeedback.From(result));
    }

    protected override void OnDisposed()
    {
        _view.CategorySelected -= OnCategorySelected;
        _view.ConditionChanged -= OnConditionChanged;
        _view.PresetChanged -= OnPresetChanged;
        _view.RowAdded -= OnRowAdded;
        _view.RowRemoved -= OnRowRemoved;
        _view.RowMoved -= OnRowMoved;
    }
}
