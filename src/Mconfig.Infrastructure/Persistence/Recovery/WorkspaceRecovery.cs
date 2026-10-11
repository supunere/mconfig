using Serilog;

namespace Mconfig.Infrastructure.Persistence.Recovery;

public enum RecoveryOutcome
{
    /// <summary>対象ファイルに触れる前に止まっていたので、一時ファイルを片付けただけ。</summary>
    NotApplied,
    /// <summary>全対象が新版になっていたので、新版を採用した（未コミットとして再開する）。</summary>
    Completed,
    /// <summary>適用完了を確認できないので、旧版一式へ戻した。</summary>
    RolledBack,
    /// <summary>外部変更・素材の欠損・記録の破損のため自動では直さない。読取専用で止まる。</summary>
    Blocked,
}

public sealed record RecoveryResult(Guid? OperationId, string Directory, RecoveryOutcome Outcome, string? Problem);

/// <summary>
/// 未完了の書込操作の復旧。通常の読込より先に記録を調べ、
/// 全対象が新版なら新版を採用し、それ以外はハッシュを照合して旧版一式へ戻す。各手順は再実行できる。
/// 判定できない場合は何も上書きせず Blocked を返す。記録と素材は削除しない。
/// </summary>
internal sealed class WorkspaceRecovery(WorkspaceLayout layout, Action<FaultPoint, string?>? fault = null)
{
    /// <summary>完了していない操作があるか（読取専用のアプリは復旧せず、これで読込を止める）。</summary>
    public bool HasPending() => Pending().Any();

    public IReadOnlyList<RecoveryResult> RecoverAll() =>
        Pending().Select(p => p.Journal is null
                ? new RecoveryResult(null, p.Directory, RecoveryOutcome.Blocked, p.Problem)
                : Recover(p.Journal))
            .ToArray();

    public RecoveryResult Recover(OperationJournal journal)
    {
        string directory = OperationJournal.DirectoryOf(layout, journal.OperationId);
        if (journal.IsFinished) return new RecoveryResult(journal.OperationId, directory, RecoveryOutcome.NotApplied, null);

        if (journal.HasCompleteFileSet)
        {
            string[] unexpected = layout.ListDataFiles()
                .Except(journal.Entries.Select(e => e.Path), StringComparer.Ordinal)
                .ToArray();
            if (unexpected.Length > 0)
                return Blocked(journal, directory,
                    $"unrecorded data files were added: {string.Join(", ", unexpected)}");
        }

        if (journal.State != OperationState.Applying)
        {
            DeleteTemps(journal);
            journal.With(OperationState.RolledBack, layout);
            return Logged(new RecoveryResult(journal.OperationId, directory, RecoveryOutcome.NotApplied, null));
        }

        string?[] current = journal.Entries.Select(e => WorkspaceFileSet.HashOf(layout.FullPath(e.Path))).ToArray();
        if (journal.Entries.Select((e, i) => current[i] == e.NewHash).All(b => b) &&
            CurrentPathsMatch(journal, useNewHashes: true))
        {
            DeleteTemps(journal);
            journal.With(OperationState.Completed, layout);
            return Logged(new RecoveryResult(journal.OperationId, directory, RecoveryOutcome.Completed, null));
        }

        // 先に全対象を判定し、一つでも判定できなければ何も変えない
        List<(OperationEntry Entry, bool Restore)> plan = [];
        for (int i = 0; i < journal.Entries.Count; i++)
        {
            OperationEntry entry = journal.Entries[i];
            if (current[i] == entry.OldHash)
            {
                plan.Add((entry, false));
            }
            else if (current[i] == entry.NewHash || current[i] is null)
            {
                if (entry.OldHash is not null &&
                    WorkspaceFileSet.HashOf(journal.OldCopyPath(layout, entry.Path)) != entry.OldHash)
                    return Blocked(journal, directory, $"{entry.Path}: the saved previous version is missing or damaged");
                plan.Add((entry, true));
            }
            else
            {
                return Blocked(journal, directory, $"{entry.Path}: matches neither the previous nor the saved version");
            }
        }

        foreach ((OperationEntry entry, bool restore) in plan.Where(p => p.Restore))
        {
            fault?.Invoke(FaultPoint.BeforeRestore, entry.Path);
            RestoreOld(journal, entry);
            fault?.Invoke(FaultPoint.AfterRestore, entry.Path);
        }
        foreach (OperationEntry entry in journal.Entries)
        {
            if (WorkspaceFileSet.HashOf(layout.FullPath(entry.Path)) != entry.OldHash)
                return Blocked(journal, directory, $"{entry.Path}: could not be restored to the previous version");
        }
        if (!CurrentPathsMatch(journal, useNewHashes: false))
            return Blocked(journal, directory, "the workspace file set changed while it was being recovered");
        DeleteTemps(journal);
        journal.With(OperationState.RolledBack, layout);
        return Logged(new RecoveryResult(journal.OperationId, directory, RecoveryOutcome.RolledBack, null));
    }

    private bool CurrentPathsMatch(OperationJournal journal, bool useNewHashes)
    {
        // Version 1 は差分だけを記録していたため、集合全体の比較はできない。
        if (!journal.HasCompleteFileSet) return true;
        HashSet<string> expected = journal.Entries
            .Where(e => (useNewHashes ? e.NewHash : e.OldHash) is not null)
            .Select(e => e.Path)
            .ToHashSet(StringComparer.Ordinal);
        return expected.SetEquals(layout.ListDataFiles());
    }

    private void RestoreOld(OperationJournal journal, OperationEntry entry)
    {
        string target = layout.FullPath(entry.Path);
        if (entry.OldHash is null)
        {
            // 追加したファイルは削除せず、取り下げた素材として退避する
            if (File.Exists(target))
            {
                string copy = journal.RolledBackCopyPath(layout, entry.Path);
                if (!File.Exists(copy))
                    FileSetTransaction.MoveToCopy(target, copy);
                else if (WorkspaceFileSet.HashOf(copy) == WorkspaceFileSet.HashOf(target))
                    File.Delete(target); // 前回の復旧で同じ内容を退避済み
                else
                    throw new IOException($"{entry.Path}: a different rolled-back copy already exists.");
            }
            return;
        }

        // 退避した旧版は素材として残し、複製を一時ファイル経由で戻す
        string temp = target + $".{journal.OperationId:N}.restore.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(journal.OldCopyPath(layout, entry.Path), temp, overwrite: true);
        using (FileStream stream = new(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            stream.Flush(flushToDisk: true);
        if (WorkspaceFileSet.HashOf(temp) != entry.OldHash)
            throw new IOException($"The previous version of {entry.Path} could not be copied.");
        File.Move(temp, target, overwrite: true);
    }

    private void DeleteTemps(OperationJournal journal)
    {
        foreach (OperationEntry entry in journal.Entries)
        {
            foreach (string temp in new[]
                     {
                         journal.TempPath(layout, entry.Path),
                         layout.FullPath(entry.Path) + $".{journal.OperationId:N}.restore.tmp",
                     })
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    private IEnumerable<(string Directory, OperationJournal? Journal, string? Problem)> Pending()
    {
        if (!Directory.Exists(layout.RecoveryDirectory)) yield break;
        foreach (string directory in Directory.EnumerateDirectories(layout.RecoveryDirectory).Order(StringComparer.Ordinal))
        {
            OperationJournal? journal;
            string? problem = null;
            try
            {
                journal = OperationJournal.Load(directory, layout);
            }
            catch (InvalidDataException ex)
            {
                journal = null;
                problem = ex.Message;
            }
            // 記録を作る前に止まった操作（記録なし）は、対象にも一時ファイルにも触れていない
            if (journal is null && problem is null) continue;
            if (journal is { IsFinished: true }) continue;
            yield return (directory, journal, problem);
        }
    }

    private static RecoveryResult Blocked(OperationJournal journal, string directory, string problem) =>
        Logged(new RecoveryResult(journal.OperationId, directory, RecoveryOutcome.Blocked, problem));

    private static RecoveryResult Logged(RecoveryResult result)
    {
        if (result.Outcome == RecoveryOutcome.Blocked)
            Log.Error("Workspace recovery blocked for {OperationId}: {Problem}", result.OperationId, result.Problem);
        else
            Log.Warning("Workspace operation {OperationId} recovered: {Outcome}", result.OperationId, result.Outcome);
        return result;
    }
}
