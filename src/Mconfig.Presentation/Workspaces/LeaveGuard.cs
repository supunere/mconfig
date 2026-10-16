using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Commands;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Presentation.DataManagement;

namespace Mconfig.Presentation.Workspaces;

public enum LeaveKind { SwitchTab, Exit }

public enum UnsavedChoice { Save, Discard, Cancel }

/// <summary>画面を離れるときの確認の View 契約。</summary>
public interface ILeaveConfirmationView
{
    /// <summary>未保存の編集がある。一時保存・破棄・キャンセルを尋ねる。</summary>
    UnsavedChoice AskUnsaved(LeaveKind kind);

    /// <summary>保存中・出力中などで、いまは離れられないことを知らせる。</summary>
    void ShowBusy();

    void ShowOperationResult(OperationDisplay result);
}

/// <summary>
/// タブ移動と終了の確認。未保存（E≠W）なら一時保存・破棄・キャンセルを尋ね、一時保存や破棄が失敗したら離れない。
/// 保存済みの未発行設定は作業途中として終了を許可する。保存・出力などの実行中は離れない。UI スレッドから呼び、待機後も同じ文脈で View を呼ぶ。
/// </summary>
public sealed class LeaveGuard(EditSession session, SaveDraftHandler save, DiscardEditsHandler discard, ILeaveConfirmationView view)
{
    public async Task<bool> CanLeaveAsync(LeaveKind kind)
    {
        WorkspaceStatus status = session.Status;
        // 終了は、E/W/C を変えない ZIP 出力なども含めて、実行中の書込操作が終わるまで受け付けない
        if (status.IsSaving || (kind == LeaveKind.Exit && session.IsRunningOperation))
        {
            view.ShowBusy();
            return false;
        }

        if (status.HasUnsavedChanges)
        {
            UnsavedChoice choice = view.AskUnsaved(kind);
            if (choice == UnsavedChoice.Cancel) return false;
            (DataOperation operation, OperationResult result) = choice == UnsavedChoice.Save
                ? (DataOperation.SaveDraft, await Task.Run(() => save.HandleAsync(new SaveDraftCommand(status.Version))))
                : (DataOperation.Discard, await Task.Run(() => discard.HandleAsync(new DiscardEditsCommand(status.Version))));
            if (result.Status != OperationStatus.Succeeded)
            {
                view.ShowOperationResult(new OperationDisplay(operation, result.Status, result.CompletedStage, [], null, result.Detail)
                {
                    Reason = result.Reason,
                });
                return false;
            }
            status = session.Status;
        }

        return true;
    }
}
