namespace Mconfig.Infrastructure.Persistence;

/// <summary>
/// ワークスペースの配置。パスはワークスペース相対（区切りは '/'）で扱い、
/// ルート外やパストラバーサルになる参照は拒否する。Git の対象は data/ の 3 種のデータファイルだけ。
/// </summary>
public sealed class WorkspaceLayout
{
    public const string SettingsPath = "data/settings_master.json";
    public const string SpecPath = "data/config/spec.json";
    public const string SchemasDirectory = "data/schemas";
    public const string SchemaSuffix = ".schema.json";
    public const string DataDirectoryName = "data";
    public const string RecoveryDirectoryName = "recovery";
    public const string ExportsDirectoryName = "exports";
    public const string LogsDirectoryName = "logs";
    public const string LockFileName = "workspace.lock";

    public WorkspaceLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }
    public string DataDirectory => FullPath(DataDirectoryName);
    public string RecoveryDirectory => FullPath(RecoveryDirectoryName);
    public string ExportsDirectory => FullPath(ExportsDirectoryName);
    public string LogsDirectory => FullPath(LogsDirectoryName);
    public string LockFile => FullPath(LockFileName);

    /// <summary>ワークスペース相対パスを絶対パスにする。絶対パス・空の区間・"."・".."・ルート外は拒否する。</summary>
    public string FullPath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new ArgumentException($"'{relativePath}' is not a workspace-relative path.", nameof(relativePath));
        string[] segments = relativePath.Split('/', '\\');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".."))
            throw new ArgumentException($"'{relativePath}' is not a workspace-relative path.", nameof(relativePath));

        string full = Path.GetFullPath(Path.Combine([Root, .. segments]));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"'{relativePath}' is outside the workspace.", nameof(relativePath));
        RejectLinkedPath(full, relativePath);
        return full;
    }

    private void RejectLinkedPath(string fullPath, string relativePath)
    {
        string current = Root;
        foreach (string segment in Path.GetRelativePath(Root, fullPath).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    $"'{relativePath}' traverses a symbolic link or junction and is not a safe workspace path.");
        }
    }

    /// <summary>Git と一時保存の対象になるデータファイルか（spec、スキーマ、設定）。</summary>
    public static bool IsDataFile(string relativePath) =>
        relativePath is SpecPath or SettingsPath ||
        relativePath.StartsWith(SchemasDirectory + "/", StringComparison.Ordinal) &&
        relativePath.EndsWith(SchemaSuffix, StringComparison.Ordinal) &&
        relativePath.IndexOf('/', SchemasDirectory.Length + 1) < 0 &&
        relativePath.Length > SchemasDirectory.Length + 1 + SchemaSuffix.Length;

    /// <summary>現在あるデータファイル（相対パス、序数順）。</summary>
    public IReadOnlyList<string> ListDataFiles()
    {
        List<string> files = [];
        if (File.Exists(FullPath(SpecPath))) files.Add(SpecPath);
        if (File.Exists(FullPath(SettingsPath))) files.Add(SettingsPath);
        string schemas = FullPath(SchemasDirectory);
        if (Directory.Exists(schemas))
        {
            files.AddRange(Directory.EnumerateFiles(schemas, "*" + SchemaSuffix, SearchOption.TopDirectoryOnly)
                .Select(path => $"{SchemasDirectory}/{Path.GetFileName(path)}")
                .Where(IsDataFile));
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>初期データの不足。空の設定を作って補わず、読込を止める理由として返す。</summary>
    public IReadOnlyList<string> DiagnoseMissingData()
    {
        List<string> problems = [];
        if (!File.Exists(FullPath(SpecPath))) problems.Add($"{SpecPath}: missing");
        if (!File.Exists(FullPath(SettingsPath))) problems.Add($"{SettingsPath}: missing");
        if (!Directory.Exists(FullPath(SchemasDirectory))) problems.Add($"{SchemasDirectory}: missing");
        return problems;
    }
}
