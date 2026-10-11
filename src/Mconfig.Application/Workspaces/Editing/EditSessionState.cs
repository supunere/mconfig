namespace Mconfig.Application.Workspaces.Editing;

public enum EditResult
{
    Applied,
    /// <summary>内容が変わらなかった。版も進まない。</summary>
    NoChange,
    /// <summary>保存中・読取専用・復旧待ちのため受け付けなかった。</summary>
    Rejected,
}

/// <summary>保存の開始時に固定した対象。完了・失敗の報告に使う。</summary>
public sealed class SaveTicket
{
    internal SaveTicket(ContentFingerprint content, SessionVersion startedAt)
    {
        Content = content;
        StartedAt = startedAt;
    }

    /// <summary>保存する編集内容 E の指紋。保存中は編集を受け付けないので、完了時も同じ内容を指す。</summary>
    public ContentFingerprint Content { get; }

    public SessionVersion StartedAt { get; }
}

/// <summary>
/// 編集内容 E、一時保存 W、確定版 C の状態遷移。内容そのものは持たず、指紋で比較する。
/// 同時に呼び出さないこと。書込の直列化はワークスペースごとの書込調整役が担う。
/// </summary>
public sealed class EditSessionState
{
    private ContentFingerprint _edited;
    private ContentFingerprint _working;
    private ContentFingerprint _committed;
    private SaveTicket? _saving;
    private bool _readOnly;
    private bool _recoveryPending;

    /// <summary>読み込んだ W と C から始める。E は W と同じ内容になる。</summary>
    public EditSessionState(ContentFingerprint working, ContentFingerprint committed)
    {
        _edited = _working = working ?? throw new ArgumentNullException(nameof(working));
        _committed = committed ?? throw new ArgumentNullException(nameof(committed));
    }

    public SessionVersion Version { get; private set; } = SessionVersion.Initial;

    /// <summary>編集内容 E の指紋。</summary>
    public ContentFingerprint Edited => _edited;

    /// <summary>一時保存 W の指紋。</summary>
    public ContentFingerprint Working => _working;

    /// <summary>確定版 C の指紋。</summary>
    public ContentFingerprint Committed => _committed;

    public WorkspaceStatus Status => new(
        Version,
        HasUnsavedChanges: _edited != _working,
        HasUncommittedChanges: _working != _committed,
        IsSaving: _saving is not null,
        IsReadOnly: _readOnly,
        IsRecoveryPending: _recoveryPending);

    private bool AcceptsChanges => _saving is null && !_readOnly && !_recoveryPending;

    /// <summary>編集コマンドの成功を反映する。E だけが変わり、W と C は維持する。</summary>
    public EditResult ApplyEdit(ContentFingerprint edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        if (!AcceptsChanges) return EditResult.Rejected;
        if (edited == _edited) return EditResult.NoChange;

        _edited = edited;
        Advance();
        return EditResult.Applied;
    }

    /// <summary>
    /// 一時保存・確定保存を始め、保存対象の版を固定する。以後、完了か失敗を報告するまで編集を受け付けない。
    /// 呼出し側が見ていた版と現在の版が異なる場合（その間に状態が変わった場合）は null を返す。
    /// </summary>
    public SaveTicket? BeginSave(SessionVersion expectedVersion)
    {
        if (!AcceptsChanges || expectedVersion != Version) return null;

        _saving = new SaveTicket(_edited, Version);
        Advance();
        return _saving;
    }

    /// <summary>一時保存の成功。W を保存した内容にする。内容が同じでも W を書き直したので版は進む。</summary>
    public void CompleteDraftSave(SaveTicket ticket)
    {
        EndSave(ticket);
        _working = ticket.Content;
        Advance();
    }

    /// <summary>確定保存（検証→一時保存→コミット）の成功。E=W=C になる。</summary>
    public void CompleteCommit(SaveTicket ticket)
    {
        EndSave(ticket);
        _working = _committed = ticket.Content;
        Advance();
    }

    /// <summary>一時保存は済んだがコミットに失敗した。E=W で、W≠C（履歴未完了）のまま残る。</summary>
    public void FailCommitAfterDraftSaved(SaveTicket ticket)
    {
        EndSave(ticket);
        _working = ticket.Content;
        Advance();
    }

    /// <summary>版の復元を W に適用した。E=W は復元した内容になり、C（現 HEAD）は維持する。</summary>
    public void CompleteRestore(SaveTicket ticket, ContentFingerprint restored)
    {
        ArgumentNullException.ThrowIfNull(restored);
        EndSave(ticket);
        _edited = _working = restored;
        Advance();
    }

    /// <summary>何も書き込めずに保存が失敗した。E/W/C は変わらず、保存中の状態だけが終わる。</summary>
    public void FailSave(SaveTicket ticket)
    {
        EndSave(ticket);
        Advance();
    }

    /// <summary>再読込・版の復元・復旧によってセッションの内容を置き換える。E は新しい W と同じになる。</summary>
    public void Replace(ContentFingerprint working, ContentFingerprint committed)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(committed);
        if (_saving is not null)
            throw new InvalidOperationException("保存中はセッションを置き換えられません。");

        _edited = _working = working;
        _committed = committed;
        Advance();
    }

    public void SetReadOnly(bool readOnly)
    {
        if (_readOnly == readOnly) return;
        _readOnly = readOnly;
        Advance();
    }

    public void SetRecoveryPending(bool recoveryPending)
    {
        if (_recoveryPending == recoveryPending) return;
        _recoveryPending = recoveryPending;
        Advance();
    }

    private void EndSave(SaveTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!ReferenceEquals(ticket, _saving))
            throw new InvalidOperationException("進行中の保存ではありません。");
        _saving = null;
    }

    private void Advance() => Version = Version.Next();
}
