using System.Collections.ObjectModel;

namespace Mconfig.Domain.Definitions;

/// <summary>
/// 定義の表示名（言語コード → 文字列）。仕様値やキーそのものは翻訳しないので、表示名がなければキーを使う。
/// </summary>
public sealed class LocalizedText
{
    public static LocalizedText None { get; } = new(new Dictionary<string, string>());

    public LocalizedText(IReadOnlyDictionary<string, string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        Texts = new ReadOnlyDictionary<string, string>(
            texts.Where(t => !string.IsNullOrEmpty(t.Value)).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, string> Texts { get; }

    /// <summary>指定した言語、なければ ja、それもなければ fallback（通常はキー）の順で解決する。</summary>
    public string Resolve(string language, string fallback) =>
        Texts.TryGetValue(language, out string? text) ? text :
        Texts.TryGetValue("ja", out string? ja) ? ja :
        fallback;

    /// <summary>内容の比較用の正規化表現（言語コード順）。</summary>
    public string Canonical => string.Join("\n", Texts.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}"));
}
