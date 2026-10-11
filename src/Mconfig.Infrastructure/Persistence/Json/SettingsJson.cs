using Mconfig.Domain.Configuration;
using Newtonsoft.Json.Linq;

namespace Mconfig.Infrastructure.Persistence.Json;

/// <summary>
/// settings_master.json の保存表現と業務モデルの対応。
/// 既知のプロパティ以外は未知プロパティとして元の要素に付けて持ち、形が壊れた要素は黙って変換せず、
/// カテゴリごと診断用（編集不可）にして元の保存表現をそのまま書き戻す。
/// </summary>
internal static class SettingsJson
{
    private const string PresetsKey = "presets";
    private const string AssignmentsKey = "assignments";

    /// <summary>
    /// 最上位のプロパティのうち、同名のスキーマがあるか presets/assignments を持つオブジェクトをカテゴリとし、
    /// それ以外は未知プロパティとして保持する。
    /// </summary>
    public static SettingsData Read(JToken root, IReadOnlySet<string> schemaNames)
    {
        if (root is not JObject settings)
            throw new InvalidDataException("The settings root is not a JSON object.");

        Dictionary<string, CategoryData> categories = new(StringComparer.Ordinal);
        List<KeyValuePair<string, string>> extensions = [];
        foreach (JProperty property in settings.Properties())
        {
            if (IsCategory(property, schemaNames))
                categories[property.Name] = ReadCategory(property.Value);
            else
                extensions.Add(KeyValuePair.Create(property.Name, JsonText.Compact(property.Value)));
        }
        return new SettingsData(categories, new ExtensionProperties(extensions));
    }

    public static JObject Write(SettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        JObject root = new();
        foreach ((string name, CategoryData category) in settings.Categories)
            root.Add(name, WriteCategory(name, category));
        AddExtensions(root, settings.Extensions);
        return root;
    }

    private static bool IsCategory(JProperty property, IReadOnlySet<string> schemaNames) =>
        schemaNames.Contains(property.Name) ||
        property.Value is JObject value && (value.ContainsKey(PresetsKey) || value.ContainsKey(AssignmentsKey));

    private static CategoryData ReadCategory(JToken token)
    {
        string stored = JsonText.Compact(token);
        if (token is not JObject category)
            return CategoryData.FromStored(null, null, storedRepresentation: stored);

        List<Preset?>? presets = (category[PresetsKey] as JArray)?.Select(ReadPreset).ToList();
        List<Assignment?>? assignments = (category[AssignmentsKey] as JArray)?.Select(ReadAssignment).ToList();
        return CategoryData.FromStored(presets!, assignments!,
            Extensions(category, PresetsKey, AssignmentsKey), stored);
    }

    // 形が既知の書式と異なる要素は null とし、カテゴリを診断用にする
    private static Preset? ReadPreset(JToken token)
    {
        if (token is not JObject preset ||
            preset["name"] is not JValue { Type: JTokenType.String } name ||
            preset["groups"] is not JObject groups ||
            groups.Properties().Any(g => g.Value is not (JObject or JValue { Type: JTokenType.Null })))
            return null;

        return new Preset
        {
            Name = (string)name.Value!,
            Content = new PresetContent(groups.Properties().Select(g => KeyValuePair.Create(g.Name,
                g.Value is JObject rows
                    ? PresetGroup.WithRows(rows.Properties().Select(r => KeyValuePair.Create(r.Name, ReadRow(r.Value))))
                    : PresetGroup.Unused))),
            Extensions = Extensions(preset, "name", "groups"),
        };
    }

    private static PresetRow ReadRow(JToken token) => token is JObject row
        ? PresetRow.WithCells(row.Properties().Select(c => KeyValuePair.Create(c.Name, ReadCell(c.Value))))
        : PresetRow.Unsupported(JsonText.Compact(token));

    private static CellValue ReadCell(JToken token) => token switch
    {
        JRaw raw => CellValue.FromNumberLiteral((string)raw.Value!),
        JValue { Type: JTokenType.Null } => CellValue.Null,
        JValue { Type: JTokenType.String } text => CellValue.FromText((string)text.Value!),
        JValue { Type: JTokenType.Boolean } flag => CellValue.FromBoolean((bool)flag.Value!),
        _ => CellValue.FromUnsupported(JsonText.Compact(token)),
    };

    private static Assignment? ReadAssignment(JToken token)
    {
        if (token is not JObject assignment ||
            assignment["specs"] is not JObject specs ||
            specs.Properties().Any(s => s.Value is not JValue { Type: JTokenType.String }) ||
            !assignment.TryGetValue("preset", out JToken? preset) ||
            preset is not JValue { Type: JTokenType.String or JTokenType.Null })
            return null;

        return new Assignment
        {
            Specs = specs.Properties().ToDictionary(s => s.Name, s => (string)((JValue)s.Value).Value!, StringComparer.Ordinal),
            Preset = (string?)((JValue)preset).Value,
            Extensions = Extensions(assignment, "specs", "preset"),
        };
    }

    private static JToken WriteCategory(string name, CategoryData category)
    {
        if (category.StoredRepresentation is not null)
            return JsonText.Parse(category.StoredRepresentation);
        if (category.IsDiagnosticOnly)
            throw new InvalidOperationException($"Category '{name}' is broken and has no stored representation to write back.");

        JObject result = new()
        {
            [PresetsKey] = new JArray(category.Presets.Select(WritePreset)),
            [AssignmentsKey] = new JArray(category.Assignments.Select(WriteAssignment)),
        };
        AddExtensions(result, category.Extensions);
        return result;
    }

    private static JObject WritePreset(Preset preset)
    {
        JObject groups = new();
        foreach ((string groupKey, PresetGroup group) in preset.Content!.Groups)
        {
            groups.Add(groupKey, group.IsUnused
                ? JValue.CreateNull()
                : new JObject(group.Rows.Select(row => new JProperty(row.Key, WriteRow(row.Value)))));
        }
        JObject result = new() { ["name"] = preset.Name, ["groups"] = groups };
        AddExtensions(result, preset.Extensions);
        return result;
    }

    private static JToken WriteRow(PresetRow row) => row.IsSupported
        ? new JObject(row.Cells.Select(cell => new JProperty(cell.Key, WriteCell(cell.Value))))
        : JsonText.Parse(row.UnsupportedRepresentation!);

    private static JToken WriteCell(CellValue value) => value.Kind switch
    {
        CellValueKind.Null => JValue.CreateNull(),
        CellValueKind.Text => new JValue(value.Text),
        CellValueKind.Number when JsonText.IsNumberLiteral(value.Text!) => new JRaw(value.Text),
        CellValueKind.Boolean => new JValue(value.Flag),
        CellValueKind.Unsupported => JsonText.Parse(value.Text!),
        _ => throw new InvalidOperationException($"Cell value '{value.Kind}' '{value.Text}' cannot be written."),
    };

    private static JObject WriteAssignment(Assignment assignment)
    {
        JObject result = new()
        {
            ["specs"] = new JObject(assignment.Specs.Select(s => new JProperty(s.Key, s.Value))),
            ["preset"] = assignment.Preset is null ? JValue.CreateNull() : new JValue(assignment.Preset),
        };
        AddExtensions(result, assignment.Extensions);
        return result;
    }

    private static ExtensionProperties Extensions(JObject source, params string[] knownKeys) =>
        new(source.Properties()
            .Where(p => !knownKeys.Contains(p.Name, StringComparer.Ordinal))
            .Select(p => KeyValuePair.Create(p.Name, JsonText.Compact(p.Value))));

    private static void AddExtensions(JObject target, ExtensionProperties extensions)
    {
        foreach ((string key, string value) in extensions.Items)
        {
            if (target.ContainsKey(key))
                throw new InvalidOperationException($"Extension property '{key}' collides with a known property.");
            target.Add(key, JsonText.Parse(value));
        }
    }
}
