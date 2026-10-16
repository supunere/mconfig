using Mconfig.Application.Presets.Commands;
using Mconfig.Application.Presets.Queries;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Application.Workspaces.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;
using Mconfig.Presentation.Common;

namespace Mconfig.Presentation.Combinations;

public sealed record PresetColumnDisplay(string Name, bool AssignedToSelection);

/// <summary>
/// 比較表のセル。Missing は未入力（欠落または null）、DiffersFromSelectedPreset は選択中のプリセットと値が違う（未使用は比べない）。
/// SavedText は一時保存した時点の値の表示で、未保存の変更があるセルだけが持つ（未入力だったときは空文字）。
/// </summary>
public sealed record ComparisonCellDisplay(string Text, ComparisonCellState State, bool Missing, bool DiffersFromSelectedPreset)
{
    public string? SavedText { get; init; }
    public bool Unsaved => SavedText is not null;
}

public sealed record ComparisonRowDisplay(
    string GroupLabel, string RowLabel, string ColumnLabel, string? Unit, bool HasDifferences, IReadOnlyList<ComparisonCellDisplay> Cells);

/// <summary>
/// プリセット比較の表示。初期表示は全件で、利用者が表示対象を選べる。
/// </summary>
public sealed record ComparisonDisplay(
    SessionVersion Version,
    string Category,
    bool Available,
    IReadOnlyList<string> AllPresets,
    IReadOnlyList<PresetColumnDisplay> Columns,
    IReadOnlyList<ComparisonRowDisplay> Rows)
{
    /// <summary>値の入力とプリセットの操作を受け付けるか（保存中・読取専用・復旧待ちでは受け付けない）。</summary>
    public bool Editable { get; init; }

    /// <summary>カテゴリの表示名。</summary>
    public string CategoryLabel { get; init; } = Category;

    public string? SelectedPreset { get; init; }
}

/// <summary>カテゴリの選択（割り当て画面）。</summary>
public sealed class CategorySelectedEventArgs(string category) : EventArgs
{
    public string Category { get; } = category;
}

/// <summary>表示対象の選択。Presets が null なら全件表示に戻す。</summary>
public sealed class PresetsChosenEventArgs(IReadOnlyList<string>? presets) : EventArgs
{
    public IReadOnlyList<string>? Presets { get; } = presets;
}

/// <summary>
/// セルの入力を始めようとしている（文字の入力や F2 の前）。共有プリセットの確認でやめたときは Presenter が Cancel を立て、View は入力を始めない。
/// </summary>
public sealed class CellEditStartingEventArgs(int row, string preset) : EventArgs
{
    public int Row { get; } = row;
    public string Preset { get; } = preset;
    public bool Cancel { get; set; }
}

/// <summary>共有プリセットを使う組み合わせの 1 行。Values は仕様の軸の順、Selected は組み合わせの表で選んでいる組み合わせ。</summary>
public sealed record SharedUsageRow(string Preset, IReadOnlyList<string> Values, bool Selected);

/// <summary>
/// 共有プリセットを編集する前の確認。Presets は確認する（選んでいる組み合わせ以外も使う）プリセット、
/// SpecLabels は仕様の軸の表示名、Rows はそれらのプリセットを使う組み合わせ。
/// </summary>
public sealed record SharedEditPrompt(IReadOnlyList<string> Presets, IReadOnlyList<string> SpecLabels, IReadOnlyList<SharedUsageRow> Rows)
{
    /// <summary>使う組み合わせが多く、一覧を途中で打ち切った。</summary>
    public bool Truncated { get; init; }
}

/// <summary>使用先を確認して変更を続けるか。</summary>
public sealed record SharedEditAnswer(bool Proceed);

/// <summary>
/// 表示中の行（Row）と、プリセット（Preset）の列のセルへの入力。Category と Version は、入力したときに表示していた比較表のもの
/// （入力中は再表示を保留するので、Presenter が最後に表示したものより古いことがある）。
/// </summary>
public sealed class CellEditedEventArgs(string category, SessionVersion version, int row, string preset, string text) : EventArgs
{
    public string Category { get; } = category;
    public SessionVersion Version { get; } = version;
    public int Row { get; } = row;
    public string Preset { get; } = preset;
    public string Text { get; } = text;
}

/// <summary>矩形貼り付け。Row は表示中の行、Column は表示中のプリセット列の位置（0 始まり）で、貼り付けの左上。</summary>
public sealed class PasteRequestedEventArgs(int row, int column, string text) : EventArgs
{
    public int Row { get; } = row;
    public int Column { get; } = column;
    public string Text { get; } = text;
}

public enum PresetOperation { Add, Duplicate, Rename, Delete }

/// <summary>プリセットの操作の要求。Preset は対象（作成では null）。</summary>
public sealed class PresetOperationRequestedEventArgs(PresetOperation operation, string? preset) : EventArgs
{
    public PresetOperation Operation { get; } = operation;
    public string? Preset { get; } = preset;
}

/// <summary>プリセット名の入力。ReferenceCount は名前の変更で追従する割り当て行の数。</summary>
public sealed record PresetNamePrompt(PresetOperation Operation, string? Source, string Initial, int ReferenceCount);

/// <summary>プリセット比較の View 契約。WinForms の型を公開しない。</summary>
public interface IPresetComparisonView
{
    event EventHandler<PresetsChosenEventArgs>? PresetsChosen;
    event EventHandler<CellEditStartingEventArgs>? CellEditStarting;
    event EventHandler<CellEditedEventArgs>? CellEdited;
    event EventHandler<PasteRequestedEventArgs>? PasteRequested;
    event EventHandler<PresetOperationRequestedEventArgs>? PresetOperationRequested;

    void ShowComparison(ComparisonDisplay comparison);

    /// <summary>プリセット名を尋ねる。やめたら null。</summary>
    string? AskPresetName(PresetNamePrompt prompt);

    bool ConfirmDeletePreset(string preset);

    /// <summary>共有プリセットを編集してよいかを尋ねる。既定の答えはキャンセル。</summary>
    SharedEditAnswer ConfirmSharedEdit(SharedEditPrompt prompt);
    void ShowEditFeedback(EditFeedback feedback);
    void ShowPasteOutOfRange(PasteOutOfRange range);
}

/// <summary>
/// プリセット比較。スキーマの行・列から表を作り、同じ位置の値の違いを示す。
/// 初期表示は全プリセット。利用者は表示対象を選べ、カテゴリを変えると全件表示に戻る。
/// カテゴリは組み合わせの表で選んだセルに従う。行の選択では表示対象と列順を変えず、割り当て先を比較基準にする。
/// セルの入力・矩形貼り付け・プリセットの操作は、表示中の版を期待版とする Command にし、成功すれば変更通知で読み直す。
/// 読み直しが届く前に続けて入力したときは、自分のセル入力の成功で進んだ版を期待版にし、自分の連続入力を競合にしない
/// （ほかの更新が挟まれば版が合わず、競合になる）。
/// 失敗したときは表示を元に戻し、理由を示す。
/// 編集するプリセットを、組み合わせの表で選んでいる組み合わせ以外も使っているときは、入力を始める前（貼り付けでは適用の前）に
/// 影響する組み合わせを示して確認する。貼り付けが複数のプリセットにまたがるときはまとめて 1 回。
/// 共有プリセットの編集では毎回確認する。
/// プリセットの複製や割り当ての追加は自動では行わない。
/// </summary>
public sealed class PresetComparisonPresenter : QueryPresenter<PresetComparisonReadModel>
{
    private readonly IPresetComparisonView _view;
    private readonly GetPresetComparisonHandler _comparison;
    private readonly GetPresetsHandler _presets;
    private readonly GetWorkspaceStatusHandler _status;
    private readonly ChangePresetCellsHandler _cells;
    private readonly PresetCommandHandlers _presetCommands;
    private readonly GetPresetUsageHandler _usage;
    private (string Category, string Preset)? _confirmedEdit;
    private IReadOnlyList<int> _shownIndexes = [];
    private CombinationContext? _context;
    private string? _category;
    private string? _managementCategoryLabel;
    private IReadOnlyList<string>? _chosen;
    private (PresetComparisonReadModel Model, int[] Indexes, string? Baseline, string Language, ComparisonRowDisplay[] Rows)? _rendered;
    // 自分のセル入力だけで続けて進んだ版の範囲（行・列を変えない操作だけを数える）。
    // 範囲内のどの版を表示していても、最新の版（Last）まで自分の入力しか挟まっていない
    private (SessionVersion First, SessionVersion Last)? _ownCellEdits;

    public PresetComparisonPresenter(
        IPresetComparisonView view, EditSession session, IQueryRunner runner, IUiDispatcher ui, DisplayLanguage language)
        : base(session, runner, ui, language)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _comparison = new GetPresetComparisonHandler(session);
        _presets = new GetPresetsHandler(session);
        _status = new GetWorkspaceStatusHandler(session);
        _cells = new ChangePresetCellsHandler(session);
        _presetCommands = new PresetCommandHandlers(session);
        _usage = new GetPresetUsageHandler(session);
        _view.CellEditStarting += OnCellEditStarting;
        _view.PresetsChosen += OnPresetsChosen;
        _view.CellEdited += OnCellEdited;
        _view.PasteRequested += OnPasteRequested;
        _view.PresetOperationRequested += OnPresetOperationRequested;
    }

    /// <summary>管理画面のカテゴリ。組み合わせの選択や比較基準を持たず、全プリセットを扱う。</summary>
    public Task SelectManagementCategoryAsync(string? category, string? label)
    {
        _context = null;
        _chosen = null;
        _managementCategoryLabel = label;
        if (_category != category) return SelectAsync(category);
        if (Current is not null) Render(Current);
        return Task.CompletedTask;
    }

    /// <summary>組み合わせ一覧の表示・選択が変わったとき。カテゴリと比較基準を反映する。</summary>
    public void OnContextChanged(object? sender, CombinationContext context)
    {
        _context = context;
        string? category = context.SelectedCategory is { } selected && context.Categories.Any(c => c.Key == selected)
            ? selected
            : context.Categories.FirstOrDefault()?.Key;
        if (category != _category) _ = SelectAsync(category);
        else if (Current is not null) Render(Current);
    }

    protected override PresetComparisonReadModel Query() =>
        _category is null
            ? new PresetComparisonReadModel("", _status.Handle().Version, false, [], [])
            : _comparison.Handle(new GetPresetComparisonQuery(_category));

    protected override SessionVersion VersionOf(PresetComparisonReadModel model) => model.Version;

    protected override void Render(PresetComparisonReadModel model)
    {
        if (model.Category != _category) return;
        string? selectedPreset = _context?.SelectedPresets.GetValueOrDefault(model.Category);
        IReadOnlyList<string> shown = _chosen is null ? model.Presets : model.Presets.Where(_chosen.Contains).ToArray();
        int baselineIndex = selectedPreset is not null && model.Presets.Contains(selectedPreset)
            ? IndexOf(model.Presets, selectedPreset) : -1;
        int[] indexes = shown.Select(p => IndexOf(model.Presets, p)).ToArray();
        _shownIndexes = indexes;

        SchemaDefinition? schema = model.Schema;
        ComparisonRowDisplay[] rows = _rendered is { } rendered && ReferenceEquals(rendered.Model, model) &&
            rendered.Indexes.SequenceEqual(indexes) && rendered.Baseline == selectedPreset && rendered.Language == Language.Code ? rendered.Rows : model.Rows.Select(row =>
        {
            SchemaGroupDefinition? group = DefinitionLabels.FindGroup(schema, row.Key.Group);
            ComparisonCell[] cells = indexes.Select(i => row.Cells[i]).ToArray();
            ComparisonCell? baseline = baselineIndex >= 0 ? row.Cells[baselineIndex] : null;
            bool differs = cells.Where(c => c.State != ComparisonCellState.Unused).Select(Text).Distinct().Count() > 1;
            return new ComparisonRowDisplay(
                DefinitionLabels.Group(group, row.Key.Group, Language.Code),
                DefinitionLabels.Row(group, row.Key.Row, Language.Code),
                DefinitionLabels.Column(group, row.Key.Column, Language.Code),
                group?.Labels.Unit(row.Key.Column),
                differs,
                cells.Select(c => new ComparisonCellDisplay(
                    Text(c), c.State, c.State == ComparisonCellState.Editable && c.Value.Kind is CellValueKind.Missing or CellValueKind.Null,
                    baseline is { State: not ComparisonCellState.Unused } && c.State != ComparisonCellState.Unused && Text(c) != Text(baseline))
                {
                    SavedText = c.SavedValue is { } saved ? CellText.Format(saved) : null,
                }).ToArray());
        }).ToArray();
        _rendered = (model, indexes, selectedPreset, Language.Code, rows);

        _view.ShowComparison(new ComparisonDisplay(
            model.Version,
            model.Category,
            model.Available,
            model.Presets,
            shown.Select(p => new PresetColumnDisplay(p, p == selectedPreset)).ToArray(),
            rows)
        {
            Editable = model.Available && Editing.IsAllowed(_status.Handle()),
            CategoryLabel = _context?.Categories.FirstOrDefault(c => c.Key == model.Category)?.Label ?? _managementCategoryLabel ?? model.Category,
            SelectedPreset = selectedPreset,
        });
    }

    public Task SelectAsync(string? category)
    {
        _confirmedEdit = null;
        _category = category;
        _chosen = null;
        return RefreshAsync();
    }

    private void OnPresetsChosen(object? sender, PresetsChosenEventArgs e)
    {
        _chosen = e.Presets is null || Current is { } model && model.Presets.All(e.Presets.Contains)
            ? null : e.Presets.ToArray();
        if (Current is not null) Render(Current);
    }

    private void OnCellEditStarting(object? sender, CellEditStartingEventArgs e)
    {
        _confirmedEdit = null;
        if (Current is not { Available: true } model) return;
        if (ConfirmShared(model, [e.Preset])) _confirmedEdit = (model.Category, e.Preset);
        else e.Cancel = true;
    }

    private void OnCellEdited(object? sender, CellEditedEventArgs e)
    {
        // 入力を始めるときに確認したプリセットは、確定のときに尋ね直さない
        bool confirmed = _confirmedEdit == (e.Category, e.Preset);
        _confirmedEdit = null;
        if (Current is not { Available: true } model || e.Row < 0 || e.Row >= model.Rows.Count)
        {
            if (Current is not null) Render(Current);
            return;
        }
        // 入力した表のカテゴリがもう表示していないものなら、行の位置が合わないので適用しない
        if (e.Category != model.Category)
        {
            Render(model);
            _view.ShowEditFeedback(EditFeedback.From(new CommandResult(CommandStatus.Conflict, model.Version)));
            return;
        }
        if (!confirmed && !ConfirmShared(model, [e.Preset]))
        {
            Render(model);
            return;
        }
        // 行はスキーマから作るので、同じカテゴリなら入力した表示と最新の結果で同じ位置になる（スキーマが変われば版が合わず競合）
        CellKey key = model.Rows[e.Row].Key;
        ChangeCells(model, [new CellInput(e.Preset, key.Group, key.Row, key.Column, e.Text)], e.Version);
    }

    /// <summary>
    /// 共有プリセットの確認。選んでいる組み合わせ以外も使うプリセットがあれば、まとめて 1 回尋ねる。
    /// 組み合わせを選んでいなければ、使っている組み合わせがあるだけで確認する。続けてよければ true。
    /// </summary>
    private bool ConfirmShared(PresetComparisonReadModel model, IReadOnlyList<string> presets)
    {
        string[] candidates = presets.Distinct(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return true;
        IReadOnlyDictionary<string, string>? selected = _context?.SelectedCombination;
        PresetUsageReadModel usage = _usage.Handle(new GetPresetUsageQuery(model.Category, candidates, selected));
        PresetUsage[] shared = usage.Usages.Where(u => u.UsedByOthers).ToArray();
        if (shared.Length == 0) return true;

        SpecAxis[] axes = usage.Spec.Axes.ToArray();
        string[] Values(IReadOnlyDictionary<string, string> c) => axes.Select(a => c.GetValueOrDefault(a.Key, "")).ToArray();
        SharedEditAnswer answer = _view.ConfirmSharedEdit(new SharedEditPrompt(
            shared.Select(u => u.Preset).ToArray(),
            axes.Select(a => a.DisplayName.Resolve(Language.Code, a.Key)).ToArray(),
            shared.SelectMany(u =>
                (u.UsedBySelected && selected is not null ? [new SharedUsageRow(u.Preset, Values(selected), true)] : Array.Empty<SharedUsageRow>())
                .Concat(u.Others.Select(c => new SharedUsageRow(u.Preset, Values(c), false)))).ToArray())
        {
            Truncated = shared.Any(u => u.MoreOthers),
        });
        return answer.Proceed;
    }

    /// <summary>
    /// 矩形貼り付け。範囲が表からはみ出せば何もしない。入力できないセルへの空の値は読み飛ばし（コピーした表を貼り戻せるように）、
    /// それ以外の値は Command が位置と理由を付けて全体を拒否する。
    /// </summary>
    private void OnPasteRequested(object? sender, PasteRequestedEventArgs e)
    {
        if (Current is not { Available: true } model) return;
        IReadOnlyList<IReadOnlyList<string>> table = TabularText.Parse(e.Text);
        if (table.Count == 0) return;
        int rows = table.Count;
        int columns = table.Max(r => r.Count);
        int availableRows = model.Rows.Count - e.Row;
        int availableColumns = _shownIndexes.Count - e.Column;
        if (e.Row < 0 || e.Column < 0 || rows > availableRows || columns > availableColumns)
        {
            _view.ShowPasteOutOfRange(new PasteOutOfRange(rows, columns, Math.Max(availableRows, 0), Math.Max(availableColumns, 0)));
            return;
        }

        List<CellInput> inputs = [];
        for (int r = 0; r < rows; r++)
        {
            PresetComparisonRow row = model.Rows[e.Row + r];
            for (int c = 0; c < table[r].Count; c++)
            {
                int preset = _shownIndexes[e.Column + c];
                string text = table[r][c];
                if (row.Cells[preset].State != ComparisonCellState.Editable && text.Length == 0) continue;
                inputs.Add(new CellInput(model.Presets[preset], row.Key.Group, row.Key.Row, row.Key.Column, text));
            }
        }
        if (inputs.Count == 0 || !ConfirmShared(model, inputs.Select(i => i.Preset).ToArray())) return;
        ChangeCells(model, inputs, model.Version);
    }

    /// <param name="seen">入力したときに表示していた版。</param>
    private void ChangeCells(PresetComparisonReadModel model, IReadOnlyList<CellInput> inputs, SessionVersion seen)
    {
        if (inputs.Count == 0) return;
        SessionVersion expected = ExpectedVersion(seen);
        ChangePresetCellsResult result = _cells.Handle(new ChangePresetCellsCommand(model.Category, inputs, expected));
        if (result.Result.Status == CommandStatus.Applied)
        {
            RecordOwnCellEdit(expected, result.Result.Version);
            return;
        }

        // 成功しなかった（または変わらなかった）ので、入力した文字を消して表示を戻す
        Render(model);
        if (Editing.Succeeded(result.Result)) return;
        _view.ShowEditFeedback(EditFeedback.From(result.Result,
            result.Problems.Select(p => new CellProblemDisplay(Location(model, p.Input), p.Input.Text, p.Kind)).ToArray()));
    }

    /// <summary>
    /// Command の期待版。表示していた版が、自分のセル入力だけで続けて進んだ範囲にあれば、その範囲の最新の版にする。
    /// セル入力は行・列・プリセットを変えないので、表示していた版の位置のまま適用できる。
    /// 範囲の外（間にほかの更新が挟まった）なら表示していた版のままにし、Command が競合として拒否する。
    /// </summary>
    private SessionVersion ExpectedVersion(SessionVersion seen) =>
        _ownCellEdits is { } own && seen.CompareTo(own.First) >= 0 && seen.CompareTo(own.Last) <= 0 ? own.Last : seen;

    /// <summary>自分のセル入力の成功を記録する。直前の範囲の最新の版から続いていれば範囲を伸ばし、そうでなければ始め直す。</summary>
    private void RecordOwnCellEdit(SessionVersion expected, SessionVersion applied) =>
        _ownCellEdits = _ownCellEdits is { } own && own.Last == expected ? (own.First, applied) : (expected, applied);

    private string Location(PresetComparisonReadModel model, CellInput input)
    {
        SchemaGroupDefinition? group = DefinitionLabels.FindGroup(model.Schema, input.Group);
        return string.Join(" / ",
            input.Preset,
            DefinitionLabels.Group(group, input.Group, Language.Code),
            DefinitionLabels.Row(group, input.Row, Language.Code),
            DefinitionLabels.Column(group, input.Column, Language.Code));
    }

    private void OnPresetOperationRequested(object? sender, PresetOperationRequestedEventArgs e)
    {
        if (Current is not { Available: true } model) return;
        if (e.Operation != PresetOperation.Add && (e.Preset is null || !model.Presets.Contains(e.Preset))) return;
        int references = e.Preset is null ? 0 :
            _presets.Handle(new GetPresetsQuery(model.Category)).Presets.FirstOrDefault(p => p.Name == e.Preset)?.ReferenceCount ?? 0;

        CommandResult? result;
        string? created = null;
        switch (e.Operation)
        {
            case PresetOperation.Add:
                created = _view.AskPresetName(new PresetNamePrompt(PresetOperation.Add, null, "", 0));
                result = created is null ? null : _presetCommands.Handle(new AddPresetCommand(model.Category, created, ExpectedVersion(model.Version)));
                break;
            case PresetOperation.Duplicate:
                created = _view.AskPresetName(new PresetNamePrompt(PresetOperation.Duplicate, e.Preset, e.Preset + "_copy", 0));
                result = created is null ? null : _presetCommands.Handle(new DuplicatePresetCommand(model.Category, e.Preset!, created, ExpectedVersion(model.Version)));
                break;
            case PresetOperation.Rename:
                created = _view.AskPresetName(new PresetNamePrompt(PresetOperation.Rename, e.Preset, e.Preset!, references));
                result = created is null ? null : _presetCommands.Handle(new RenamePresetCommand(model.Category, e.Preset!, created, ExpectedVersion(model.Version)));
                break;
            default:
                // 参照されているプリセットは消せない。確認を出す前に知らせる
                if (references > 0)
                {
                    _view.ShowEditFeedback(new EditFeedback(CommandStatus.Invalid, PresetOperationFailure.InUse, []));
                    return;
                }
                result = _view.ConfirmDeletePreset(e.Preset!)
                    ? _presetCommands.Handle(new DeletePresetCommand(model.Category, e.Preset!, ExpectedVersion(model.Version)))
                    : null;
                break;
        }
        if (result is null) return;
        if (!Editing.Succeeded(result))
        {
            _view.ShowEditFeedback(EditFeedback.From(result));
            return;
        }

        // 任意に選んだ比較対象を、作成・名前の変更・削除に追従させる。
        // 変更通知による読み直しが先に表示していることがあるので、選び直した後に表示し直す
        if (_chosen is null) return;
        if (created is not null)
        {
            string name = created.Trim();
            _chosen = e.Operation == PresetOperation.Rename
                ? _chosen.Select(p => p == e.Preset ? name : p).ToArray()
                : [.. _chosen, name];
        }
        else if (e.Operation == PresetOperation.Delete)
        {
            _chosen = _chosen.Where(p => p != e.Preset).ToArray();
        }
        if (Current is not null) Render(Current);
    }

    private static string Text(ComparisonCell cell) => cell.State == ComparisonCellState.Unused ? "" : CellText.Format(cell.Value);

    private static int IndexOf(IReadOnlyList<string> presets, string name)
    {
        for (int i = 0; i < presets.Count; i++) if (presets[i] == name) return i;
        throw new InvalidOperationException($"Preset {name} is not in the comparison.");
    }

    protected override void OnDisposed()
    {
        _view.PresetsChosen -= OnPresetsChosen;
        _view.CellEditStarting -= OnCellEditStarting;
        _view.CellEdited -= OnCellEdited;
        _view.PasteRequested -= OnPasteRequested;
        _view.PresetOperationRequested -= OnPresetOperationRequested;
    }
}
