namespace Mconfig.Domain.Configuration;

/// <summary>
/// 全件検証（割り当ての被覆）の進捗。Checked は調べた組み合わせの数、Total は調べる数（組み合わせの数 × カテゴリの数、
/// long に収まらなければ long の最大値）。
/// </summary>
public readonly record struct CoverageProgress(long Checked, long Total)
{
    /// <summary>進捗を知らせる間隔（調べた組み合わせの数）。</summary>
    public const int ReportInterval = 8_192;

    public bool Completed => Checked >= Total;
}
