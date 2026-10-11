using System.Globalization;
using System.Text;

namespace Mconfig.Infrastructure.Persistence;

/// <summary>
/// アプリ間の編集排他。workspace.lock を共有不可で開いたまま保持する。
/// 取得できない二つ目のアプリは読取専用で開く。外部エディターや Git CLI との排他は保証しない。
/// </summary>
public sealed class WorkspaceLock : IDisposable
{
    private readonly FileStream _stream;

    private WorkspaceLock(FileStream stream) => _stream = stream;

    /// <summary>ロックを取得する。別のプロセス（または同じプロセスの別の保持者）が持っていれば null。</summary>
    public static WorkspaceLock? TryAcquire(WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        FileStream stream;
        try
        {
            stream = new FileStream(layout.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        // 調査用に保持者を書いておく（排他そのものはファイルを開いていることで成り立つ）
        byte[] owner = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"pid={Environment.ProcessId} machine={Environment.MachineName} since={DateTimeOffset.UtcNow:O}\n"));
        stream.SetLength(0);
        stream.Write(owner);
        stream.Flush(flushToDisk: true);
        return new WorkspaceLock(stream);
    }

    public void Dispose() => _stream.Dispose();
}
