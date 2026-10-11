namespace Mconfig.Infrastructure.Persistence;

/// <summary>
/// 操作の記録（操作記録・コミット記録・出力記録・公開完了記録）を、途中で止まっても旧版か新版のどちらかが残るように書く。
/// </summary>
internal static class DurableFile
{
    /// <summary>
    /// 同じディレクトリの {path}.tmp へ BOM なし UTF-8 で書き、ディスクまで書き出してから path へ上書きで移す。
    /// ディレクトリは呼出し側が作る。失敗した例外はそのまま伝え、残った .tmp は次の書き込みで上書きされる。
    /// </summary>
    public static void WriteText(string path, string text)
    {
        string temp = path + ".tmp";
        using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (StreamWriter writer = new(stream))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
