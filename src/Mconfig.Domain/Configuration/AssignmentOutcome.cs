namespace Mconfig.Domain.Configuration;

public enum AssignmentOutcomeKind { Unassigned, Disabled, Assigned }

/// <summary>ある組み合わせに対する割り当て結果。既定値は Unassigned。</summary>
public readonly record struct AssignmentOutcome
{
    private AssignmentOutcome(AssignmentOutcomeKind kind, string? presetName)
    {
        Kind = kind;
        PresetName = presetName;
    }

    public AssignmentOutcomeKind Kind { get; }

    /// <summary>Assigned のときだけ値を持つ。</summary>
    public string? PresetName { get; }

    public static AssignmentOutcome Unassigned { get; } = new(AssignmentOutcomeKind.Unassigned, null);

    public static AssignmentOutcome Disabled { get; } = new(AssignmentOutcomeKind.Disabled, null);

    public static AssignmentOutcome Assigned(string presetName) =>
        new(AssignmentOutcomeKind.Assigned, presetName ?? throw new ArgumentNullException(nameof(presetName)));
}
