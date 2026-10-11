using Mconfig.Application.Workspaces.Editing;

namespace Mconfig.Presentation.Common;

/// <summary>
/// Query の結果を画面へ出す Presenter の共通部分。セッションの変更通知で読み直し、
/// Query はバックグラウンドで実行して UI スレッドへ戻す。古い要求の結果と、現在の版と異なる版の結果は捨てる。
/// 変更通知を受けたら、読み直しを待たずに実行中の要求を無効にする（待ち時間の間に変更前の結果を表示しない）。
/// 破棄後は通知を受けても何もしない。
/// </summary>
public abstract class QueryPresenter<TModel> : IDisposable where TModel : class
{
    private readonly EditSession _session;
    private readonly IQueryRunner _runner;
    private readonly IUiDispatcher _ui;
    private readonly RequestTracker _requests = new();
    private readonly RequestTracker _changes = new();
    private SessionVersion _shown = SessionVersion.Initial;
    private bool _disposed;
    private bool _active = true;
    private readonly object _queryGate = new();
    private CancellationTokenSource? _queryCancellation;

    protected QueryPresenter(EditSession session, IQueryRunner runner, IUiDispatcher ui, DisplayLanguage language)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        Language = language ?? throw new ArgumentNullException(nameof(language));
        _session.Changed += OnSessionChanged;
        Language.Changed += OnLanguageChanged;
    }

    protected DisplayLanguage Language { get; }

    /// <summary>最後に表示したリードモデル。</summary>
    protected TModel? Current { get; private set; }

    /// <summary>
    /// 画面の選択・フィルター・変更通知に応じて読み直す。完了した結果が最新の要求で、版が現在のセッションの版と同じときだけ表示する
    /// （版が進んでいれば、その変更通知による読み直しが後から表示する）。
    /// </summary>
    public Task RefreshAsync()
    {
        if (_disposed || !_active) return Task.CompletedTask;
        long request = _requests.Next();
        CancellationTokenSource cancellation = new();
        lock (_queryGate)
        {
            _queryCancellation?.Cancel();
            _queryCancellation = cancellation;
        }
        Task<TModel> query = _runner.RunAsync(() => Query(cancellation.Token));
        return query.ContinueWith(t =>
        {
            lock (_queryGate)
            {
                if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null;
                cancellation.Dispose();
            }
            if (t.IsFaulted || t.IsCanceled) return;
            TModel model = t.Result;
            _ui.Post(() =>
            {
                if (_disposed || !_active || !_requests.IsLatest(request)) return;
                SessionVersion version = VersionOf(model);
                if (version < _shown || version != _session.Status.Version) return;
                _shown = version;
                Current = model;
                Render(model);
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>同じ時点の E から投影する Query（バックグラウンドで実行される）。</summary>
    protected abstract TModel Query();

    protected virtual TModel Query(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Query();
    }

    /// <summary>非表示の画面は照会を停止し、再表示時に最新の版を読み直す。</summary>
    public Task SetActiveAsync(bool active)
    {
        if (_active == active || _disposed) return Task.CompletedTask;
        _active = active;
        _changes.Next();
        InvalidateQuery();
        return active ? RefreshAsync() : Task.CompletedTask;
    }

    private void InvalidateQuery()
    {
        _requests.Next();
        lock (_queryGate) _queryCancellation?.Cancel();
    }

    protected abstract SessionVersion VersionOf(TModel model);

    /// <summary>UI スレッドで表示する。</summary>
    protected abstract void Render(TModel model);

    /// <summary>
    /// 変更通知から読み直すまでの待ち時間。0 より大きければ、続けて届いた通知は最後の通知から待ってまとめて読み直す
    /// （重い検証を伴う Query で、連続編集のたびに走らせないため）。
    /// </summary>
    protected virtual TimeSpan ChangeDelay => TimeSpan.Zero;

    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        // 変更前の E から投影している要求の結果を、次の読み直しまでの待ち時間の間に表示しない
        InvalidateQuery();
        if (!_active) return;
        _ = RefreshAfterChangeAsync();
    }

    private async Task RefreshAfterChangeAsync()
    {
        long change = _changes.Next();
        if (ChangeDelay > TimeSpan.Zero)
        {
            await Task.Delay(ChangeDelay).ConfigureAwait(false);
            if (!_changes.IsLatest(change)) return;
        }
        await RefreshAsync().ConfigureAwait(false);
    }

    // 言語の切替は表示名だけを変えるので、読み直さずに手元の結果を表示し直す
    private void OnLanguageChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (!_disposed && _active && Current is not null) Render(Current);
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changes.Next();
        InvalidateQuery();
        _session.Changed -= OnSessionChanged;
        Language.Changed -= OnLanguageChanged;
        OnDisposed();
        GC.SuppressFinalize(this);
    }

    /// <summary>破棄時に、派生クラスが View の購読などを解除する。</summary>
    protected virtual void OnDisposed()
    {
    }
}
