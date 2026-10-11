namespace Mconfig.Presentation.Common;

/// <summary>UI スレッドへ処理を戻す。WinForms では Control.BeginInvoke 相当、試験では同期実行。</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Query をバックグラウンドで実行する。試験では完了の順序を制御できる実装に差し替える。</summary>
public interface IQueryRunner
{
    Task<T> RunAsync<T>(Func<T> query);
}

public sealed class BackgroundQueryRunner : IQueryRunner
{
    public static BackgroundQueryRunner Instance { get; } = new();

    public Task<T> RunAsync<T>(Func<T> query) => Task.Run(query);
}

/// <summary>
/// 画面の要求識別子。SessionVersion とは別に、同じ版でも過去の選択・フィルター要求に対応する結果を捨てるために使う。
/// </summary>
public sealed class RequestTracker
{
    private long _latest;

    public long Next() => Interlocked.Increment(ref _latest);

    public bool IsLatest(long request) => Interlocked.Read(ref _latest) == request;
}

/// <summary>表示言語。定義の表示名はこの言語 → ja → キーの順で解決する。</summary>
public sealed class DisplayLanguage
{
    public DisplayLanguage(string code) => Code = code ?? throw new ArgumentNullException(nameof(code));

    public string Code { get; private set; }

    public event EventHandler? Changed;

    public void Change(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code == Code) return;
        Code = code;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
