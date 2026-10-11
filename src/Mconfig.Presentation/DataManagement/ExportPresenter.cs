using Mconfig.Application.Ports;
using Mconfig.Application.Releases.Commands;
using Mconfig.Application.Releases.Queries;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Application.Workspaces.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.Common;

namespace Mconfig.Presentation.DataManagement;

/// <summary>保存と履歴・出力の画面に共通の View 契約（操作の状態・進捗・結果）。WinForms の型を公開しない。</summary>
public interface IDataOperationView
{
    /// <summary>実行中の全件検証（検証・履歴に保存・出力の中の検証、未割当の書出し）をやめる。</summary>
    event EventHandler? CancelRequested;

    void ShowState(DataStateDisplay state);

    /// <summary>全件検証などの進捗。null なら進捗の表示を消す。</summary>
    void ShowProgress(ProgressDisplay? progress);

    void ShowOperationResult(OperationDisplay result);
}

/// <summary>出力の画面の View 契約。確認の問い合わせは UI スレッドで呼ぶ。</summary>
public interface IExportView : IDataOperationView
{
    event EventHandler<ExportRequestedEventArgs>? ExportRequested;
    event EventHandler<ResumeExportRequestedEventArgs>? ResumeExportRequested;

    void ShowPendingExports(PendingExportsDisplay pending);
    ExportDecision ConfirmExport(ExportPreviewDisplay preview);
}

/// <summary>出力のために Presenter が使う Handler 一式（Composition Root が具体実装を渡して作る）。</summary>
public sealed record ExportHandlers(ReleaseQueryHandlers Releases, ExportReleaseHandler Export, ResumeExportHandler Resume);

/// <summary>
/// 出力の画面。テスト版・リリース版の ZIP 出力と、中断した出力の再開を Query と Command で行う。
/// プレビューで対象・比較元・差分・警告を示し、実行時はプレビューで見た対象と比較元を照合する。
/// 操作は <see cref="DataOperationGate"/> を通して、保存と履歴の画面の操作と一つずつ実行する。
/// </summary>
public sealed class ExportPresenter : IDisposable
{
    private readonly IExportView _view;
    private readonly EditSession _session;
    private readonly ExportHandlers _handlers;
    private readonly DataOperationGate _gate;
    private readonly IQueryRunner _runner;
    private readonly IUiDispatcher _ui;
    private readonly DataDisplayMapper _map;
    private readonly GetWorkspaceStatusHandler _status;
    private readonly RequestTracker _pendingRequests = new();
    private bool _disposed;

    public ExportPresenter(
        IExportView view, EditSession session, ExportHandlers handlers, DataOperationGate gate,
        IQueryRunner runner, IUiDispatcher ui, DisplayLanguage language)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _map = new DataDisplayMapper(language ?? throw new ArgumentNullException(nameof(language)), new GetDefinitionsHandler(session));
        _status = new GetWorkspaceStatusHandler(session);

        _session.Changed += OnSessionChanged;
        _gate.BusyChanged += OnBusyChanged;
        _gate.ProgressChanged += OnProgressChanged;
        _view.ExportRequested += OnExportRequested;
        _view.ResumeExportRequested += OnResumeExportRequested;
        _view.CancelRequested += OnCancelRequested;
    }

    /// <summary>最初の表示。状態と未完了の出力を読む。</summary>
    public Task LoadAsync()
    {
        ShowState();
        return LoadPendingExportsAsync();
    }

    private void OnSessionChanged(object? sender, SessionChangedEventArgs e) => _ui.Post(ShowState);

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
            status.IsReadOnly, status.IsRecoveryPending, _gate.Busy));
    }

    private void OnExportRequested(object? sender, ExportRequestedEventArgs e) => _ = ExportAsync(e.Kind);

    /// <summary>
    /// ZIP 出力。プレビューで対象・比較元・差分・警告を示し、比較元を選び直せる。実行時はプレビューで見た対象と比較元を照合する。
    /// </summary>
    public async Task<OperationResult?> ExportAsync(ReleaseKind kind)
    {
        if (_gate.Busy) return null;
        DataOperation operation = Operation(kind);
        ReleaseComparison? compareTo = null;
        while (true)
        {
            ReleasePreviewReadModel preview;
            // プレビューの全件検証も取消できる。取消したら出力しない
            CancellationTokenSource? source = await _gate.OnUi(() => _gate.BeginCancelable(operation)).ConfigureAwait(false);
            if (source is null) return null;
            CancellationToken cancellation = source.Token;
            IProgress<CoverageProgress>? progress = _gate.CurrentProgress;
            try
            {
                preview = await (await _runner.RunAsync(() => _handlers.Releases.HandleAsync(new PreviewReleaseQuery(kind, compareTo), cancellation, progress))
                    .ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ShowResult(operation, new OperationResult(OperationStatus.Canceled, _status.Handle().Version, Guid.Empty, OperationStage.NotStarted, [], null));
                return null;
            }
            catch (Exception ex) when (ex is WorkspaceUnreadableException or WorkspaceConflictException or IOException or InvalidOperationException)
            {
                _ui.Post(() => _view.ShowOperationResult(DataDisplayMapper.Unavailable(operation, ex.Message)));
                return null;
            }
            finally
            {
                _ui.Post(() => _gate.EndCancelable(source));
            }

            IReadOnlyList<IssueDisplay> issues = _map.Issues(preview.Issues.Select(i => DataDisplayMapper.Item(i, ValidationOperation.Export)).ToArray());
            ExportPreviewDisplay display = new(
                kind, preview.Blocker, preview.Plan?.CompareTo, preview.Plan?.Candidates ?? [],
                preview.Plan?.Commits.Select(DataDisplayMapper.Revision).ToArray() ?? [],
                DataDisplayMapper.Changes(preview.Plan?.Changes ?? []), DataDisplayMapper.Changes(preview.Plan?.UncommittedChanges ?? []),
                issues, preview.NeedsDraftSave, preview.NeedsApproval);
            ExportDecision decision = await _gate.OnUi(() => _view.ConfirmExport(display)).ConfigureAwait(false);
            if (decision.Action == ExportAction.ChangeCompareTo)
            {
                compareTo = decision.CompareTo;
                continue;
            }
            if (decision.Action == ExportAction.Cancel || preview.Blocker != ReleaseBlocker.None) return null;

            // プレビューで確認した警告は、表示した内容の指紋で承認する
            WarningApproval? approval = preview.NeedsApproval && preview.TargetKey is not null && kind == ReleaseKind.Test
                ? new WarningApproval(new ContentFingerprint(preview.TargetKey), WorkspaceValidator.RuleVersion)
                : null;
            OperationResult? result = await Run(operation, approval).ConfigureAwait(false);
            // 承認待ちの結果だけは示さずに扱う
            if (result?.Status == OperationStatus.ConfirmationRequired && result.Fingerprint is not null)
            {
                // リリース版は E の指紋で承認する（プレビューの対象キーは SHA のため）。
                // 警告はプレビューで示して出力を選んでもらったので、その内容に対する承認とする
                result = await Run(operation, new WarningApproval(result.Fingerprint, WorkspaceValidator.RuleVersion)).ConfigureAwait(false);
            }
            if (result is not null) ShowResult(operation, result);
            _ = LoadPendingExportsAsync();
            return result;

            Task<OperationResult?> Run(DataOperation op, WarningApproval? withApproval) =>
                _gate.RunAsync(op, _ => _handlers.Export.HandleAsync(new ExportReleaseCommand(
                    kind, preview.SessionVersion, withApproval, ReleaseComparison.From(preview.Plan?.CompareTo), preview.TargetKey),
                    _gate.Token, _gate.CurrentProgress), cancelable: true);
        }
    }

    private void OnResumeExportRequested(object? sender, ResumeExportRequestedEventArgs e) =>
        _ = ResumeExportAsync(e.OperationId);

    public async Task<OperationResult?> ResumeExportAsync(Guid operationId)
    {
        OperationResult? result = await _gate.RunAsync(DataOperation.ResumeExport,
            _ => _handlers.Resume.HandleAsync(new ResumeExportCommand(operationId))).ConfigureAwait(false);
        if (result is not null) ShowResult(DataOperation.ResumeExport, result);
        await LoadPendingExportsAsync().ConfigureAwait(false);
        return result;
    }

    private async Task LoadPendingExportsAsync()
    {
        long request = _pendingRequests.Next();
        PendingExportsReadModel pending;
        try
        {
            pending = await (await _runner.RunAsync(() => _handlers.Releases.HandleAsync(new GetPendingExportsQuery())).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            pending = new PendingExportsReadModel([], [new PendingExportProblem(null, "recovery", PendingExportProblemKind.ListUnavailable)]);
        }
        _ui.Post(() =>
        {
            if (!_disposed && _pendingRequests.IsLatest(request)) _view.ShowPendingExports(new PendingExportsDisplay(
                pending.Exports.Select(p => new PendingExportDisplay(p.OperationId, p.Kind, p.Name, p.CreatedAt, p.Stage)).ToArray(), pending.Problems));
        });
    }

    private void ShowResult(DataOperation operation, OperationResult result) => _ui.Post(() =>
    {
        if (!_disposed) _view.ShowOperationResult(_map.Result(operation, result));
    });

    private static DataOperation Operation(ReleaseKind kind) => kind == ReleaseKind.Release ? DataOperation.ExportRelease : DataOperation.ExportTest;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Changed -= OnSessionChanged;
        _gate.BusyChanged -= OnBusyChanged;
        _gate.ProgressChanged -= OnProgressChanged;
        _view.ExportRequested -= OnExportRequested;
        _view.ResumeExportRequested -= OnResumeExportRequested;
        _view.CancelRequested -= OnCancelRequested;
    }
}
