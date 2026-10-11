using Mconfig.Application.Ports;
using Mconfig.Infrastructure.Persistence.Json;

namespace Mconfig.Infrastructure.Persistence.Recovery;

/// <summary>対象ファイル一つの変更。NewContent が null なら削除、OldHash が null なら追加。</summary>
internal sealed record FileChange(string Path, string? OldHash, byte[]? NewContent);

/// <summary>故障注入の位置（試験用）。Path は対象ファイルごとの位置でだけ渡す。</summary>
internal enum FaultPoint
{
    JournalCreated,
    TempWritten,
    Prepared,
    BeforeApply,
    AfterApply,
    Applied,
    BeforeRestore,
    AfterRestore,
    SnapshotRead,
    SnapshotFileRead,
}

/// <summary>
/// データファイル一式の書換え。ファイル置換・複数ファイル更新を原子的とはみなさず、
/// 記録 → 一時ファイルの書込・Flush・再読込検証 → 置換（旧版は復旧素材へ退避）・追加・削除 → 結果照合 → 完了記録、
/// の順に進める。途中で止まった操作は <see cref="WorkspaceRecovery"/> が記録から判定する。
/// </summary>
internal sealed class FileSetTransaction(WorkspaceLayout layout, Action<FaultPoint, string?>? fault = null, Func<string, Stream>? openTemporaryFile = null)
{
    public OperationJournal Apply(
        Guid operationId, string kind, IReadOnlyList<FileChange> changes,
        string? oldFingerprint, string? newFingerprint, CancellationToken cancellationToken, string? revision = null)
    {
        if (changes.Count == 0 || changes.All(c => c.OldHash == (c.NewContent is null ? null : WorkspaceFileSet.Hash(c.NewContent))) ||
            changes.Any(c => !WorkspaceLayout.IsDataFile(c.Path)) ||
            changes.Select(c => c.Path).Distinct(StringComparer.Ordinal).Count() != changes.Count)
            throw new ArgumentException("Changes must be distinct workspace data files.", nameof(changes));

        OperationJournal journal = new(OperationJournal.CurrentVersion, operationId, kind, DateTimeOffset.UtcNow, OperationState.Preparing,
            oldFingerprint, newFingerprint,
            changes.Select(c => new OperationEntry(c.Path, c.OldHash,
                c.NewContent is null ? null : WorkspaceFileSet.Hash(c.NewContent))).ToArray(), revision);
        if (Directory.Exists(OperationJournal.DirectoryOf(layout, operationId)))
            throw new InvalidOperationException($"Operation {operationId} has already been recorded.");
        journal.Save(layout);
        fault?.Invoke(FaultPoint.JournalCreated, null);

        // 1. 一時ファイルを対象と同じディレクトリに書き、Flush 後に読み直して検証する
        foreach (FileChange change in changes.Where(IsChanged).Where(c => c.NewContent is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string temp = journal.TempPath(layout, change.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            using (Stream stream = openTemporaryFile?.Invoke(temp) ?? new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(change.NewContent);
                if (stream is FileStream fileStream) fileStream.Flush(flushToDisk: true);
                else stream.Flush();
            }
            byte[] written = WorkspaceFileSet.ReadBytes(temp);
            if (WorkspaceFileSet.Hash(written) != WorkspaceFileSet.Hash(change.NewContent!))
                throw new IOException($"The temporary file for {change.Path} did not read back as written.");
            JsonText.Parse(written);
            fault?.Invoke(FaultPoint.TempWritten, change.Path);
        }
        journal = journal.With(OperationState.Prepared, layout);
        fault?.Invoke(FaultPoint.Prepared, null);

        // 2. 対象に触れる直前に、全対象が記録した旧版のままか確かめる（ここより後は取り消さない）
        cancellationToken.ThrowIfCancellationRequested();
        foreach (FileChange change in changes)
        {
            if (WorkspaceFileSet.HashOf(layout.FullPath(change.Path)) != change.OldHash)
                throw new WorkspaceConflictException($"{change.Path} changed outside the application.");
        }
        journal = journal.With(OperationState.Applying, layout);

        // 3. 置換・追加・削除。置換と削除の旧版は復旧素材として退避する
        foreach (FileChange change in changes.Where(IsChanged))
        {
            fault?.Invoke(FaultPoint.BeforeApply, change.Path);
            string target = layout.FullPath(change.Path);
            if (change.NewContent is null)
            {
                MoveToCopy(target, journal.OldCopyPath(layout, change.Path));
            }
            else if (change.OldHash is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(journal.TempPath(layout, change.Path), target, overwrite: false);
            }
            else
            {
                string backup = journal.OldCopyPath(layout, change.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Replace(journal.TempPath(layout, change.Path), target, backup, ignoreMetadataErrors: true);
            }
            fault?.Invoke(FaultPoint.AfterApply, change.Path);
        }

        // 4. 結果を照合してから完了を記録する
        foreach (OperationEntry entry in journal.Entries)
        {
            if (WorkspaceFileSet.HashOf(layout.FullPath(entry.Path)) != entry.NewHash)
                throw new IOException($"{entry.Path} does not contain the saved content.");
        }
        fault?.Invoke(FaultPoint.Applied, null);
        return journal.With(OperationState.Completed, layout);
    }

    private static bool IsChanged(FileChange change) =>
        change.OldHash != (change.NewContent is null ? null : WorkspaceFileSet.Hash(change.NewContent));

    internal static void MoveToCopy(string source, string copy)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Move(source, copy, overwrite: false);
    }
}
