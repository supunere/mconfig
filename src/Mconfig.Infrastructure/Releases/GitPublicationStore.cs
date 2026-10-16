using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;
using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Persistence;
using Newtonsoft.Json;

namespace Mconfig.Infrastructure.Releases;

/// <summary>準備コミットはHEADから切り離し、ZIP作成成功後だけ履歴に接続する。</summary>
public sealed class GitPublicationStore(GitDataRepository repository, WorkspaceLock? editLock, IReleaseArchive archive) : IPublicationStore
{
    private sealed record Journal(Guid OperationId, string? Parent, string Target, string Fingerprint, string Reason, string Author);
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal Action? BeforeHistoryCommit { get; init; }

    public async Task<PublicationOutcome> PublishAsync(PublicationRequest request, CancellationToken cancellationToken)
    {
        if (editLock is null) throw new InvalidOperationException("The workspace is open read-only.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkspaceFileSet files = WorkspaceFileSet.Read(repository.Layout);
            if (repository.ReadHead() != request.Parent?.Value || files.Stamp(request.Parent?.Value) != request.Stamp)
                throw new WorkspaceConflictException("Files or HEAD changed before publication.");
            string fingerprint = WorkspaceFingerprints.Of(JsonWorkspaceStore.Parse(files).Content).Value;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.Parent?.Value}\n{fingerprint}")));
            string folder = repository.Layout.FullPath("recovery/publications");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, key + ".json");
            Journal journal;
            if (File.Exists(path))
            {
                journal = JsonConvert.DeserializeObject<Journal>(File.ReadAllText(path))
                    ?? throw new IOException("The publication record cannot be read.");
                if (journal.Parent != request.Parent?.Value || journal.Fingerprint != fingerprint)
                    throw new WorkspaceConflictException("The publication record does not match the saved content.");
                bool valid = repository.WithRepository(repo =>
                {
                    Commit target = GitDataRepository.Lookup(repo, journal.Target);
                    return target.Parents.FirstOrDefault()?.Sha == journal.Parent &&
                           target.Tree.Id == GitRevisionStore.BuildTree(repo, files).Id &&
                           GitDataRepository.OperationOf(target) == journal.OperationId;
                });
                if (!valid) throw new IOException("The prepared publication does not match its record.");
            }
            else
            {
                if (repository.State == DataRepositoryState.NotInitialized) Repository.Init(repository.Layout.DataDirectory);
                journal = repository.WithRepository(repo =>
                {
                    if (repo.Info.IsHeadDetached) throw new WorkspaceConflictException("HEAD must be on a branch.");
                    Tree tree = GitRevisionStore.BuildTree(repo, files);
                    Signature author = GitRevisionStore.Author(request.Author);
                    Commit? parent = request.Parent is null ? null : GitDataRepository.Lookup(repo, request.Parent.Value);
                    string message = $"{request.Reason.Trim()}\n\n{GitDataRepository.OperationTrailer}: {request.OperationId:D}\n";
                    Commit target = repo.ObjectDatabase.CreateCommit(author, author, message, tree,
                        parent is null ? [] : [parent], prettifyMessage: false);
                    return new Journal(request.OperationId, request.Parent?.Value, target.Sha, fingerprint, request.Reason, request.Author);
                });
                DurableFile.WriteText(path, JsonConvert.SerializeObject(journal));
            }

            // 同じ保存内容でやり直した場合は、同じ操作IDのZIP作成を安全に続行する。
            ReleaseOutput output = await archive.ExportAsync(new(ReleaseKind.Release, journal.OperationId, request.Stamp,
                PreparedRevision: new(journal.Target)), CancellationToken.None).ConfigureAwait(false);
            BeforeHistoryCommit?.Invoke();
            if (WorkspaceFileSet.Read(repository.Layout).Stamp(repository.ReadHead()) != request.Stamp)
                throw new WorkspaceConflictException("Files or HEAD changed during publication.");
            repository.WithRepository(repo =>
            {
                Commit target = GitDataRepository.Lookup(repo, journal.Target);
                // index更新の失敗でも履歴を先に進めない。
                GitRevisionStore.UpdateIndex(repo, target.Tree);
                GitRevisionStore.MoveBranch(repo, journal.Parent, target, journal.Reason);
                return true;
            });
            return new(new(journal.Target), files.Stamp(journal.Target), output.Path);
        }
        finally { _gate.Release(); }
    }
}
