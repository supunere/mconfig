using Mconfig.Application.Ports;

namespace Mconfig.Presentation.DataManagement;

public sealed class PublishRequestedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

public sealed class RevisionEventArgs(RevisionId? revision) : EventArgs
{
    public RevisionId? Revision { get; } = revision;
}

/// <summary>検証結果の 1 件の該当箇所へ移る要求。</summary>
public sealed class IssueEventArgs(IssueDisplay issue) : EventArgs
{
    public IssueDisplay Issue { get; } = issue;
}
