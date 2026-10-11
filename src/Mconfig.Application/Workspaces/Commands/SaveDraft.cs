using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Workspaces.Commands;

/// <summary>
/// 一時保存。未入力は許すが、構造・参照のエラーがあれば保存しない。
/// 壊れたカテゴリ（診断専用）は元の保存表現のまま書き戻すので、ほかのカテゴリの変更は保存できる。
/// </summary>
public sealed record SaveDraftCommand(SessionVersion ExpectedVersion);

public sealed class SaveDraftHandler(EditSession session, IWorkspaceStore store)
{
    public Task<OperationResult> HandleAsync(SaveDraftCommand command, CancellationToken cancellationToken = default) =>
        session.RunOperationAsync("SaveDraft", async () =>
        {
            Guid operationId = Guid.NewGuid();
            // 検証は写しに対してロックの外で行う。その後の編集は保存開始時の版の照合で競合になる
            SessionSnapshot snapshot = session.Capture();
            IReadOnlyList<ValidationIssue> issues = WorkspaceChecks.Validate(snapshot.Content, includeCoverage: false);
            if (snapshot.Status.Version != command.ExpectedVersion)
                return WorkspaceChecks.Result(session, OperationStatus.Conflict, operationId, OperationStage.NotStarted);
            if (WorkspaceChecks.BlocksTemporarySave(issues, snapshot.Content))
                return WorkspaceChecks.Result(session, OperationStatus.ValidationFailed, operationId, OperationStage.NotStarted, issues);
            if (cancellationToken.IsCancellationRequested)
                return WorkspaceChecks.Result(session, OperationStatus.Canceled, operationId, OperationStage.NotStarted);

            SavePlan? plan = session.BeginSave(command.ExpectedVersion);
            if (plan is null)
                return WorkspaceChecks.Result(session, WorkspaceChecks.WhyNotStarted(session.Status), operationId, OperationStage.NotStarted);

            (WorkspaceStamp? stamp, OperationResult? failure) =
                await SaveOperations.SaveAsync(session, store, plan, plan.Content, operationId, cancellationToken).ConfigureAwait(false);
            if (failure is not null) return failure;

            session.CompleteDraftSave(plan, stamp!);
            return WorkspaceChecks.Result(session, OperationStatus.Succeeded, operationId, OperationStage.DraftSaved, issues);
        });
}
