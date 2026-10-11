using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Workspaces.Editing;

/// <summary>
/// 起動時の構造・参照エラーの判定。エラーがあれば編集の排他を持っていても読取専用で開く。
/// 編集セッションの開始と、設定ファイルの復元候補の検証が同じ規則を使う。
/// </summary>
public static class StartupValidation
{
    /// <summary>
    /// 編集時の規則でエラーになる検証結果があるか。壊れたカテゴリ（診断専用）自身の内容に関するエラーだけは、そのカテゴリを
    /// 編集不可にする扱い（DOM-005 の例外、DOM-007）なので数えない。スキーマ不整合は除外しない。編集中の未入力は警告なので含まない。
    /// </summary>
    public static bool HasErrors(WorkspaceContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return WorkspaceValidator.Validate(content.Settings, content.Spec, content.Schemas).Any(i =>
            ValidationPolicy.SeverityFor(i.Code, ValidationOperation.Edit) == ValidationSeverity.Error &&
            !(ValidationPolicy.IsDiagnosticCategoryError(i.Code) &&
              content.Settings.Categories.TryGetValue(i.Target.Category, out CategoryData? category) && category.IsDiagnosticOnly));
    }
}
