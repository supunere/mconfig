using LibGit2Sharp;
using Mconfig.Application.Ports;
using Mconfig.Infrastructure.Persistence;

namespace Mconfig.Infrastructure.History;

/// <summary>データ専用リポジトリの状態。初回・初期化途中・破損を区別して診断する。</summary>
public enum DataRepositoryState
{
    /// <summary>data/.git がない（初回）。初期コミットで作る。</summary>
    NotInitialized,
    /// <summary>リポジトリはあるがコミットがない（初期化の途中で止まった場合を含む）。</summary>
    Empty,
    Ready,
    /// <summary>HEAD がブランチを指していない。読めるが確定保存しない。</summary>
    Detached,
    /// <summary>data/.git はあるが開けない。初期化し直さない。</summary>
    Damaged,
}

/// <summary>
/// data/ の専用 Git リポジトリ。対象は設定・仕様・スキーマの既知ファイルだけで、
/// 版の読込は作業ツリーと独立に行う（checkout しない）。Repository は操作ごとに開き、並行処理間で共有しない。
/// </summary>
public sealed class GitDataRepository
{
    /// <summary>コミットメッセージに記録する操作ID（応答喪失後の重複コミット防止に使う）。</summary>
    public const string OperationTrailer = "Mconfig-Operation";

    private const string DataPrefix = WorkspaceLayout.DataDirectoryName + "/";

    public GitDataRepository(WorkspaceLayout layout) => Layout = layout ?? throw new ArgumentNullException(nameof(layout));

    public WorkspaceLayout Layout { get; }

    public DataRepositoryState State
    {
        get
        {
            string git = Path.Combine(Layout.DataDirectory, ".git");
            if (!Directory.Exists(git) && !File.Exists(git)) return DataRepositoryState.NotInitialized;
            try
            {
                if (!Repository.IsValid(Layout.DataDirectory)) return DataRepositoryState.Damaged;
                using Repository repo = new(Layout.DataDirectory);
                if (repo.Info.IsHeadUnborn) return DataRepositoryState.Empty;
                return repo.Info.IsHeadDetached ? DataRepositoryState.Detached : DataRepositoryState.Ready;
            }
            catch (LibGit2SharpException)
            {
                return DataRepositoryState.Damaged;
            }
        }
    }

    /// <summary>HEAD のコミット。初回・コミットなしなら null。破損していれば <see cref="WorkspaceUnreadableException"/>。</summary>
    public string? ReadHead() => State switch
    {
        DataRepositoryState.NotInitialized or DataRepositoryState.Empty => null,
        DataRepositoryState.Damaged => throw Damaged(),
        _ => WithRepository(repo => repo.Head.Tip.Sha),
    };

    /// <summary>コミットの既知データファイル（ワークスペース相対パス → バイト列）。作業ツリーには触れない。</summary>
    public IReadOnlyDictionary<string, byte[]> ReadFiles(string revision) => WithRepository(repo =>
    {
        Commit commit = Lookup(repo, revision);
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach ((string treePath, TreeEntry entry) in Entries(commit.Tree, ""))
        {
            string path = DataPrefix + treePath;
            if (entry.TargetType != TreeEntryTargetType.Blob || !WorkspaceLayout.IsDataFile(path)) continue;
            using Stream content = ((Blob)entry.Target).GetContentStream();
            using MemoryStream copy = new();
            content.CopyTo(copy);
            files[path] = copy.ToArray();
        }
        return (IReadOnlyDictionary<string, byte[]>)files;
    });

    public RevisionInfo ReadRevision(string revision) => WithRepository(repo => ToInfo(Lookup(repo, revision)));

    public IReadOnlyList<RevisionInfo> ReadHistory(int maxCount, string? startAfter)
    {
        if (maxCount <= 0 || ReadHead() is null) return [];
        return WithRepository(repo =>
        {
            IEnumerable<Commit> commits = repo.Commits.QueryBy(new CommitFilter
            {
                IncludeReachableFrom = repo.Head.Tip,
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            });
            if (startAfter is not null)
            {
                Commit after = Lookup(repo, startAfter);
                commits = commits.SkipWhile(c => c.Id != after.Id).Skip(1);
            }
            return (IReadOnlyList<RevisionInfo>)commits.Take(maxCount).Select(ToInfo).ToArray();
        });
    }

    /// <summary>from から辿れるコミットのうち、stopAt（とその祖先）を除いたもの。新しい順。</summary>
    public IReadOnlyList<RevisionInfo> ReadRange(string from, string? stopAt) => WithRepository(repo =>
    {
        CommitFilter filter = new()
        {
            IncludeReachableFrom = Lookup(repo, from),
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
        };
        if (stopAt is not null) filter.ExcludeReachableFrom = Lookup(repo, stopAt);
        return (IReadOnlyList<RevisionInfo>)repo.Commits.QueryBy(filter).Select(ToInfo).ToArray();
    });

    /// <summary>from から辿れるコミットの、履歴上の近さ（from 自身が 0）。</summary>
    public IReadOnlyDictionary<string, int> AncestorDistances(string from) => WithRepository(repo =>
    {
        Dictionary<string, int> distances = new(StringComparer.Ordinal);
        foreach (Commit commit in repo.Commits.QueryBy(new CommitFilter
                 {
                     IncludeReachableFrom = Lookup(repo, from),
                     SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
                 }))
            distances[commit.Sha] = distances.Count;
        return (IReadOnlyDictionary<string, int>)distances;
    });

    internal T WithRepository<T>(Func<Repository, T> action)
    {
        try
        {
            using Repository repo = new(Layout.DataDirectory);
            return action(repo);
        }
        catch (RepositoryNotFoundException)
        {
            throw Damaged();
        }
    }

    internal static Commit Lookup(Repository repo, string revision) =>
        (ObjectId.TryParse(revision, out ObjectId? id) ? repo.Lookup<Commit>(id) : null)
        ?? throw new WorkspaceUnreadableException($"The revision {revision} does not exist.", [$"revision {revision}: missing"]);

    /// <summary>コミットメッセージの操作ID。なければ null。</summary>
    internal static Guid? OperationOf(Commit commit)
    {
        string prefix = OperationTrailer + ": ";
        string? line = commit.Message.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        return line is not null && Guid.TryParse(line[prefix.Length..], out Guid id) ? id : null;
    }

    /// <summary>変更理由（操作IDのトレーラーを除いた本文）。</summary>
    internal static string ReasonOf(Commit commit)
    {
        string prefix = OperationTrailer + ": ";
        return string.Join('\n', commit.Message.Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => !l.StartsWith(prefix, StringComparison.Ordinal))).Trim();
    }

    private static RevisionInfo ToInfo(Commit commit) => new(
        new RevisionId(commit.Sha),
        commit.Parents.FirstOrDefault() is { } parent ? new RevisionId(parent.Sha) : null,
        ReasonOf(commit),
        commit.Author.Email is { Length: > 0 } email ? $"{commit.Author.Name} <{email}>" : commit.Author.Name,
        commit.Author.When);

    private static IEnumerable<(string Path, TreeEntry Entry)> Entries(Tree tree, string prefix)
    {
        foreach (TreeEntry entry in tree)
        {
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                foreach ((string Path, TreeEntry Entry) child in Entries((Tree)entry.Target, prefix + entry.Name + "/")) yield return child;
            }
            else
            {
                yield return (prefix + entry.Name, entry);
            }
        }
    }

    private WorkspaceUnreadableException Damaged() =>
        new("The data repository cannot be opened.", [$"{WorkspaceLayout.DataDirectoryName}/.git: damaged or unreadable"]);
}
