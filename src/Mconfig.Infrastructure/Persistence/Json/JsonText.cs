using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mconfig.Infrastructure.Persistence.Json;

/// <summary>保存ファイルの符号化と改行（元のファイルに合わせて書き戻す）。</summary>
internal sealed record JsonFileFormat(bool ByteOrderMark, string NewLine, bool FinalNewLine)
{
    /// <summary>既存の data/ と同じ形式（BOM 付き UTF-8、LF、末尾改行なし）。新しく作るファイルに使う。</summary>
    public static JsonFileFormat Default { get; } = new(true, "\n", false);
}

/// <summary>
/// 既存 JSON の読み書き。数値は元の表記のまま JRaw として保持し、文字列を日時として解釈しない。
/// 黙って情報を失う入力（重複キー、コメント、元の表記を特定できない数値など）は拒否する。
/// </summary>
internal static partial class JsonText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLiteral();

    public static bool IsNumberLiteral(string text) => NumberLiteral().IsMatch(text);

    /// <summary>ファイルのバイト列を文字列と形式に分ける。UTF-8 として不正なら <see cref="InvalidDataException"/>。</summary>
    public static (string Text, JsonFileFormat Format) Decode(byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The file is not valid UTF-8.", ex);
        }
        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool finalNewLine = text.EndsWith('\n');
        return (text, new JsonFileFormat(bom, newLine, finalNewLine));
    }

    public static JToken Parse(byte[] bytes) => Parse(Decode(bytes).Text);

    public static JToken Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int[] lineStarts = LineStarts(text);
        try
        {
            using JsonTextReader reader = new(new StringReader(text))
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double,
                MaxDepth = 128,
            };
            if (!reader.Read()) throw new InvalidDataException("The JSON document is empty.");
            JToken root = ReadValue(reader, text, lineStarts);
            if (reader.Read()) throw new InvalidDataException("Additional content after the JSON document.");
            return root;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>字下げ 2 の JSON を、指定の形式のバイト列にする。</summary>
    public static byte[] Serialize(JToken token, JsonFileFormat format)
    {
        StringWriter text = new(CultureInfo.InvariantCulture) { NewLine = format.NewLine };
        using (JsonTextWriter writer = new(text) { Formatting = Formatting.Indented, Indentation = 2, IndentChar = ' ', CloseOutput = false })
        {
            token.WriteTo(writer);
        }
        if (format.FinalNewLine) text.Write(format.NewLine);
        byte[] body = StrictUtf8.GetBytes(text.ToString());
        return format.ByteOrderMark ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    /// <summary>空白のない JSON 表記（未知プロパティや理解できない値の保存表現）。数値は元の表記のまま。</summary>
    public static string Compact(JToken token) => token.ToString(Formatting.None);

    /// <summary>オブジェクトのプロパティ順に依存しない正規化表現。配列順と数値の保存表現は内容として維持する。</summary>
    public static string Canonical(JToken token) => Sort(token).ToString(Formatting.None);

    private static JToken Sort(JToken token) => token switch
    {
        JObject obj => new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new JProperty(p.Name, Sort(p.Value)))),
        JArray array => new JArray(array.Select(Sort)),
        _ => token.DeepClone(),
    };

    private static JToken ReadValue(JsonTextReader reader, string text, int[] lineStarts)
    {
        switch (reader.TokenType)
        {
            case JsonToken.StartObject:
                JObject obj = new();
                while (true)
                {
                    Next(reader);
                    if (reader.TokenType == JsonToken.EndObject) return obj;
                    if (reader.TokenType != JsonToken.PropertyName) throw Unexpected(reader);
                    string name = (string)reader.Value!;
                    Next(reader);
                    JToken value = ReadValue(reader, text, lineStarts);
                    if (obj.ContainsKey(name))
                        throw new InvalidDataException($"Duplicate property '{name}' at {reader.Path}.");
                    obj.Add(name, value);
                }
            case JsonToken.StartArray:
                JArray array = new();
                while (true)
                {
                    Next(reader);
                    if (reader.TokenType == JsonToken.EndArray) return array;
                    array.Add(ReadValue(reader, text, lineStarts));
                }
            case JsonToken.Integer:
            case JsonToken.Float:
                return new JRaw(RawNumber(reader, text, lineStarts));
            case JsonToken.String:
                return new JValue((string)reader.Value!);
            case JsonToken.Boolean:
                return new JValue((bool)reader.Value!);
            case JsonToken.Null:
                return JValue.CreateNull();
            default:
                throw Unexpected(reader);
        }
    }

    private static void Next(JsonTextReader reader)
    {
        if (!reader.Read()) throw new InvalidDataException("Unexpected end of the JSON document.");
    }

    private static InvalidDataException Unexpected(JsonTextReader reader) =>
        new($"Unsupported JSON token '{reader.TokenType}' at {reader.Path}.");

    /// <summary>
    /// 読み終えた数値トークンの元の表記を本文から取り出す。読込器が解釈した値と一致しなければ拒否する
    /// （位置の食い違いなどで別の文字列を保存しないため）。
    /// </summary>
    private static string RawNumber(JsonTextReader reader, string text, int[] lineStarts)
    {
        IJsonLineInfo line = reader;
        int end = lineStarts[line.LineNumber - 1] + line.LinePosition;
        int start = end;
        while (start > 0 && "0123456789+-.eE".Contains(text[start - 1])) start--;
        string raw = text[start..end];

        bool matches = IsNumberLiteral(raw) && reader.Value switch
        {
            long l => BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger b) && b == l,
            BigInteger big => BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger b) && b == big,
            double d => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed.Equals(d),
            _ => false,
        };
        if (!matches)
            throw new InvalidDataException($"Unsupported number notation at {reader.Path}.");
        return raw;
    }

    // Newtonsoft.Json と同じく、CRLF・LF・単独の CR をそれぞれ一つの改行として数える
    private static int[] LineStarts(string text)
    {
        List<int> starts = [0];
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }
}
