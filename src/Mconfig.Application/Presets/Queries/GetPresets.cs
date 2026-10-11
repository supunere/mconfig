using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Presets.Queries;

public sealed record GetPresetsQuery(string Category);

/// <summary>プリセットと、割り当てからの参照件数（リネーム・削除の確認に使う）。</summary>
public sealed record PresetSummary(string Name, int ReferenceCount);

public sealed record PresetListReadModel(string Category, SessionVersion Version, bool Available, IReadOnlyList<PresetSummary> Presets);

public sealed class GetPresetsHandler(EditSession session)
{
    public PresetListReadModel Handle(GetPresetsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        (PresetListReadModel model, SessionVersion version) = session.Read(content =>
        {
            if (!content.Settings.Categories.TryGetValue(query.Category, out CategoryData? category) || category.IsDiagnosticOnly)
                return new PresetListReadModel(query.Category, SessionVersion.Initial, false, []);

            return new PresetListReadModel(query.Category, SessionVersion.Initial, true, category.Presets
                .Select(p => new PresetSummary(p.Name,
                    category.Assignments.Count(a => string.Equals(a.Preset, p.Name, StringComparison.Ordinal))))
                .ToArray());
        });
        return model with { Version = version };
    }
}
