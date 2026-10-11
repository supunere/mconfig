namespace Mconfig.Domain.Configuration;

/// <summary>プリセット・セル・割り当ての業務操作を拒否した理由。</summary>
public enum PresetOperationFailure
{
    InvalidName,
    DuplicateName,
    InvalidCategory,
    MissingPreset,
    /// <summary>割り当てから参照されているため削除できない。</summary>
    InUse,
    /// <summary>値を入力できないセル（グループがない・未使用、行が理解できない構造、値が理解できない）。</summary>
    CellNotEditable,
    /// <summary>入力の一部が変換・検証できないので、何も変えなかった（個々の理由は呼出し側が返す）。</summary>
    InvalidInput,
    /// <summary>指定した割り当て行がない。</summary>
    MissingAssignment,
    /// <summary>割り当ての条件に、仕様にないキーまたは値がある。</summary>
    InvalidCondition,
}
