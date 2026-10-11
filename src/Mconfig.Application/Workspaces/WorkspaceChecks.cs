using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Workspaces;

/// <summary>保存・コミット・出力で共通に使う検証と承認の判定。規則そのものは Domain が持つ。</summary>
internal static class WorkspaceChecks
{
    /// <summary>構造・参照・未入力・編集不能に加え、必要なら全件の被覆を検証する。</summary>
    /// <remarks>全件の被覆は取消でき、途中で取消されたら OperationCanceledException を送出する（検証未完了）。</remarks>
    public static IReadOnlyList<ValidationIssue> Validate(
        WorkspaceContent content, bool includeCoverage, CancellationToken cancellationToken = default, IProgress<CoverageProgress>? progress = null)
    {
        IReadOnlyList<ValidationIssue> issues = WorkspaceValidator.Validate(content.Settings, content.Spec, content.Schemas);
        return includeCoverage
            ? [.. issues, .. WorkspaceValidator.ValidateCoverage(content.Settings, content.Spec, cancellationToken, progress)]
            : issues;
    }

    public static bool HasErrors(IEnumerable<ValidationIssue> issues, ValidationOperation operation) =>
        issues.Any(i => ValidationPolicy.SeverityFor(i.Code, operation) == ValidationSeverity.Error);

    /// <summary>
    /// 一時保存を止めるエラーがあるか。壊れたカテゴリ（診断専用）自身の内容に関するエラーは、元の保存表現が
    /// あればそのまま書き戻すので止めない（ほかのカテゴリの変更を一時保存できる）。スキーマ不整合や、元の保存表現がない場合は止める。
    /// 確定保存・出力はこの例外を持たず、<see cref="HasErrors"/> で止める。
    /// </summary>
    public static bool BlocksTemporarySave(IEnumerable<ValidationIssue> issues, WorkspaceContent content) =>
        issues.Any(i =>
            ValidationPolicy.SeverityFor(i.Code, ValidationOperation.TemporarySave) == ValidationSeverity.Error &&
            !(ValidationPolicy.IsDiagnosticCategoryError(i.Code) &&
              content.Settings.Categories.TryGetValue(i.Target.Category, out CategoryData? category) &&
              category.IsDiagnosticOnly && category.StoredRepresentation is not null));

    public static bool NeedsApproval(IEnumerable<ValidationIssue> issues) =>
        issues.Any(i => ValidationPolicy.RequiresConfirmation(i.Code));

    /// <summary>承認が、いまの内容の指紋と、いまの検証規則の版に対するものか。内容が変われば無効になる。</summary>
    public static bool IsApproved(WarningApproval? approval, ContentFingerprint fingerprint) =>
        approval is not null && approval.Fingerprint == fingerprint && approval.RuleVersion == WorkspaceValidator.RuleVersion;

    /// <summary>保存を開始できなかった理由（状態による拒否か、版の競合か）。</summary>
    public static OperationStatus WhyNotStarted(WorkspaceStatus status) =>
        status.AcceptsChanges ? OperationStatus.Conflict : OperationStatus.Rejected;

    public static OperationResult Result(
        EditSession session, OperationStatus status, Guid operationId, OperationStage stage,
        IReadOnlyList<ValidationIssue>? issues = null, ContentFingerprint? fingerprint = null, string? detail = null,
        OperationReason? reason = null) =>
        new(status, session.Status.Version, operationId, stage, issues ?? [], fingerprint, Detail: detail, Reason: reason);

    /// <summary>写した時点の状態で、何も書き込まずに止めた結果。</summary>
    public static OperationResult Stop(
        SessionSnapshot snapshot, OperationStatus status, Guid operationId,
        IReadOnlyList<ValidationIssue>? issues = null, OperationReason? reason = null) =>
        new(status, snapshot.Status.Version, operationId, OperationStage.NotStarted, issues ?? [], snapshot.Edited, Reason: reason);

    /// <summary>書込を伴う操作を始められる状態と版か。始められなければ止めた結果、始められれば null。</summary>
    public static OperationResult? CheckWritable(SessionSnapshot snapshot, SessionVersion expectedVersion, Guid operationId) =>
        !snapshot.Status.AcceptsChanges ? Stop(snapshot, OperationStatus.Rejected, operationId)
        : snapshot.Status.Version != expectedVersion ? Stop(snapshot, OperationStatus.Conflict, operationId)
        : null;

    /// <summary>
    /// 確定保存・出力の前の全件検証（被覆を含む）と承認の確認。写しに対してロックの外で行う。
    /// 取消・エラー・未承認の警告があれば止めた結果を、進めてよければ null を返す。Issues は止めたかどうかによらず検証結果。
    /// </summary>
    public static (OperationResult? Stopped, IReadOnlyList<ValidationIssue> Issues) ValidateForWrite(
        SessionSnapshot snapshot, ValidationOperation operation, WarningApproval? approval, Guid operationId,
        CancellationToken cancellationToken, IProgress<CoverageProgress>? progress)
    {
        IReadOnlyList<ValidationIssue> issues;
        try
        {
            issues = Validate(snapshot.Content, includeCoverage: true, cancellationToken, progress);
        }
        catch (OperationCanceledException)
        {
            return (Stop(snapshot, OperationStatus.Canceled, operationId), []);
        }
        if (HasErrors(issues, operation))
            return (Stop(snapshot, OperationStatus.ValidationFailed, operationId, issues), issues);
        if (NeedsApproval(issues) && !IsApproved(approval, snapshot.Edited))
            return (Stop(snapshot, OperationStatus.ConfirmationRequired, operationId, issues), issues);
        return (null, issues);
    }
}
