using Mconfig.Application.Workspaces.Editing;
using Mconfig.Application.Workspaces.Queries;
using Mconfig.Presentation.Common;

namespace Mconfig.Presentation.Workspaces;

/// <summary>
/// 状態表示。未保存（E≠W）と未コミット（W≠C）を別々に示し、読取専用・復旧待ち・保存中を識別できるようにする。
/// UnsavedChanges は一時保存していない変更の件数、Errors は履歴に保存を妨げるエラーの件数、Unassigned は未割当の件数。
/// 組み合わせが多すぎて未割当を数えていなければ Unassigned は null（全件の確認は検証で行う）。
/// 文言は View がリソースから作る。
/// </summary>
public sealed record StatusDisplay(
    bool ReadOnly, bool RecoveryPending, bool Saving, bool Unsaved, bool Uncommitted, int UnsavedChanges, int Errors, int? Unassigned);

public interface IWorkspaceStatusView
{
    void ShowStatus(StatusDisplay status);
}

/// <summary>状態表示の Presenter。件数の集計（検証と全組み合わせの評価）は Query として背景で行う。</summary>
/// <remarks>検証を伴うので、changeDelay（既定 0）を指定すると連続した編集をまとめてから数え直す。</remarks>
public sealed class WorkspaceStatusPresenter(
    IWorkspaceStatusView view, EditSession session, IQueryRunner runner, IUiDispatcher ui, DisplayLanguage language,
    TimeSpan changeDelay = default)
    : QueryPresenter<WorkspaceSummaryReadModel>(session, runner, ui, language)
{
    protected override TimeSpan ChangeDelay => changeDelay;

    private readonly GetWorkspaceSummaryHandler _summary = new(session);

    protected override WorkspaceSummaryReadModel Query() => _summary.Handle();

    protected override SessionVersion VersionOf(WorkspaceSummaryReadModel model) => model.Status.Version;

    protected override void Render(WorkspaceSummaryReadModel model) => view.ShowStatus(new StatusDisplay(
        model.Status.IsReadOnly, model.Status.IsRecoveryPending, model.Status.IsSaving,
        model.Status.HasUnsavedChanges, model.Status.HasUncommittedChanges,
        model.UnsavedChanges, model.Errors, model.Unassigned));
}
