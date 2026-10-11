using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Workspaces.Editing;

public enum CommandStatus
{
    Applied,
    /// <summary>業務操作は成功したが内容が変わらなかった。</summary>
    NoChange,
    /// <summary>呼出し側が見ていた版が古い。最新の状態を照会し直す必要がある。</summary>
    Conflict,
    /// <summary>保存中・読取専用・復旧待ちのため受け付けなかった。</summary>
    Rejected,
    /// <summary>業務規則に反するため適用しなかった。内容は変わっていない。</summary>
    Invalid,
}

/// <summary>編集 Command の結果。Version は処理後のセッションの版（変化がなければ処理前と同じ）。</summary>
public sealed record CommandResult(CommandStatus Status, SessionVersion Version, PresetOperationFailure? Failure = null);
