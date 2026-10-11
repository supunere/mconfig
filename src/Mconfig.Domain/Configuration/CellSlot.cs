namespace Mconfig.Domain.Configuration;

/// <summary>セルに値を入力できるか。</summary>
public enum CellEditability
{
    Editable,
    /// <summary>グループが未使用（値を持たない）。</summary>
    Unused,
    /// <summary>グループがない、行が理解できない構造、または値が理解できない。</summary>
    NotEditable,
}

/// <summary>セルの値と、値を入力できるか（<see cref="PresetContent.CellAt"/>）。</summary>
public readonly record struct CellSlot(CellEditability Editability, CellValue Value);
