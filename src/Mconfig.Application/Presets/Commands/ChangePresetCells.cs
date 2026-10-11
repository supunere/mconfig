using System.Text;
using System.Text.RegularExpressions;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

namespace Mconfig.Application.Presets.Commands;

/// <summary>利用者が入力した一つのセルの文字列。</summary>
public sealed record CellInput(string Preset, string Group, string Row, string Column, string Text);

/// <summary>
/// セルの入力・矩形貼り付けを一括で適用する。全件を変換・検証してから適用し、
/// 1 件でも不正なら何も変えずに位置と理由を返す。
/// </summary>
public sealed record ChangePresetCellsCommand(string Category, IReadOnlyList<CellInput> Inputs, SessionVersion ExpectedVersion);

public enum CellInputProblemKind
{
    /// <summary>数値のセルに、数値として読めない文字列が入力された。</summary>
    NotANumber,
    /// <summary>真偽値のセルに、true/false 以外が入力された。</summary>
    NotABoolean,
    /// <summary>未使用のグループ、理解できない構造、スキーマにない位置など、値を入力できないセル。</summary>
    NotEditable,
}

/// <summary>入力できなかったセル。Index は Inputs 内の位置。</summary>
public sealed record CellInputProblem(int Index, CellInput Input, CellInputProblemKind Kind);

public sealed record ChangePresetCellsResult(CommandResult Result, IReadOnlyList<CellInputProblem> Problems);

public sealed class ChangePresetCellsHandler(EditSession session)
{
    public ChangePresetCellsResult Handle(ChangePresetCellsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        List<CellInputProblem> problems = [];
        CommandResult result = session.Apply(command.ExpectedVersion, content =>
        {
            if (!content.Settings.Categories.TryGetValue(command.Category, out CategoryData? category) || category.IsDiagnosticOnly)
                throw new PresetOperationException(PresetOperationFailure.InvalidCategory);
            SchemaDefinition? schema = content.Schemas.FirstOrDefault(s => s.Name == command.Category);

            List<CellChange> changes = [];
            for (int i = 0; i < command.Inputs.Count; i++)
            {
                CellInput input = command.Inputs[i];
                Preset? preset = category.Presets.FirstOrDefault(p => p.Name == input.Preset);
                if (preset is null)
                    throw new PresetOperationException(PresetOperationFailure.MissingPreset);

                CellValue? current = EditableValue(schema, preset, input);
                if (current is null)
                {
                    problems.Add(new CellInputProblem(i, input, CellInputProblemKind.NotEditable));
                    continue;
                }
                CellInputProblemKind? problem = CellInputRules.TryConvert(current.Value, input.Text, out CellValue value);
                if (problem is not null)
                    problems.Add(new CellInputProblem(i, input, problem.Value));
                else if (!value.Equals(current.Value))
                    changes.Add(new CellChange(input.Preset, input.Group, input.Row, input.Column, value));
            }

            // 一部だけを反映しない
            if (problems.Count > 0) throw new PresetOperationException(PresetOperationFailure.InvalidInput);
            if (changes.Count > 0) category.ChangeCells(changes);
        });
        return new ChangePresetCellsResult(result, problems);
    }

    /// <summary>入力できるセルなら現在の値、できなければ null。スキーマにある位置で、プリセットの内容が入力を受け付けるセルに限る。</summary>
    private static CellValue? EditableValue(SchemaDefinition? schema, Preset preset, CellInput input)
    {
        if (schema is null || !schema.Defines(input.Group, input.Row, input.Column) || preset.Content is null) return null;
        CellSlot cell = preset.Content.CellAt(input.Group, input.Row, input.Column);
        return cell.Editability == CellEditability.Editable ? cell.Value : null;
    }
}

/// <summary>
/// 入力文字列からセルの値への変換。スキーマに型がないので、いまの値の型で判定する。
/// 文字列のセルは入力のまま文字列にし、暗黙に数値へ変えない。数値・未入力のセルは JSON の数値表記だけを受け付け、
/// 全角の数字・記号は半角にする。桁区切りや小数点のカンマなど曖昧な表記は受け付けない（0 に置き換えない）。
/// 数値は入力した表記のまま保存する（60.0 を 60 にしない）。空欄は未入力（null）とする。
/// </summary>
public static partial class CellInputRules
{
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex JsonNumber();

    public static CellInputProblemKind? TryConvert(CellValue current, string? text, out CellValue value)
    {
        text ??= "";
        if (string.IsNullOrWhiteSpace(text))
        {
            // 欠落は欠落のまま（値を書き足さない）
            value = current.Kind == CellValueKind.Missing ? CellValue.Missing : CellValue.Null;
            return null;
        }

        switch (current.Kind)
        {
            case CellValueKind.Text:
                value = CellValue.FromText(text);
                return null;
            case CellValueKind.Boolean:
                string flag = text.Trim();
                if (string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)) value = CellValue.FromBoolean(true);
                else if (string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)) value = CellValue.FromBoolean(false);
                else
                {
                    value = current;
                    return CellInputProblemKind.NotABoolean;
                }
                return null;
            case CellValueKind.Number or CellValueKind.Missing or CellValueKind.Null:
                string literal = ToHalfWidth(text.Trim());
                if (!JsonNumber().IsMatch(literal))
                {
                    value = current;
                    return CellInputProblemKind.NotANumber;
                }
                value = current.Kind == CellValueKind.Number && current.Text == literal ? current : CellValue.FromNumberLiteral(literal);
                return null;
            default:
                value = current;
                return CellInputProblemKind.NotEditable;
        }
    }

    // 全角英数字・記号（U+FF01〜U+FF5E）と全角空白、数学のマイナスを半角にする
    private static string ToHalfWidth(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char c in text)
        {
            builder.Append(c switch
            {
                >= '！' and <= '～' => (char)(c - 0xFEE0),
                '　' => ' ',
                '−' => '-',
                _ => c,
            });
        }
        return builder.ToString().Trim();
    }
}
