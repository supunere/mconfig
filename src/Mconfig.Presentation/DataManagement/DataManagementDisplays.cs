using Mconfig.Application.History;
using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Presentation.DataManagement;

/// <summary>
/// 検証結果の 1 件。Location は対象（カテゴリ / プリセット / グループ / 行 / 列）を表示名でつないだもの、
/// Args は文言の組み立てに使う値（行番号は 1 始まり）。文言は View がコードごとのリソースから作る。
/// </summary>
public sealed record IssueDisplay(
    ValidationSeverity Severity, ValidationCode Code, string Location, IReadOnlyList<string> Args, bool RequiresConfirmation)
{
    /// <summary>対象の位置（該当箇所へ移るときに使う）。</summary>
    public ValidationTarget? Target { get; init; }
}

/// <summary>該当箇所へ移るときの画面。</summary>
public enum NavigationScreen { Values, Assignments }

/// <summary>検証結果から移る先（画面とカテゴリ）。</summary>
public sealed record NavigationTarget(NavigationScreen Screen, string Category)
{
    /// <summary>
    /// 検証結果の該当箇所。割り当ての誤り（仕様にないキー・値、ないプリセット、未割当、使われない行）は割り当て画面、
    /// それ以外（プリセットの値や構造）は組み合わせと値の画面の、そのカテゴリへ移る。カテゴリの分からない結果（仕様の誤り）と、
    /// 設定にないカテゴリ（MissingCategory）は移れない（null）。
    /// </summary>
    public static NavigationTarget? For(IssueDisplay issue) =>
        issue.Target is not { Category: { Length: > 0 } category } || issue.Code == ValidationCode.MissingCategory
            ? null
            : new NavigationTarget(
                issue.Code is ValidationCode.UnknownSpecKey or ValidationCode.UnknownSpecValue or ValidationCode.MissingPreset or
                    ValidationCode.UncoveredCombinations or ValidationCode.UnreachableAssignment
                    ? NavigationScreen.Assignments
                    : NavigationScreen.Values,
                category);
}

/// <summary>
/// 検証結果。Stale は検証した後に内容が変わったこと（結果が古い）。Incomplete は全件検証を途中で取消したこと
/// （検証未完了で、結果は示さない。履歴に保存・出力できる結果として扱わない）。
/// </summary>
public sealed record ValidationDisplay(IReadOnlyList<IssueDisplay> Items, bool Stale)
{
    public bool Incomplete { get; init; }

    /// <summary>未割当の組み合わせがある（CSV に書き出せる）。</summary>
    public bool HasUncovered => Items.Any(i => i.Code == ValidationCode.UncoveredCombinations);

    public int Errors => Items.Count(i => i.Severity == ValidationSeverity.Error);
    public int Warnings => Items.Count(i => i.Severity == ValidationSeverity.Warning);
}

/// <summary>
/// 保存・履歴の操作ボタンの状態。Busy は保存・出力などを実行中。
/// </summary>
public sealed record DataStateDisplay(bool Editable, bool Unsaved, bool Uncommitted, bool ReadOnly, bool RecoveryPending, bool Busy)
{
    /// <summary>履歴に記録する作成者（Windows のユーザー名）。</summary>
    public string Author { get; init; } = "";
}

/// <summary>履歴の 1 件。版の識別子・日時・作者・理由で対象を一意に確認できるように示す。</summary>
public sealed record RevisionDisplay(RevisionId Id, DateTimeOffset When, string Author, string Reason);

/// <summary>
/// 意味単位の変更の 1 件。Location は識別キーをつないだもの、Before/After はセルや項目の前後の値
/// （割り当て行は AssignmentBefore/After で、View が条件と割り当て先を書式化する）。
/// </summary>
public sealed record ChangeDisplay(ContentChangeKind Kind, string Location, string? Before, string? After)
{
    public AssignmentSnapshot? AssignmentBefore { get; init; }
    public AssignmentSnapshot? AssignmentAfter { get; init; }
}

/// <summary>
/// 復元のプレビュー。Choice は、そのまま復元できる・未確定の変更を退避して破棄すれば復元できる・
/// 未保存の編集があるので先に一時保存か破棄が必要・復元できない（版が壊れている、保存中など）のいずれか。
/// </summary>
public enum RestoreChoice { Restore, RestoreByDiscarding, SaveOrDiscardFirst, Unavailable }

public sealed record RestorePreviewDisplay(
    RevisionDisplay Revision, IReadOnlyList<ChangeDisplay> Changes, IReadOnlyList<IssueDisplay> Issues, RestoreChoice Choice);

public enum DataOperation { SaveDraft, Discard, Restore, ExportUncovered, Publish }

/// <summary>
/// 全件検証などの進捗。Operation が null なら「検証」、それ以外はその操作の中の検証・書出し。
/// Checked と Total は調べた数と調べる数。CanCancel は取消できる区間であること。
/// </summary>
public sealed record ProgressDisplay(DataOperation? Operation, long Checked, long Total, bool CanCancel)
{
    public int Percent => Total <= 0 ? 100 : (int)Math.Min(100, Checked * 100 / Total);
}

/// <summary>
/// 保存・確定保存・復元・出力の結果。Stage はどこまで完了したか（JSON 保存だけで確定保存の成功と表示しないため）。
/// Detail は技術的な要約（通常画面ではコピー用の診断としてだけ示す）。
/// </summary>
public sealed record OperationDisplay(
    DataOperation Operation,
    OperationStatus Status,
    OperationStage Stage,
    IReadOnlyList<IssueDisplay> Issues,
    string? Output,
    string? Detail)
{
    /// <summary>業務上の前提を満たさずに止めた理由（表示言語の文言は View が決める）。</summary>
    public OperationReason? Reason { get; init; }
    public string? FixedPublicationMetadata { get; init; }
}
