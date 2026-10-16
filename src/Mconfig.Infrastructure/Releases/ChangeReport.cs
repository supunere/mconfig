using System.Globalization;
using System.Text;
using Mconfig.Application.History;
using Mconfig.Application.Ports;
using Mconfig.Domain.Configuration;

namespace Mconfig.Infrastructure.Releases;

/// <summary>
/// ZIP に同梱する change_diff_report.txt。比較元から出力内容への意味差分と、各コミットの理由・作者・日時を
/// 対応させて書く。テスト版の未確定の変更は確定済みの変更と分け、作者や日時を付けない。固定入力から毎回同じ文面になる。
/// </summary>
internal static class ChangeReport
{
    public static string Write(
        ExportJournal journal,
        IReadOnlyList<ContentChange> changes,
        IReadOnlyList<RevisionInfo> commits,
        IReadOnlyList<ContentChange> uncommitted)
    {
        StringBuilder text = new();
        void Line(string value = "") => text.Append(value).Append("\r\n");

        bool release = journal.Kind == ReleaseKind.Release;
        Line("変更差分レポート");
        Line();
        Line($"種別: {(release ? "設定の発行" : "テスト版")}");
        Line($"出力名: {journal.Name}");
        Line($"作成日時: {Time(journal.CreatedAt)} ({journal.TimeZone})");
        if (release)
        {
            Line($"対象のバージョン: {journal.Target}");
        }
        else
        {
            Line($"基準の確定版: {journal.BaseHead ?? "なし（まだ確定保存していない）"}");
            Line($"未確定の変更: {(journal.UncommittedChanges ? "あり（下の「未確定の変更」を参照）" : "なし")}");
        }
        Line(journal.CompareToRevision is null
            ? "比較元: なし（全内容を変更として記載）"
            : $"比較元: {journal.CompareToName}（バージョン {journal.CompareToRevision}）");
        Line();

        Line("■ 変更内容（比較元 → この出力）");
        WriteChanges(Line, changes);
        Line();

        Line(journal.CompareToRevision is null ? "■ 記録済みの変更の履歴" : "■ 記録済みの変更の履歴（比較元より後）");
        if (commits.Count == 0) Line("（なし）");
        foreach (RevisionInfo commit in commits)
        {
            Line($"- {Time(commit.When)}  {commit.Author}  {Short(commit.Id.Value)}");
            foreach (string reason in commit.Message.Split('\n'))
                Line($"    {reason.TrimEnd('\r')}");
        }

        if (!release)
        {
            Line();
            Line("■ 未確定の変更（確定保存されていない。基準の確定版からの差分）");
            WriteChanges(Line, uncommitted);
        }
        return text.ToString();
    }

    private static void WriteChanges(Action<string> line, IReadOnlyList<ContentChange> changes)
    {
        if (changes.Count == 0) line("（なし）");
        foreach (ContentChange change in changes) line($"- {Describe(change)}");
    }

    public static string Describe(ContentChange change)
    {
        ChangeLocation l = change.Location;
        string where = string.Join(" ", new[]
        {
            l.DefinitionPath is null ? null : $"定義ファイル {l.DefinitionPath}",
            l.SpecKey is null ? null : $"仕様 {l.SpecKey}",
            l.Schema is null ? null : $"スキーマ {l.Schema}",
            l.Category is null ? null : $"カテゴリ {l.Category}",
            l.Preset is null ? null : $"プリセット {l.Preset}",
            l.Group is null ? null : $"グループ {l.Group}",
            l.Row is null ? null : $"行 {l.Row}",
            l.Column is null ? null : $"列 {l.Column}",
            l.Index is null ? null : $"割り当て {l.Index + 1} 行目",
        }.Where(s => s is not null));
        string what = change.Kind switch
        {
            ContentChangeKind.SpecAxisAdded => $"追加（値: {Items(change.ItemsAfter)}）",
            ContentChangeKind.SpecAxisRemoved => $"削除（値: {Items(change.ItemsBefore)}）",
            ContentChangeKind.SpecValuesChanged => $"値の変更: {Items(change.ItemsBefore)} → {Items(change.ItemsAfter)}",
            ContentChangeKind.SpecAxesReordered => $"仕様の順序の変更: {Items(change.ItemsBefore)} → {Items(change.ItemsAfter)}",
            ContentChangeKind.SchemaAdded => "追加",
            ContentChangeKind.SchemaRemoved => "削除",
            ContentChangeKind.SchemaGroupAdded => "グループの追加",
            ContentChangeKind.SchemaGroupRemoved => "グループの削除",
            ContentChangeKind.SchemaGroupsReordered => $"グループの順序の変更: {Items(change.ItemsBefore)} → {Items(change.ItemsAfter)}",
            ContentChangeKind.SchemaRowsChanged => $"行の変更: {Items(change.ItemsBefore)} → {Items(change.ItemsAfter)}",
            ContentChangeKind.SchemaColumnsChanged => $"列の変更: {Items(change.ItemsBefore)} → {Items(change.ItemsAfter)}",
            ContentChangeKind.CategoryAdded => "追加",
            ContentChangeKind.CategoryRemoved => "削除",
            ContentChangeKind.PresetAdded => "追加",
            ContentChangeKind.PresetRemoved => "削除",
            ContentChangeKind.PresetsReordered => $"プリセットの順序の変更: {Items(change.ItemsBefore)} → {Items(change.ItemsAfter)}",
            ContentChangeKind.GroupStateChanged => $"使用状態の変更: {State(change.ItemsBefore)} → {State(change.ItemsAfter)}",
            ContentChangeKind.RowChanged => $"編集できない構造の変更: {Items(change.ItemsBefore)} → {Items(change.ItemsAfter)}",
            ContentChangeKind.CellChanged => $"{Cell(change.CellBefore)} → {Cell(change.CellAfter)}",
            ContentChangeKind.AssignmentAdded => $"追加: {Assignment(change.AssignmentAfter)}",
            ContentChangeKind.AssignmentRemoved => $"削除: {Assignment(change.AssignmentBefore)}",
            ContentChangeKind.AssignmentChanged => $"{Assignment(change.AssignmentBefore)} → {Assignment(change.AssignmentAfter)}",
            ContentChangeKind.DisplayNamesChanged => $"表示名または単位の変更: {Items(change.ItemsBefore)} → {Items(change.ItemsAfter)}",
            ContentChangeKind.OtherDataChanged => "その他のデータの変更（アプリが解釈しない項目を含む）" +
                (change.ItemsBefore is null && change.ItemsAfter is null ? "" : $": {Items(change.ItemsBefore)} → {Items(change.ItemsAfter)}"),
            _ => change.Kind.ToString(),
        };
        return where.Length == 0 ? what : $"[{where}] {what}";
    }

    private static string Items(IReadOnlyList<string>? items) => items is null || items.Count == 0 ? "（なし）" : string.Join(", ", items);

    private static string State(IReadOnlyList<string>? items) => items?.FirstOrDefault() switch
    {
        "absent" => "なし",
        "unused" => "使用しない",
        "used" => "使用する",
        var other => other ?? "（なし）",
    };

    private static string Cell(CellValue? value) => value switch
    {
        null => "（なし）",
        { Kind: CellValueKind.Missing } => "（未入力）",
        { Kind: CellValueKind.Null } => "null",
        { Kind: CellValueKind.Text } v => $"\"{v.Text}\"",
        { Kind: CellValueKind.Boolean } v => v.Flag ? "true" : "false",
        { } v => v.Text ?? "",
    };

    private static string Assignment(AssignmentSnapshot? assignment) => assignment is null
        ? "（なし）"
        : $"{string.Join(", ", assignment.Conditions.Select(c => $"{c.Key}={c.Value}"))} → {assignment.Preset ?? "無効"}";

    private static string Time(DateTimeOffset when) => when.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
