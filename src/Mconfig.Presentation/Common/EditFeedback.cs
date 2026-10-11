using Mconfig.Application.Presets.Commands;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Presentation.Common;

/// <summary>入力できなかったセルの位置（表示名で示す）と理由。</summary>
public sealed record CellProblemDisplay(string Location, string Input, CellInputProblemKind Kind);

/// <summary>
/// 編集 Command が成功しなかったときに利用者へ示す内容。文言は View がリソースから作る。
/// Conflict は表示していた版が古かった、Rejected は保存中・読取専用・復旧待ち、Invalid は業務規則か入力の誤り。
/// </summary>
public sealed record EditFeedback(CommandStatus Status, PresetOperationFailure? Failure, IReadOnlyList<CellProblemDisplay> CellProblems)
{
    public static EditFeedback From(CommandResult result, IReadOnlyList<CellProblemDisplay>? problems = null) =>
        new(result.Status, result.Failure, problems ?? []);
}

/// <summary>貼り付けの範囲が表からはみ出す（何も変更しない）。</summary>
public sealed record PasteOutOfRange(int PasteRows, int PasteColumns, int AvailableRows, int AvailableColumns);

/// <summary>編集を受け付ける状態か（保存中・読取専用・復旧待ちでは編集させない）。</summary>
public static class Editing
{
    public static bool IsAllowed(WorkspaceStatus status) => !status.IsReadOnly && !status.IsRecoveryPending && !status.IsSaving;

    public static bool Succeeded(CommandResult result) => result.Status is CommandStatus.Applied or CommandStatus.NoChange;
}
