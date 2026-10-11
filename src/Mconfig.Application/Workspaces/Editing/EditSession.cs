using Mconfig.Application.Ports;
using Mconfig.Domain.Configuration;

using Mconfig.Application.Workspaces;

namespace Mconfig.Application.Workspaces.Editing;

/// <summary>
/// ワークスペースの編集セッション。編集内容 E を所有する。
/// 編集は Command Handler から <see cref="Apply"/> で、参照は Query Handler から <see cref="Read{T}"/> で行い、
/// どちらも一つのロックで直列化するので Query は固定された E から投影できる。
/// 保存・コミット・復元・出力はワークスペースごとに一つずつ実行する（<see cref="RunExclusiveAsync{T}"/>）。
/// </summary>
public sealed class EditSession
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly EditSessionState _state;
    private readonly WorkspaceFingerprintCache _fingerprints = new();
    private WorkspaceContent _content;
    private SavedContent _saved;
    private WorkspaceStamp _stamp;
    private RevisionId? _head;

    /// <summary>未コミットの初期状態など、確定版がないときの C の指紋。</summary>
    public static ContentFingerprint NoCommit { get; } = new("(no commit)");

    private readonly IOperationLog? _log;

    /// <param name="log">保存・コミット・復元・出力の記録先。null なら記録しない。</param>
    public EditSession(LoadedWorkspace loaded, IOperationLog? log = null)
    {
        _log = log;
        ArgumentNullException.ThrowIfNull(loaded);
        _content = loaded.Working;
        _saved = SavedContent.Capture(loaded.Working);
        _stamp = loaded.Stamp;
        _head = loaded.Head;
        _state = new EditSessionState(
            _fingerprints.Of(loaded.Working),
            loaded.Committed is null ? NoCommit : WorkspaceFingerprints.Of(loaded.Committed));
        // 起動時の構造・参照エラーがあれば、編集の排他を持っていても読取専用で始める
        if (loaded.ReadOnly || StartupValidation.HasErrors(loaded.Working)) _state.SetReadOnly(true);
    }

    public WorkspaceStatus Status
    {
        get { lock (_gate) return _state.Status; }
    }

    /// <summary>
    /// 業務操作を E に適用する。版が期待と違えば競合、編集を受け付けない状態なら拒否とし、何も変更しない。
    /// 業務操作が規則違反（<see cref="PresetOperationException"/>）で失敗した場合も内容は変わらない（業務操作は検証してから変更する）。
    /// それ以外の例外は業務上の拒否ではない（不具合）ので、無効な入力として扱わずに呼出し側へ伝える。
    /// </summary>
    internal CommandResult Apply(SessionVersion expectedVersion, Action<WorkspaceContent> operation)
    {
        return Mutate(() =>
        {
            WorkspaceStatus status = _state.Status;
            if (!status.AcceptsChanges)
                return new CommandResult(CommandStatus.Rejected, status.Version);
            if (expectedVersion != status.Version)
                return new CommandResult(CommandStatus.Conflict, status.Version);

            try
            {
                operation(_content);
            }
            catch (PresetOperationException ex)
            {
                return new CommandResult(CommandStatus.Invalid, status.Version, ex.Failure);
            }

            EditResult result = _state.ApplyEdit(_fingerprints.Of(_content));
            return new CommandResult(
                result == EditResult.Applied ? CommandStatus.Applied : CommandStatus.NoChange, _state.Version);
        });
    }

    /// <summary>
    /// 観測できる状態（E/W/C・読取専用・復旧待ち・保存中）が変わり、版が進んだときの通知。
    /// 状態の更新を終えてロックを外してから、更新した処理のスレッドで通知する。受け取った側は Query で読み直す。
    /// </summary>
    public event EventHandler<SessionChangedEventArgs>? Changed;

    private T Mutate<T>(Func<T> change)
    {
        T result;
        SessionVersion before, after;
        lock (_gate)
        {
            before = _state.Version;
            result = change();
            after = _state.Version;
        }
        if (after != before) Changed?.Invoke(this, new SessionChangedEventArgs(after));
        return result;
    }

    private void Mutate(Action change) => Mutate(() =>
    {
        change();
        return 0;
    });

    /// <summary>同じ時点の状態・E/W/C の指紋・内容・HEAD から投影する。投影は内容を変更してはならない。</summary>
    internal T Inspect<T>(Func<SessionSnapshot, T> projection)
    {
        lock (_gate)
        {
            return projection(new SessionSnapshot(
                _state.Status, _state.Edited, _state.Working, _state.Committed, _content, _head, _stamp, _saved));
        }
    }

    /// <summary>
    /// 重い検証のための写し。ロックの中では設定の一覧を写すだけにし、検証はロックの外で写しに対して行う。
    /// 状態と指紋は写した時点のもので、その後の編集は保存開始時の版の照合で競合になる。
    /// </summary>
    internal SessionSnapshot Capture() => Inspect(s => s with { Content = s.Content.Snapshot() });

    /// <summary>E から読み取り専用の結果を投影する。投影は E を変更してはならない。</summary>
    internal (T Result, SessionVersion Version) Read<T>(Func<WorkspaceContent, T> projection)
    {
        lock (_gate) return (projection(_content), _state.Version);
    }

    /// <summary>
    /// 書込系の操作を一つずつ実行し、結果と所要時間を記録する。予期しない例外は記録してから呼出し側へ伝える。
    /// </summary>
    internal Task<OperationResult> RunOperationAsync(string operation, Func<Task<OperationResult>> run) =>
        RunExclusiveAsync(async () =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                OperationResult result = await run().ConfigureAwait(false);
                _log?.Completed(new OperationLogEntry(
                    operation, result.OperationId, result.Version.Value, result.Status, result.CompletedStage,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started), result.Revision?.Value, result.Issues.Count));
                return result;
            }
            catch (Exception ex)
            {
                _log?.Failed(operation, System.Diagnostics.Stopwatch.GetElapsedTime(started), ex);
                throw;
            }
        });

    /// <summary>
    /// 書込系の操作（保存・確定保存・復元・出力・出力の再開など）を実行中か。ZIP 出力のように E/W/C を変えない操作も含む。
    /// 終了はこれが終わるまで受け付けない。
    /// </summary>
    public bool IsRunningOperation => _writer.CurrentCount == 0;

    /// <summary>書込系の操作を、ワークスペースごとに一つずつ実行する（ARC-005 の書込調整役）。</summary>
    internal async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation)
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// 保存の対象を固定する。版が expectedVersion と同じで、編集を受け付ける状態のときだけ開始できる。
    /// 開始後は完了か失敗を報告するまで、編集を受け付けない（内容は変わらない）。
    /// </summary>
    internal SavePlan? BeginSave(SessionVersion expectedVersion)
    {
        return Mutate(() =>
        {
            SaveTicket? ticket = _state.BeginSave(expectedVersion);
            return ticket is null ? null : new SavePlan(ticket, _content, _stamp, _head, _state.Edited);
        });
    }

    internal void CompleteDraftSave(SavePlan plan, WorkspaceStamp stamp)
    {
        Mutate(() =>
        {
            _state.CompleteDraftSave(plan.Ticket);
            _saved = SavedContent.Capture(plan.Content);
            _stamp = stamp;
        });
    }

    internal void CompleteCommit(SavePlan plan, WorkspaceStamp stamp, RevisionId head)
    {
        Mutate(() =>
        {
            _state.CompleteCommit(plan.Ticket);
            _saved = SavedContent.Capture(plan.Content);
            _stamp = stamp;
            _head = head;
        });
    }

    internal void FailCommitAfterDraftSaved(SavePlan plan, WorkspaceStamp stamp, bool recoveryPending = false)
    {
        Mutate(() =>
        {
            _state.FailCommitAfterDraftSaved(plan.Ticket);
            _saved = SavedContent.Capture(plan.Content);
            _stamp = stamp;
            if (recoveryPending) _state.SetRecoveryPending(true);
        });
    }

    internal void CompleteRestore(SavePlan plan, WorkspaceContent restored, WorkspaceStamp stamp)
    {
        Mutate(() =>
        {
            _state.CompleteRestore(plan.Ticket, _fingerprints.Of(restored));
            _content = restored;
            _saved = SavedContent.Capture(restored);
            _stamp = stamp;
        });
    }

    internal void FailSave(SavePlan plan, bool recoveryPending = false)
    {
        Mutate(() =>
        {
            _state.FailSave(plan.Ticket);
            if (recoveryPending) _state.SetRecoveryPending(true);
        });
    }

    /// <summary>
    /// ディスクから読み直した内容で E と W を置き換える（未保存編集の破棄・再読込）。
    /// ファイルが自分の保存以降に外部で変わっていれば、黙って差し替えずに false を返す。
    /// </summary>
    internal bool TryReplaceWithReloaded(LoadedWorkspace loaded, SessionVersion expectedVersion)
    {
        return Mutate(() =>
        {
            if (_state.Status.IsSaving || expectedVersion != _state.Version || loaded.Stamp != _stamp) return false;
            _content = loaded.Working;
            _saved = SavedContent.Capture(loaded.Working);
            _head = loaded.Head;
            _state.Replace(
                _fingerprints.Of(loaded.Working),
                loaded.Committed is null ? NoCommit : WorkspaceFingerprints.Of(loaded.Committed));
            return true;
        });
    }
}

/// <summary>保存開始時に固定した対象。Content は保存中に変わらない（編集を受け付けないため）。</summary>
internal sealed record SavePlan(
    SaveTicket Ticket, WorkspaceContent Content, WorkspaceStamp Stamp, RevisionId? Head, ContentFingerprint Fingerprint);

/// <summary>
/// ある時点のセッションの状態。Content は業務操作を通じてだけ変わる E そのもので、投影中にだけ使う。
/// Saved は一時保存 W の時点の設定で、未保存の変更の件数と変更前の値を示すのに使う。
/// </summary>
internal sealed record SessionSnapshot(
    WorkspaceStatus Status,
    ContentFingerprint Edited,
    ContentFingerprint Working,
    ContentFingerprint Committed,
    WorkspaceContent Content,
    RevisionId? Head,
    WorkspaceStamp Stamp,
    SavedContent Saved);

/// <summary>セッションの版が進んだことの通知。</summary>
public sealed class SessionChangedEventArgs(SessionVersion version) : EventArgs
{
    public SessionVersion Version { get; } = version;
}
