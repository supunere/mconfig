using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces.Editing;

namespace Mconfig.Application.Workspaces.Commands;

/// <summary>保存・コミット時の Port の失敗を、セッションの状態と結果に対応付ける。</summary>
internal static class SaveOperations
{
    /// <summary>W を書き込む。失敗したら保存中の状態を終わらせ、結果を返す（成功時は null）。</summary>
    public static async Task<(WorkspaceStamp? Stamp, OperationResult? Failure)> SaveAsync(
        EditSession session, IWorkspaceStore store, SavePlan plan, WorkspaceContent content, Guid operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await store.SaveAsync(content, plan.Stamp, operationId, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            (OperationStatus status, bool recovery) = Classify(ex);
            session.FailSave(plan, recovery);
            return (null, WorkspaceChecks.Result(session, status, operationId, OperationStage.NotStarted, detail: ex.Message));
        }
    }

    public static (OperationStatus Status, bool RecoveryPending) Classify(Exception ex) => ex switch
    {
        WorkspaceConflictException => (OperationStatus.Conflict, false),
        WorkspaceRecoveryRequiredException => (OperationStatus.RecoveryPending, true),
        // 一式がそろわない・解釈できない版やファイルは適用しない
        WorkspaceUnreadableException => (OperationStatus.Rejected, false),
        OperationCanceledException => (OperationStatus.Canceled, false),
        _ => (OperationStatus.TechnicalFailure, false),
    };
}
