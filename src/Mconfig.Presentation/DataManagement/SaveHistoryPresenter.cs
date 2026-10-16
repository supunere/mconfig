using Mconfig.Application.Releases.Commands;
using Mconfig.Application.Assignments.Commands;
using Mconfig.Application.History;
using Mconfig.Application.History.Commands;
using Mconfig.Application.History.Queries;
using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Commands;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Application.Workspaces.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.Common;

namespace Mconfig.Presentation.DataManagement;

/// <summary>保存と履歴の画面の View 契約。確認の問い合わせは UI スレッドで呼ぶ。</summary>
public interface ISaveHistoryView : IDataOperationView
{
    event EventHandler? ValidateRequested;
    event EventHandler? DiscardRequested;
    event EventHandler<PublishRequestedEventArgs>? PublishRequested;
    event EventHandler? HistoryRequested;
    event EventHandler? MoreHistoryRequested;
    event EventHandler<RevisionEventArgs>? RevisionSelected;
    event EventHandler<RevisionEventArgs>? RestoreRequested;
    event EventHandler<RevisionEventArgs>? CompareWithCurrentRequested;
    event EventHandler<IssueEventArgs>? NavigateRequested;

    /// <summary>未割当の組み合わせを CSV に書き出す。</summary>
    event EventHandler? ExportUncoveredRequested;

    /// <summary>未割当の組み合わせの書出し先のパスを尋ねる（ファイルは開かない）。やめたら null。</summary>
    string? ChooseCsvPath();

    /// <summary>書き出した件数と場所を知らせる。取消・失敗したら written は null（既存のファイルは変わっていない）。</summary>
    void ShowUncoveredExported(long? written, string path, string? error);

    /// <summary>前回の履歴保存からの変更。読めなかったときは null。</summary>
    void ShowUncommittedChanges(IReadOnlyList<ChangeDisplay>? changes);

    /// <summary>選んだ版から現在までの違い（「現在との違い」）。</summary>
    void ShowDifferenceFromCurrent(RevisionDisplay revision, IReadOnlyList<ChangeDisplay> changes);
    void ShowValidation(ValidationDisplay validation);
    void ShowHistory(IReadOnlyList<RevisionDisplay> revisions, bool hasMore);
    void ShowRevisionChanges(RevisionDisplay? revision, IReadOnlyList<ChangeDisplay> changes);

    bool ConfirmDiscard();
    bool ConfirmShortReason(int length);
    bool ConfirmRestore(RestorePreviewDisplay preview);
}

/// <summary>保存と履歴のために Presenter が使う Handler 一式（Composition Root が具体実装を渡して作る）。</summary>
public sealed record SaveHistoryHandlers(
    ValidateWorkspaceHandler Validate,
    SaveDraftHandler SaveDraft,
    DiscardEditsHandler Discard,
    PublishWorkspaceHandler Publish,
    HistoryQueryHandlers History,
    RestoreRevisionHandler Restore)
{
    /// <summary>未割当の組み合わせの CSV 書出し（なければ書き出せない）。</summary>
    public ExportUncoveredCombinationsHandler? Uncovered { get; init; }
}

/// <summary>
/// 保存・発行・履歴の画面。検証・保存・破棄・発行・履歴と意味差分・復元を Query と Command で行う。
/// 発行はエラー・警告を拒否し、短い理由だけを確認して再実行する。
/// 結果は完了した段階とともに示し、履歴と状態は変更通知で読み直す。操作は <see cref="DataOperationGate"/> を通して一つずつ実行する。
/// 前回の発行からの変更は、内容が変わるたびに背景で求め直す。
/// 検証結果の該当箇所へ移る要求は、移る先（画面とカテゴリ）を決めて <see cref="NavigationRequested"/> で知らせる。
/// </summary>
public sealed class SaveHistoryPresenter : IDisposable
{
    public const int HistoryPageSize = 100;

    private readonly ISaveHistoryView _view;
    private readonly EditSession _session;
    private readonly SaveHistoryHandlers _handlers;
    private readonly DataOperationGate _gate;
    private readonly IQueryRunner _runner;
    private readonly IUiDispatcher _ui;
    private readonly DataDisplayMapper _map;
    private readonly string _author;
    private readonly TimeSpan _revalidateDelay;
    private readonly GetWorkspaceStatusHandler _status;
    private readonly List<RevisionDisplay> _history = [];
    private readonly RequestTracker _historyRequests = new();
    private readonly RequestTracker _diffRequests = new();
    private readonly RequestTracker _uncommittedRequests = new();
    private readonly RequestTracker _changeRequests = new();
    private ContentFingerprint? _validatedFingerprint;
    private ValidationDisplay? _validation;
    private bool _disposed;

    public SaveHistoryPresenter(
        ISaveHistoryView view, EditSession session, SaveHistoryHandlers handlers, DataOperationGate gate,
        IQueryRunner runner, IUiDispatcher ui, DisplayLanguage language, string author, TimeSpan revalidateDelay = default)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _map = new DataDisplayMapper(language ?? throw new ArgumentNullException(nameof(language)), new GetDefinitionsHandler(session));
        _author = author ?? throw new ArgumentNullException(nameof(author));
        _revalidateDelay = revalidateDelay;
        _status = new GetWorkspaceStatusHandler(session);

        _session.Changed += OnSessionChanged;
        _gate.BusyChanged += OnBusyChanged;
        _gate.ProgressChanged += OnProgressChanged;
        _view.ValidateRequested += OnValidateRequested;
        _view.DiscardRequested += OnDiscardRequested;
        _view.PublishRequested += OnPublishRequested;
        _view.HistoryRequested += OnHistoryRequested;
        _view.MoreHistoryRequested += OnMoreHistoryRequested;
        _view.RevisionSelected += OnRevisionSelected;
        _view.RestoreRequested += OnRestoreRequested;
        _view.CompareWithCurrentRequested += OnCompareWithCurrentRequested;
        _view.NavigateRequested += OnNavigateRequested;
        _view.CancelRequested += OnCancelRequested;
        _view.ExportUncoveredRequested += OnExportUncoveredRequested;
    }

    /// <summary>検証結果の該当箇所へ移る要求（画面の切り替えは Composition Root が行う）。</summary>
    public event EventHandler<NavigationTarget>? NavigationRequested;

    /// <summary>最初の表示。状態・履歴・前回の発行からの変更を読む。</summary>
    public Task LoadAsync()
    {
        ShowState();
        return Task.WhenAll(LoadHistoryAsync(append: false), LoadUncommittedChangesAsync());
    }

    // ---- 状態 ----

    private void OnSessionChanged(object? sender, SessionChangedEventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        ShowState();
        _ = RecountAfterChangeAsync();
        // 検証した後に内容が変わったら、結果が古いことを示す（自動では検証し直さない）
        if (_validation is { Incomplete: false })
        {
            bool stale = _validatedFingerprint != _status.GetEditedFingerprint();
            if (_validation.Stale != stale)
            {
                _validation = _validation with { Stale = stale };
                _view.ShowValidation(_validation);
            }
        }
    });

    private void OnBusyChanged(object? sender, EventArgs e) => ShowState();

    private void OnProgressChanged(object? sender, EventArgs e)
    {
        if (!_disposed) _view.ShowProgress(_gate.Progress);
    }

    private void OnCancelRequested(object? sender, EventArgs e) => _gate.Cancel();

    private void ShowState()
    {
        if (_disposed) return;
        WorkspaceStatus status = _status.Handle();
        _view.ShowState(new DataStateDisplay(
            Editing.IsAllowed(status), status.HasUnsavedChanges, status.HasUncommittedChanges,
            status.IsReadOnly, status.IsRecoveryPending, _gate.Busy) { Author = _author });
    }

    /// <summary>連続した編集では、最後の編集から少し待ってからまとめて数え直す。</summary>
    private async Task RecountAfterChangeAsync()
    {
        long request = _changeRequests.Next();
        if (_revalidateDelay > TimeSpan.Zero)
        {
            await Task.Delay(_revalidateDelay).ConfigureAwait(false);
            if (!_changeRequests.IsLatest(request)) return;
        }
        await LoadUncommittedChangesAsync().ConfigureAwait(false);
    }

    private async Task LoadUncommittedChangesAsync()
    {
        long request = _uncommittedRequests.Next();
        IReadOnlyList<ChangeDisplay>? changes;
        SessionVersion? version = null;
        try
        {
            ChangesReadModel model = await (await _runner.RunAsync(() => _handlers.History.HandleAsync(new GetUncommittedChangesQuery())).ConfigureAwait(false))
                .ConfigureAwait(false);
            changes = DataDisplayMapper.Changes(model.Changes);
            version = model.Version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 履歴が読めなくても、ほかの操作は続けられるようにする
            changes = null;
        }
        _ui.Post(() =>
        {
            if (_disposed || !_uncommittedRequests.IsLatest(request)) return;
            // 求めている間に内容が変わった結果は古いので示さない（変更通知で求め直す）
            if (version is not null && version != _status.Handle().Version) return;
            _view.ShowUncommittedChanges(changes);
        });
    }

    private void OnNavigateRequested(object? sender, IssueEventArgs e)
    {
        if (NavigationTarget.For(e.Issue) is { } target) NavigationRequested?.Invoke(this, target);
    }

    private void OnCompareWithCurrentRequested(object? sender, RevisionEventArgs e)
    {
        if (e.Revision is not null) _ = CompareWithCurrentAsync(e.Revision);
    }

    private async Task CompareWithCurrentAsync(RevisionId revision)
    {
        RevisionDisplay info = await _gate.OnUi(() => _history.FirstOrDefault(r => r.Id == revision)).ConfigureAwait(false)
            ?? new RevisionDisplay(revision, default, "", "");
        ChangesReadModel model;
        try
        {
            model = await (await _runner.RunAsync(() => _handlers.History.HandleAsync(new GetDifferenceFromCurrentQuery(revision))).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkspaceUnreadableException or IOException or InvalidOperationException)
        {
            _ui.Post(() => _view.ShowOperationResult(DataDisplayMapper.Unavailable(DataOperation.Restore, ex.Message)));
            return;
        }
        _ui.Post(() =>
        {
            if (!_disposed) _view.ShowDifferenceFromCurrent(info, DataDisplayMapper.Changes(model.Changes));
        });
    }

    // ---- 検証 ----

    private void OnValidateRequested(object? sender, EventArgs e) => _ = ValidateAsync();

    /// <summary>
    /// E を履歴に保存の規則で検証する（被覆を含む）。進捗を示し、取消できる。取消したら「検証未完了」として結果を示さない。
    /// ほかの全件検証を実行中なら始めない。
    /// </summary>
    public async Task ValidateAsync()
    {
        CancellationTokenSource? source = await _gate.OnUi(() => _gate.BeginCancelable(null)).ConfigureAwait(false);
        if (source is null) return;
        CancellationToken cancellation = source.Token;
        IProgress<CoverageProgress>? progress = _gate.CurrentProgress;
        try
        {
            WorkspaceValidationReadModel model = await _runner.RunAsync(
                () => _handlers.Validate.Handle(new ValidateWorkspaceQuery(ValidationOperation.Commit), cancellation, progress)).ConfigureAwait(false);
            _ui.Post(() =>
            {
                if (_disposed) return;
                _validatedFingerprint = model.Fingerprint;
                _validation = new ValidationDisplay(_map.Issues(model.Items, model.Schemas), model.Fingerprint != _status.GetEditedFingerprint());
                _view.ShowValidation(_validation);
            });
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() =>
            {
                if (_disposed) return;
                _validation = new ValidationDisplay([], false) { Incomplete = true };
                _view.ShowValidation(_validation);
            });
        }
        finally
        {
            _ui.Post(() => _gate.EndCancelable(source));
        }
    }

    // ---- 未割当の書出し ----

    private void OnExportUncoveredRequested(object? sender, EventArgs e) => _ = ExportUncoveredAsync();

    /// <summary>
    /// 未割当の組み合わせを 1 件ずつ評価しながら CSV に書き出す（Command）。書込と後始末は Command が行い、
    /// 取消・失敗しても出力先の既存ファイルは変わらない。
    /// </summary>
    public async Task ExportUncoveredAsync()
    {
        if (_handlers.Uncovered is not { } handler) return;
        string? path = await _gate.OnUi(() => _view.ChooseCsvPath()).ConfigureAwait(false);
        if (path is null) return;
        CancellationTokenSource? source = await _gate.OnUi(() => _gate.BeginCancelable(DataOperation.ExportUncovered)).ConfigureAwait(false);
        if (source is null) return;
        CancellationToken cancellation = source.Token;
        IProgress<CoverageProgress>? progress = _gate.CurrentProgress;
        long? written = null;
        string? error = null;
        try
        {
            written = (await _runner.RunAsync(() => handler.Handle(new ExportUncoveredCombinationsCommand(path), cancellation, progress))
                .ConfigureAwait(false)).Rows;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            _ui.Post(() => _gate.EndCancelable(source));
        }
        _ui.Post(() =>
        {
            if (!_disposed) _view.ShowUncoveredExported(written, path, error);
        });
    }

    // ---- 一時保存・破棄・確定保存 ----

    public Task<OperationResult?> SaveDraftAsync() => RunOperationAsync(DataOperation.SaveDraft,
        version => _handlers.SaveDraft.HandleAsync(new SaveDraftCommand(version)));

    private void OnDiscardRequested(object? sender, EventArgs e)
    {
        if (!_view.ConfirmDiscard()) return;
        _ = RunOperationAsync(DataOperation.Discard, version => _handlers.Discard.HandleAsync(new DiscardEditsCommand(version)));
    }

    private void OnPublishRequested(object? sender, PublishRequestedEventArgs e) => _ = PublishAsync(e.Reason);

    /// <summary>
    /// 履歴に記録する。エラー・警告は拒否し、短い理由だけは利用者に確認して再実行する。
    /// 確認の間にセッションが変われば、Handler のバージョン照合で再実行を拒否する。
    /// </summary>
    public async Task<OperationResult?> PublishAsync(string reason)
    {
        const DataOperation operation = DataOperation.Publish;
        OperationResult? result = await RunOperationAsync(operation,
            version => _handlers.Publish.HandleAsync(new(reason, _author, version), _gate.Token, _gate.CurrentProgress),
            showConfirmation: false, cancelable: true)
            .ConfigureAwait(false);
        if (result?.Status != OperationStatus.ConfirmationRequired) return result;

        bool approved = await _gate.OnUi(() => _view.ConfirmShortReason(reason.Trim().Length)).ConfigureAwait(false);
        if (!approved) return result;

        return await RunOperationAsync(operation,
            _ => _handlers.Publish.HandleAsync(new(reason, _author, result.Version, ConfirmShortReason: true), _gate.Token, _gate.CurrentProgress),
            cancelable: true)
            .ConfigureAwait(false);
    }

    // ---- 履歴・差分 ----

    private void OnHistoryRequested(object? sender, EventArgs e) => _ = LoadHistoryAsync(append: false);

    private void OnMoreHistoryRequested(object? sender, EventArgs e) => _ = LoadHistoryAsync(append: true);

    private async Task LoadHistoryAsync(bool append)
    {
        long request = _historyRequests.Next();
        RevisionId? after = append && _history.Count > 0 ? _history[^1].Id : null;
        IReadOnlyList<RevisionInfo> page;
        try
        {
            page = await (await _runner.RunAsync(() => _handlers.History.HandleAsync(new GetHistoryQuery(HistoryPageSize, after))).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkspaceUnreadableException or IOException or InvalidOperationException)
        {
            page = [];
        }
        _ui.Post(() =>
        {
            if (_disposed || !_historyRequests.IsLatest(request)) return;
            if (!append) _history.Clear();
            _history.AddRange(page.Select(DataDisplayMapper.Revision));
            _view.ShowHistory(_history.ToArray(), page.Count == HistoryPageSize);
        });
    }

    private void OnRevisionSelected(object? sender, RevisionEventArgs e) => _ = ShowRevisionAsync(e.Revision);

    /// <summary>選んだ版の差分を表示する。取得中・取得失敗時は旧版の結果を残さない。</summary>
    public async Task ShowRevisionAsync(RevisionId? revision)
    {
        long request = _diffRequests.Next();
        _ui.Post(() =>
        {
            if (!_disposed && _diffRequests.IsLatest(request)) _view.ShowRevisionChanges(null, []);
        });
        if (revision is null) return;
        RevisionDiffReadModel diff;
        try
        {
            diff = await (await _runner.RunAsync(() => _handlers.History.HandleAsync(new GetRevisionDiffQuery(revision))).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkspaceUnreadableException or IOException or InvalidOperationException)
        {
            _ui.Post(() =>
            {
                if (_disposed || !_diffRequests.IsLatest(request)) return;
                _view.ShowRevisionChanges(null, []);
                _view.ShowOperationResult(DataDisplayMapper.Unavailable(DataOperation.Restore, ex.Message));
            });
            return;
        }
        _ui.Post(() =>
        {
            if (_disposed || !_diffRequests.IsLatest(request)) return;
            _view.ShowRevisionChanges(DataDisplayMapper.Revision(diff.Revision), DataDisplayMapper.Changes(diff.Changes));
        });
    }

    // ---- 復元 ----

    private void OnRestoreRequested(object? sender, RevisionEventArgs e)
    {
        if (e.Revision is not null) _ = RestoreAsync(e.Revision);
    }

    /// <summary>
    /// 版の復元。プレビューで差分と検証を示し、承認されたらプレビューを取った版を期待版として実行する。
    /// 未確定の変更だけがあれば、それを退避・破棄することを示したうえで復元する。
    /// </summary>
    public async Task<OperationResult?> RestoreAsync(RevisionId revision)
    {
        if (_gate.Busy) return null;
        RestorePreviewReadModel preview;
        RevisionDisplay info = await _gate.OnUi(() => _history.FirstOrDefault(r => r.Id == revision)).ConfigureAwait(false)
            ?? new RevisionDisplay(revision, default, "", "");
        try
        {
            preview = await (await _runner.RunAsync(() => _handlers.History.HandleAsync(new PreviewRestoreQuery(revision))).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkspaceUnreadableException or IOException or InvalidOperationException)
        {
            _ui.Post(() => _view.ShowOperationResult(DataDisplayMapper.Unavailable(DataOperation.Restore, ex.Message)));
            return null;
        }

        RestoreChoice choice = preview.CanRestore ? RestoreChoice.Restore
            : preview.CanRestoreByDiscarding ? RestoreChoice.RestoreByDiscarding
            : preview.HasUnsavedChanges ? RestoreChoice.SaveOrDiscardFirst
            : RestoreChoice.Unavailable;
        bool confirmed = await _gate.OnUi(() => _view.ConfirmRestore(new RestorePreviewDisplay(
            info, DataDisplayMapper.Changes(preview.Changes),
            _map.Issues(preview.Issues.Select(i => DataDisplayMapper.Item(i, ValidationOperation.Edit)).ToArray()), choice))).ConfigureAwait(false);
        if (!confirmed || choice is RestoreChoice.SaveOrDiscardFirst or RestoreChoice.Unavailable) return null;

        return await RunOperationAsync(DataOperation.Restore, _ => _handlers.Restore.HandleAsync(
            new RestoreRevisionCommand(revision, preview.SessionVersion, DiscardUncommitted: choice == RestoreChoice.RestoreByDiscarding)))
            .ConfigureAwait(false);
    }

    // ---- 共通 ----

    /// <summary>
    /// 保存系の操作を実行して結果を示す（承認待ちは showConfirmation が false なら呼出し側が扱う）。
    /// 確定保存・復元で書き込んだ後は履歴を読み直す。
    /// </summary>
    private async Task<OperationResult?> RunOperationAsync(
        DataOperation operation, Func<SessionVersion, Task<OperationResult>> run, bool showConfirmation = true, bool cancelable = false)
    {
        OperationResult? result = await _gate.RunAsync(operation, run, cancelable).ConfigureAwait(false);
        if (result is null) return null;
        if (result.Status != OperationStatus.ConfirmationRequired || showConfirmation)
        {
            _ui.Post(() =>
            {
                if (!_disposed) _view.ShowOperationResult(_map.Result(operation, result));
            });
        }
        if (result.CompletedStage >= OperationStage.DraftSaved && operation is DataOperation.Publish or DataOperation.Restore)
            _ = LoadHistoryAsync(append: false);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Changed -= OnSessionChanged;
        _gate.BusyChanged -= OnBusyChanged;
        _gate.ProgressChanged -= OnProgressChanged;
        _view.ValidateRequested -= OnValidateRequested;
        _view.DiscardRequested -= OnDiscardRequested;
        _view.PublishRequested -= OnPublishRequested;
        _view.HistoryRequested -= OnHistoryRequested;
        _view.MoreHistoryRequested -= OnMoreHistoryRequested;
        _view.RevisionSelected -= OnRevisionSelected;
        _view.RestoreRequested -= OnRestoreRequested;
        _view.CompareWithCurrentRequested -= OnCompareWithCurrentRequested;
        _view.NavigateRequested -= OnNavigateRequested;
        _view.CancelRequested -= OnCancelRequested;
        _view.ExportUncoveredRequested -= OnExportUncoveredRequested;
    }
}
