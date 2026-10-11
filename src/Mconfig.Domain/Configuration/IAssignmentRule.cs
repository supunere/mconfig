namespace Mconfig.Domain.Configuration;

/// <summary>
/// カテゴリの割り当て行。Conditions に一致した組み合わせへ PresetName を割り当てる。
/// PresetName が null の行は、その組み合わせを明示的に存在しないもの（Disabled）とする。
/// </summary>
public interface IAssignmentRule
{
    IReadOnlyDictionary<string, string> Conditions { get; }
    string? PresetName { get; }
}
