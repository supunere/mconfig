namespace Mconfig.Domain.Definitions;

/// <summary>
/// 仕様の軸。キーと、とりうる値（順序付き）。値の空・重複や予約値は読み込めるようにしたまま検証でエラーにする。
/// </summary>
public sealed record SpecAxis
{
    public SpecAxis(string key, IEnumerable<string> values, LocalizedText? displayName = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Values = Array.AsReadOnly(values?.ToArray() ?? throw new ArgumentNullException(nameof(values)));
        DisplayName = displayName ?? LocalizedText.None;
    }

    public string Key { get; }
    public IReadOnlyList<string> Values { get; }

    /// <summary>軸の表示名。値そのものは翻訳しない。</summary>
    public LocalizedText DisplayName { get; }
}

/// <summary>
/// 仕様（spec.json の業務上の内容）。組み合わせは各軸の値の直積になる。
/// キーは仕様値を引く鍵なので一意でなければならず、重複したキーの仕様は作れない。
/// </summary>
public sealed class SpecDefinition
{
    public SpecDefinition(IEnumerable<SpecAxis> axes)
    {
        SpecAxis[] items = axes?.ToArray() ?? throw new ArgumentNullException(nameof(axes));
        if (FindDuplicateKey(items) is { } duplicate)
            throw new ArgumentException($"仕様キー '{duplicate}' が重複しています。", nameof(axes));
        Axes = Array.AsReadOnly(items);
    }

    /// <summary>最初に重複した仕様キー。重複がなければ null。</summary>
    public static string? FindDuplicateKey(IEnumerable<SpecAxis> axes)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        return axes.Select(a => a.Key).FirstOrDefault(key => !keys.Add(key));
    }

    public static SpecDefinition Empty { get; } = new([]);

    public IReadOnlyList<SpecAxis> Axes { get; }
}
