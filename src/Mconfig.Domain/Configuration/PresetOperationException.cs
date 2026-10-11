namespace Mconfig.Domain.Configuration;

public sealed class PresetOperationException : InvalidOperationException
{
    public PresetOperationException(PresetOperationFailure failure) : base(failure.ToString()) =>
        Failure = failure;

    public PresetOperationFailure Failure { get; }
}
