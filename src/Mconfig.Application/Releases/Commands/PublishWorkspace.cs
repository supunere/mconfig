using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Commands;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Releases.Commands;

public sealed record PublishWorkspaceCommand(string Reason, string Author, SessionVersion ExpectedVersion,
    bool ConfirmShortReason = false);

/// <summary>検証→保存→ZIP作成→履歴の確定を一つの書込操作として実行する。</summary>
public sealed class PublishWorkspaceHandler(EditSession session, IWorkspaceStore store, IPublicationStore publications)
{
    public Task<OperationResult> HandleAsync(PublishWorkspaceCommand command, CancellationToken cancellationToken = default,
        IProgress<CoverageProgress>? progress = null) => session.RunOperationAsync("Publish", async () =>
    {
        Guid operation = Guid.NewGuid();
        SessionSnapshot snapshot = session.Capture();
        if (WorkspaceChecks.CheckWritable(snapshot, command.ExpectedVersion, operation) is { } stopped) return stopped;
        bool existingVersion = snapshot.Head is not null && snapshot.Edited == snapshot.Working && snapshot.Working == snapshot.Committed;
        ValidationCode? reason = existingVersion ? null : CommitReason.Check(command.Reason);
        if (reason == ValidationCode.ReasonEmpty)
            return WorkspaceChecks.Stop(snapshot, OperationStatus.ValidationFailed, operation,
                [new ValidationIssue(ValidationCode.ReasonEmpty, new(""), [])]);
        IReadOnlyList<ValidationIssue> issues;
        try { issues = WorkspaceChecks.Validate(snapshot.Content, true, cancellationToken, progress); }
        catch (OperationCanceledException) { return WorkspaceChecks.Stop(snapshot, OperationStatus.Canceled, operation); }
        if (issues.Any(i => ValidationPolicy.SeverityFor(i.Code, ValidationOperation.Commit) is ValidationSeverity.Error or ValidationSeverity.Warning))
            return WorkspaceChecks.Stop(snapshot, OperationStatus.ValidationFailed, operation, issues);
        if (reason == ValidationCode.ReasonTooShort && !command.ConfirmShortReason)
            return WorkspaceChecks.Stop(snapshot, OperationStatus.ConfirmationRequired, operation,
                [new ValidationIssue(ValidationCode.ReasonTooShort, new(""), [])]);
        if (cancellationToken.IsCancellationRequested) return WorkspaceChecks.Stop(snapshot, OperationStatus.Canceled, operation);
        SavePlan? plan = session.BeginSave(command.ExpectedVersion);
        if (plan is null)
            return WorkspaceChecks.Result(session, WorkspaceChecks.WhyNotStarted(session.Status), operation, OperationStage.NotStarted);
        (WorkspaceStamp? saved, OperationResult? failure) =
            await SaveOperations.SaveAsync(session, store, plan, plan.Content, operation, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;
        try
        {
            // 書込開始後は取消しない。ZIPと履歴の確定が終わるまで編集も受け付けない。
            PublicationOutcome output = await publications.PublishAsync(
                new(command.Reason, command.Author, operation, plan.Head, saved!, existingVersion), CancellationToken.None).ConfigureAwait(false);
            session.CompleteCommit(plan, output.Stamp, output.Revision);
            return new(OperationStatus.Succeeded, session.Status.Version, operation, OperationStage.Exported,
                issues, snapshot.Edited, output.Revision, output.Path,
                Reason: existingVersion ? OperationReason.ExistingVersionExported : null, FixedPublicationMetadata: output.FixedMetadata);
        }
        catch (Exception ex)
        {
            var classified = SaveOperations.Classify(ex);
            session.FailCommitAfterDraftSaved(plan, saved!, classified.RecoveryPending);
            return WorkspaceChecks.Result(session, classified.Status, operation, OperationStage.DraftSaved, issues, snapshot.Edited, ex.Message);
        }
    });
}
