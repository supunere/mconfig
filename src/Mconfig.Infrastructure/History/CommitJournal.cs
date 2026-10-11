using Mconfig.Infrastructure.Persistence;
using Mconfig.Infrastructure.Persistence.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mconfig.Infrastructure.History;

internal enum CommitState
{
    /// <summary>コミット予定を記録した。コミットとブランチ更新の成否は HEAD を照合して判定する。</summary>
    Prepared,
    Completed,
    /// <summary>コミットされなかった（W は保存済み、C は旧版のまま）。</summary>
    NotCommitted,
}

/// <summary>
/// コミット予定の記録。コミット前に操作ID・期待親・tree・内容指紋を recovery/{操作ID}/commit.json へ残し、
/// 応答を失った場合や再起動時に、同じ操作のコミットが HEAD にあるかで完了を判定する。
/// </summary>
internal sealed record CommitJournal(Guid OperationId, string? ExpectedParent, string Tree, string Fingerprint, CommitState State)
{
    public const string FileName = "commit.json";

    public static string PathOf(WorkspaceLayout layout, Guid operationId) =>
        Path.Combine(layout.FullPath($"{WorkspaceLayout.RecoveryDirectoryName}/{operationId:N}"), FileName);

    public CommitJournal Save(WorkspaceLayout layout)
    {
        string path = PathOf(layout, OperationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JObject json = new()
        {
            ["version"] = 1,
            ["operationId"] = OperationId.ToString("D"),
            ["expectedParent"] = ExpectedParent,
            ["tree"] = Tree,
            ["fingerprint"] = Fingerprint,
            ["state"] = State.ToString(),
        };
        DurableFile.WriteText(path, json.ToString(Formatting.Indented));
        return this;
    }

    public CommitJournal With(CommitState state, WorkspaceLayout layout) => (this with { State = state }).Save(layout);

    /// <summary>完了していないコミット記録。壊れた記録は Problem に理由を入れて返す。</summary>
    public static IEnumerable<(CommitJournal? Journal, string? Problem)> Pending(WorkspaceLayout layout)
    {
        if (!Directory.Exists(layout.RecoveryDirectory)) yield break;
        foreach (string directory in Directory.EnumerateDirectories(layout.RecoveryDirectory).Order(StringComparer.Ordinal))
        {
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) continue;
            CommitJournal? journal = null;
            string? problem = null;
            try
            {
                JObject json = (JObject)JsonText.Parse(File.ReadAllBytes(path));
                journal = new CommitJournal(
                    Guid.Parse((string)json["operationId"]!),
                    (string?)json["expectedParent"],
                    (string?)json["tree"] ?? throw new InvalidDataException("tree is missing"),
                    (string?)json["fingerprint"] ?? "",
                    Enum.Parse<CommitState>((string)json["state"]!));
                if (journal.OperationId.ToString("N") != Path.GetFileName(directory))
                    throw new InvalidDataException("The commit journal does not belong to its directory.");
            }
            catch (Exception ex)
            {
                journal = null;
                problem = $"{path}: damaged commit journal ({ex.Message})";
            }
            if (journal is { State: not CommitState.Prepared }) continue;
            yield return (journal, problem);
        }
    }
}
