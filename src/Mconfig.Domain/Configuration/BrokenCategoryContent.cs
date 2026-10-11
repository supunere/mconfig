namespace Mconfig.Domain.Configuration;

/// <summary>
/// 壊れたカテゴリを読めた範囲の内容（診断用）。一覧そのものが欠落していれば null、要素が壊れていれば null の要素を持ち、
/// プリセットの Content や割り当ての Specs が欠けていることもある。業務の操作には使わない。
/// </summary>
public sealed record BrokenCategoryContent(IReadOnlyList<Preset?>? Presets, IReadOnlyList<Assignment?>? Assignments);
