using System.Globalization;
using Mconfig.Application.Ports;
using Mconfig.Infrastructure.Persistence;
using Mconfig.Infrastructure.Persistence.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mconfig.Infrastructure.Releases;

/// <summary>出力の段階。一連の操作を原子的とみなさず、段階ごとに記録して再開する。</summary>
internal enum ExportStage
{
    /// <summary>入力・日時・名前・比較元を固定した。一時ZIPの作成とタグ（リリース版）の前。</summary>
    Prepared,
    /// <summary>一時ZIPを検証し、リリース版は対象 SHA へタグを付けた。</summary>
    Tagged,
    /// <summary>ZIP を最終名へ移した。公開完了記録の前。</summary>
    Published,
    Completed,
}

/// <summary>
/// 出力の操作記録（recovery/{操作ID}/export.json）。対象・比較元・出力日時・名前・ZIP のハッシュを固定し、
/// 中断後は同じ値で再開・再生成する。テスト版の入力（W）は同じディレクトリの input/ に複製して残す。
/// </summary>
internal sealed record ExportJournal(
    Guid OperationId,
    ReleaseKind Kind,
    string Name,
    DateTimeOffset CreatedAt,
    string TimeZone,
    string AppVersion,
    string? Target,
    string? BaseHead,
    string? WorkingFingerprint,
    bool UncommittedChanges,
    string? CompareToName,
    string? CompareToRevision,
    ExportStage Stage,
    string? ZipSha256)
{
    public const string FileName = "export.json";

    public static string DirectoryOf(WorkspaceLayout layout, Guid operationId) =>
        layout.FullPath($"{WorkspaceLayout.RecoveryDirectoryName}/{operationId:N}");

    /// <summary>テスト版の固定入力の置き場所（ワークスペース相対パスをそのまま使う）。</summary>
    public string InputPath(WorkspaceLayout layout, string dataPath) =>
        layout.FullPath($"{WorkspaceLayout.RecoveryDirectoryName}/{OperationId:N}/input/{dataPath}");

    public string InputDirectory(WorkspaceLayout layout) =>
        layout.FullPath($"{WorkspaceLayout.RecoveryDirectoryName}/{OperationId:N}/input");

    public string FinalPath(WorkspaceLayout layout) => layout.FullPath($"{WorkspaceLayout.ExportsDirectoryName}/{Name}.zip");

    public string TempPath(WorkspaceLayout layout) => layout.FullPath($"{WorkspaceLayout.ExportsDirectoryName}/{Name}.zip.{OperationId:N}.tmp");

    public ExportJournal Save(WorkspaceLayout layout)
    {
        string directory = DirectoryOf(layout, OperationId);
        Directory.CreateDirectory(directory);
        JObject json = new()
        {
            ["version"] = 1,
            ["operationId"] = OperationId.ToString("D"),
            ["kind"] = Kind.ToString(),
            ["name"] = Name,
            ["createdAt"] = CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["timeZone"] = TimeZone,
            ["appVersion"] = AppVersion,
            ["target"] = Target,
            ["baseHead"] = BaseHead,
            ["workingFingerprint"] = WorkingFingerprint,
            ["uncommittedChanges"] = UncommittedChanges,
            ["compareToName"] = CompareToName,
            ["compareToRevision"] = CompareToRevision,
            ["stage"] = Stage.ToString(),
            ["zipSha256"] = ZipSha256,
        };
        DurableFile.WriteText(Path.Combine(directory, FileName), json.ToString(Formatting.Indented));
        return this;
    }

    public ExportJournal With(ExportStage stage, WorkspaceLayout layout, string? zipSha256 = null) =>
        (this with { Stage = stage, ZipSha256 = zipSha256 ?? ZipSha256 }).Save(layout);

    /// <summary>記録を読む。なければ null、壊れていれば <see cref="InvalidDataException"/>。</summary>
    public static ExportJournal? Load(WorkspaceLayout layout, Guid operationId)
    {
        string path = Path.Combine(DirectoryOf(layout, operationId), FileName);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            JObject json = (JObject)JsonText.Parse(bytes);
            ExportJournal journal = new(
                Guid.Parse((string)json["operationId"]!),
                Enum.Parse<ReleaseKind>((string)json["kind"]!),
                (string)json["name"]!,
                DateTimeOffset.Parse((string)json["createdAt"]!, CultureInfo.InvariantCulture),
                (string)json["timeZone"]!,
                (string)json["appVersion"]!,
                (string?)json["target"],
                (string?)json["baseHead"],
                (string?)json["workingFingerprint"],
                (bool)json["uncommittedChanges"]!,
                (string?)json["compareToName"],
                (string?)json["compareToRevision"],
                Enum.Parse<ExportStage>((string)json["stage"]!),
                (string?)json["zipSha256"]);
            if (journal.OperationId != operationId)
                throw new InvalidDataException("The export journal does not belong to its directory.");
            if ((int?)json["version"] != 1 || !Enum.IsDefined(journal.Kind) || !Enum.IsDefined(journal.Stage))
                throw new InvalidDataException("The export journal has an unsupported version, kind or stage.");
            // 名前はファイル名とタグ名に使うので、ルート外や別の場所を指す値を拒否する
            layout.FullPath($"{WorkspaceLayout.ExportsDirectoryName}/{journal.Name}.zip");
            if (journal.Name.Contains('/') || journal.Name.Contains('\\'))
                throw new InvalidDataException("The export name is not a file name.");
            return journal;
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"The export journal '{path}' is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>再開候補を読む。読めない記録も完了を確認できない問題として返す。</summary>
    public static PendingExportsReadModel LoadPending(WorkspaceLayout layout, CancellationToken cancellationToken)
    {
        List<PendingExport> exports = [];
        List<PendingExportProblem> problems = [];
        try
        {
            // Directory.Exists/File.Exists はアクセス失敗も false にするので、列挙・読込の失敗を捕捉する。
            foreach (string directory in Directory.EnumerateDirectories(layout.RecoveryDirectory).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id)) continue;
                string location = $"{WorkspaceLayout.RecoveryDirectoryName}/{id:N}/{FileName}";
                try
                {
                    // 別種の復旧記録のディレクトリは出力候補に含めない。
                    if (!Directory.EnumerateFiles(directory, FileName).Any()) continue;
                    ExportJournal? journal = Load(layout, id);
                    if (journal is null)
                        problems.Add(new PendingExportProblem(id, location, PendingExportProblemKind.UnreadableJournal));
                    else if (journal.Stage != ExportStage.Completed)
                        exports.Add(new PendingExport(id, journal.Kind, journal.Name, journal.CreatedAt, journal.Stage.ToString()));
                }
                catch (InvalidDataException ex)
                {
                    PendingExportProblemKind kind = ex.InnerException is IOException or UnauthorizedAccessException
                        ? PendingExportProblemKind.UnreadableJournal : PendingExportProblemKind.DamagedJournal;
                    problems.Add(new PendingExportProblem(id, location, kind));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add(new PendingExportProblem(id, location, PendingExportProblemKind.UnreadableJournal));
                }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new PendingExportProblem(null, WorkspaceLayout.RecoveryDirectoryName, PendingExportProblemKind.ListUnavailable));
        }
        return new PendingExportsReadModel(exports, problems);
    }
}

/// <summary>
/// 公開完了記録（exports/published/{名前}.json）。比較元の判定に使うので、復旧素材（recovery/）の保持期間とは独立に残す。
/// </summary>
internal sealed record PublishedRecord(
    string Name, ReleaseKind Kind, Guid OperationId, string? Target, string? CompareToRevision, string ZipSha256, DateTimeOffset PublishedAt)
{
    public static string DirectoryOf(WorkspaceLayout layout) => layout.FullPath($"{WorkspaceLayout.ExportsDirectoryName}/published");

    public static string PathOf(WorkspaceLayout layout, string name) => Path.Combine(DirectoryOf(layout), name + ".json");

    public void Save(WorkspaceLayout layout)
    {
        Directory.CreateDirectory(DirectoryOf(layout));
        JObject json = new()
        {
            ["version"] = 1,
            ["name"] = Name,
            ["kind"] = Kind.ToString(),
            ["operationId"] = OperationId.ToString("D"),
            ["target"] = Target,
            ["compareToRevision"] = CompareToRevision,
            ["zipSha256"] = ZipSha256,
            ["publishedAt"] = PublishedAt.ToString("O", CultureInfo.InvariantCulture),
        };
        DurableFile.WriteText(PathOf(layout, Name), json.ToString(Formatting.Indented));
    }

    public static PublishedRecord? Load(string path)
    {
        try
        {
            JObject json = (JObject)JsonText.Parse(File.ReadAllBytes(path));
            return new PublishedRecord(
                (string)json["name"]!,
                Enum.Parse<ReleaseKind>((string)json["kind"]!),
                Guid.Parse((string)json["operationId"]!),
                (string?)json["target"],
                (string?)json["compareToRevision"],
                (string)json["zipSha256"]!,
                DateTimeOffset.Parse((string)json["publishedAt"]!, CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException or InvalidCastException or NullReferenceException)
        {
            return null;
        }
    }

    public static IEnumerable<PublishedRecord> LoadAll(WorkspaceLayout layout)
    {
        string directory = DirectoryOf(layout);
        if (!Directory.Exists(directory)) yield break;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            if (Load(path) is { } record && Path.GetFileNameWithoutExtension(path) == record.Name) yield return record;
        }
    }
}
