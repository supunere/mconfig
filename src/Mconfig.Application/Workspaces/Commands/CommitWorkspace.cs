using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Workspaces.Commands;

/// <summary>
/// 確定保存。Approval は確認付き警告の承認、ConfirmShortReason は短い理由の確認。
/// </summary>
public sealed record CommitWorkspaceCommand(
    string Reason,
    string Author,
    SessionVersion ExpectedVersion,
    WarningApproval? Approval = null,
    bool ConfirmShortReason = false,
    bool RejectWarnings = false);

public sealed class CommitWorkspaceHandler(EditSession session, IWorkspaceStore store, IRevisionStore revisions)
{
    /// <remarks>
    /// 全件検証は写しに対してロックの外で行い、進捗を progress に知らせる。検証の途中で取消されたら何も書き込まずに Canceled を返す
    /// （検証未完了で履歴に保存しない）。書込を始めた後は取消で中断しない。
    /// </remarks>
    public Task<OperationResult> HandleAsync(
        CommitWorkspaceCommand command, CancellationToken cancellationToken = default, IProgress<CoverageProgress>? progress = null) =>
        session.RunOperationAsync("Commit", async () =>
        {
            Guid operationId = Guid.NewGuid();
            ValidationCode? reasonIssue = CommitReason.Check(command.Reason);
            if (reasonIssue == ValidationCode.ReasonEmpty)
                return WorkspaceChecks.Result(session, OperationStatus.ValidationFailed, operationId, OperationStage.NotStarted,
                    [new ValidationIssue(ValidationCode.ReasonEmpty, new(""), [])]);

            // 検証は保存対象と同じ版の E の写しに対して行う
            (OperationResult? precheck, ContentFingerprint fingerprint, IReadOnlyList<ValidationIssue> issues) =
                Precheck(session.Capture(), command, operationId, cancellationToken, progress);
            if (precheck is not null) return precheck;

            if (reasonIssue == ValidationCode.ReasonTooShort && !command.ConfirmShortReason)
                return WorkspaceChecks.Result(session, OperationStatus.ConfirmationRequired, operationId, OperationStage.NotStarted,
                    [.. issues, new ValidationIssue(ValidationCode.ReasonTooShort, new(""), [])], fingerprint);
            if (cancellationToken.IsCancellationRequested)
                return WorkspaceChecks.Result(session, OperationStatus.Canceled, operationId, OperationStage.NotStarted);

            SavePlan? plan = session.BeginSave(command.ExpectedVersion);
            if (plan is null)
                return WorkspaceChecks.Result(session, WorkspaceChecks.WhyNotStarted(session.Status), operationId, OperationStage.NotStarted);

            (WorkspaceStamp? stamp, OperationResult? failure) =
                await SaveOperations.SaveAsync(session, store, plan, plan.Content, operationId, cancellationToken).ConfigureAwait(false);
            if (failure is not null) return failure;

            // 書込を始めたら取消で中断しない。コミットは完了か失敗まで進める
            CommitOutcome outcome;
            try
            {
                outcome = await revisions.CommitAsync(
                    new CommitRequest(command.Reason.Trim(), command.Author, operationId, plan.Head, stamp!), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (OperationStatus status, bool recovery) = SaveOperations.Classify(ex);
                // 一時保存は済んだので E=W、C は旧版のまま（履歴保存未完了）
                session.FailCommitAfterDraftSaved(plan, stamp!, recovery);
                return WorkspaceChecks.Result(session, status, operationId, OperationStage.DraftSaved, issues, fingerprint, ex.Message);
            }

            session.CompleteCommit(plan, outcome.Stamp, outcome.Revision);
            return new OperationResult(OperationStatus.Succeeded, session.Status.Version, operationId, OperationStage.Committed,
                issues, fingerprint, outcome.Revision);
        });

    private static (OperationResult? Result, ContentFingerprint Fingerprint, IReadOnlyList<ValidationIssue> Issues) Precheck(
        SessionSnapshot snapshot, CommitWorkspaceCommand command, Guid operationId, CancellationToken cancellationToken,
        IProgress<CoverageProgress>? progress)
    {
        if (WorkspaceChecks.CheckWritable(snapshot, command.ExpectedVersion, operationId) is { } stopped)
            return (stopped, snapshot.Edited, []);
        // E=W=C なら空のコミットを作らない
        if (snapshot.Edited == snapshot.Working && snapshot.Working == snapshot.Committed)
            return (WorkspaceChecks.Stop(snapshot, OperationStatus.NoChanges, operationId), snapshot.Edited, []);

        (OperationResult? result, IReadOnlyList<ValidationIssue> issues) = WorkspaceChecks.ValidateForWrite(
            snapshot, ValidationOperation.Commit, command.Approval, operationId, cancellationToken, progress);
        if (command.RejectWarnings && issues.Any(i => ValidationPolicy.SeverityFor(i.Code, ValidationOperation.Commit) == ValidationSeverity.Warning))
            return (WorkspaceChecks.Stop(snapshot, OperationStatus.ValidationFailed, operationId, issues), snapshot.Edited, issues);
        return (result, snapshot.Edited, issues);
    }
}
