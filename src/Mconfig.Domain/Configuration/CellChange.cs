namespace Mconfig.Domain.Configuration;

/// <summary>プリセットの一つのセルへ設定する値。</summary>
public sealed record CellChange(string Preset, string Group, string Row, string Column, CellValue Value);
