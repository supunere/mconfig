using System.Collections.ObjectModel;
using System.Numerics;
using Mconfig.Application.Assignments.Queries;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;
using Mconfig.Presentation.Common;

namespace Mconfig.Presentation.Combinations;

/// <summary>組み合わせの割り当て結果の種類。色に頼らず識別できるよう、View は種類ごとに文言も変える。</summary>
public enum OutcomeDisplayKind
{
    Assigned,
    Disabled,
    Unassigned,
    /// <summary>カテゴリの構造が壊れていて評価できない。</summary>
    Unavailable,
}

public sealed record OutcomeDisplay(OutcomeDisplayKind Kind, string? Preset);

public sealed record ColumnDisplay(string Key, string Label);

/// <summary>仕様値のフィルター。Selected が null なら「全て」（表示だけの選択で、データは変えない）。</summary>
public sealed record FilterDisplay(string Key, string Label, IReadOnlyList<string> Values, string? Selected);

public sealed record CombinationDisplayRow(IReadOnlyDictionary<string, string> Values, IReadOnlyList<OutcomeDisplay> Outcomes);

/// <summary>組み合わせの表。SelectedCategory は選んでいるセルのカテゴリ（値の表に出すカテゴリ）。</summary>
/// <summary>
/// 組み合わせの表。Rows は表示する位置の行だけをその場で求める一覧（全行を保持しない）。
/// TotalCount は絞り込む前の組み合わせの数、Large は <see cref="CombinationSpace.LargeThreshold"/> を超えること（表示と検証の費用を事前に知らせる）。
/// SpecColumns は未指定の条件の列だけ。各行の Values は、ルール移動や値の参照で使うため、指定済みの条件も含む。
/// </summary>
public sealed record CombinationTableDisplay(
    SessionVersion Version,
    IReadOnlyList<ColumnDisplay> SpecColumns,
    IReadOnlyList<ColumnDisplay> CategoryColumns,
    IReadOnlyList<CombinationDisplayRow> Rows,
    BigInteger TotalCount,
    int? SelectedIndex)
{
    public bool Large => TotalCount > CombinationSpace.LargeThreshold;

    /// <summary>行が多すぎて、先頭から int の最大値までの行だけを表示している。</summary>
    public bool Truncated { get; init; }

    public string? SelectedCategory { get; init; }

    /// <summary>絞り込んでいるか（「絞り込みを解除」を押せるか）。</summary>
    public bool Filtered { get; init; }
}

public sealed class FilterChangedEventArgs(string key, string? value) : EventArgs
{
    public string Key { get; } = key;

    /// <summary>null は「全て」。</summary>
    public string? Value { get; } = value;
}

/// <summary>
/// 表のセルの選択。Index は表示中の行、Category はカテゴリの列を選んだときのカテゴリで、仕様の列を選んだときは null
/// （直前のカテゴリを保つ）。
/// </summary>
public sealed class CellSelectedEventArgs(int? index, string? category) : EventArgs
{
    public int? Index { get; } = index;
    public string? Category { get; } = category;
}

public sealed class AssignmentNavigationEventArgs(string category, IReadOnlyDictionary<string, string> combination) : EventArgs
{
    public string Category { get; } = category;
    public IReadOnlyDictionary<string, string> Combination { get; } = combination;
}

/// <summary>組み合わせ一覧の View 契約。WinForms の型を公開しない。</summary>
public interface ICombinationListView
{
    event EventHandler<FilterChangedEventArgs>? FilterChanged;
    event EventHandler<CellSelectedEventArgs>? CellSelected;
    event EventHandler? FiltersCleared;
    event EventHandler? AssignmentRequested;

    void ShowFilters(IReadOnlyList<FilterDisplay> filters);
    void ShowCombinations(CombinationTableDisplay table);
}

/// <summary>
/// 一覧で選ばれている状態を比較表へ渡す文脈。Categories は表示順のカテゴリと表示名、SelectedPresets は選んだ組み合わせの
/// カテゴリごとの割り当て先。
/// SelectedCategory は値の表に出すカテゴリ（選んだセルのカテゴリ。まだ選んでいなければ先頭）。
/// </summary>
public sealed record CombinationContext(
    SessionVersion Version,
    IReadOnlyList<ColumnDisplay> Categories,
    IReadOnlyDictionary<string, string>? SelectedCombination,
    IReadOnlyDictionary<string, string> SelectedPresets)
{
    public string? SelectedCategory { get; init; }
}

/// <summary>
/// 組み合わせ一覧。一覧・フィルター・3 状態の表示は、同じ時点の E から投影した一つのリードモデルに基づく。
/// フィルターと選択は画面側の状態で、データや版を変えない。
/// 表はセル単位で選び、カテゴリの列を選ぶとそのカテゴリを値の表へ渡す。仕様の列を選んだときは直前のカテゴリを保つ。
/// </summary>
public sealed class CombinationListPresenter : QueryPresenter<CombinationListReadModel>
{
    private readonly ICombinationListView _view;
    private readonly GetCombinationsHandler _combinations;
    private readonly Dictionary<string, string?> _filters = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string>? _selected;
    private string? _category;
    private (CombinationListReadModel Model, string Filters, CombinationRows Rows)? _shown;
    private DisplayRows? _displayRows;

    public CombinationListPresenter(
        ICombinationListView view, EditSession session, IQueryRunner runner, IUiDispatcher ui, DisplayLanguage language)
        : base(session, runner, ui, language)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _combinations = new GetCombinationsHandler(session);
        _view.FilterChanged += OnFilterChanged;
        _view.CellSelected += OnCellSelected;
        _view.FiltersCleared += OnFiltersCleared;
        _view.AssignmentRequested += OnAssignmentRequested;
    }

    /// <summary>表示内容や選択が変わったとき（比較表の Presenter が受け取る）。</summary>
    public event EventHandler<CombinationContext>? ContextChanged;
    public event EventHandler<AssignmentNavigationEventArgs>? AssignmentNavigationRequested;

    private void OnAssignmentRequested(object? sender, EventArgs e)
    {
        if (_selected is null || _category is null) return;
        AssignmentNavigationRequested?.Invoke(this, new(_category,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_selected, StringComparer.Ordinal))));
    }

    protected override CombinationListReadModel Query() => _combinations.Handle(new GetCombinationsQuery());

    protected override SessionVersion VersionOf(CombinationListReadModel model) => model.Version;

    protected override void Render(CombinationListReadModel model)
    {
        // 定義から消えた軸・値のフィルターは「全て」に戻す
        foreach (string key in _filters.Keys.ToArray())
        {
            SpecAxis? axis = model.Spec.Axes.FirstOrDefault(a => a.Key == key);
            if (axis is null || (_filters[key] is { } value && !axis.Values.Contains(value))) _filters.Remove(key);
        }
        ShowTable(model);
    }

    private void ShowTable(CombinationListReadModel model)
    {
        _view.ShowFilters(model.Spec.Axes
            .Select(a => new FilterDisplay(a.Key, a.DisplayName.Resolve(Language.Code, a.Key), a.Values, _filters.GetValueOrDefault(a.Key)))
            .ToArray());
        CombinationRows rows = Shown(model).Rows;
        long? selectedIndex = _selected is null ? null : rows.IndexOf(_selected);
        if (selectedIndex is null || selectedIndex >= rows.Count) (_selected, selectedIndex) = (null, null);

        ColumnDisplay[] categories = model.Categories
            .Select(c => new ColumnDisplay(c, DefinitionLabels.Category(model.Schemas, c, Language.Code)))
            .ToArray();
        if (_category is null || !model.Categories.Contains(_category)) _category = model.Categories.FirstOrDefault();
        _view.ShowCombinations(new CombinationTableDisplay(
            model.Version,
            model.Spec.Axes.Where(a => _filters.GetValueOrDefault(a.Key) is null)
                .Select(a => new ColumnDisplay(a.Key, a.DisplayName.Resolve(Language.Code, a.Key))).ToArray(),
            categories,
            _displayRows!,
            model.Rows.TotalCount,
            (int?)selectedIndex)
        {
            SelectedCategory = _category,
            Filtered = _filters.Count > 0,
            Truncated = rows.Truncated,
        });
        RaiseContext(model, categories, selectedIndex is long index ? rows.At(index) : null);
    }

    /// <summary>
    /// 絞り込んだ一覧。リードモデルか絞り込みが変わったときだけ作り直し、選択だけの変更では使い回す。
    /// </summary>
    private (CombinationListReadModel Model, string Filters, CombinationRows Rows) Shown(
        CombinationListReadModel model)
    {
        string filters = string.Join("\u0001", _filters.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}"));
        if (_shown is { } shown && ReferenceEquals(shown.Model, model) && shown.Filters == filters) return shown;

        CombinationRows rows = model.Rows.Restrict(_filters);
        _shown = (model, filters, rows);
        // 同じ一覧のあいだは同じ行の一覧を渡し、View が選択だけを描き直せるようにする
        _displayRows = new DisplayRows(rows, model.Categories);
        return _shown.Value;
    }

    private static OutcomeDisplay Outcome(CombinationRow row, string category) =>
        !row.Outcomes.TryGetValue(category, out AssignmentOutcome outcome)
            ? new OutcomeDisplay(OutcomeDisplayKind.Unavailable, null)
            : outcome.Kind switch
            {
                AssignmentOutcomeKind.Assigned => new OutcomeDisplay(OutcomeDisplayKind.Assigned, outcome.PresetName),
                AssignmentOutcomeKind.Disabled => new OutcomeDisplay(OutcomeDisplayKind.Disabled, null),
                _ => new OutcomeDisplay(OutcomeDisplayKind.Unassigned, null),
            };

    private void RaiseContext(CombinationListReadModel model, IReadOnlyList<ColumnDisplay> categories, CombinationRow? selected)
    {
        Dictionary<string, string> selectedPresets = new(StringComparer.Ordinal);
        foreach (string category in model.Categories)
        {
            if (selected is not null && selected.Outcomes.TryGetValue(category, out AssignmentOutcome o) && o.PresetName is { } name)
                selectedPresets[category] = name;
        }
        ContextChanged?.Invoke(this, new CombinationContext(model.Version, categories, selected?.Values,
            new ReadOnlyDictionary<string, string>(selectedPresets))
        {
            SelectedCategory = _category,
        });
    }

    /// <summary>表示する位置の行だけを、求められたときに作る一覧（View の仮想表示から呼ばれる）。</summary>
    private sealed class DisplayRows(CombinationRows rows, IReadOnlyList<string> categories) : IReadOnlyList<CombinationDisplayRow>
    {
        public int Count => rows.Count;

        public CombinationDisplayRow this[int index]
        {
            get
            {
                CombinationRow row = rows[index];
                return new CombinationDisplayRow(row.Values, categories.Select(c => Outcome(row, c)).ToArray());
            }
        }

        public IEnumerator<CombinationDisplayRow> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // フィルターと選択は手元のリードモデルから表示し直すだけで、読み直さない（版もデータも変わらない）
    private void OnFilterChanged(object? sender, FilterChangedEventArgs e)
    {
        if (e.Value is null) _filters.Remove(e.Key);
        else _filters[e.Key] = e.Value;
        if (Current is not null) ShowTable(Current);
    }

    private void OnCellSelected(object? sender, CellSelectedEventArgs e)
    {
        if (Current is null) return;
        CombinationRows rows = Shown(Current).Rows;
        _selected = e.Index is int i && i >= 0 && i < rows.Count ? rows.Space.At(i) : null;
        if (e.Category is not null && Current.Categories.Contains(e.Category)) _category = e.Category;
        ShowTable(Current);
    }

    /// <summary>値の表に出すカテゴリを選ぶ（検証結果の該当箇所へ移るとき）。組み合わせの選択は保つ。</summary>
    public void SelectCategory(string category)
    {
        _category = category;
        if (Current is not null) ShowTable(Current);
    }

    private void OnFiltersCleared(object? sender, EventArgs e)
    {
        if (_filters.Count == 0) return;
        _filters.Clear();
        if (Current is not null) Render(Current);
    }

    protected override void OnDisposed()
    {
        _view.FilterChanged -= OnFilterChanged;
        _view.CellSelected -= OnCellSelected;
        _view.FiltersCleared -= OnFiltersCleared;
        _view.AssignmentRequested -= OnAssignmentRequested;
    }
}
