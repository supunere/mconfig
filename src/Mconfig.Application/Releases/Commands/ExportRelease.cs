using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Commands;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Releases.Commands;

/// <summary>
/// テスト版・リリース版 ZIP の出力。ExpectedTargetKey と CompareTo はプレビューで見た値で、
/// 実行直前に照合する（リリースは対象 SHA、テスト版は出力する内容 E の指紋）。
/// </summary>
public sealed record ExportReleaseCommand(
    ReleaseKind Kind,
    SessionVersion ExpectedVersion,
    WarningApproval? Approval = null,
    ReleaseComparison? CompareTo = null,
    string? ExpectedTargetKey = null);

/// <summary>
/// リリース版は E=W=C で確定版（HEAD）があることを要求する。テスト版は E≠W なら、検証と承認の後に一時保存してから
/// W を固定して出力する（コミットはしないので理由は要求しない）。読取専用・復旧待ち・内容エラーでは出力しない。
/// </summary>
public sealed class ExportReleaseHandler(EditSession session, IWorkspaceStore store, IReleaseArchive archive)
{
    /// <remarks>全件検証は写しに対してロックの外で行い、途中で取消されたら何も出力せずに Canceled を返す。</remarks>
    public Task<OperationResult> HandleAsync(
        ExportReleaseCommand command, CancellationToken cancellationToken = default, IProgress<CoverageProgress>? progress = null) =>
        session.RunOperationAsync(command.Kind == ReleaseKind.Release ? "ExportRelease" : "ExportTest", async () =>
        {
            Guid operationId = Guid.NewGuid();
            (OperationResult? precheck, IReadOnlyList<ValidationIssue> issues, ContentFingerprint fingerprint, bool needsSave) =
                Precheck(session.Capture(), command, operationId, cancellationToken, progress);
            if (precheck is not null) return precheck;

            OperationStage stage = OperationStage.NotStarted;
            if (needsSave)
            {
                SavePlan? plan = session.BeginSave(command.ExpectedVersion);
                if (plan is null)
                    return WorkspaceChecks.Result(session, WorkspaceChecks.WhyNotStarted(session.Status), operationId, OperationStage.NotStarted);
                (WorkspaceStamp? saved, OperationResult? failure) =
                    await SaveOperations.SaveAsync(session, store, plan, plan.Content, operationId, cancellationToken).ConfigureAwait(false);
                if (failure is not null) return failure;
                session.CompleteDraftSave(plan, saved!);
                stage = OperationStage.DraftSaved;
            }

            WorkspaceStamp stamp = session.Inspect(s => s.Stamp);
            ReleaseOutput output;
            try
            {
                output = await archive.ExportAsync(new ReleaseRequest(command.Kind, operationId, stamp, command.CompareTo), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 途中で止まった出力は記録が残り、ResumeExport で再開できる。未完了を成功として返さない
                return WorkspaceChecks.Result(session, SaveOperations.Classify(ex).Status, operationId, stage, issues, fingerprint, ex.Message);
            }

            return new OperationResult(OperationStatus.Succeeded, session.Status.Version, operationId, OperationStage.Exported,
                issues, fingerprint, output.Target, output.Path, Tag: output.Tag);
        });

    private static (OperationResult?, IReadOnlyList<ValidationIssue>, ContentFingerprint, bool NeedsSave) Precheck(
        SessionSnapshot snapshot, ExportReleaseCommand command, Guid operationId, CancellationToken cancellationToken,
        IProgress<CoverageProgress>? progress)
    {
        (OperationResult?, IReadOnlyList<ValidationIssue>, ContentFingerprint, bool) Stop(OperationResult result) =>
            (result, result.Issues, snapshot.Edited, false);

        if (WorkspaceChecks.CheckWritable(snapshot, command.ExpectedVersion, operationId) is { } stopped)
            return Stop(stopped);
        WorkspaceStatus status = snapshot.Status;
        if (command.Kind == ReleaseKind.Release && (status.HasUnsavedChanges || status.HasUncommittedChanges || snapshot.Head is null))
            return Stop(WorkspaceChecks.Stop(snapshot, OperationStatus.Rejected, operationId, reason: OperationReason.ReleaseNeedsCommittedHead));

        // プレビューで見た対象と同じか（リリースは対象 SHA、テスト版は出力する E の指紋）
        string targetKey = command.Kind == ReleaseKind.Release ? snapshot.Head!.Value : snapshot.Edited.Value;
        if (command.ExpectedTargetKey is not null && command.ExpectedTargetKey != targetKey)
            return Stop(WorkspaceChecks.Stop(snapshot, OperationStatus.Conflict, operationId, reason: OperationReason.ChangedAfterPreview));

        (OperationResult? result, IReadOnlyList<ValidationIssue> issues) = WorkspaceChecks.ValidateForWrite(
            snapshot, ValidationOperation.Export, command.Approval, operationId, cancellationToken, progress);
        if (result is not null) return Stop(result);
        return (null, issues, snapshot.Edited, command.Kind == ReleaseKind.Test && status.HasUnsavedChanges);
    }
}

/// <summary>中断した出力を、記録した固定入力で再開する。</summary>
public sealed record ResumeExportCommand(Guid OperationId);

public sealed class ResumeExportHandler(EditSession session, IReleaseArchive archive)
{
    public Task<OperationResult> HandleAsync(ResumeExportCommand command, CancellationToken cancellationToken = default) =>
        session.RunOperationAsync("ResumeExport", async () =>
        {
            try
            {
                ReleaseOutput output = await archive.ResumeAsync(command.OperationId, cancellationToken).ConfigureAwait(false);
                return new OperationResult(OperationStatus.Succeeded, session.Status.Version, command.OperationId, OperationStage.Exported,
                    [], null, output.Target, output.Path, Tag: output.Tag);
            }
            catch (Exception ex)
            {
                return WorkspaceChecks.Result(session, SaveOperations.Classify(ex).Status, command.OperationId, OperationStage.NotStarted,
                    detail: ex.Message);
            }
        });
}
