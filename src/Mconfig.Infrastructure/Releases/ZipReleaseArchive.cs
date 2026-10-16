using System.Globalization;
using System.IO.Compression;
using System.Text;
using LibGit2Sharp;
using Mconfig.Application.History;
using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Mconfig.Infrastructure.Releases;

/// <summary>出力の故障注入の位置（試験用）。</summary>
internal enum ExportFaultPoint { Recorded, ZipWritten, Tagged, Published }

/// <summary>
/// テスト版・リリース版 ZIP の出力。入力（リリースは対象コミット、テスト版は期待スタンプと一致する W。プレビューは渡された E）、
/// 出力日時・操作ID・名前・比較元を固定して記録し、一時ZIP → 検証 → タグ（リリース版）→ 公開 → 公開完了記録の順に進める。
/// 同名の別内容の ZIP や別 SHA のタグは上書きしない。出力先は workspace/exports。
/// </summary>
public sealed class ZipReleaseArchive : IReleaseArchive
{
    public const string ReportFileName = "change_diff_report.txt";
    public const string ManifestFileName = "manifest.json";
    private const string DataPrefix = WorkspaceLayout.DataDirectoryName + "/";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly GitDataRepository _repository;
    private readonly WorkspaceLock? _lock;
    private readonly string _appVersion;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="editLock">出力（ファイル・タグの作成）に必要な編集の排他。null なら参照だけを行う。</param>
    public ZipReleaseArchive(GitDataRepository repository, WorkspaceLock? editLock, string appVersion, Func<DateTimeOffset>? clock = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _lock = editLock;
        _appVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>試験用の故障注入。</summary>
    internal Action<ExportFaultPoint>? Faults { get; init; }

    private WorkspaceLayout Layout => _repository.Layout;

    public Task<IReadOnlyList<ReleaseCandidate>> GetReleaseCandidatesAsync(RevisionId? from, CancellationToken cancellationToken) =>
        Task.Run(() => Candidates(from?.Value), cancellationToken);

    public Task<ReleasePlan> PlanAsync(ReleasePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // テスト版のプレビューは Query が固定した E から作る。暗黙にディスク上の W へ置き換えない
        if (request.Kind == ReleaseKind.Test && request.Content is null)
            throw new ArgumentException("A test plan needs the content to be exported.", nameof(request));
        WorkspaceContent? content = request.Kind == ReleaseKind.Test ? request.Content : null;
        return Task.Run(() => Plan(request.Kind, request.Stamp, request.CompareTo, content).Plan, cancellationToken);
    }

    public Task<PendingExportsReadModel> GetPendingExportsAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ExportJournal.LoadPending(Layout, cancellationToken), cancellationToken);

    public async Task<ReleaseOutput> ExportAsync(ReleaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireLock();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                // 同じ操作IDの二度目の実行は、記録した入力で続きを行う
                if (ExportJournal.Load(Layout, request.OperationId) is { } existing)
                {
                    if (request.PreparedRevision is { } revised && existing.Target != revised.Value)
                    {
                        if (IsPublicationFixed(request.OperationId))
                            throw new WorkspaceConflictException("Published metadata cannot be replaced.");
                        // 未公開の操作に属するタグだけを差し替える。完成済みの出力には触れない。
                        string? tagged = TagTarget(existing.Name);
                        if (tagged is not null)
                        {
                            if (tagged != existing.Target) throw new WorkspaceConflictException("The prepared tag was changed outside the application.");
                            _repository.WithRepository(repo => { repo.Tags.Remove(existing.Name); return true; });
                        }
                        existing = existing with { Target = revised.Value, Stage = ExportStage.Prepared, ZipSha256 = null };
                        existing.Save(Layout);
                    }
                    return Continue(existing);
                }

                (ReleasePlan plan, IReadOnlyDictionary<string, byte[]> files) = Plan(request.Kind, request.Stamp, request.CompareTo, preparedRevision: request.PreparedRevision);
                cancellationToken.ThrowIfCancellationRequested();

                DateTimeOffset createdAt = _clock();
                string prefix = request.Kind == ReleaseKind.Release ? "release" : "test";
                string name = string.Create(CultureInfo.InvariantCulture, $"{prefix}-{createdAt:yyyyMMdd-HHmmss}-{request.OperationId:N}");
                ExportJournal journal = new(request.OperationId, request.Kind, name, createdAt, TimeZoneInfo.Local.Id, _appVersion,
                    plan.Target?.Value, plan.BaseHead?.Value, plan.WorkingFingerprint?.Value, plan.UncommittedChanges.Count > 0,
                    plan.CompareTo?.Name, plan.CompareTo?.Revision.Value, ExportStage.Prepared, null);
                if (File.Exists(journal.FinalPath(Layout)) || (request.Kind == ReleaseKind.Release && TagTarget(name) is not null))
                    throw new WorkspaceConflictException($"An output named {name} already exists.");

                // テスト版の W はコミットにないので、再開できるよう固定した入力を復旧素材に残す
                if (request.Kind == ReleaseKind.Test)
                {
                    foreach ((string path, byte[] bytes) in files)
                    {
                        string copy = journal.InputPath(Layout, path);
                        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                        File.WriteAllBytes(copy, bytes);
                    }
                }
                journal.Save(Layout);
                Faults?.Invoke(ExportFaultPoint.Recorded);
                return Continue(journal);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal bool IsCompletedOutputMissing(Guid operationId)
    {
        ExportJournal? journal = ExportJournal.Load(Layout, operationId);
        return journal is null || journal is { Stage: ExportStage.Completed } && !File.Exists(journal.FinalPath(Layout));
    }

    internal bool IsPublicationFixed(Guid operationId)
    {
        ExportJournal? journal = ExportJournal.Load(Layout, operationId);
        return journal is not null && (journal.Stage is ExportStage.Published or ExportStage.Completed || File.Exists(journal.FinalPath(Layout)));
    }

    public async Task<ReleaseOutput> ResumeAsync(Guid operationId, CancellationToken cancellationToken)
    {
        RequireLock();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ExportJournal journal = ExportJournal.Load(Layout, operationId)
                    ?? throw new InvalidOperationException($"There is no export {operationId} to resume.");
                return Continue(journal);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 計画 ──

    /// <summary>
    /// 出力予定の内容と、出力するファイル（ワークスペース相対パス → バイト列）。副作用はない。
    /// テスト版で edited を渡すと、計画（差分・未確定差分・指紋）はその内容から作る（プレビュー用。Files はディスクの W のままで使わない）。
    /// </summary>
    private (ReleasePlan Plan, IReadOnlyDictionary<string, byte[]> Files) Plan(
        ReleaseKind kind, WorkspaceStamp stamp, ReleaseComparison? compareTo, WorkspaceContent? edited = null, RevisionId? preparedRevision = null)
    {
        IReadOnlyList<string> missing = Layout.DiagnoseMissingData();
        if (missing.Count > 0) throw new WorkspaceUnreadableException("The workspace data is incomplete.", missing);
        WorkspaceFileSet disk = WorkspaceFileSet.Read(Layout);
        string? head = _repository.ReadHead();
        if (disk.Stamp(head) != stamp)
            throw new WorkspaceConflictException("The workspace files or HEAD changed after they were loaded.");

        // 発行ではまだHEADを進めず、準備済みコミットからZIPを作成する。
        if (preparedRevision is not null)
        {
            if (kind != ReleaseKind.Release) throw new ArgumentException("Only publication can use a prepared revision.");
            head = preparedRevision.Value;
        }

        IReadOnlyDictionary<string, byte[]> files;
        WorkspaceContent content;
        IReadOnlyList<ContentChange> uncommitted = [];
        if (kind == ReleaseKind.Release)
        {
            if (head is null) throw new WorkspaceConflictException("There is no committed version to release.");
            files = _repository.ReadFiles(head);
            content = Parse(files, $"revision {head}");
        }
        else
        {
            files = disk.Files;
            content = edited ?? Parse(files, null);
            WorkspaceContent committed = head is null ? WorkspaceDiff.Empty : Parse(_repository.ReadFiles(head), $"HEAD {head}");
            uncommitted = WorkspaceDiff.Compare(committed, content);
        }

        IReadOnlyList<ReleaseCandidate> candidates = Candidates(head);
        ReleaseCandidate? compare = compareTo is null
            ? candidates.FirstOrDefault()
            : compareTo == ReleaseComparison.None ? null
            : candidates.FirstOrDefault(c => c.Name == compareTo.Name && c.Revision == compareTo.Revision)
              ?? throw new WorkspaceConflictException($"{compareTo.Name} ({compareTo.Revision!.Value}) is not a published release in the history of the current version.");
        (IReadOnlyList<ContentChange> changes, IReadOnlyList<RevisionInfo> commits) = Differences(content, head, compare?.Revision.Value);

        ReleasePlan plan = new(kind,
            kind == ReleaseKind.Release ? new RevisionId(head!) : null,
            kind == ReleaseKind.Test && head is not null ? new RevisionId(head) : null,
            kind == ReleaseKind.Test ? WorkspaceFingerprints.Of(content) : null,
            compare, candidates, commits, changes, uncommitted);
        return (plan, files);
    }

    /// <summary>比較元から出力内容への意味差分と、比較元より後のコミット（それぞれ当時の定義で比較する）。</summary>
    private (IReadOnlyList<ContentChange> Changes, IReadOnlyList<RevisionInfo> Commits) Differences(
        WorkspaceContent content, string? head, string? compareRevision)
    {
        WorkspaceContent baseline = compareRevision is null
            ? WorkspaceDiff.Empty
            : Parse(_repository.ReadFiles(compareRevision), $"revision {compareRevision}");
        IReadOnlyList<RevisionInfo> commits = head is null ? [] : _repository.ReadRange(head, compareRevision);
        return (WorkspaceDiff.Compare(baseline, content), commits);
    }

    /// <summary>
    /// 公開完了済みで、タグが記録した対象を指し、from の祖先（from を含む）にあるリリース。
    /// 履歴上で近い順、同じコミットでは公開日時の新しい順。未完了のリリースや記録のないタグは含めない。
    /// </summary>
    private IReadOnlyList<ReleaseCandidate> Candidates(string? from)
    {
        if (from is null) return [];
        IReadOnlyDictionary<string, int> distances = _repository.AncestorDistances(from);
        return PublishedRecord.LoadAll(Layout)
            .Where(r => r.Kind == ReleaseKind.Release && r.Target is not null && distances.ContainsKey(r.Target) && TagTarget(r.Name) == r.Target)
            .OrderBy(r => distances[r.Target!])
            .ThenByDescending(r => r.PublishedAt)
            .Select(r => new ReleaseCandidate(r.Name, new RevisionId(r.Target!), r.PublishedAt))
            .ToArray();
    }

    // ── 出力と公開 ──

    private ReleaseOutput Continue(ExportJournal journal)
    {
        string temp = journal.TempPath(Layout);
        string final = journal.FinalPath(Layout);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);

        if (journal.Stage == ExportStage.Prepared)
        {
            // 一時ZIPがないか記録と異なれば、固定した入力とメタデータから作り直す
            if (!File.Exists(temp) || journal.ZipSha256 is null || WorkspaceFileSet.HashOf(temp) != journal.ZipSha256)
            {
                IReadOnlyList<(string Path, byte[] Bytes)> entries = Entries(journal);
                WriteZip(temp, entries, journal.CreatedAt);
                Verify(temp, entries);
                journal = journal.With(ExportStage.Prepared, Layout, WorkspaceFileSet.HashOf(temp));
            }
            Faults?.Invoke(ExportFaultPoint.ZipWritten);
            if (journal.Kind == ReleaseKind.Release) EnsureTag(journal);
            journal = journal.With(ExportStage.Tagged, Layout);
            Faults?.Invoke(ExportFaultPoint.Tagged);
        }

        if (journal.Stage == ExportStage.Tagged)
        {
            if (File.Exists(final))
            {
                if (WorkspaceFileSet.HashOf(final) != journal.ZipSha256)
                    throw new WorkspaceConflictException($"{journal.Name}.zip already exists with different content.");
            }
            else if (File.Exists(temp) && WorkspaceFileSet.HashOf(temp) == journal.ZipSha256)
            {
                File.Move(temp, final, overwrite: false);
            }
            else
            {
                // 公開前に一時ZIPが失われた: 作り直してから公開する
                return Continue(journal.With(ExportStage.Prepared, Layout));
            }
            journal = journal.With(ExportStage.Published, Layout);
            Faults?.Invoke(ExportFaultPoint.Published);
        }

        // 公開済み・完了済みの再実行でも、最終 ZIP を記録と照合する。欠損は再生成せず停止する。
        if (journal.Stage is ExportStage.Published or ExportStage.Completed)
        {
            if (!File.Exists(final) || journal.ZipSha256 is null || WorkspaceFileSet.HashOf(final) != journal.ZipSha256)
                throw new WorkspaceConflictException($"{journal.Name}.zip does not match the recorded output.");
        }

        if (journal.Stage == ExportStage.Completed && journal.Kind == ReleaseKind.Release && TagTarget(journal.Name) != journal.Target)
            throw new WorkspaceConflictException($"The tag {journal.Name} does not match the recorded version.");

        if (journal.Stage == ExportStage.Published)
        {
            if (journal.Kind == ReleaseKind.Release) EnsureTag(journal);
            string recordPath = PublishedRecord.PathOf(Layout, journal.Name);
            PublishedRecord? existing = File.Exists(recordPath) ? PublishedRecord.Load(recordPath) : null;
            if (existing is not null && (existing.OperationId != journal.OperationId || existing.ZipSha256 != journal.ZipSha256))
                throw new WorkspaceConflictException($"A different publication of {journal.Name} is already recorded.");
            if (existing is null)
                new PublishedRecord(journal.Name, journal.Kind, journal.OperationId, journal.Target, journal.CompareToRevision,
                    journal.ZipSha256!, _clock()).Save(Layout);
            journal = journal.With(ExportStage.Completed, Layout);
            Log.Information("Export {Name} completed", journal.Name);
        }

        return new ReleaseOutput(final, journal.Kind == ReleaseKind.Release ? journal.Name : null,
            journal.Target is null ? null : new RevisionId(journal.Target),
            journal.CompareToRevision is null ? null : new RevisionId(journal.CompareToRevision));
    }

    /// <summary>ZIP の中身。データファイル（data/ を除いた相対パス）、差分レポート、manifest の順で、固定入力から毎回同じ内容になる。</summary>
    private IReadOnlyList<(string Path, byte[] Bytes)> Entries(ExportJournal journal)
    {
        IReadOnlyDictionary<string, byte[]> files = journal.Kind == ReleaseKind.Release
            ? _repository.ReadFiles(journal.Target!)
            : ReadInputs(journal);
        WorkspaceContent content = Parse(files, journal.Kind == ReleaseKind.Release ? $"revision {journal.Target}" : "fixed input");
        if (journal.Kind == ReleaseKind.Test && WorkspaceFingerprints.Of(content).Value != journal.WorkingFingerprint)
            throw new WorkspaceRecoveryRequiredException($"The fixed input of {journal.Name} is damaged.");

        string? head = journal.Kind == ReleaseKind.Release ? journal.Target : journal.BaseHead;
        (IReadOnlyList<ContentChange> changes, IReadOnlyList<RevisionInfo> commits) = Differences(content, head, journal.CompareToRevision);
        IReadOnlyList<ContentChange> uncommitted = journal.Kind == ReleaseKind.Test
            ? WorkspaceDiff.Compare(head is null ? WorkspaceDiff.Empty : Parse(_repository.ReadFiles(head), $"HEAD {head}"), content)
            : [];

        List<(string Path, byte[] Bytes)> entries = files
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => (f.Key[DataPrefix.Length..], f.Value))
            .ToList();
        entries.Add((ReportFileName, [0xEF, 0xBB, 0xBF, .. Utf8.GetBytes(ChangeReport.Write(journal, changes, commits, uncommitted))]));

        // manifest は自身を除く内容ファイルのハッシュを持つ（ZIP 全体のハッシュは操作記録へ）
        JObject manifest = new()
        {
            ["formatVersion"] = 1,
            ["kind"] = journal.Kind == ReleaseKind.Release ? "release" : "test",
            ["name"] = journal.Name,
            ["operationId"] = journal.OperationId.ToString("D"),
            ["createdAt"] = journal.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["timeZone"] = journal.TimeZone,
            ["appVersion"] = journal.AppVersion,
            ["targetCommit"] = journal.Target,
            ["baseHead"] = journal.BaseHead,
            ["workingFingerprint"] = journal.WorkingFingerprint,
            ["uncommittedChanges"] = journal.UncommittedChanges,
            ["compareTo"] = journal.CompareToRevision is null
                ? JValue.CreateNull()
                : new JObject { ["name"] = journal.CompareToName, ["commit"] = journal.CompareToRevision },
            ["files"] = new JArray(entries.Select(e => new JObject
            {
                ["path"] = e.Path,
                ["sha256"] = WorkspaceFileSet.Hash(e.Bytes),
            })),
        };
        entries.Add((ManifestFileName, Utf8.GetBytes(manifest.ToString(Formatting.Indented))));
        return entries;
    }

    private IReadOnlyDictionary<string, byte[]> ReadInputs(ExportJournal journal)
    {
        string root = journal.InputDirectory(Layout);
        if (!Directory.Exists(root))
            throw new WorkspaceRecoveryRequiredException($"The fixed input of {journal.Name} is missing.");
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (WorkspaceLayout.IsDataFile(relative)) files[relative] = File.ReadAllBytes(path);
        }
        return files;
    }

    private static void WriteZip(string path, IReadOnlyList<(string Path, byte[] Bytes)> entries, DateTimeOffset createdAt)
    {
        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string name, byte[] bytes) in entries)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = createdAt;
                    using Stream content = entry.Open();
                    content.Write(bytes);
                }
            }
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>書いた ZIP を読み直し、ファイル集合と各内容が固定入力から作ったものと一致することを確かめる。</summary>
    private static void Verify(string path, IReadOnlyList<(string Path, byte[] Bytes)> expected)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        string[] names = zip.Entries.Select(e => e.FullName).ToArray();
        if (!names.SequenceEqual(expected.Select(e => e.Path)))
            throw new IOException($"{Path.GetFileName(path)} does not contain the expected files.");
        foreach ((string name, byte[] bytes) in expected)
        {
            using Stream stream = zip.GetEntry(name)!.Open();
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            if (!copy.ToArray().AsSpan().SequenceEqual(bytes))
                throw new IOException($"{name} in {Path.GetFileName(path)} did not read back as written.");
        }
    }

    /// <summary>対象 SHA へ注釈付きタグを付ける。同名のタグが同じ SHA を指していれば何もしない。別の SHA なら止める。</summary>
    private void EnsureTag(ExportJournal journal)
    {
        string? existing = TagTarget(journal.Name);
        if (existing == journal.Target) return;
        if (existing is not null)
            throw new WorkspaceConflictException($"The tag {journal.Name} already points to another version.");
        _repository.WithRepository(repo =>
        {
            Signature tagger = new(Environment.UserName, $"{Environment.UserName}@local", DateTimeOffset.Now);
            repo.ApplyTag(journal.Name, journal.Target!, tagger,
                $"Release {journal.Name}\n\n{GitDataRepository.OperationTrailer}: {journal.OperationId:D}\n");
            return 0;
        });
    }

    /// <summary>タグが指すコミット（注釈付きタグは参照先をたどる）。タグがなければ null。</summary>
    private string? TagTarget(string name) =>
        _repository.State is DataRepositoryState.NotInitialized
            ? null
            : _repository.WithRepository(repo => repo.Tags[name] is { } tag ? (tag.PeeledTarget as LibGit2Sharp.Commit)?.Sha ?? "" : null);

    private static WorkspaceContent Parse(IReadOnlyDictionary<string, byte[]> files, string? source) =>
        JsonWorkspaceStore.Parse(WorkspaceFileSet.From(files), source).Content;

    private void RequireLock()
    {
        if (_lock is null)
            throw new InvalidOperationException("The workspace is open read-only because another application is editing it.");
    }
}
