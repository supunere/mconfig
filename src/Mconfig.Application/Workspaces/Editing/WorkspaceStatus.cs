namespace Mconfig.Application.Workspaces.Editing;

/// <summary>同じ時点で読み取った編集セッションの状態。</summary>
/// <param name="HasUnsavedChanges">編集内容 E が一時保存 W と異なる。</param>
/// <param name="HasUncommittedChanges">一時保存 W が確定版 C と異なる。</param>
public sealed record WorkspaceStatus(
    SessionVersion Version,
    bool HasUnsavedChanges,
    bool HasUncommittedChanges,
    bool IsSaving,
    bool IsReadOnly,
    bool IsRecoveryPending)
{
    /// <summary>編集と保存を受け付ける状態か（保存中・読取専用・復旧待ちでは受け付けない）。</summary>
    public bool AcceptsChanges => !IsSaving && !IsReadOnly && !IsRecoveryPending;
}
