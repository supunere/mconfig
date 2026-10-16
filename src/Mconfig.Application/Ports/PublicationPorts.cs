using Mconfig.Application.Workspaces.Editing;

namespace Mconfig.Application.Ports;

public sealed record PublicationRequest(string Reason, string Author, Guid OperationId,
    RevisionId? Parent, WorkspaceStamp Stamp, bool ExistingVersion = false);

public sealed record PublicationOutcome(RevisionId Revision, WorkspaceStamp Stamp, string Path, string? FixedMetadata = null);

/// <summary>ZIPの作成を終えてから履歴を進める。失敗時の再試行は内部の固定入力を再利用する。</summary>
public interface IPublicationStore
{
    Task<PublicationOutcome> PublishAsync(PublicationRequest request, CancellationToken cancellationToken);
}
