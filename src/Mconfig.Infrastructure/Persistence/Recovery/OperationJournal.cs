using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mconfig.Infrastructure.Persistence.Recovery;

/// <summary>書込操作の段階。Applying 以降に止まった操作は、ハッシュを照合して新版の採用か旧版一式への復帰を決める。</summary>
internal enum OperationState
{
    /// <summary>記録を作り、一時ファイルを書いている。対象ファイルには触れていない。</summary>
    Preparing,
    /// <summary>一時ファイルを書き終え、再読込で検証した。対象ファイルには触れていない。</summary>
    Prepared,
    /// <summary>対象ファイルの置換・追加・削除を始めた。</summary>
    Applying,
    Completed,
    RolledBack,
}

/// <summary>対象ファイル一つ。ハッシュが null はファイルがない（追加・削除）ことを表す。</summary>
internal sealed record OperationEntry(string Path, string? OldHash, string? NewHash);

/// <summary>
/// 復旧記録。対象ファイルを変える前に、旧版・新版のハッシュと内容指紋、対象集合、段階を残す。
/// recovery/{操作ID}/ に、記録と旧版（old/）・取り下げた新版（rolledback/）の素材を置く。
/// </summary>
internal sealed record OperationJournal(
    int Version,
    Guid OperationId,
    string Kind,
    DateTimeOffset CreatedAt,
    OperationState State,
    string? OldFingerprint,
    string? NewFingerprint,
    IReadOnlyList<OperationEntry> Entries,
    string? Revision = null)
{
    public const string FileName = "journal.json";
    public const int CurrentVersion = 2;

    /// <summary>Version 2 以降は変更有無にかかわらず操作時点の全データファイルを記録する。</summary>
    public bool HasCompleteFileSet => Version >= 2;

    public static string DirectoryOf(WorkspaceLayout layout, Guid operationId) =>
        layout.FullPath($"{WorkspaceLayout.RecoveryDirectoryName}/{operationId:N}");

    /// <summary>旧版の退避先（置換・削除の前の内容）。</summary>
    public string OldCopyPath(WorkspaceLayout layout, string path) =>
        layout.FullPath($"{WorkspaceLayout.RecoveryDirectoryName}/{OperationId:N}/old/{path}");

    /// <summary>取り下げた新版の退避先（ファイルは削除せず素材として残す）。</summary>
    public string RolledBackCopyPath(WorkspaceLayout layout, string path) =>
        layout.FullPath($"{WorkspaceLayout.RecoveryDirectoryName}/{OperationId:N}/rolledback/{path}");

    /// <summary>対象と同じディレクトリに置く一時ファイル（操作IDで識別できる名前）。</summary>
    public string TempPath(WorkspaceLayout layout, string path) =>
        layout.FullPath($"{path}.{OperationId:N}.tmp");

    public bool IsFinished => State is OperationState.Completed or OperationState.RolledBack;

    /// <summary>
    /// 読取りをまたいだ操作記録の追加・段階更新を検出する世代。完了済みの操作も含め、記録の生バイトで比較する。
    /// </summary>
    public static string Generation(WorkspaceLayout layout)
    {
        if (!Directory.Exists(layout.RecoveryDirectory)) return "none";
        return string.Join("|", Directory.EnumerateDirectories(layout.RecoveryDirectory)
            .Order(StringComparer.Ordinal)
            .Select(directory =>
            {
                string journal = Path.Combine(directory, FileName);
                return Path.GetFileName(directory) + ":" +
                    (File.Exists(journal) ? WorkspaceFileSet.Hash(File.ReadAllBytes(journal)) : "missing");
            }));
    }

    /// <summary>記録を置き換える。一時ファイルに書いて Flush してから同じディレクトリで名前を変える。</summary>
    public void Save(WorkspaceLayout layout)
    {
        string directory = DirectoryOf(layout, OperationId);
        Directory.CreateDirectory(directory);
        JObject json = new()
        {
            ["version"] = Version,
            ["operationId"] = OperationId.ToString("D"),
            ["kind"] = Kind,
            ["createdAt"] = CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["state"] = State.ToString(),
            ["oldFingerprint"] = OldFingerprint,
            ["newFingerprint"] = NewFingerprint,
            ["revision"] = Revision,
            ["entries"] = new JArray(Entries.Select(e => new JObject
            {
                ["path"] = e.Path,
                ["oldSha256"] = e.OldHash,
                ["newSha256"] = e.NewHash,
            })),
        };
        DurableFile.WriteText(Path.Combine(directory, FileName), json.ToString(Formatting.Indented));
    }

    public OperationJournal With(OperationState state, WorkspaceLayout layout)
    {
        OperationJournal next = this with { State = state };
        next.Save(layout);
        return next;
    }

    /// <summary>記録を読む。壊れていれば <see cref="InvalidDataException"/>。記録がなければ null（記録を作る前に止まった）。</summary>
    public static OperationJournal? Load(string directory, WorkspaceLayout layout)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            JObject json = (JObject)Json.JsonText.Parse(File.ReadAllBytes(path));
            int version = (int?)json["version"]
                ?? throw new InvalidDataException("The operation journal has no format version.");
            if (version is < 1 or > CurrentVersion)
                throw new InvalidDataException($"Unsupported operation journal version {version}.");
            Guid id = Guid.Parse((string)json["operationId"]!);
            if (!string.Equals(Path.GetFileName(directory), id.ToString("N"), StringComparison.Ordinal))
                throw new InvalidDataException("The journal does not belong to its directory.");
            OperationEntry[] entries = ((JArray)json["entries"]!).Select(e => new OperationEntry(
                (string)e["path"]!, (string?)e["oldSha256"], (string?)e["newSha256"])).ToArray();
            // 記録の対象はワークスペースのデータファイルに限る（記録を改変したルート外の参照を拒否する）
            if (entries.Length == 0 || entries.Any(e => !WorkspaceLayout.IsDataFile(e.Path)))
                throw new InvalidDataException("The journal refers to files outside the workspace data.");
            foreach (OperationEntry entry in entries) layout.FullPath(entry.Path);
            return new OperationJournal(
                version,
                id,
                (string)json["kind"]!,
                DateTimeOffset.Parse((string)json["createdAt"]!, CultureInfo.InvariantCulture),
                Enum.TryParse((string)json["state"]!, out OperationState state) && Enum.IsDefined(state)
                    ? state
                    : throw new InvalidDataException("Unknown operation state."),
                (string?)json["oldFingerprint"],
                (string?)json["newFingerprint"],
                entries,
                (string?)json["revision"]);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"The operation journal '{path}' is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>読める記録をすべて返す（壊れた記録は除く）。</summary>
    public static IEnumerable<OperationJournal> LoadAll(WorkspaceLayout layout)
    {
        if (!Directory.Exists(layout.RecoveryDirectory)) yield break;
        foreach (string directory in Directory.EnumerateDirectories(layout.RecoveryDirectory))
        {
            OperationJournal? journal;
            try
            {
                journal = Load(directory, layout);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (journal is not null) yield return journal;
        }
    }
}
