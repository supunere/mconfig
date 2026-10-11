using Mconfig.Application.History;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;

namespace Mconfig.Application.Ports;

/// <summary>読み込んだ時点のファイル集合・各バイト列のハッシュ・期待 HEAD をまとめた不透明な値。</summary>
public sealed record WorkspaceStamp(string Value);

/// <summary>コミットの識別子。</summary>
public sealed record RevisionId(string Value);

/// <summary>
/// 読込結果。Working は一時保存 W、Committed は確定版 C（初期コミットがなければ null）、Head は C のコミット。
/// ReadOnly は編集の排他を得られなかった（別のアプリが編集中）ことを表し、編集・保存を受け付けない。
/// RestoredFrom は、版の復元を適用したがまだ確定保存していない場合の復元元。
/// </summary>
public sealed record LoadedWorkspace(
    WorkspaceContent Working, WorkspaceContent? Committed, WorkspaceStamp Stamp, RevisionId? Head, bool ReadOnly = false,
    RevisionId? RestoredFrom = null);

/// <summary>版の復元の結果。Content は書き込んだ一式を読み直した内容で、W になる。</summary>
public sealed record RestoredWorkspace(WorkspaceContent Content, WorkspaceStamp Stamp);

/// <summary>保存と読込の契約。実装は Infrastructure が持つ。</summary>
public interface IWorkspaceStore
{
    Task<LoadedWorkspace> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 内容一式を W として保存し、新しいスタンプを返す。ファイルが expectedStamp の時点から外部で変わっていれば
    /// <see cref="WorkspaceConflictException"/>、旧版・新版のどちらか判定できない状態なら
    /// <see cref="WorkspaceRecoveryRequiredException"/> を投げる。
    /// </summary>
    Task<WorkspaceStamp> SaveAsync(WorkspaceContent content, WorkspaceStamp expectedStamp, Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// 指定コミットの設定・仕様・スキーマ一式（ファイルの追加・削除を含む）を W として適用する。
    /// HEAD とタグは動かさない。外部変更は <see cref="WorkspaceConflictException"/>、一式がそろわない・解釈できない版は
    /// <see cref="WorkspaceUnreadableException"/>。
    /// </summary>
    Task<RestoredWorkspace> RestoreAsync(RevisionId revision, WorkspaceStamp expectedStamp, Guid operationId, CancellationToken cancellationToken);
}

/// <summary>
/// コミットの依頼。ExpectedStamp は直前の一時保存の結果で、コミットする W がそのファイルであることを確かめる。
/// ExpectedParent が null なら初期コミット。
/// </summary>
public sealed record CommitRequest(string Reason, string Author, Guid OperationId, RevisionId? ExpectedParent, WorkspaceStamp ExpectedStamp);

public sealed record CommitOutcome(RevisionId Revision, WorkspaceStamp Stamp);

/// <summary>履歴の一件。Parent は差分の比較元（初期コミットは null）。Message は変更理由（操作の識別情報は除く）。</summary>
public sealed record RevisionInfo(RevisionId Id, RevisionId? Parent, string Message, string Author, DateTimeOffset When);

/// <summary>版と履歴の契約。実装は Infrastructure が持つ。</summary>
public interface IRevisionStore
{
    /// <summary>
    /// 現在の W をコミットし、新しいコミットと（HEAD を含む）新しいスタンプを返す。
    /// ファイルや HEAD が期待と異なれば <see cref="WorkspaceConflictException"/>。同じ操作のコミットが既にあれば作り直さない。
    /// </summary>
    Task<CommitOutcome> CommitAsync(CommitRequest request, CancellationToken cancellationToken);

    /// <summary>HEAD から新しい順に最大 maxCount 件。StartAfter を渡すとその次から（段階的な取得）。</summary>
    Task<IReadOnlyList<RevisionInfo>> GetHistoryAsync(int maxCount, RevisionId? startAfter, CancellationToken cancellationToken);

    /// <summary>指定コミットの情報。</summary>
    Task<RevisionInfo> GetRevisionAsync(RevisionId revision, CancellationToken cancellationToken);

    /// <summary>
    /// 指定コミットの内容一式を、作業ディレクトリと独立して読む（checkout しない）。
    /// 一式がそろわない・解釈できない版は <see cref="WorkspaceUnreadableException"/>。
    /// </summary>
    Task<WorkspaceContent> ReadAsync(RevisionId revision, CancellationToken cancellationToken);
}

public enum ReleaseKind { Test, Release }

/// <summary>比較元になりうる、公開完了済みのリリース。</summary>
public sealed record ReleaseCandidate(string Name, RevisionId Revision, DateTimeOffset PublishedAt);

/// <summary>
/// 明示した比較元。None は全件出力、それ以外はリリース名と SHA を両方照合する。
/// 依頼の CompareTo が null の場合だけ、既定の公開済み祖先リリースを選ぶ。
/// </summary>
public sealed record ReleaseComparison
{
    private ReleaseComparison(string? name, RevisionId? revision) => (Name, Revision) = (name, revision);

    public string? Name { get; }
    public RevisionId? Revision { get; }
    public static ReleaseComparison None { get; } = new(null, null);

    /// <summary>プレビューで確認した候補（null は比較なし）を、明示指定にする。</summary>
    public static ReleaseComparison From(ReleaseCandidate? candidate) =>
        candidate is null ? None : new(candidate.Name, candidate.Revision);
}

/// <summary>
/// 出力計画の依頼。Stamp は読み込んだ時点の W と HEAD で、計画時に照合する。
/// Content はテスト版で出力する内容（Query が固定した E の写し）で、テスト版では必須。計画の差分・未確定差分・指紋はこの内容から作り、
/// ディスク上の W へ置き換えない（出力時に E を一時保存してから W を固定するため、プレビューと出力内容が一致する）。リリース版では使わない。
/// </summary>
public sealed record ReleasePlanRequest(ReleaseKind Kind, WorkspaceStamp Stamp, ReleaseComparison? CompareTo = null, WorkspaceContent? Content = null);

/// <summary>
/// 出力予定の内容。Target はリリースの対象コミット、BaseHead はテスト版の基準 HEAD、
/// WorkingFingerprint はテスト版で出力する内容（プレビューは E、出力は固定した W）の指紋。Commits は比較元から対象（テスト版は基準 HEAD）までのコミット、
/// Changes は比較元から出力内容への意味差分、UncommittedChanges はテスト版の HEAD から出力内容への未確定の差分。
/// </summary>
public sealed record ReleasePlan(
    ReleaseKind Kind,
    RevisionId? Target,
    RevisionId? BaseHead,
    ContentFingerprint? WorkingFingerprint,
    ReleaseCandidate? CompareTo,
    IReadOnlyList<ReleaseCandidate> Candidates,
    IReadOnlyList<RevisionInfo> Commits,
    IReadOnlyList<ContentChange> Changes,
    IReadOnlyList<ContentChange> UncommittedChanges)
{
    /// <summary>プレビューと実行で同じ対象かを照合する値（リリースは対象 SHA、テスト版は出力する内容の指紋）。</summary>
    public string TargetKey => Kind == ReleaseKind.Release ? Target!.Value : WorkingFingerprint!.Value;
}

/// <summary>
/// ZIP 出力の依頼。CompareTo はプレビューで見たリリース名と SHA、または比較なしを明示する。
/// CompareTo が null なら既定の比較元（履歴上で最も近い公開完了済みリリース）を使う。
/// </summary>
public sealed record ReleaseRequest(
    ReleaseKind Kind,
    Guid OperationId,
    WorkspaceStamp Stamp,
    ReleaseComparison? CompareTo = null,
    string? ExpectedTargetKey = null);

public sealed record ReleaseOutput(string Path, string? Tag, RevisionId? Target, RevisionId? CompareTo);

/// <summary>中断した出力（再開できる）。</summary>
public sealed record PendingExport(Guid OperationId, ReleaseKind Kind, string Name, DateTimeOffset CreatedAt, string Stage);

public enum PendingExportProblemKind { DamagedJournal, UnreadableJournal, ListUnavailable }

/// <summary>完了を確認できない出力記録。Location はワークスペース相対パス。再開候補には含めない。</summary>
public sealed record PendingExportProblem(Guid? OperationId, string Location, PendingExportProblemKind Kind);

/// <summary>再開候補と、記録や一覧を読み取れず完了を確認できない問題。</summary>
public sealed record PendingExportsReadModel(IReadOnlyList<PendingExport> Exports, IReadOnlyList<PendingExportProblem> Problems);

/// <summary>テスト版・リリース版 ZIP の出力と公開の契約。実装は Infrastructure が持つ。</summary>
public interface IReleaseArchive
{
    /// <summary>from の祖先（from 自身を含む）にある公開完了済みリリース。履歴上で近い順、同じコミットは公開日時の新しい順。</summary>
    Task<IReadOnlyList<ReleaseCandidate>> GetReleaseCandidatesAsync(RevisionId? from, CancellationToken cancellationToken);

    /// <summary>
    /// 出力予定の内容を返す。ファイル・タグ・記録を作らない。テスト版は依頼の Content から計画を作る
    /// （Content がなければ <see cref="ArgumentException"/>）。ファイルや HEAD が Stamp と異なれば <see cref="WorkspaceConflictException"/>。
    /// </summary>
    Task<ReleasePlan> PlanAsync(ReleasePlanRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 固定した入力から ZIP を作って公開する。ファイルや HEAD が期待と異なれば <see cref="WorkspaceConflictException"/>、
    /// 途中で止まったら記録を残して例外を投げる（<see cref="ResumeAsync"/> で再開できる）。
    /// </summary>
    Task<ReleaseOutput> ExportAsync(ReleaseRequest request, CancellationToken cancellationToken);

    /// <summary>中断した出力を、記録した固定入力で再開する。</summary>
    Task<ReleaseOutput> ResumeAsync(Guid operationId, CancellationToken cancellationToken);

    Task<PendingExportsReadModel> GetPendingExportsAsync(CancellationToken cancellationToken);
}

/// <summary>ファイルや HEAD が期待と異なる（外部変更）。自動で続行しない。</summary>
public sealed class WorkspaceConflictException(string message) : Exception(message);

/// <summary>書込の途中で止まった等で、旧版・新版を判定できず復旧が必要。</summary>
public sealed class WorkspaceRecoveryRequiredException(string message) : Exception(message);

/// <summary>
/// ワークスペースを読み込めない（ファイルの不足・JSON の破損・対象外の配置）。既存データを空の設定で置き換えず、
/// Problems に対象と理由を挙げる。
/// </summary>
public sealed class WorkspaceUnreadableException(string message, IReadOnlyList<string> problems) : Exception(message)
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
