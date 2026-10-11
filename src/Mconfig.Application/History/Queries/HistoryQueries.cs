using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.History.Queries;

/// <summary>履歴の段階的な取得。StartAfter は前回取得した最後の版。</summary>
public sealed record GetHistoryQuery(int MaxCount = 100, RevisionId? StartAfter = null);

public sealed record GetRevisionDiffQuery(RevisionId Revision);

/// <summary>ある版で何が変わったか。比較元は親の版（初期コミットは空の内容）で、それぞれ当時の定義で比較する。</summary>
public sealed record RevisionDiffReadModel(RevisionInfo Revision, IReadOnlyList<ContentChange> Changes);

/// <summary>前回の履歴保存（HEAD）から、いまの編集内容 E までの変更（「履歴に保存」の前に確かめる）。</summary>
public sealed record GetUncommittedChangesQuery;

/// <summary>選んだ版から、いまの編集内容 E までの違い（「現在との違い」）。</summary>
public sealed record GetDifferenceFromCurrentQuery(RevisionId Revision);

/// <summary>比較の結果。Version は比べた E の版。</summary>
public sealed record ChangesReadModel(SessionVersion Version, IReadOnlyList<ContentChange> Changes);

/// <summary>復元のプレビュー。作業ツリーの取り出しやファイル置換、HEAD の移動は行わない。</summary>
public sealed record PreviewRestoreQuery(RevisionId Revision);

/// <summary>
/// Changes は現在の編集内容 E から対象の版への差分。CanRestore はいまの状態のまま復元できること。
/// 未確定の変更（W≠C）だけがあるときは、それを退避・破棄して復元できる（CanRestoreByDiscarding）。
/// 未保存の編集（E≠W）は先に一時保存か破棄をしてもらう。
/// </summary>
public sealed record RestorePreviewReadModel(
    RevisionId Revision,
    SessionVersion SessionVersion,
    ContentFingerprint Fingerprint,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<ContentChange> Changes,
    bool CanRestore)
{
    public bool HasUnsavedChanges { get; init; }
    public bool HasUncommittedChanges { get; init; }
    public bool CanRestoreByDiscarding { get; init; }
}

/// <summary>履歴の参照（GetHistory / GetRevisionDiff / PreviewRestore）。どれもセッションとファイルを変更しない。</summary>
public sealed class HistoryQueryHandlers(EditSession session, IRevisionStore revisions)
{
    // 版の内容は変わらないので、HEAD の内容は版が変わるまで使い回す（編集のたびに読み直さない）
    private readonly object _cacheGate = new();
    private (RevisionId Id, WorkspaceContent Content)? _head;

    public Task<IReadOnlyList<RevisionInfo>> HandleAsync(GetHistoryQuery query, CancellationToken cancellationToken = default) =>
        revisions.GetHistoryAsync(query.MaxCount, query.StartAfter, cancellationToken);

    public async Task<RevisionDiffReadModel> HandleAsync(GetRevisionDiffQuery query, CancellationToken cancellationToken = default)
    {
        RevisionInfo info = await revisions.GetRevisionAsync(query.Revision, cancellationToken).ConfigureAwait(false);
        WorkspaceContent after = await revisions.ReadAsync(info.Id, cancellationToken).ConfigureAwait(false);
        WorkspaceContent before = info.Parent is null
            ? WorkspaceDiff.Empty
            : await revisions.ReadAsync(info.Parent, cancellationToken).ConfigureAwait(false);
        return new RevisionDiffReadModel(info, WorkspaceDiff.Compare(before, after));
    }

    /// <summary>
    /// HEAD の版を読み、E と比べる。まだ履歴に保存していなければ空の内容と比べる。
    /// HEAD・E・版は同じ時点で写して固定し、読込中に確定保存されても、古い HEAD との差分に新しい版を付けない。
    /// </summary>
    public async Task<ChangesReadModel> HandleAsync(GetUncommittedChangesQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        SessionSnapshot snapshot = session.Capture();
        WorkspaceContent committed = snapshot.Head is null
            ? WorkspaceDiff.Empty
            : await ReadHeadAsync(snapshot.Head, cancellationToken).ConfigureAwait(false);
        return new ChangesReadModel(snapshot.Status.Version, WorkspaceDiff.Compare(committed, snapshot.Content));
    }

    private async Task<WorkspaceContent> ReadHeadAsync(RevisionId head, CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_head is { } cached && cached.Id == head) return cached.Content;
        }
        WorkspaceContent content = await revisions.ReadAsync(head, cancellationToken).ConfigureAwait(false);
        lock (_cacheGate) _head = (head, content);
        return content;
    }

    public async Task<ChangesReadModel> HandleAsync(GetDifferenceFromCurrentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        WorkspaceContent revision = await revisions.ReadAsync(query.Revision, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<ContentChange> changes, SessionVersion version) = session.Read(current => WorkspaceDiff.Compare(revision, current));
        return new ChangesReadModel(version, changes);
    }

    public async Task<RestorePreviewReadModel> HandleAsync(PreviewRestoreQuery query, CancellationToken cancellationToken = default)
    {
        WorkspaceContent target = await revisions.ReadAsync(query.Revision, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<ContentChange> changes, SessionVersion version) = session.Read(current => WorkspaceDiff.Compare(current, target));
        WorkspaceStatus status = session.Status;
        IReadOnlyList<ValidationIssue> issues = WorkspaceValidator.Validate(target.Settings, target.Spec, target.Schemas);
        bool possible = status.Version == version && !status.IsSaving && !status.IsRecoveryPending && !status.IsReadOnly &&
            !issues.Any(i => ValidationPolicy.SeverityFor(i.Code, ValidationOperation.Edit) == ValidationSeverity.Error);
        return new RestorePreviewReadModel(query.Revision, version, WorkspaceFingerprints.Of(target), issues, changes,
            possible && !status.HasUnsavedChanges && !status.HasUncommittedChanges)
        {
            HasUnsavedChanges = status.HasUnsavedChanges,
            HasUncommittedChanges = status.HasUncommittedChanges,
            CanRestoreByDiscarding = possible && !status.HasUnsavedChanges && status.HasUncommittedChanges,
        };
    }
}
