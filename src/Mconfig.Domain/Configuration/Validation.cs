namespace Mconfig.Domain.Configuration;

public enum ValidationSeverity { Warning, Error }

/// <summary>検証を適用する操作。</summary>
public enum ValidationOperation { Edit, TemporarySave, Commit, Export }

public enum ValidationCode
{
    /// <summary>プリセットまたは割り当ての構造が不正。</summary>
    InvalidStructure,
    /// <summary>プリセット名が空か重複。</summary>
    InvalidPresetName,
    /// <summary>設定カテゴリに対応するスキーマがない。</summary>
    MissingSchema,
    /// <summary>スキーマ内のグループキーが重複。</summary>
    DuplicateSchemaGroup,
    /// <summary>スキーマ内の行キーが重複。</summary>
    DuplicateSchemaRow,
    /// <summary>スキーマ内の列キーが重複。</summary>
    DuplicateSchemaColumn,
    /// <summary>スキーマに対応する設定カテゴリがない。</summary>
    MissingCategory,
    /// <summary>仕様の値の集合が空。</summary>
    EmptySpecValues,
    /// <summary>仕様の値が重複している。</summary>
    DuplicateSpecValue,
    /// <summary>仕様の値に予約値 any がある。</summary>
    ReservedSpecValue,
    /// <summary>割り当ての仕様キーが仕様に存在しない。</summary>
    UnknownSpecKey,
    /// <summary>割り当ての仕様値が仕様に存在しない。</summary>
    UnknownSpecValue,
    /// <summary>割り当てのプリセットが存在しない。</summary>
    MissingPreset,
    /// <summary>スキーマにないグループ（orphan）。</summary>
    OrphanGroup,
    /// <summary>スキーマにない行（orphan）。</summary>
    OrphanRow,
    /// <summary>スキーマにない列（orphan）。</summary>
    OrphanColumn,
    /// <summary>スキーマにあるグループがプリセットにない（未使用 null とは別。使用するかどうかが決まっていない）。</summary>
    MissingGroup,
    /// <summary>使用中グループの行がない。</summary>
    BlankRow,
    /// <summary>使用中グループのセルが未入力。</summary>
    BlankCell,
    /// <summary>既知の行が編集できない構造。</summary>
    UnsupportedRow,
    /// <summary>既知のセルが編集できない構造。</summary>
    UnsupportedCell,
    /// <summary>どの割り当て行にも一致しない組み合わせがある。確認付き警告。</summary>
    UncoveredCombinations,
    /// <summary>上位の行に覆われて採用されない割り当て行がある。確認付き警告。</summary>
    UnreachableAssignment,
    /// <summary>変更理由が空白のみ。コミット不可。</summary>
    ReasonEmpty,
    /// <summary>変更理由が短い。コミット前に確認。</summary>
    ReasonTooShort,
}

/// <summary>検証結果の対象。上位から順に、分かる範囲まで指定する。</summary>
public sealed record ValidationTarget(
    string Category, string? Preset = null, string? Group = null, string? Row = null, string? Column = null);

public static class ValidationPolicy
{
    /// <summary>
    /// 診断専用カテゴリを元の保存表現のまま保持するとき、ワークスペース全体の操作から除外できる問題か。
    /// スキーマ欠落はカテゴリの保存表現ではなく定義一式との不整合なので除外しない。
    /// </summary>
    public static bool IsDiagnosticCategoryError(ValidationCode code) => code is
        ValidationCode.InvalidStructure or ValidationCode.InvalidPresetName or
        ValidationCode.UnknownSpecKey or ValidationCode.UnknownSpecValue or ValidationCode.MissingPreset or
        ValidationCode.OrphanGroup or ValidationCode.OrphanRow or ValidationCode.OrphanColumn or
        ValidationCode.MissingGroup or ValidationCode.BlankRow or ValidationCode.BlankCell or
        ValidationCode.UnsupportedRow or ValidationCode.UnsupportedCell;

    /// <summary>
    /// 検証結果の重大度を操作ごとに決める。
    /// 構造・参照の誤りは常にエラー、orphan は内容を保持したまま警告にとどめる。
    /// 必要なグループの欠落・未入力と編集できない構造は、編集中と一時保存では修正候補（警告）、確定保存と出力ではエラー。
    /// </summary>
    public static ValidationSeverity SeverityFor(ValidationCode code, ValidationOperation operation) => code switch
    {
        ValidationCode.OrphanGroup or ValidationCode.OrphanRow or ValidationCode.OrphanColumn or
        ValidationCode.UncoveredCombinations or ValidationCode.UnreachableAssignment or
        ValidationCode.ReasonTooShort =>
            ValidationSeverity.Warning,
        ValidationCode.MissingGroup or ValidationCode.BlankRow or ValidationCode.BlankCell or
        ValidationCode.UnsupportedRow or ValidationCode.UnsupportedCell =>
            operation is ValidationOperation.Commit or ValidationOperation.Export
                ? ValidationSeverity.Error
                : ValidationSeverity.Warning,
        _ => ValidationSeverity.Error,
    };

    /// <summary>
    /// 確定保存・出力の前に利用者の確認（承認）を求める警告か。orphan は通知だけで保存を妨げない。
    /// </summary>
    public static bool RequiresConfirmation(ValidationCode code) =>
        code is ValidationCode.UncoveredCombinations or ValidationCode.UnreachableAssignment or ValidationCode.ReasonTooShort;
}

/// <summary>確定保存の変更理由の規則。理由の検証はコミット操作にだけ適用する。</summary>
public static class CommitReason
{
    public const int MinimumLength = 10;

    /// <summary>理由の検証結果。問題がなければ null。</summary>
    public static ValidationCode? Check(string? reason)
    {
        string trimmed = reason?.Trim() ?? "";
        if (trimmed.Length == 0) return ValidationCode.ReasonEmpty;
        return trimmed.Length < MinimumLength ? ValidationCode.ReasonTooShort : null;
    }
}
