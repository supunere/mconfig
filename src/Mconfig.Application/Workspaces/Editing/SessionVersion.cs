namespace Mconfig.Application.Workspaces.Editing;

/// <summary>
/// 編集セッションの版。E/W/C や読取専用・復旧待ち・保存中など、外部から観測できる状態が変わるたびに進む。
/// 未保存・未コミットの判定には使わない（判定は内容の指紋で行う）。
/// </summary>
public readonly record struct SessionVersion(long Value) : IComparable<SessionVersion>
{
    public static SessionVersion Initial { get; } = new(0);

    public SessionVersion Next() => new(Value + 1);

    public int CompareTo(SessionVersion other) => Value.CompareTo(other.Value);

    public static bool operator <(SessionVersion left, SessionVersion right) => left.Value < right.Value;
    public static bool operator >(SessionVersion left, SessionVersion right) => left.Value > right.Value;
}
