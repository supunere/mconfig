using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Commands;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.History.Commands;

/// <summary>
/// 版の復元。プレビューに使った対象コミットと現在のセッションの版を渡し、適用直前に再確認する。
/// 復元後は E=W が対象版の内容になり、C（現 HEAD）は維持する。新しい復元コミットは理由付きの確定保存で別に作る。
/// 未確定の変更（W≠C）は、DiscardUncommitted で明示したときだけ破棄する。置き換える現在のファイルは復旧素材へ退避される
/// 。未保存の編集（E≠W）はメモリにしかないので、先に一時保存か破棄をしてもらう。
/// </summary>
public sealed record RestoreRevisionCommand(RevisionId Revision, SessionVersion ExpectedVersion, bool DiscardUncommitted = false);

public sealed class RestoreRevisionHandler(EditSession session, IWorkspaceStore store, IRevisionStore revisions)
{
    public Task<OperationResult> HandleAsync(RestoreRevisionCommand command, CancellationToken cancellationToken = default) =>
        session.RunOperationAsync("Restore", async () =>
        {
            Guid operationId = Guid.NewGuid();
            WorkspaceStatus status = session.Status;
            if (status.Version != command.ExpectedVersion)
                return WorkspaceChecks.Result(session, OperationStatus.Conflict, operationId, OperationStage.NotStarted);
            if (!status.AcceptsChanges)
                return WorkspaceChecks.Result(session, OperationStatus.Rejected, operationId, OperationStage.NotStarted);
            // 未保存・未コミットの変更があれば、先に確定保存するか明示的に破棄してもらう
            if (status.HasUnsavedChanges || (status.HasUncommittedChanges && !command.DiscardUncommitted))
                return WorkspaceChecks.Result(session, OperationStatus.Rejected, operationId, OperationStage.NotStarted,
                    reason: OperationReason.RestoreNeedsNoPendingChanges);

            WorkspaceContent target;
            try
            {
                target = await revisions.ReadAsync(command.Revision, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return WorkspaceChecks.Result(session, SaveOperations.Classify(ex).Status, operationId, OperationStage.NotStarted, detail: ex.Message);
            }

            // 構造や参照が壊れた版は適用しない（現在の定義と混ぜて補完しない）
            IReadOnlyList<ValidationIssue> issues = WorkspaceChecks.Validate(target, includeCoverage: false);
            if (WorkspaceChecks.HasErrors(issues, ValidationOperation.Edit))
                return WorkspaceChecks.Result(session, OperationStatus.ValidationFailed, operationId, OperationStage.NotStarted, issues);
            if (cancellationToken.IsCancellationRequested)
                return WorkspaceChecks.Result(session, OperationStatus.Canceled, operationId, OperationStage.NotStarted);

            SavePlan? plan = session.BeginSave(command.ExpectedVersion);
            if (plan is null)
                return WorkspaceChecks.Result(session, WorkspaceChecks.WhyNotStarted(session.Status), operationId, OperationStage.NotStarted);

            // 対象版のファイル一式（追加・削除を含む）を W として適用する。HEAD は動かさない
            RestoredWorkspace restored;
            try
            {
                restored = await store.RestoreAsync(command.Revision, plan.Stamp, operationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (OperationStatus failed, bool recovery) = SaveOperations.Classify(ex);
                session.FailSave(plan, recovery);
                return WorkspaceChecks.Result(session, failed, operationId, OperationStage.NotStarted, detail: ex.Message);
            }

            session.CompleteRestore(plan, restored.Content, restored.Stamp);
            return WorkspaceChecks.Result(session, OperationStatus.Succeeded, operationId, OperationStage.DraftSaved, issues,
                WorkspaceFingerprints.Of(restored.Content));
        });
}
