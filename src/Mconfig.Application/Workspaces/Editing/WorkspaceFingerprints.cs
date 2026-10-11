using System.Security.Cryptography;
using System.Text;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

using Mconfig.Application.Workspaces;

namespace Mconfig.Application.Workspaces.Editing;

/// <summary>
/// ワークスペース内容（設定・仕様・スキーマ）の正規化指紋。保存時の整形やオブジェクトのキー順には
/// 依存せず、業務上の内容（プリセットと割り当ての順序、値の種類と数値表記、理解できない値の保存表現、
/// 仕様の軸と値の順序、スキーマのグループ・行・列の順序、定義の表示名と単位）が同じなら同じ指紋になる。
/// </summary>
public static class WorkspaceFingerprints
{
    public static ContentFingerprint Of(WorkspaceContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        StringBuilder text = new();
        WriteWorkspace(text, content, p => WritePreset(text, p), a => WriteAssignment(text, a));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return new ContentFingerprint(Convert.ToHexString(hash));
    }

    // キャッシュした区間も同じ正規化バイト列の同じ位置に入力し、既存の保存・出力記録の指紋と互換にする。
    internal static ContentFingerprint Of(WorkspaceContent content, Func<Preset, byte[]> presetBytes, Func<Assignment, byte[]> assignmentBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        StringBuilder text = new();
        WriteWorkspace(text, content,
            p => { Flush(); hash.AppendData(presetBytes(p)); },
            a => { Flush(); hash.AppendData(assignmentBytes(a)); });
        Flush();
        return new ContentFingerprint(Convert.ToHexString(hash.GetHashAndReset()));

        void Flush()
        {
            if (text.Length == 0) return;
            hash.AppendData(Encoding.UTF8.GetBytes(text.ToString()));
            text.Clear();
        }
    }

    internal static byte[] BytesOf(Preset preset)
    {
        StringBuilder text = new();
        WritePreset(text, preset);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    internal static byte[] BytesOf(Assignment assignment)
    {
        StringBuilder text = new();
        WriteAssignment(text, assignment);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static void WriteWorkspace(StringBuilder text, WorkspaceContent content, Action<Preset> preset, Action<Assignment> assignment)
    {

        foreach ((string path, string source) in content.DefinitionSources.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            Write(text, "definition-path", path);
            Write(text, "definition-source", source);
        }

        // 仕様・スキーマの並び順は表示や組み合わせの順序を決めるので、内容として含める
        foreach (SpecAxis axis in content.Spec.Axes)
        {
            Write(text, "axis", axis.Key);
            Write(text, "axis-name", axis.DisplayName.Canonical);
            foreach (string value in axis.Values) Write(text, "axis-value", value);
        }
        foreach (SchemaDefinition schema in content.Schemas.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            Write(text, "schema", schema.Name);
            Write(text, "schema-name", schema.DisplayName.Canonical);
            foreach (SchemaGroupDefinition group in schema.Groups)
            {
                Write(text, "schema-group", group.Key);
                Write(text, "schema-group-name", group.Labels.Group.Canonical);
                foreach (string row in group.Rows)
                {
                    Write(text, "schema-row", row);
                    Write(text, "schema-row-name", group.Labels.Row(row).Canonical);
                }
                foreach (string column in group.Columns)
                {
                    Write(text, "schema-column", column);
                    Write(text, "schema-column-name", group.Labels.Column(column).Canonical);
                    Write(text, "schema-column-unit", group.Labels.Unit(column) ?? "");
                }
            }
        }

        foreach ((string name, CategoryData category) in content.Settings.Categories.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            Write(text, "category", name);
            WriteCategory(text, category, preset, assignment);
        }
        WriteExtensions(text, content.Settings.Extensions);
    }

    private static void WriteCategory(StringBuilder text, CategoryData category, Action<Preset> writePreset, Action<Assignment> writeAssignment)
    {
        // 診断用カテゴリの元の保存表現と、未知プロパティも内容として区別する
        if (category.StoredRepresentation is not null) Write(text, "stored", category.StoredRepresentation);
        WriteExtensions(text, category.Extensions);
        // 壊れたカテゴリ（診断用）は読めた範囲の内容で、一覧や要素が欠けていることも区別する
        IReadOnlyList<Preset?>? presets = category.IsDiagnosticOnly ? category.Broken?.Presets : category.Presets;
        IReadOnlyList<Assignment?>? assignments = category.IsDiagnosticOnly ? category.Broken?.Assignments : category.Assignments;
        if (presets is null) Write(text, "presets", "missing");
        foreach (Preset? preset in presets ?? [])
        {
            if (preset is null) { Write(text, "preset", "missing"); continue; }
            writePreset(preset);
        }

        if (assignments is null) Write(text, "assignments", "missing");
        foreach (Assignment? assignment in assignments ?? [])
        {
            if (assignment is null) { Write(text, "assignment", "missing"); continue; }
            writeAssignment(assignment);
        }
    }

    private static void WritePreset(StringBuilder text, Preset preset)
    {
        Write(text, "preset", preset.Name);
        WriteExtensions(text, preset.Extensions);
        if (preset.Content is null) { Write(text, "content", "missing"); return; }
        foreach ((string groupKey, PresetGroup group) in preset.Content.Groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Write(text, "group", groupKey);
            if (group.IsUnused) { Write(text, "unused", ""); continue; }
            foreach ((string rowKey, PresetRow row) in group.Rows.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                Write(text, "row", rowKey);
                if (!row.IsSupported) { Write(text, "unsupported-row", row.UnsupportedRepresentation!); continue; }
                foreach ((string columnKey, CellValue value) in row.Cells.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    Write(text, "cell", columnKey);
                    Write(text, value.Kind.ToString(), value.Kind == CellValueKind.Boolean ? value.Flag.ToString() : value.Text ?? "");
                }
            }
        }
    }

    private static void WriteAssignment(StringBuilder text, Assignment assignment)
    {
        Write(text, "assignment", assignment.Preset is null ? "disabled" : "preset");
        Write(text, "target", assignment.Preset ?? "");
        WriteExtensions(text, assignment.Extensions);
        foreach ((string key, string value) in (assignment.Specs ?? new Dictionary<string, string>()).OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            Write(text, "condition", key);
            Write(text, "value", value);
        }
    }

    private static void WriteExtensions(StringBuilder text, ExtensionProperties extensions)
    {
        foreach ((string key, string value) in extensions.Items.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            Write(text, "extension", key);
            Write(text, "extension-value", value);
        }
    }

    // 長さを前置して、区切り文字を含む値でも曖昧にならないようにする
    private static void Write(StringBuilder text, string tag, string value) =>
        text.Append(tag).Append(':').Append(value.Length).Append(':').Append(value).Append('\n');
}
