using System.Security.Cryptography;
using System.Text;
using Mconfig.Application.Ports;

namespace Mconfig.Infrastructure.Persistence;

/// <summary>
/// ある時点のデータファイル一式の生バイト列。外部変更の検出（スタンプ）は内容指紋でなく、
/// このバイト列のハッシュ・ファイル集合・HEAD で行う。
/// </summary>
internal sealed class WorkspaceFileSet
{
    private WorkspaceFileSet(IReadOnlyDictionary<string, byte[]> files) => Files = files;

    /// <summary>相対パス → バイト列。</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>コミットから読んだファイルなど、既にあるバイト列から作る。</summary>
    public static WorkspaceFileSet From(IReadOnlyDictionary<string, byte[]> files) =>
        new(new Dictionary<string, byte[]>(files, StringComparer.Ordinal));

    public static WorkspaceFileSet Read(WorkspaceLayout layout, Action<string>? fileRead = null)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in layout.ListDataFiles())
        {
            files[path] = ReadBytes(layout.FullPath(path));
            fileRead?.Invoke(path);
        }
        return new WorkspaceFileSet(files);
    }

    public static byte[] ReadBytes(string fullPath)
    {
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>ファイルがなければ null。</summary>
    public static string? HashOf(string fullPath) => File.Exists(fullPath) ? Hash(ReadBytes(fullPath)) : null;

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public WorkspaceStamp Stamp(string? head)
    {
        StringBuilder text = new();
        foreach ((string path, byte[] bytes) in Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            text.Append("file\t").Append(path).Append('\t').Append(Hash(bytes)).Append('\n');
        text.Append("head\t").Append(head ?? "-").Append('\n');
        return new WorkspaceStamp("v1:" + Hash(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
