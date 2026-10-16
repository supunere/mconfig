namespace Mconfig.Presentation.DataManagement;

/// <summary>保存・発行・履歴の操作で共有する状態・進捗・結果の表示契約。</summary>
public interface IDataOperationView
{
    /// <summary>実行中の検証を中止する。書込開始後の発行は中断しない。</summary>
    event EventHandler? CancelRequested;
    void ShowState(DataStateDisplay state);
    void ShowProgress(ProgressDisplay? progress);
    void ShowOperationResult(OperationDisplay result);
}
