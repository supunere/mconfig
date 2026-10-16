using Mconfig.Infrastructure.Persistence.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Mconfig.Infrastructure.Persistence.Recovery;

/// <summary>
/// 復旧用コピーの保持。recovery/{操作ID}/ のうち、記録がすべて完了（または取消済み）の操作は新しい方から
/// <see cref="KeepCompleted"/> 件だけ残し、それより古いものを消す。未完了の記録、読めない記録、記録のない操作は消さない。
/// 公開完了したリリースの記録は exports/published にあり、ここでは扱わない。
/// </summary>
internal static class RecoveryRetention
{
    public const int KeepCompleted = 10;

    /// <summary>古い完了済みの操作を消し、消した数を返す。消せなかったものは残す（次回に再試行）。</summary>
    public static int Prune(WorkspaceLayout layout, int keep = KeepCompleted)
    {
        if (!Directory.Exists(layout.RecoveryDirectory)) return 0;
        // b3cfd08の発行記録は操作フォルダー外にある。印のない旧記録も保守的に保護する。
        HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);
        string publications = Path.Combine(layout.RecoveryDirectory, "publications");
        try
        {
            if (Directory.Exists(publications))
                foreach (string path in Directory.EnumerateFiles(publications, "*.json"))
                {
                    JObject journal = (JObject)JsonText.Parse(File.ReadAllBytes(path));
                    if (!Guid.TryParse((string?)journal["OperationId"], out Guid id)) return 0;
                    referenced.Add(id.ToString("N"));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        { Log.Warning(ex, "Publication references could not be read; recovery copies were retained"); return 0; }
        string[] finished = Directory.EnumerateDirectories(layout.RecoveryDirectory)
            .Where(d => !referenced.Contains(Path.GetFileName(d)) || File.Exists(Path.Combine(d, "publication.json")))
            .Where(IsFinished)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ThenByDescending(d => d, StringComparer.Ordinal)
            .Skip(keep)
            .ToArray();
        int removed = 0;
        foreach (string directory in finished)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning("Could not remove the recovery copy {Directory}: {Message}", Path.GetFileName(directory), ex.Message);
            }
        }
        if (removed > 0) Log.Information("Removed {Count} old recovery copies (keeping the latest {Keep})", removed, keep);
        return removed;
    }

    /// <summary>操作の記録（ファイル書換え・コミット・出力）が一つ以上あり、どれも完了しているか。</summary>
    internal static bool IsFinished(string directory)
    {
        (string File, Func<string, bool> Done)[] journals =
        [
            ("journal.json", state => state is "Completed" or "RolledBack"),
            ("commit.json", state => state is "Completed" or "NotCommitted"),
            ("export.json", stage => stage is "Completed"),
            ("publication.json", state => state is "Completed"),
        ];
        bool any = false;
        foreach ((string file, Func<string, bool> done) in journals)
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path)) continue;
            any = true;
            try
            {
                JObject json = (JObject)JsonText.Parse(File.ReadAllBytes(path));
                string? state = (string?)json["state"] ?? (string?)json["stage"];
                if (state is null || !done(state)) return false;
            }
            catch (Exception)
            {
                // 読めない記録は、診断のために残す
                return false;
            }
        }
        return any;
    }
}
