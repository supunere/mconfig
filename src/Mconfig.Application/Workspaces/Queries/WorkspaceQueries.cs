using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

namespace Mconfig.Application.Workspaces.Queries;

/// <summary>同じ時点の E/W/C と操作状態。</summary>
public sealed class GetWorkspaceStatusHandler(EditSession session)
{
    public WorkspaceStatus Handle() => session.Status;

    /// <summary>保存中などの状態変更とは独立した、現在の編集内容の指紋。</summary>
    public ContentFingerprint GetEditedFingerprint() => session.Inspect(s => s.Edited);
}

/// <summary>
/// 状態表示の要約。UnsavedChanges は一時保存 W から E までの変更の件数、
/// Errors は履歴に保存（コミット）を妨げる検証エラーの件数、Unassigned はどの割り当て行にも一致しない組み合わせとカテゴリの組の数。
/// 組み合わせが <see cref="CombinationSpace.LargeThreshold"/> を超えるときは Unassigned を数えない（null、全件の確認は検証で行う）。
/// </summary>
public sealed record WorkspaceSummaryReadModel(WorkspaceStatus Status, int UnsavedChanges, int Errors, int? Unassigned);

public sealed class GetWorkspaceSummaryHandler(EditSession session)
{
    public WorkspaceSummaryReadModel Handle(CancellationToken cancellationToken = default)
    {
        // 同期区間では写しと未保存の件数だけを求め、検証と評価はロックの外で行う
        (WorkspaceStatus status, WorkspaceContent content, int unsaved) = session.Inspect(s =>
            (s.Status, s.Content.Snapshot(), s.Status.HasUnsavedChanges ? Math.Max(1, s.Saved.CountChanges(s.Content)) : 0));

        int errors = WorkspaceChecks.Validate(content, includeCoverage: false, cancellationToken)
            .Count(i => ValidationPolicy.SeverityFor(i.Code, ValidationOperation.Commit) == ValidationSeverity.Error);
        CombinationSpace space = CombinationSpace.Of(content.Spec);
        int? unassigned = space.IsLarge ? null : content.Settings.Categories.Values
            .Where(c => !c.IsDiagnosticOnly)
            .Sum(c => space.Enumerate(cancellationToken).Count(values =>
                AssignmentEvaluation.Evaluate(c.Assignments, values).Kind == AssignmentOutcomeKind.Unassigned));
        return new WorkspaceSummaryReadModel(status, unsaved, errors, unassigned);
    }
}

/// <summary>E を指定した操作の規則で検証する（ValidateWorkspace）。承認はしない（Query は状態を変えない）。</summary>
public sealed record ValidateWorkspaceQuery(ValidationOperation Operation, bool IncludeCoverage = true);

public sealed record ValidationResultItem(ValidationIssue Issue, ValidationSeverity Severity, bool RequiresConfirmation);

/// <summary>
/// 検証結果のリードモデル。Fingerprint と RuleVersion は確認付き警告の承認に使う。
/// 承認は <see cref="WarningApproval"/> として Command に渡し、Command が適用直前に再確認する。
/// </summary>
public sealed record WorkspaceValidationReadModel(
    SessionVersion Version,
    ContentFingerprint Fingerprint,
    string RuleVersion,
    ValidationOperation Operation,
    IReadOnlyList<ValidationResultItem> Items)
{
    public bool HasErrors => Items.Any(i => i.Severity == ValidationSeverity.Error);
    public bool NeedsApproval => Items.Any(i => i.RequiresConfirmation);

    /// <summary>同じ時点のスキーマ（不変）。対象の表示名の解決に使う。</summary>
    public IReadOnlyList<SchemaDefinition> Schemas { get; init; } = [];
}

public sealed class ValidateWorkspaceHandler(EditSession session)
{
    /// <summary>
    /// 写しを取ってからロックの外で検証する（検証中も編集を止めない）。結果の版と指紋は写した時点のもの。
    /// 取消されたら OperationCanceledException を送出する（検証未完了で、結果を返さない）。
    /// </summary>
    public WorkspaceValidationReadModel Handle(
        ValidateWorkspaceQuery query, CancellationToken cancellationToken = default, IProgress<CoverageProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        SessionSnapshot s = session.Capture();
        IReadOnlyList<ValidationIssue> issues = WorkspaceChecks.Validate(s.Content, query.IncludeCoverage, cancellationToken, progress);
        return new WorkspaceValidationReadModel(
            s.Status.Version, s.Edited, WorkspaceValidator.RuleVersion, query.Operation,
            issues.Select(i => new ValidationResultItem(
                i, ValidationPolicy.SeverityFor(i.Code, query.Operation), ValidationPolicy.RequiresConfirmation(i.Code))).ToArray())
        {
            Schemas = s.Content.Schemas,
        };
    }
}

/// <summary>同じ時点の仕様とスキーマ（不変）。検証結果などの対象を表示名で示すのに使う。</summary>
public sealed record DefinitionsReadModel(SessionVersion Version, SpecDefinition Spec, IReadOnlyList<SchemaDefinition> Schemas);

public sealed class GetDefinitionsHandler(EditSession session)
{
    public DefinitionsReadModel Handle() =>
        session.Inspect(s => new DefinitionsReadModel(s.Status.Version, s.Content.Spec, s.Content.Schemas));
}
