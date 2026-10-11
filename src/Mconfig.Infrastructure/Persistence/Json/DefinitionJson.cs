using Mconfig.Domain.Definitions;
using Newtonsoft.Json.Linq;

namespace Mconfig.Infrastructure.Persistence.Json;

/// <summary>
/// spec.json と *.schema.json から業務上の定義を読む。定義ファイルはアプリから書き換えないので、
/// 未知プロパティはファイルに残り、業務上の定義とは別に比較用の表現を取り出す。
/// </summary>
internal static class DefinitionJson
{
    public static SpecDefinition ReadSpec(JToken root)
    {
        JArray specs = (root as JObject)?["specs"] as JArray
            ?? throw new InvalidDataException("spec.json must be an object with a 'specs' array.");
        SpecAxis[] axes = specs.Select((spec, i) => new SpecAxis(
            Text(spec, "key", $"specs[{i}]"),
            Texts(spec["values"], $"specs[{i}].values"),
            DisplayName(spec))).ToArray();
        // 重複したキーはどの仕様値を指すか決められないので、仕様として読めない（同名のスキーマと同じ扱い）
        if (SpecDefinition.FindDuplicateKey(axes) is { } duplicate)
            throw new InvalidDataException($"spec.json has a duplicate spec key '{duplicate}'.");
        return new SpecDefinition(axes);
    }

    public static SchemaDefinition ReadSchema(JToken root)
    {
        string name = Text(root, "name", "schema");
        JArray groups = (root as JObject)?["groups"] as JArray
            ?? throw new InvalidDataException("The schema must have a 'groups' array.");
        return new SchemaDefinition(name, groups.Select((group, i) => new SchemaGroupDefinition(
            Text(group, "key", $"groups[{i}]"),
            Keys(group["rows"], $"groups[{i}].rows"),
            Keys(group["columns"], $"groups[{i}].columns"),
            GroupLabels(group))), DisplayName(root));
    }

    /// <summary>解釈しない内容だけを、識別キーに基づく JSON Pointer と保存表現で取り出す。</summary>
    public static string UnknownProperties(JToken root, bool isSpec)
    {
        JObject properties = new();
        if (isSpec)
        {
            Collect(root, "", "specs");
            foreach (JToken axis in (JArray)root["specs"]!)
                Collect(axis, "/specs/" + Pointer((string)axis["key"]!), "key", "values", "displayName");
        }
        else
        {
            Collect(root, "", "name", "displayName", "groups");
            foreach (JToken group in (JArray)root["groups"]!)
            {
                string path = "/groups/" + Pointer((string)group["key"]!);
                Collect(group, path, "key", "displayName", "rows", "columns");
                foreach (JToken row in (JArray)group["rows"]!)
                    Collect(row, path + "/rows/" + Pointer((string)row["key"]!), "key", "displayName");
                foreach (JToken column in (JArray)group["columns"]!)
                    Collect(column, path + "/columns/" + Pointer((string)column["key"]!), "key", "displayName", "unit");
            }
        }
        return JsonText.Canonical(properties);

        void Collect(JToken owner, string path, params string[] known)
        {
            foreach (JProperty property in ((JObject)owner).Properties())
            {
                string pointer = path + "/" + Pointer(property.Name);
                if (!known.Contains(property.Name, StringComparer.Ordinal))
                    properties[pointer] = property.Value.DeepClone();
                else if (property.Name == "displayName")
                {
                    // 読み飛ばした不正な表示名や空の翻訳も、復元で変わる保存内容として残す。
                    if (property.Value is JObject names)
                    {
                        JObject ignored = new(names.Properties()
                            .Where(p => p.Value is not JValue { Type: JTokenType.String } value || string.IsNullOrEmpty((string?)value.Value))
                            .Select(p => new JProperty(p.Name, p.Value.DeepClone())));
                        if (ignored.Count > 0) properties[pointer] = ignored;
                    }
                    else
                        properties[pointer] = property.Value.DeepClone();
                }
                else if (property.Name == "unit" &&
                         (property.Value is not JValue { Type: JTokenType.String } unit || string.IsNullOrEmpty((string?)unit.Value)))
                    properties[pointer] = property.Value.DeepClone();
            }
        }
    }

    private static string Pointer(string key) => key.Replace("~", "~0").Replace("/", "~1");

    // 表示名と単位は表示のための情報なので、形が想定と違っても定義の読込は止めず、読めた分だけ使う
    private static LocalizedText DisplayName(JToken? owner) =>
        (owner as JObject)?["displayName"] is JObject names
            ? new LocalizedText(names.Properties()
                .Where(p => p.Value is JValue { Type: JTokenType.String })
                .ToDictionary(p => p.Name, p => (string)((JValue)p.Value).Value!, StringComparer.Ordinal))
            : LocalizedText.None;

    private static SchemaGroupLabels GroupLabels(JToken group)
    {
        IEnumerable<JObject> Items(string property) => ((group as JObject)?[property] as JArray)?.OfType<JObject>() ?? [];
        Dictionary<string, LocalizedText> rows = new(StringComparer.Ordinal);
        foreach (JObject row in Items("rows"))
            if (row["key"] is JValue { Type: JTokenType.String } key) rows[(string)key.Value!] = DisplayName(row);
        Dictionary<string, LocalizedText> columns = new(StringComparer.Ordinal);
        Dictionary<string, string> units = new(StringComparer.Ordinal);
        foreach (JObject column in Items("columns"))
        {
            if (column["key"] is not JValue { Type: JTokenType.String } key) continue;
            columns[(string)key.Value!] = DisplayName(column);
            if (column["unit"] is JValue { Type: JTokenType.String } unit && ((string)unit.Value!).Length > 0)
                units[(string)key.Value!] = (string)unit.Value!;
        }
        return new SchemaGroupLabels(DisplayName(group), rows, columns, units);
    }

    private static string Text(JToken? owner, string property, string path) =>
        (owner as JObject)?[property] is JValue { Type: JTokenType.String } value
            ? (string)value.Value!
            : throw new InvalidDataException($"{path}.{property} must be a string.");

    private static string[] Texts(JToken? token, string path) =>
        token is JArray array && array.All(v => v is JValue { Type: JTokenType.String })
            ? array.Select(v => (string)((JValue)v).Value!).ToArray()
            : throw new InvalidDataException($"{path} must be an array of strings.");

    private static string[] Keys(JToken? token, string path) =>
        token is JArray array
            ? array.Select((item, i) => Text(item, "key", $"{path}[{i}]")).ToArray()
            : throw new InvalidDataException($"{path} must be an array.");
}
