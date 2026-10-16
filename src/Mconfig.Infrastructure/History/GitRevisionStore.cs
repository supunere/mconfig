using LibGit2Sharp;
using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Infrastructure.Persistence;
using Serilog;

namespace Mconfig.Infrastructure.History;

/// <summary>コミットの故障注入の位置（試験用）。</summary>
internal enum CommitFaultPoint { Prepared, CommitCreated, BranchUpdated, IndexUpdated }

/// <summary>コミット記録の照合結果。</summary>
internal enum CommitResolution { Completed, NotCommitted, Unknown }

/// <summary>
/// 履歴と確定保存。コミットは既知のデータファイルだけから tree を作り、
/// 外部で stage された内容や対象外のファイルを含めない。初期化時にタグを作らず、既存の履歴とタグを書き換えない。
/// </summary>
public sealed class GitRevisionStore : IRevisionStore
{
    private readonly GitDataRepository _repository;
    private readonly WorkspaceLock? _lock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="editLock">確定保存に必要な編集の排他。null なら履歴の参照だけを行う。</param>
    public GitRevisionStore(GitDataRepository repository, WorkspaceLock? editLock)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _lock = editLock;
    }

    /// <summary>試験用の故障注入。</summary>
    internal Action<CommitFaultPoint>? Faults { get; init; }

    private WorkspaceLayout Layout => _repository.Layout;

    public async Task<CommitOutcome> CommitAsync(CommitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_lock is null)
            throw new InvalidOperationException("The workspace is open read-only because another application is editing it.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Commit(request), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<RevisionInfo>> GetHistoryAsync(int maxCount, RevisionId? startAfter, CancellationToken cancellationToken) =>
        Task.Run(() => _repository.ReadHistory(maxCount, startAfter?.Value), cancellationToken);

    public Task<RevisionInfo> GetRevisionAsync(RevisionId revision, CancellationToken cancellationToken) =>
        Task.Run(() => _repository.ReadRevision(revision.Value), cancellationToken);

    public Task<WorkspaceContent> ReadAsync(RevisionId revision, CancellationToken cancellationToken) =>
        Task.Run(() => JsonWorkspaceStore.Parse(WorkspaceFileSet.From(_repository.ReadFiles(revision.Value)), revision.Value).Content,
            cancellationToken);

    private CommitOutcome Commit(CommitRequest request)
    {
        DataRepositoryState state = _repository.State;
        if (state == DataRepositoryState.Damaged) _repository.ReadHead(); // 診断付きの例外にする
        if (state == DataRepositoryState.Detached)
            throw new WorkspaceConflictException("The data repository HEAD is not on a branch.");

        // コミットする W が一時保存したファイルそのもので、HEAD が期待親のままであることを確かめる
        IReadOnlyList<string> missing = Layout.DiagnoseMissingData();
        if (missing.Count > 0) throw new WorkspaceUnreadableException("The workspace data is incomplete.", missing);
        WorkspaceFileSet files = WorkspaceFileSet.Read(Layout);
        string? head = _repository.ReadHead();
        if (files.Stamp(head) != request.ExpectedStamp || head != request.ExpectedParent?.Value)
            throw new WorkspaceConflictException("The workspace files or HEAD changed before the commit.");
        ContentFingerprint fingerprint = WorkspaceFingerprints.Of(JsonWorkspaceStore.Parse(files).Content);

        // 初回だけリポジトリを作る（既存のリポジトリを初期化し直さない。タグは作らない）
        if (state == DataRepositoryState.NotInitialized)
        {
            Repository.Init(Layout.DataDirectory);
            Log.Information("Initialized the data repository");
        }

        CommitJournal? journal = null;
        try
        {
            return _repository.WithRepository(repo =>
            {
                Commit? parent = head is null ? null : GitDataRepository.Lookup(repo, head);
                Tree tree = BuildTree(repo, files);
                if (parent is not null && parent.Tree.Id == tree.Id)
                    throw new InvalidOperationException("The files are identical to the committed version; no commit is created.");

                journal = new CommitJournal(request.OperationId, head, tree.Sha, fingerprint.Value, CommitState.Prepared).Save(Layout);
                Faults?.Invoke(CommitFaultPoint.Prepared);

                Signature author = Author(request.Author);
                string message = $"{request.Reason.Trim()}\n\n{GitDataRepository.OperationTrailer}: {request.OperationId:D}\n";
                Commit commit = repo.ObjectDatabase.CreateCommit(author, author, message, tree,
                    parent is null ? [] : [parent], prettifyMessage: false);
                Faults?.Invoke(CommitFaultPoint.CommitCreated);

                MoveBranch(repo, head, commit, request.Reason);
                Faults?.Invoke(CommitFaultPoint.BranchUpdated);
                UpdateIndex(repo, tree);
                Faults?.Invoke(CommitFaultPoint.IndexUpdated);

                return Complete(journal, files, commit.Sha);
            });
        }
        catch (Exception ex) when (journal is not null)
        {
            // 応答を失っても、同じ操作のコミットが HEAD にあれば完了とし、作り直さない
            switch (CommitRecovery.Resolve(_repository, journal))
            {
                case CommitResolution.Completed:
                    Log.Warning(ex, "Commit {OperationId} completed although a later step failed", request.OperationId);
                    return Complete(journal, files, _repository.ReadHead()!);
                case CommitResolution.NotCommitted:
                    journal.With(CommitState.NotCommitted, Layout);
                    throw;
                default:
                    throw new WorkspaceRecoveryRequiredException($"The commit result could not be confirmed: {ex.Message}");
            }
        }
    }

    private CommitOutcome Complete(CommitJournal journal, WorkspaceFileSet files, string revision)
    {
        string? head = _repository.ReadHead();
        if (head != revision)
            throw new WorkspaceRecoveryRequiredException("HEAD moved after this operation's commit was created.");
        bool sameCommit = _repository.WithRepository(repo =>
        {
            Commit commit = GitDataRepository.Lookup(repo, revision);
            return commit.Tree.Sha == journal.Tree &&
                   GitDataRepository.OperationOf(commit) == journal.OperationId &&
                   commit.Parents.FirstOrDefault()?.Sha == journal.ExpectedParent;
        });
        if (!sameCommit)
            throw new WorkspaceRecoveryRequiredException("HEAD does not match the commit prepared by this operation.");
        journal.With(CommitState.Completed, Layout);
        return new CommitOutcome(new RevisionId(revision), files.Stamp(revision));
    }

    internal static Tree BuildTree(Repository repo, WorkspaceFileSet files)
    {
        TreeDefinition definition = new();
        foreach ((string path, byte[] bytes) in files.Files)
        {
            using MemoryStream content = new(bytes);
            Blob blob = repo.ObjectDatabase.CreateBlob(content);
            definition.Add(path[(WorkspaceLayout.DataDirectoryName.Length + 1)..], blob, Mode.NonExecutableFile);
        }
        return repo.ObjectDatabase.CreateTree(definition);
    }

    internal static void MoveBranch(Repository repo, string? expectedParent, Commit commit, string reason)
    {
        // ブランチ更新の直前にも HEAD を確かめる（期待親と異なれば作ったコミットは使わない）
        string? current = repo.Info.IsHeadUnborn ? null : repo.Head.Tip.Sha;
        if (current != expectedParent)
            throw new WorkspaceConflictException("HEAD moved while the commit was being created.");

        string branch = repo.Refs.Head is SymbolicReference symbolic
            ? symbolic.TargetIdentifier
            : throw new WorkspaceConflictException("The data repository HEAD is not on a branch.");
        string log = $"commit: {reason.Split('\n')[0].Trim()}";
        if (repo.Refs[branch] is { } existing)
            repo.Refs.UpdateTarget(existing, commit.Id, log);
        else
            repo.Refs.Add(branch, commit.Id, log);
    }

    /// <summary>既知ファイルの index だけをコミットに合わせる（外部で stage された他の内容はそのまま残す）。</summary>
    internal static void UpdateIndex(Repository repo, Tree tree)
    {
        HashSet<string> committed = [];
        foreach ((string path, TreeEntry entry) in Walk(tree, ""))
        {
            repo.Index.Add((Blob)entry.Target, path, entry.Mode);
            committed.Add(path);
        }
        foreach (IndexEntry entry in repo.Index.ToArray())
        {
            string path = entry.Path.Replace('\\', '/');
            if (!committed.Contains(path) && WorkspaceLayout.IsDataFile(WorkspaceLayout.DataDirectoryName + "/" + path))
                repo.Index.Remove(entry.Path);
        }
        repo.Index.Write();

        static IEnumerable<(string Path, TreeEntry Entry)> Walk(Tree tree, string prefix)
        {
            foreach (TreeEntry entry in tree)
            {
                if (entry.TargetType == TreeEntryTargetType.Tree)
                {
                    foreach ((string Path, TreeEntry Entry) child in Walk((Tree)entry.Target, prefix + entry.Name + "/")) yield return child;
                }
                else
                {
                    yield return (prefix + entry.Name, entry);
                }
            }
        }
    }

    /// <summary>作者。"名前 &lt;メール&gt;" 形式ならそのまま、名前だけなら name@local を補う。</summary>
    internal static Signature Author(string author)
    {
        string text = author?.Trim() ?? "";
        if (text.Length == 0) throw new ArgumentException("The author is required.", nameof(author));
        int open = text.LastIndexOf('<');
        if (open > 0 && text.EndsWith('>'))
            return new Signature(text[..open].Trim(), text[(open + 1)..^1].Trim(), DateTimeOffset.Now);
        return new Signature(text, $"{text}@local", DateTimeOffset.Now);
    }
}
