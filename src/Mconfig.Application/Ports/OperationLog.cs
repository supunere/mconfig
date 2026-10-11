using Mconfig.Application.Workspaces;

namespace Mconfig.Application.Ports;

/// <summary>
/// 操作の記録。Operation は操作の種類、Version は完了時のセッションの版、TargetSha は対象の版（コミット・復元・出力）、
/// Stage は完了した段階、Status は結果、IssueCount は検証の指摘の件数。設定値の本文や利用者名は含めない。
/// </summary>
public sealed record OperationLogEntry(
    string Operation, Guid OperationId, long Version, OperationStatus Status, OperationStage Stage, TimeSpan Elapsed,
    string? TargetSha, int IssueCount);

/// <summary>操作の記録先（実装は Infrastructure が持ち、ローカルのログへ書く。外部へは送らない）。</summary>
public interface IOperationLog
{
    void Completed(OperationLogEntry entry);

    /// <summary>予期しない例外で操作が止まった。例外はスタックトレースを含めて記録する。</summary>
    void Failed(string operation, TimeSpan elapsed, Exception exception);
}
