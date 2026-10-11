using Mconfig.Application.Ports;
using Serilog;

namespace Mconfig.Infrastructure.Logging;

/// <summary>
/// 操作の記録をローカルのログへ書く。操作ID・版・対象 SHA・段階・所要時間・結果・指摘の件数を残し、
/// 設定値の本文や利用者名は書かない。ログの出力に失敗しても操作は止めない（整合性に必要な記録は操作記録が担う）。
/// </summary>
public sealed class SerilogOperationLog(ILogger logger) : IOperationLog
{
    public void Completed(OperationLogEntry entry)
    {
        try
        {
            logger.Information(
                "Operation {Operation} {OperationId} finished: {Status} (stage {Stage}, version {Version}, target {TargetSha}, issues {IssueCount}) in {ElapsedMs:0} ms",
                entry.Operation, entry.OperationId, entry.Status, entry.Stage, entry.Version, entry.TargetSha ?? "-", entry.IssueCount,
                entry.Elapsed.TotalMilliseconds);
        }
        catch (Exception)
        {
            // 記録できなくても操作の結果は変えない
        }
    }

    public void Failed(string operation, TimeSpan elapsed, Exception exception)
    {
        try
        {
            logger.Error(exception, "Operation {Operation} failed unexpectedly after {ElapsedMs:0} ms", operation, elapsed.TotalMilliseconds);
        }
        catch (Exception)
        {
        }
    }
}
