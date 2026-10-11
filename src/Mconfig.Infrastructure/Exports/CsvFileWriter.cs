using System.Text;
using Mconfig.Application.Ports;

namespace Mconfig.Infrastructure.Exports;

/// <summary>
/// CSV を同じフォルダの一時ファイルへ書き、完了してから出力先へ置き換える（<see cref="ICsvFileWriter"/>）。
/// Excel で文字化けしないよう、BOM 付きの UTF-8 で書く。途中で止まったら一時ファイルだけを消し、既存の出力先は残す。
/// </summary>
public sealed class CsvFileWriter : ICsvFileWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public T Write<T>(string path, Func<TextWriter, T> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        string full = Path.GetFullPath(path);
        string temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            T result;
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (StreamWriter writer = new(stream, Utf8WithBom, bufferSize: 64 * 1024, leaveOpen: true))
                {
                    result = write(writer);
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
            return result;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなかった一時ファイルは出力先と別名なので、既存のファイルには影響しない
        }
    }
}
