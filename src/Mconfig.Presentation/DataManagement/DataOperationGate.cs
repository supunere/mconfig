using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Application.Workspaces.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.Common;

namespace Mconfig.Presentation.DataManagement;

/// <summary>
/// 「保存と履歴」と「出力」の画面が共有する操作の実行役。保存系の操作は画面をまたいで一度に一つだけ実行し、
/// 全件検証（単独の検証、履歴に保存・出力の中の検証、未割当の書出し）は一つだけを取消・進捗表示の対象にする。
/// 状態は UI スレッドで読み書きし、変わったら <see cref="BusyChanged"/>・<see cref="ProgressChanged"/> で知らせる。
/// </summary>
public sealed class DataOperationGate : IDisposable
{
    private readonly GetWorkspaceStatusHandler _status;
    private readonly IQueryRunner _runner;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _cancel;
    private bool _disposed;

    public DataOperationGate(EditSession session, IQueryRunner runner, IUiDispatcher ui)
    {
        _status = new GetWorkspaceStatusHandler(session ?? throw new ArgumentNullException(nameof(session)));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <summary>保存・確定保存・復元・出力などを実行中か。</summary>
    public bool Busy { get; private set; }

    /// <summary>実行中の全件検証などの進捗。なければ null。</summary>
    public ProgressDisplay? Progress { get; private set; }

    public event EventHandler? BusyChanged;
    public event EventHandler? ProgressChanged;

    /// <summary>実行中の取消できる処理のトークンと進捗の通知先（処理の中から使う）。</summary>
    public CancellationToken Token => _cancel?.Token ?? CancellationToken.None;

    public IProgress<CoverageProgress>? CurrentProgress { get; private set; }

    /// <summary>取消できる処理を始める（UI スレッド）。ほかに実行中なら null。</summary>
    public CancellationTokenSource? BeginCancelable(DataOperation? operation)
    {
        if (_disposed || _cancel is not null) return null;
        _cancel = new CancellationTokenSource();
        CurrentProgress = new UiProgress(this, operation);
        SetProgress(new ProgressDisplay(operation, 0, 0, CanCancel: true));
        return _cancel;
    }

    /// <summary>始めた処理のものなら、取消と進捗の表示を終える（UI スレッド）。</summary>
    public void EndCancelable(CancellationTokenSource? source)
    {
        if (source is null || !ReferenceEquals(source, _cancel)) return;
        _cancel.Dispose();
        _cancel = null;
        CurrentProgress = null;
        SetProgress(null);
    }

    /// <summary>実行中の全件検証をやめる。</summary>
    public void Cancel() => _cancel?.Cancel();

    /// <summary>
    /// 保存系の操作を一つずつ実行する。期待版は開始時点の版。ほかの操作を実行中なら始めずに null を返す。結果は呼出し側が示す。
    /// </summary>
    /// <remarks>
    /// cancelable なら、中の全件検証を取消でき、進捗を示す（run は <see cref="Token"/> と <see cref="CurrentProgress"/> を使う）。
    /// 単独の検証を実行中なら、それをやめてから始める。
    /// </remarks>
    public async Task<OperationResult?> RunAsync(
        DataOperation operation, Func<SessionVersion, Task<OperationResult>> run, bool cancelable = false)
    {
        CancellationTokenSource? source = null;
        bool started = await OnUi(() =>
        {
            if (Busy || _disposed) return false;
            Busy = true;
            if (cancelable)
            {
                if (_cancel is { } running)
                {
                    running.Cancel();
                    EndCancelable(running);
                }
                source = BeginCancelable(operation);
            }
            BusyChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }).ConfigureAwait(false);
        if (!started) return null;

        try
        {
            SessionVersion version = _status.Handle().Version;
            return await (await _runner.RunAsync(() => run(version)).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            _ui.Post(() =>
            {
                Busy = false;
                EndCancelable(source);
                if (!_disposed) BusyChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    /// <summary>UI スレッドで実行して結果を受け取る（確認の問い合わせなど）。</summary>
    public Task<T> OnUi<T>(Func<T> action)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private void SetProgress(ProgressDisplay? progress)
    {
        Progress = progress;
        if (!_disposed) ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>進捗を UI スレッドへ知らせる。表示の更新は 0.1 秒に 1 回まで（完了は必ず知らせる）。</summary>
    private sealed class UiProgress(DataOperationGate owner, DataOperation? operation) : IProgress<CoverageProgress>
    {
        private long _lastTicks;

        public void Report(CoverageProgress value)
        {
            long now = Environment.TickCount64;
            if (!value.Completed && now - Interlocked.Read(ref _lastTicks) < 100) return;
            Interlocked.Exchange(ref _lastTicks, now);
            owner._ui.Post(() =>
            {
                if (!owner._disposed && owner._cancel is not null)
                    owner.SetProgress(new ProgressDisplay(operation, value.Checked, value.Total, CanCancel: true));
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancel?.Cancel();
    }
}
