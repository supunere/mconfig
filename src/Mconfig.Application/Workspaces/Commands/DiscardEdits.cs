using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces.Editing;

namespace Mconfig.Application.Workspaces.Commands;

/// <summary>
/// 未保存の編集を破棄して E を W へ戻す。W はディスクから読み直す。
/// 自分の保存以降にファイルが外部で変わっていれば、黙って差し替えず競合とする。未コミット分の破棄は扱わない（版の復元を使う）。
/// </summary>
public sealed record DiscardEditsCommand(SessionVersion ExpectedVersion);

public sealed class DiscardEditsHandler(EditSession session, IWorkspaceStore store)
{
    public Task<OperationResult> HandleAsync(DiscardEditsCommand command, CancellationToken cancellationToken = default) =>
        session.RunOperationAsync("Discard", async () =>
        {
            Guid operationId = Guid.NewGuid();
            WorkspaceStatus status = session.Status;
            if (status.Version != command.ExpectedVersion)
                return WorkspaceChecks.Result(session, OperationStatus.Conflict, operationId, OperationStage.NotStarted);
            if (status.IsSaving)
                return WorkspaceChecks.Result(session, OperationStatus.Rejected, operationId, OperationStage.NotStarted);
            if (!status.HasUnsavedChanges)
                return WorkspaceChecks.Result(session, OperationStatus.NoChanges, operationId, OperationStage.NotStarted);

            LoadedWorkspace loaded;
            try
            {
                loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return WorkspaceChecks.Result(session, SaveOperations.Classify(ex).Status, operationId, OperationStage.NotStarted, detail: ex.Message);
            }

            return session.TryReplaceWithReloaded(loaded, command.ExpectedVersion)
                ? WorkspaceChecks.Result(session, OperationStatus.Succeeded, operationId, OperationStage.NotStarted)
                : WorkspaceChecks.Result(session, OperationStatus.Conflict, operationId, OperationStage.NotStarted,
                    reason: OperationReason.FilesChangedOutside);
        });
}
