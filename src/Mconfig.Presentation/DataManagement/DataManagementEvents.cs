using Mconfig.Application.Ports;

namespace Mconfig.Presentation.DataManagement;

public sealed class CommitRequestedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

public sealed class RevisionEventArgs(RevisionId? revision) : EventArgs
{
    public RevisionId? Revision { get; } = revision;
}

public sealed class ExportRequestedEventArgs(ReleaseKind kind) : EventArgs
{
    public ReleaseKind Kind { get; } = kind;
}

public sealed class ResumeExportRequestedEventArgs(Guid operationId) : EventArgs
{
    public Guid OperationId { get; } = operationId;
}

/// <summary>検証結果の 1 件の該当箇所へ移る要求。</summary>
public sealed class IssueEventArgs(IssueDisplay issue) : EventArgs
{
    public IssueDisplay Issue { get; } = issue;
}
