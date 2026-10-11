using System.Runtime.ExceptionServices;
using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Definitions;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Persistence.Json;
using Mconfig.Infrastructure.Persistence.Recovery;
using Newtonsoft.Json.Linq;

namespace Mconfig.Infrastructure.Persistence;

/// <summary>
/// JSON ファイルのワークスペース。編集の排他（<see cref="WorkspaceLock"/>）を持つ場合だけ書込と復旧を行い、
/// 持たない場合は読取専用で開く。書込はこのインスタンス内でも一つずつ行う。
/// データ専用リポジトリを渡すと、HEAD をスタンプに含め、確定版 C を HEAD から読み、版の復元を行う。
/// </summary>
public sealed class JsonWorkspaceStore : IWorkspaceStore
{
    private readonly WorkspaceLayout _layout;
    private readonly WorkspaceLock? _lock;
    private readonly GitDataRepository? _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonWorkspaceStore(WorkspaceLayout layout, WorkspaceLock? editLock, GitDataRepository? repository = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _lock = editLock;
        _repository = repository;
    }

    /// <summary>試験用の故障注入。</summary>
    internal Action<FaultPoint, string?>? Faults { get; init; }

    /// <summary>一時ファイルの実書込み・Flush失敗を再現する試験用境界。通常は耐久FlushするFileStreamを使う。</summary>
    internal Func<string, Stream>? OpenTemporaryFile { get; init; }

    public bool IsReadOnly => _lock is null;

    /// <summary>
    /// 読み込む。書込可能なら未完了の操作を先に復旧し、判定できなければ読取専用で止める
    /// （<see cref="WorkspaceRecoveryRequiredException"/>）。読取専用のアプリは未完了の操作があれば読まない。
    /// </summary>
    public async Task<LoadedWorkspace> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                string? generation = IsReadOnly ? ReadOperationGeneration() : null;
                RecoverOrStop();
                WorkspaceFileSet files = ReadFiles();
                Faults?.Invoke(FaultPoint.SnapshotRead, null);
                // 読取専用プロセスは編集ロックを共有できないため、前後の操作記録とファイル集合を照合し、
                // 書換えと重なったスナップショットを通常表示しない。
                if (IsReadOnly)
                {
                    RecoverOrStop();
                    WorkspaceFileSet verification = ReadFiles();
                    RecoverOrStop();
                    if (!SameFiles(files, verification) || generation != ReadOperationGeneration())
                        throw new WorkspaceRecoveryRequiredException(
                            "The workspace changed while a read-only snapshot was being loaded.");
                }
                ParsedWorkspace parsed = Parse(files);
                string? head = _repository?.ReadHead();
                WorkspaceContent? committed = head is null
                    ? null
                    : Parse(WorkspaceFileSet.From(_repository!.ReadFiles(head)), $"HEAD {head}").Content;
                LoadedWorkspace loaded = new(parsed.Content, committed, files.Stamp(head),
                    head is null ? null : new RevisionId(head), IsReadOnly, RestoredFrom(files, parsed.Content, committed));
                // 完了済みの復旧用コピーは直近の分だけ残す。未完了の操作は上で復旧済みか、読取専用で止まっている
                if (!IsReadOnly) RecoveryRetention.Prune(_layout);
                return loaded;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 一時保存。期待スタンプとファイル集合・生バイト・HEAD を照合し、外部変更があれば
    /// 上書きしない。失敗したら記録から旧版へ戻し、W は変わらない。旧版・新版を判定できなければ復旧待ちにする。
    /// </summary>
    public async Task<WorkspaceStamp> SaveAsync(
        WorkspaceContent content, WorkspaceStamp expectedStamp, Guid operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(expectedStamp);
        if (IsReadOnly)
            throw new InvalidOperationException("The workspace is open read-only because another application is editing it.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Save(content, expectedStamp, operationId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private WorkspaceStamp Save(WorkspaceContent content, WorkspaceStamp expectedStamp, Guid operationId, CancellationToken cancellationToken)
    {
        RecoverOrStop();
        WorkspaceFileSet files = ReadFiles();
        string? head = _repository?.ReadHead();
        if (files.Stamp(head) != expectedStamp)
            throw new WorkspaceConflictException("The workspace files or HEAD changed after they were loaded.");

        ParsedWorkspace disk = Parse(files);
        ContentFingerprint fingerprint = WorkspaceFingerprints.Of(content);
        // 仕様・スキーマはアプリから書き換えない（版の復元では一式を復元する）
        if (WorkspaceFingerprints.Of(content with { Spec = disk.Content.Spec, Schemas = disk.Content.Schemas }) != fingerprint)
            throw new NotSupportedException("Saving changed spec or schema definitions is not supported.");

        byte[] current = files.Files[WorkspaceLayout.SettingsPath];
        byte[] next = JsonText.Serialize(SettingsJson.Write(content.Settings), JsonText.Decode(current).Format);
        // 書き出す前に読み直し、業務上の内容が失われていないことを確かめる
        WorkspaceContent reread = content with { Settings = SettingsJson.Read(JsonText.Parse(next), disk.SchemaNames) };
        if (WorkspaceFingerprints.Of(reread) != fingerprint)
            throw new InvalidOperationException("The settings could not be written without changing their content.");
        if (current.AsSpan().SequenceEqual(next)) return expectedStamp;

        cancellationToken.ThrowIfCancellationRequested();
        FileChange[] changes = files.Files.OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new FileChange(f.Key, WorkspaceFileSet.Hash(f.Value),
                f.Key == WorkspaceLayout.SettingsPath ? next : f.Value))
            .ToArray();
        Apply(operationId, "SaveDraft", changes,
            WorkspaceFingerprints.Of(disk.Content).Value, fingerprint.Value, null, cancellationToken);

        WorkspaceFileSet saved = ReadFiles();
        if (!saved.Files.TryGetValue(WorkspaceLayout.SettingsPath, out byte[]? written) || !written.AsSpan().SequenceEqual(next))
            throw new WorkspaceRecoveryRequiredException("The saved settings file does not contain the saved content.");
        return saved.Stamp(head);
    }

    /// <summary>
    /// 版の復元。対象コミットの既知ファイル一式を作業ツリーと独立に読み、
    /// 追加・削除を含めて記録付きで適用する。HEAD・タグ・index は動かさない。
    /// </summary>
    public async Task<RestoredWorkspace> RestoreAsync(
        RevisionId revision, WorkspaceStamp expectedStamp, Guid operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(expectedStamp);
        if (IsReadOnly)
            throw new InvalidOperationException("The workspace is open read-only because another application is editing it.");
        GitDataRepository repository = _repository
            ?? throw new InvalidOperationException("Restoring needs the data repository.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                RecoverOrStop();
                WorkspaceFileSet files = ReadFiles();
                string? head = repository.ReadHead();
                if (files.Stamp(head) != expectedStamp)
                    throw new WorkspaceConflictException("The workspace files or HEAD changed after they were loaded.");

                // 一式がそろわない・解釈できない版は、現在の定義と混ぜて補完せずに止める
                WorkspaceFileSet target = WorkspaceFileSet.From(repository.ReadFiles(revision.Value));
                ParsedWorkspace parsed = Parse(target, $"revision {revision.Value}");

                FileChange[] changes = files.Files.Keys.Union(target.Files.Keys).Order(StringComparer.Ordinal)
                    .Select(path => (path, current: files.Files.GetValueOrDefault(path), next: target.Files.GetValueOrDefault(path)))
                    .Select(f => new FileChange(f.path, f.current is null ? null : WorkspaceFileSet.Hash(f.current), f.next))
                    .ToArray();
                if (changes.Any(c => c.OldHash != (c.NewContent is null ? null : WorkspaceFileSet.Hash(c.NewContent))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Apply(operationId, "Restore", changes, WorkspaceFingerprints.Of(Parse(files).Content).Value,
                        WorkspaceFingerprints.Of(parsed.Content).Value, revision.Value, cancellationToken);
                }

                WorkspaceFileSet restored = ReadFiles();
                if (!SameFiles(restored, target))
                    throw new WorkspaceRecoveryRequiredException("The restored files do not match the revision.");
                return new RestoredWorkspace(parsed.Content, restored.Stamp(head));
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>記録付きで書き換える。失敗したら記録から判定し、旧版へ戻せたら元の例外を、判定できなければ復旧待ちを返す。</summary>
    private void Apply(Guid operationId, string kind, IReadOnlyList<FileChange> changes,
        string? oldFingerprint, string? newFingerprint, string? revision, CancellationToken cancellationToken)
    {
        try
        {
            new FileSetTransaction(_layout, Faults, OpenTemporaryFile).Apply(operationId, kind, changes, oldFingerprint, newFingerprint,
                cancellationToken, revision);
        }
        catch (Exception ex)
        {
            OperationJournal? journal = OperationJournal.Load(OperationJournal.DirectoryOf(_layout, operationId), _layout);
            RecoveryOutcome outcome = journal is null
                ? RecoveryOutcome.NotApplied
                : new WorkspaceRecovery(_layout).Recover(journal).Outcome;
            if (outcome == RecoveryOutcome.Blocked)
                throw new WorkspaceRecoveryRequiredException(
                    $"Writing stopped midway and the files could not be restored automatically: {ex.Message}");
            if (outcome != RecoveryOutcome.Completed)
                ExceptionDispatchInfo.Capture(ex).Throw();
        }
    }

    private static bool SameFiles(WorkspaceFileSet a, WorkspaceFileSet b) =>
        a.Files.Count == b.Files.Count &&
        a.Files.All(f => b.Files.TryGetValue(f.Key, out byte[]? other) && f.Value.AsSpan().SequenceEqual(other));

    /// <summary>
    /// 版の復元を適用したまま確定保存していない場合の復元元。最後に完了した書込が復元で、ファイルがその結果のままで、
    /// 確定版と内容が異なる場合に返す（起動時に利用者へ示すため）。
    /// </summary>
    private RevisionId? RestoredFrom(WorkspaceFileSet files, WorkspaceContent working, WorkspaceContent? committed)
    {
        if (committed is not null && WorkspaceFingerprints.Of(working) == WorkspaceFingerprints.Of(committed)) return null;
        OperationJournal? last = OperationJournal.LoadAll(_layout)
            .Where(j => j.State == OperationState.Completed)
            .MaxBy(j => j.CreatedAt);
        if (last is not { Kind: "Restore", Revision: not null }) return null;
        bool unchanged = last.Entries.All(e =>
            (files.Files.TryGetValue(e.Path, out byte[]? bytes) ? WorkspaceFileSet.Hash(bytes) : null) == e.NewHash);
        return unchanged ? new RevisionId(last.Revision) : null;
    }

    private void RecoverOrStop()
    {
        WorkspaceRecovery recovery = new(_layout, Faults);
        if (IsReadOnly)
        {
            if (recovery.HasPending() || CommitRecovery.HasPending(_layout))
                throw new WorkspaceRecoveryRequiredException(
                    "Another application is saving the workspace or a previous save did not finish.");
            return;
        }
        // ファイルの書換えを先に片付け、その後でコミット記録を HEAD と照合する
        List<string> problems = recovery.RecoverAll().Where(r => r.Outcome == RecoveryOutcome.Blocked).Select(r => r.Problem!).ToList();
        if (problems.Count == 0 && _repository is not null)
            problems.AddRange(CommitRecovery.RecoverPending(_repository));
        if (problems.Count > 0)
            throw new WorkspaceRecoveryRequiredException(
                "An unfinished operation could not be recovered: " + string.Join("; ", problems));
    }

    private string ReadOperationGeneration()
    {
        try
        {
            return OperationJournal.Generation(_layout);
        }
        catch (IOException ex)
        {
            throw new WorkspaceRecoveryRequiredException(
                $"A workspace operation changed while the read-only snapshot was being loaded: {ex.Message}");
        }
    }

    private WorkspaceFileSet ReadFiles()
    {
        IReadOnlyList<string> missing = _layout.DiagnoseMissingData();
        if (missing.Count > 0)
            throw new WorkspaceUnreadableException("The workspace data is incomplete.", missing);
        return WorkspaceFileSet.Read(_layout,
            IsReadOnly && Faults is not null ? path => Faults(FaultPoint.SnapshotFileRead, path) : null);
    }

    internal sealed record ParsedWorkspace(WorkspaceContent Content, IReadOnlySet<string> SchemaNames);

    /// <summary>ファイル一式を業務モデルにする。読めないファイルはまとめて報告し、既存データを空で補わない。</summary>
    internal static ParsedWorkspace Parse(WorkspaceFileSet files, string? source = null)
    {
        foreach (string required in new[] { WorkspaceLayout.SpecPath, WorkspaceLayout.SettingsPath })
        {
            if (!files.Files.ContainsKey(required))
                throw new WorkspaceUnreadableException("The workspace data is incomplete.",
                    [$"{(source is null ? "" : source + ": ")}{required}: missing"]);
        }
        List<string> problems = [];
        SpecDefinition? spec = Read(files, WorkspaceLayout.SpecPath, DefinitionJson.ReadSpec, problems);
        List<SchemaDefinition> schemas = [];
        foreach (string path in files.Files.Keys.Where(p => p.StartsWith(WorkspaceLayout.SchemasDirectory + "/", StringComparison.Ordinal)))
        {
            SchemaDefinition? schema = Read(files, path, DefinitionJson.ReadSchema, problems);
            if (schema is null) continue;
            if (schemas.Any(s => s.Name == schema.Name))
                problems.Add($"{path}: duplicate schema name '{schema.Name}'");
            else
                schemas.Add(schema);
        }
        HashSet<string> schemaNames = schemas.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        Domain.Configuration.SettingsData? settings =
            Read(files, WorkspaceLayout.SettingsPath, token => SettingsJson.Read(token, schemaNames), problems);

        if (problems.Count > 0 || spec is null || settings is null)
            throw new WorkspaceUnreadableException("The workspace data cannot be read.",
                source is null ? problems : problems.Select(p => $"{source}: {p}").ToArray());
        Dictionary<string, string> definitionSources = files.Files
            .Where(f => f.Key == WorkspaceLayout.SpecPath ||
                        f.Key.StartsWith(WorkspaceLayout.SchemasDirectory + "/", StringComparison.Ordinal))
            .ToDictionary(f => f.Key, f => JsonText.Canonical(JsonText.Parse(f.Value)), StringComparer.Ordinal);
        return new ParsedWorkspace(new WorkspaceContent(settings, spec, schemas)
        {
            DefinitionSources = definitionSources,
            DefinitionUnknownProperties = definitionSources.ToDictionary(f => f.Key,
                f => DefinitionJson.UnknownProperties(JsonText.Parse(f.Value), f.Key == WorkspaceLayout.SpecPath),
                StringComparer.Ordinal),
        }, schemaNames);
    }

    private static T? Read<T>(WorkspaceFileSet files, string path, Func<JToken, T> map, List<string> problems) where T : class
    {
        try
        {
            return map(JsonText.Parse(files.Files[path]));
        }
        catch (InvalidDataException ex)
        {
            problems.Add($"{path}: {ex.Message}");
            return null;
        }
    }
}
