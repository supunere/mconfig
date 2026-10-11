using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Workspaces;

public enum OperationStatus
{
    Succeeded,
    /// <summary>確定版と内容が同じなので、何もしなかった（空のコミットを作らない）。</summary>
    NoChanges,
    /// <summary>検証エラー。何も書き込んでいない。</summary>
    ValidationFailed,
    /// <summary>確認付き警告の承認、または短い理由の確認が必要。</summary>
    ConfirmationRequired,
    /// <summary>セッションの版、ファイル、HEAD のいずれかが期待と異なる。</summary>
    Conflict,
    /// <summary>保存中・読取専用・復旧待ち、または前提（未保存・未コミットの有無）を満たさない。</summary>
    Rejected,
    Canceled,
    TechnicalFailure,
    /// <summary>旧版・新版を判定できず、復旧が必要。</summary>
    RecoveryPending,
}

/// <summary>
/// 前提を満たさずに止めた理由（利用者に次の操作を示すため）。文言は画面側が表示言語で決める。
/// </summary>
public enum OperationReason
{
    /// <summary>復元の前に、未保存の変更の一時保存か破棄、または未コミットの変更の破棄の指定が要る。</summary>
    RestoreNeedsNoPendingChanges,
    /// <summary>リリース版は、未確定の変更がなく確定版（HEAD）があるときだけ出力できる。</summary>
    ReleaseNeedsCommittedHead,
    /// <summary>プレビューの後に対象の内容が変わった。</summary>
    ChangedAfterPreview,
    /// <summary>自分の保存の後に、ワークスペースのファイルが外部で変わった。</summary>
    FilesChangedOutside,
}

/// <summary>どこまで完了したか。</summary>
public enum OperationStage { NotStarted, DraftSaved, Committed, Exported }

/// <summary>
/// 保存・コミット・復元・出力の結果。技術的な例外の詳細はログに残し、ここでは要約だけを返す。
/// Fingerprint と RuleVersion は、確認付き警告を承認するときに使う。
/// Reason は業務上の前提を満たさなかった理由、Detail は技術的な失敗の要約（例外の文言）で、表示言語に訳さない。
/// </summary>
public sealed record OperationResult(
    OperationStatus Status,
    SessionVersion Version,
    Guid OperationId,
    OperationStage CompletedStage,
    IReadOnlyList<ValidationIssue> Issues,
    ContentFingerprint? Fingerprint = null,
    RevisionId? Revision = null,
    string? Output = null,
    string? Detail = null,
    string? Tag = null,
    OperationReason? Reason = null);

/// <summary>
/// 確認付き警告の承認。対象内容の指紋と検証規則の版に紐付き、内容が変われば無効になる。
/// </summary>
public sealed record WarningApproval(ContentFingerprint Fingerprint, string RuleVersion);
