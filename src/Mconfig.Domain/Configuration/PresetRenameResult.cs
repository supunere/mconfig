namespace Mconfig.Domain.Configuration;

public readonly record struct PresetRenameResult(bool Changed, string NewName, int ReferenceCount);
