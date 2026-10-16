using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;
using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Persistence;
using Newtonsoft.Json;
using Serilog;

namespace Mconfig.Infrastructure.Releases;

/// <summary>準備コミットはHEADから切り離し、ZIP作成成功後だけ履歴に接続する。</summary>
public sealed class GitPublicationStore(GitDataRepository repository, WorkspaceLock? editLock, ZipReleaseArchive archive) : IPublicationStore
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
            if (request.ExistingVersion)
            {
                // 同じバージョンの正常なZIPがあれば、その場所を返す。履歴は増やさない。
                foreach (ReleaseCandidate candidate in await archive.GetReleaseCandidatesAsync(request.Parent, cancellationToken).ConfigureAwait(false))
                {
                    if (candidate.Revision != request.Parent) continue;
                    PublishedRecord? record = PublishedRecord.Load(PublishedRecord.PathOf(repository.Layout, candidate.Name));
                    string zip = repository.Layout.FullPath($"exports/{candidate.Name}.zip");
                    if (record is not null && WorkspaceFileSet.HashOf(zip) == record.ZipSha256)
                    {
                        if (WorkspaceFileSet.Read(repository.Layout).Stamp(repository.ReadHead()) != request.Stamp)
                            throw new WorkspaceConflictException("Files or HEAD changed while locating the published ZIP.");
                        return new(request.Parent!, request.Stamp, zip);
                    }
                }
            }
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
                    return (request.ExistingVersion ? target.Sha == journal.Parent : target.Parents.FirstOrDefault()?.Sha == journal.Parent) &&
                           (request.ExistingVersion ? SameContent(target.Sha, fingerprint) : target.Tree.Id == GitRevisionStore.BuildTree(repo, files).Id) &&
                           (request.ExistingVersion || GitDataRepository.OperationOf(target) == journal.OperationId);
                });
                if (!valid) throw new IOException("The prepared publication does not match its record.");
                if (request.ExistingVersion && archive.IsCompletedOutputMissing(journal.OperationId))
                {
                    // 前のZIPが失われた場合は、同じ履歴から新しい出力を作る。
                    journal = journal with { OperationId = request.OperationId };
                    DurableFile.WriteText(path, JsonConvert.SerializeObject(journal));
                }
                else if ((journal.Reason.Trim() != request.Reason.Trim() || journal.Author.Trim() != request.Author.Trim()) &&
                         !archive.IsPublicationFixed(journal.OperationId))
                {
                    // 公開前の訂正は、同じ操作IDで準備コミットとZIPを作り直す。
                    journal = Prepare(journal.OperationId, request, files, fingerprint);
                    DurableFile.WriteText(path, JsonConvert.SerializeObject(journal));
                }
            }
            else
            {
                if (repository.State == DataRepositoryState.NotInitialized) Repository.Init(repository.Layout.DataDirectory);
                journal = Prepare(request.OperationId, request, files, fingerprint);
                DurableFile.WriteText(path, JsonConvert.SerializeObject(journal));
            }

            string marker = repository.Layout.FullPath($"recovery/{journal.OperationId:N}/publication.json");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            DurableFile.WriteText(marker, "{\"state\":\"Prepared\"}");
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
                if (!request.ExistingVersion) GitRevisionStore.MoveBranch(repo, journal.Parent, target, journal.Reason);
                return true;
            });
            // HEAD確定後の整理失敗を「発行失敗」とは扱わない。未完了の印は次回も保持される。
            try { DurableFile.WriteText(marker, "{\"state\":\"Completed\"}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Log.Warning(ex, "Could not finish publication recovery marker {OperationId}", journal.OperationId); }
            string? fixedMetadata = !request.ExistingVersion &&
                (journal.Reason.Trim() != request.Reason.Trim() || journal.Author.Trim() != request.Author.Trim())
                ? $"{journal.Reason} / {journal.Author}" : null;
            return new(new(journal.Target), files.Stamp(journal.Target), output.Path, fixedMetadata);
        }
        finally { _gate.Release(); }
    }

    private bool SameContent(string revision, string fingerprint) =>
        WorkspaceFingerprints.Of(JsonWorkspaceStore.Parse(WorkspaceFileSet.From(repository.ReadFiles(revision))).Content).Value == fingerprint;

    private Journal Prepare(Guid operationId, PublicationRequest request, WorkspaceFileSet files, string fingerprint) =>
        repository.WithRepository(repo =>
        {
            if (repo.Info.IsHeadDetached) throw new WorkspaceConflictException("HEAD must be on a branch.");
            Tree tree = GitRevisionStore.BuildTree(repo, files);
            Commit? parent = request.Parent is null ? null : GitDataRepository.Lookup(repo, request.Parent.Value);
            if (request.ExistingVersion && !SameContent(parent!.Sha, fingerprint))
                throw new WorkspaceConflictException("The saved content differs from the requested version.");
            if (request.ExistingVersion)
                return new Journal(operationId, parent!.Sha, parent.Sha, fingerprint,
                    GitDataRepository.ReasonOf(parent), parent.Author.Name);
            Signature author = GitRevisionStore.Author(request.Author);
            string message = $"{request.Reason.Trim()}\n\n{GitDataRepository.OperationTrailer}: {operationId:D}\n";
            Commit target = repo.ObjectDatabase.CreateCommit(author, author, message, tree,
                parent is null ? [] : [parent], prettifyMessage: false);
            return new Journal(operationId, request.Parent?.Value, target.Sha, fingerprint, request.Reason, request.Author);
        });
}
