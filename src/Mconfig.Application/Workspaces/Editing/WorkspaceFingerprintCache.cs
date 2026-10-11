using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Workspaces.Editing;

/// <summary>
/// 不変のプリセット・割り当ての正規化UTF-8区間を再利用する。編集時は変更したプリセットだけセルを走査する。
/// 保持は直近の内容に含まれる要素だけで、過去の版や別セッションを保持しない。
/// 呼出しはセッションのロックで直列化する。指紋の形式は <see cref="WorkspaceFingerprints.Of(WorkspaceContent)"/> と同じ。
/// </summary>
public sealed class WorkspaceFingerprintCache
{
    private Dictionary<Preset, byte[]> _presets = new(ReferenceEqualityComparer.Instance);
    private Dictionary<Assignment, byte[]> _assignments = new(ReferenceEqualityComparer.Instance);

    public ContentFingerprint Of(WorkspaceContent content)
    {
        Dictionary<Preset, byte[]> presets = new(ReferenceEqualityComparer.Instance);
        Dictionary<Assignment, byte[]> assignments = new(ReferenceEqualityComparer.Instance);
        ContentFingerprint fingerprint = WorkspaceFingerprints.Of(content,
            p => Reuse(p, _presets, presets, WorkspaceFingerprints.BytesOf),
            a => Reuse(a, _assignments, assignments, WorkspaceFingerprints.BytesOf));
        _presets = presets;
        _assignments = assignments;
        return fingerprint;
    }

    private static byte[] Reuse<T>(T value, Dictionary<T, byte[]> previous, Dictionary<T, byte[]> current, Func<T, byte[]> serialize) where T : class
    {
        if (current.TryGetValue(value, out byte[]? bytes)) return bytes;
        if (!previous.TryGetValue(value, out bytes)) bytes = serialize(value);
        current.Add(value, bytes);
        return bytes;
    }
}
