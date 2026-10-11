using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Persistence.Recovery;

namespace Mconfig.Infrastructure.Persistence;

/// <summary>復元候補の出どころ。</summary>
public enum BackupSource
{
    /// <summary>
    /// 復旧素材（recovery/{操作ID}/old/）に退避された、書き換える前の設定ファイル。設定ファイルだけを戻し、
    /// 現在の仕様・スキーマと組み合わせて使う。
    /// </summary>
    File,
    /// <summary>データ専用リポジトリの過去コミット。設定・仕様・スキーマを一式で戻す。</summary>
    Commit,
}

/// <summary>
/// 復元候補。Location はファイルならワークスペース相対パス、コミットなら SHA。
/// ContentHash はファイルなら設定ファイルの SHA-256、コミットならデータファイル一式のハッシュ。
/// Description はコミットの変更理由（ファイルには null）。
/// </summary>
public sealed record BackupCandidate(BackupSource Source, string Location, DateTimeOffset SavedAt, string ContentHash, string? Description = null);

/// <summary>
/// 読めなくなったワークスペースのバックアップ・過去コミットからの復旧。
/// 候補は、復元した後に使う定義と組み合わせて読込み、起動時と同じ構造・参照の検証（<see cref="StartupValidation"/>）を通り、
/// 壊れたカテゴリもないものだけにする（復元した直後に読取専用や編集不可にならない）。
/// 復元時は書き換える現ファイルを復旧素材へ退避し、削除しない。復元後の内容は W となり、確定版とは別の未コミットの状態になる。
/// </summary>
public sealed class WorkspaceBackups
{
    /// <summary>候補として調べる過去コミットの数（HEAD から新しい順）。</summary>
    public const int MaxCommitCandidates = 20;

    private readonly WorkspaceLayout _layout;
    // 復元は書込なので、編集の排他を持つアプリだけが行える
    private readonly WorkspaceLock _lock;
    private readonly GitDataRepository? _repository;

    /// <param name="repository">渡すと、過去コミットも候補にする。</param>
    public WorkspaceBackups(WorkspaceLayout layout, WorkspaceLock editLock, GitDataRepository? repository = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _lock = editLock ?? throw new ArgumentNullException(nameof(editLock));
        _repository = repository;
    }

    /// <summary>検証を通った候補を新しい順に返す。現在のファイルと同じ内容や、同じ内容の重複は除く。</summary>
    public IReadOnlyList<BackupCandidate> FindCandidates()
    {
        WorkspaceFileSet current = WorkspaceFileSet.Read(_layout);
        string? currentSettings = current.Files.TryGetValue(WorkspaceLayout.SettingsPath, out byte[]? settings)
            ? WorkspaceFileSet.Hash(settings)
            : null;
        string currentSet = SetHash(current);
        List<BackupCandidate> found = [];

        IEnumerable<string> paths = Directory.Exists(_layout.RecoveryDirectory)
            ? Directory.EnumerateDirectories(_layout.RecoveryDirectory)
                .Select(d => $"{WorkspaceLayout.RecoveryDirectoryName}/{Path.GetFileName(d)}/old/{WorkspaceLayout.SettingsPath}")
            : [];
        foreach (string path in paths)
        {
            string full = _layout.FullPath(path);
            if (!File.Exists(full)) continue;
            byte[] bytes = WorkspaceFileSet.ReadBytes(full);
            string hash = WorkspaceFileSet.Hash(bytes);
            if (hash != currentSettings && IsUsable(WithSettings(current, bytes)))
                found.Add(new BackupCandidate(BackupSource.File, path, File.GetLastWriteTimeUtc(full), hash));
        }
        foreach (RevisionInfo revision in History())
        {
            if (ReadCommitted(revision.Id.Value) is not { } committed) continue;
            string hash = SetHash(committed);
            if (hash != currentSet && IsUsable(committed))
                found.Add(new BackupCandidate(BackupSource.Commit, revision.Id.Value, revision.When, hash, revision.Message));
        }

        return found.OrderByDescending(c => c.SavedAt).DistinctBy(c => (c.Source, c.ContentHash)).ToArray();
    }

    /// <summary>
    /// 候補で置き換える。ファイルの候補は設定ファイルだけを、コミットの候補は設定・仕様・スキーマの一式（追加・削除を含む）を、
    /// 記録付きで書き換える。書き換える現ファイルは recovery/{操作ID}/old/ へ退避し、削除しない。
    /// 候補が一覧の時点から変わっていれば置き換えない。
    /// </summary>
    public void Restore(BackupCandidate candidate, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!FindCandidates().Contains(candidate))
            throw new WorkspaceConflictException("The backup is no longer available as it was listed.");

        WorkspaceFileSet current = WorkspaceFileSet.Read(_layout);
        WorkspaceFileSet target = candidate.Source == BackupSource.Commit
            ? ReadCommitted(candidate.Location) ?? throw new WorkspaceConflictException("The revision can no longer be read.")
            : WithSettings(current, WorkspaceFileSet.ReadBytes(_layout.FullPath(candidate.Location)));
        FileChange[] changes = current.Files.Keys.Union(target.Files.Keys).Order(StringComparer.Ordinal)
            .Select(path => (path, now: current.Files.GetValueOrDefault(path), next: target.Files.GetValueOrDefault(path)))
            .Select(f => new FileChange(f.path, f.now is null ? null : WorkspaceFileSet.Hash(f.now), f.next))
            .ToArray();

        FileSetTransaction transaction = new(_layout);
        try
        {
            transaction.Apply(operationId, "RestoreBackup", changes, null, null, CancellationToken.None,
                candidate.Source == BackupSource.Commit ? candidate.Location : null);
        }
        catch (Exception)
        {
            OperationJournal? journal = OperationJournal.Load(OperationJournal.DirectoryOf(_layout, operationId), _layout);
            if (journal is not null && new WorkspaceRecovery(_layout).Recover(journal).Outcome == RecoveryOutcome.Blocked)
                throw new WorkspaceRecoveryRequiredException("Restoring the backup stopped midway and could not be undone.");
            throw;
        }
    }

    /// <summary>HEAD から新しい順の過去コミット。リポジトリがない・壊れている場合は候補にしない。</summary>
    private IReadOnlyList<RevisionInfo> History()
    {
        if (_repository is null) return [];
        try
        {
            return _repository.ReadHistory(MaxCommitCandidates, null);
        }
        catch (Exception ex) when (ex is WorkspaceUnreadableException or LibGit2Sharp.LibGit2SharpException)
        {
            return [];
        }
    }

    /// <summary>コミットのデータファイル一式。読めなければ null。</summary>
    private WorkspaceFileSet? ReadCommitted(string revision)
    {
        if (_repository is null) return null;
        try
        {
            return WorkspaceFileSet.From(_repository.ReadFiles(revision));
        }
        catch (Exception ex) when (ex is WorkspaceUnreadableException or LibGit2Sharp.LibGit2SharpException)
        {
            return null;
        }
    }

    private static WorkspaceFileSet WithSettings(WorkspaceFileSet files, byte[] settings) =>
        WorkspaceFileSet.From(new Dictionary<string, byte[]>(files.Files, StringComparer.Ordinal)
        {
            [WorkspaceLayout.SettingsPath] = settings,
        });

    /// <summary>ファイル一式の内容のハッシュ（HEAD を含まない）。</summary>
    private static string SetHash(WorkspaceFileSet files) => files.Stamp(null).Value;

    /// <summary>
    /// 一式として読み込めて、どのカテゴリも壊れておらず、起動時の構造・参照の検証を通るか。
    /// 通らない候補を選ぶと、復元した直後に読取専用や編集不可になって作業を再開できない。
    /// </summary>
    private static bool IsUsable(WorkspaceFileSet files)
    {
        try
        {
            WorkspaceContent content = JsonWorkspaceStore.Parse(files).Content;
            return content.Settings.Categories.Values.All(c => !c.IsDiagnosticOnly) && !StartupValidation.HasErrors(content);
        }
        catch (WorkspaceUnreadableException)
        {
            return false;
        }
    }
}
