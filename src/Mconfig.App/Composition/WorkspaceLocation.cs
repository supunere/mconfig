using Mconfig.App.Resources;
using Mconfig.Infrastructure.Persistence;

namespace Mconfig.App.Composition;

/// <summary>
/// ワークスペースの場所。実行ファイルの場所とは分け、既定は %LOCALAPPDATA%\mconfig\workspace。
/// 起動引数 --workspace &lt;フォルダ&gt; が優先し、最後に開いた場所を %LOCALAPPDATA%\mconfig\app-settings.json に記憶する。
/// </summary>
internal static class WorkspaceLocation
{
    public static string DefaultRoot => Path.Combine(AppSettingsFile.AppDataDirectory, "workspace");

    /// <summary>起動引数 → 記憶した場所 → 既定の順に決める。</summary>
    public static string Resolve(string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, "--workspace", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length) return Path.GetFullPath(args[index + 1]);
        return Remembered() ?? DefaultRoot;
    }

    public static void Remember(string root) => AppSettingsFile.Set(new Dictionary<string, string> { ["workspace"] = root });

    private static string? Remembered() => AppSettingsFile.Get("workspace") is { Length: > 0 } root ? root : null;

    public static bool HasData(string root) =>
        File.Exists(Path.Combine(root, "data", "config", "spec.json")) &&
        File.Exists(Path.Combine(root, "data", "settings_master.json")) &&
        Directory.Exists(Path.Combine(root, "data", "schemas"));

    /// <summary>初期化してよい空の場所か。欠損した data や復旧記録も既存ワークスペースとして扱う。</summary>
    public static bool HasWorkspaceArtifacts(string root) =>
        Directory.Exists(Path.Combine(root, "data")) ||
        Directory.Exists(Path.Combine(root, "recovery")) ||
        File.Exists(Path.Combine(root, WorkspaceLayout.LockFileName));

    /// <summary>アプリに同梱した初期データ（実行ファイルの近くの data）。開発中はリポジトリの data も探す。</summary>
    public static string? BundledData()
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (string candidate in new[]
                 {
                     Path.Combine(baseDir, "data"),
                     Path.Combine(baseDir, "..", "data"),
                     Path.Combine(baseDir, "..", "..", "..", "..", "..", "data"),
                 })
        {
            string full = Path.GetFullPath(candidate);
            if (HasData(Path.GetDirectoryName(full)!) && Path.GetFileName(full) == "data") return full;
        }
        return null;
    }

    /// <summary>
    /// 同梱データの設定・仕様・スキーマだけを新しいワークスペースの data へ複製する（Git の履歴や出力物は複製しない）。
    /// 既にデータがある場所には書き込まない。
    /// </summary>
    public static void CreateFromBundled(string root, string bundledData)
    {
        if (HasWorkspaceArtifacts(root)) throw new InvalidOperationException("The workspace already contains data or recovery material.");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(data, "config"));
        Directory.CreateDirectory(Path.Combine(data, "schemas"));
        File.Copy(Path.Combine(bundledData, "config", "spec.json"), Path.Combine(data, "config", "spec.json"));
        File.Copy(Path.Combine(bundledData, "settings_master.json"), Path.Combine(data, "settings_master.json"));
        foreach (string schema in Directory.GetFiles(Path.Combine(bundledData, "schemas"), "*.schema.json"))
            File.Copy(schema, Path.Combine(data, "schemas", Path.GetFileName(schema)));
    }

    /// <summary>
    /// データのない場所を開こうとしたとき、既存のワークスペースを選ぶか、同梱データから作るかを尋ねる。
    /// 空の設定を黙って作らない。やめたら null。
    /// </summary>
    public static string? Prepare(string root)
    {
        if (HasWorkspaceArtifacts(root)) return root;
        while (!HasData(root))
        {
            // AutoSize のフォームに Dock した子を置くと大きさが決まらず縮むので、表で内容の大きさに合わせる
            using Form dialog = new()
            {
                Text = UiText.Get("Setup_Title"),
                StartPosition = FormStartPosition.CenterScreen,
                AutoScaleMode = AutoScaleMode.Dpi,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = true,
            };
            TableLayoutPanel layout = new()
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(16),
                Location = new Point(0, 0),
            };
            Label message = new()
            {
                Text = UiText.Format("Setup_Message", root),
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                MinimumSize = new Size(420, 0),
                Margin = new Padding(0, 0, 0, 16),
            };
            FlowLayoutPanel buttons = new()
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0),
                WrapContents = false,
            };
            Button exit = new() { Text = UiText.Get("Setup_Exit"), AutoSize = true, DialogResult = DialogResult.Cancel };
            Button create = new() { Text = UiText.Get("Setup_CreateFromBundled"), AutoSize = true, DialogResult = DialogResult.Yes };
            Button choose = new() { Text = UiText.Get("Setup_ChooseExisting"), AutoSize = true, DialogResult = DialogResult.OK };
            buttons.Controls.AddRange([exit, create, choose]);
            layout.Controls.Add(message, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = create;
            dialog.CancelButton = exit;

            switch (dialog.ShowDialog())
            {
                case DialogResult.OK:
                    using (FolderBrowserDialog folder = new() { UseDescriptionForTitle = true, Description = UiText.Get("Setup_ChooseExisting") })
                    {
                        if (folder.ShowDialog() != DialogResult.OK) continue;
                        if (HasData(folder.SelectedPath)) return folder.SelectedPath;
                        MessageBox.Show(UiText.Get("Setup_FolderInvalid"), UiText.Get("Setup_Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    break;
                case DialogResult.Yes:
                    if (BundledData() is not { } bundled)
                    {
                        MessageBox.Show(UiText.Get("Setup_NoBundled"), UiText.Get("Setup_Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        break;
                    }
                    CreateFromBundled(root, bundled);
                    break;
                default:
                    return null;
            }
        }
        return root;
    }
}
