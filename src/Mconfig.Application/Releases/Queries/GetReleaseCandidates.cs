using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Releases.Queries;

/// <summary>比較元になりうるリリースの一覧（GetReleaseCandidates）。現在の HEAD の祖先にある公開完了済みのものだけ。</summary>
public sealed record GetReleaseCandidatesQuery;

/// <summary>中断して再開を待っている出力の一覧。</summary>
public sealed record GetPendingExportsQuery;

/// <summary>出力のプレビュー（PreviewRelease）。ZIP・タグ・記録を作らず、一時保存もしない。</summary>
public sealed record PreviewReleaseQuery(ReleaseKind Kind, ReleaseComparison? CompareTo = null);

/// <summary>出力できない理由。</summary>
public enum ReleaseBlocker { None, ReadOnly, RecoveryPending, Saving, UncommittedChanges, NoCommit, ValidationErrors }

/// <summary>
/// 出力のプレビュー。TargetKey は実行時に ExportReleaseCommand へ渡して照合する値（リリースは対象 SHA、テスト版は E の指紋）。
/// NeedsDraftSave はテスト版で E≠W のとき（出力時に一時保存する）。Plan は出力できる状態でだけ持つ。
/// </summary>
public sealed record ReleasePreviewReadModel(
    ReleaseKind Kind,
    SessionVersion SessionVersion,
    string? TargetKey,
    ReleasePlan? Plan,
    IReadOnlyList<ValidationIssue> Issues,
    bool NeedsDraftSave,
    bool NeedsApproval,
    ReleaseBlocker Blocker);

public sealed class ReleaseQueryHandlers(EditSession session, IReleaseArchive archive)
{
    public Task<IReadOnlyList<ReleaseCandidate>> HandleAsync(GetReleaseCandidatesQuery query, CancellationToken cancellationToken = default) =>
        archive.GetReleaseCandidatesAsync(session.Inspect(s => s.Head), cancellationToken);

    public Task<PendingExportsReadModel> HandleAsync(GetPendingExportsQuery query, CancellationToken cancellationToken = default) =>
        archive.GetPendingExportsAsync(cancellationToken);

    /// <remarks>全件検証は写しに対してロックの外で行う。取消されたら OperationCanceledException を送出する。</remarks>
    public async Task<ReleasePreviewReadModel> HandleAsync(
        PreviewReleaseQuery query, CancellationToken cancellationToken = default, IProgress<CoverageProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        SessionSnapshot snapshot = session.Capture();
        (WorkspaceStatus status, ContentFingerprint edited, RevisionId? head, WorkspaceStamp stamp) =
            (snapshot.Status, snapshot.Edited, snapshot.Head, snapshot.Stamp);
        IReadOnlyList<ValidationIssue> issues = WorkspaceChecks.Validate(snapshot.Content, includeCoverage: true, cancellationToken, progress);

        bool release = query.Kind == ReleaseKind.Release;
        ReleaseBlocker blocker =
            status.IsReadOnly ? ReleaseBlocker.ReadOnly :
            status.IsRecoveryPending ? ReleaseBlocker.RecoveryPending :
            status.IsSaving ? ReleaseBlocker.Saving :
            release && (status.HasUnsavedChanges || status.HasUncommittedChanges) ? ReleaseBlocker.UncommittedChanges :
            release && head is null ? ReleaseBlocker.NoCommit :
            WorkspaceChecks.HasErrors(issues, ValidationOperation.Export) ? ReleaseBlocker.ValidationErrors :
            ReleaseBlocker.None;

        // テスト版は、検証・対象キーと同じ E の写しから計画を作る（E≠W でも保存しない。出力時に E を一時保存して同じ内容を出す）
        ReleasePlan? plan = blocker is ReleaseBlocker.None or ReleaseBlocker.ValidationErrors
            ? await archive.PlanAsync(
                new ReleasePlanRequest(query.Kind, stamp, query.CompareTo, release ? null : snapshot.Content), cancellationToken).ConfigureAwait(false)
            : null;
        return new ReleasePreviewReadModel(
            query.Kind,
            status.Version,
            release ? head?.Value : edited.Value,
            plan,
            issues,
            NeedsDraftSave: !release && status.HasUnsavedChanges,
            NeedsApproval: WorkspaceChecks.NeedsApproval(issues),
            blocker);
    }
}
