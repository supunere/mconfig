using LibGit2Sharp;
using Mconfig.Application.Ports;
using Serilog;

namespace Mconfig.Infrastructure.History;

/// <summary>コミット記録と HEAD の照合。確定保存の失敗時と起動時に使う。</summary>
internal static class CommitRecovery
{
    /// <summary>
    /// 記録した操作のコミットが HEAD そのものかを、操作ID・期待親・tree で照合する。
    /// 内容が同じというだけで別の操作のコミットを完了扱いにしない。
    /// </summary>
    public static CommitResolution Resolve(GitDataRepository repository, CommitJournal journal)
    {
        string? head;
        try
        {
            head = repository.ReadHead();
        }
        catch (WorkspaceUnreadableException)
        {
            return CommitResolution.Unknown;
        }
        if (head == journal.ExpectedParent) return CommitResolution.NotCommitted;
        if (head is null) return CommitResolution.Unknown;

        return repository.WithRepository(repo =>
        {
            Commit commit = GitDataRepository.Lookup(repo, head);
            if (GitDataRepository.OperationOf(commit) != journal.OperationId)
                return CommitResolution.Unknown;
            bool sameParent = commit.Parents.FirstOrDefault()?.Sha == journal.ExpectedParent;
            return sameParent && commit.Tree.Sha == journal.Tree ? CommitResolution.Completed : CommitResolution.Unknown;
        });
    }

    /// <summary>起動時に、完了していないコミット記録を照合する。判定できない記録の理由を返す。</summary>
    public static IReadOnlyList<string> RecoverPending(GitDataRepository repository)
    {
        List<string> problems = [];
        foreach ((CommitJournal? journal, string? problem) in CommitJournal.Pending(repository.Layout))
        {
            if (journal is null)
            {
                problems.Add(problem!);
                continue;
            }
            CommitResolution resolution = Resolve(repository, journal);
            Log.Warning("Commit {OperationId} recovered: {Resolution}", journal.OperationId, resolution);
            if (resolution == CommitResolution.Completed) journal.With(CommitState.Completed, repository.Layout);
            else if (resolution == CommitResolution.NotCommitted) journal.With(CommitState.NotCommitted, repository.Layout);
            else problems.Add($"commit {journal.OperationId}: the result could not be confirmed");
        }
        return problems;
    }

    /// <summary>完了していないコミット記録があるか（読取専用のアプリはこれで読込を止める）。</summary>
    public static bool HasPending(Persistence.WorkspaceLayout layout) => CommitJournal.Pending(layout).Any();
}
