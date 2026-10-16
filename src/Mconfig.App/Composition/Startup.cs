using System.Globalization;
using System.Reflection;
using Mconfig.App.Resources;
using Mconfig.App.Views;
using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Infrastructure.Exports;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Logging;
using Mconfig.Infrastructure.Persistence;
using Mconfig.Application.Assignments.Commands;
using Mconfig.Application.Assignments.Queries;
using Mconfig.Application.History.Commands;
using Mconfig.Application.History.Queries;
using Mconfig.Application.Releases.Commands;
using Mconfig.Application.Releases.Queries;
using Mconfig.Application.Workspaces.Commands;
using Mconfig.Application.Workspaces.Queries;
using Mconfig.Infrastructure.Releases;
using Mconfig.Presentation.Assignments;
using Mconfig.Presentation.Combinations;
using Mconfig.Presentation.Common;
using Mconfig.Presentation.DataManagement;
using Mconfig.Presentation.Workspaces;
using Serilog;

namespace Mconfig.App.Composition;

/// <summary>
/// 起動と手動配線。具体実装の生成はここと Program.cs に限る。
/// 以前の起動オプション --new-ui は、指定されても無視する（新しい画面が既定）。
/// </summary>
internal static class Startup
{
    public static void Run(string[] args)
    {
        string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "ja";
        UiText.Culture = CultureInfo.GetCultureInfo(language);

        string? root = WorkspaceLocation.Prepare(WorkspaceLocation.Resolve(args));
        if (root is null) return;
        WorkspaceLayout layout = new(root);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(layout.LogsDirectory, "mconfig_.log"),
                // 日ごとに切り替え、30 日を過ぎたものを消す（起動しない日があっても 30 日で消えるよう、日数でも区切る）
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                retainedFileTimeLimit: TimeSpan.FromDays(30),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u5}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("Application starting ({Version})", Assembly.GetExecutingAssembly().GetName().Version);
        HandleUnexpectedErrors();

        // 編集の排他を取れなければ、別のアプリが編集中なので読取専用で開く
        using WorkspaceLock? editLock = WorkspaceLock.TryAcquire(layout);
        if (editLock is null)
            MessageBox.Show(UiText.Get("Startup_ReadOnlyNotice"), UiText.Get("App_Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);

        try
        {
            GitDataRepository repository = new(layout);
            JsonWorkspaceStore store = new(layout, editLock, repository);
            LoadedWorkspace? loaded = null;
            while (loaded is null)
            {
                try
                {
                    loaded = store.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (WorkspaceUnreadableException ex) when (editLock is not null)
                {
                    // 読み込めるバックアップ・過去コミットから選んで復元し、読み直す
                    if (!RestoreFromBackup(new WorkspaceBackups(layout, editLock, repository), ex)) throw;
                }
            }
            WorkspaceLocation.Remember(root);
            EditSession session = new(loaded, new SerilogOperationLog(Log.Logger));
            // 起動時の構造・参照エラーは読取専用で開き、直す箇所は「保存と履歴」の検証結果で案内する
            if (!loaded.ReadOnly && session.Status.IsReadOnly)
            {
                Log.Warning("The workspace opens read-only because the loaded content has errors");
                MessageBox.Show(UiText.Get("Startup_InvalidDataReadOnly"), UiText.Get("App_Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            GitRevisionStore revisions = new(repository, editLock);
            ZipReleaseArchive archive = new(repository, editLock,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

            DisplayLanguage displayLanguage = new(language);
            using MainForm form = new(displayLanguage, root, session.Status.IsReadOnly);
            RestoreLayout(form);
            form.FormClosed += (_, _) => RememberLayout(form);
            ControlDispatcher ui = new(form);
            IQueryRunner runner = BackgroundQueryRunner.Instance;
            SaveDraftHandler saveDraft = new(session, store);
            DiscardEditsHandler discard = new(session, store);
            using CombinationListPresenter list = new(form.CombinationList, session, runner, ui, displayLanguage);
            using PresetComparisonPresenter comparison = new(form.Comparison, session, runner, ui, displayLanguage);
            using PresetComparisonPresenter presetManagement = new(form.Assignments.PresetManagement, session, runner, ui, displayLanguage);
            _ = presetManagement.SetActiveAsync(false);
            form.Assignments.CategoryContextChanged += (_, _) =>
                _ = presetManagement.SelectManagementCategoryAsync(form.Assignments.CurrentCategory, form.Assignments.CurrentCategoryLabel);
            form.Assignments.PresetManagement.VisibleChanged += (_, _) =>
                _ = presetManagement.SetActiveAsync(form.Assignments.PresetManagement.Visible);
            using AssignmentPresenter assignments = new(form.Assignments, session, runner, ui, displayLanguage);
            _ = assignments.SetActiveAsync(false);
            form.Assignments.VisibleChanged += (_, _) => _ = assignments.SetActiveAsync(form.Assignments.Visible);
            // 検証を伴う数え直しは、連続した編集をまとめて 300ms 後に行う
            TimeSpan revalidateDelay = TimeSpan.FromMilliseconds(300);
            using WorkspaceStatusPresenter status = new(form.Status, session, runner, ui, displayLanguage, revalidateDelay);
            // 保存と履歴・出力の画面は、操作を一つずつ実行する実行役を共有する
            using DataOperationGate gate = new(session, runner, ui);
            using SaveHistoryPresenter data = new(form.History, session, new SaveHistoryHandlers(
                    new ValidateWorkspaceHandler(session),
                    saveDraft,
                    discard,
                    new CommitWorkspaceHandler(session, store, revisions),
                    new HistoryQueryHandlers(session, revisions),
                    new RestoreRevisionHandler(session, store, revisions))
                {
                    Uncovered = new ExportUncoveredCombinationsHandler(session, new CsvFileWriter()),
                    Publish = new Mconfig.Application.Releases.Commands.PublishWorkspaceHandler(session, store, new GitPublicationStore(repository, editLock, archive)),
                },
                gate, runner, ui, displayLanguage, Environment.UserName, revalidateDelay);
            form.UseLeaveGuard(new LeaveGuard(session, saveDraft, discard, form, warnOnUncommitted: false));
            form.SaveRequested += (_, _) => _ = data.SaveDraftAsync();
            form.ValidateRequested += (_, _) => _ = data.ValidateAsync();

            // 検証結果の該当箇所へ: 直す画面を開いて、そのカテゴリを選ぶ
            data.NavigationRequested += (_, target) => form.OpenScreen(
                target.Screen == NavigationScreen.Assignments ? AppScreen.Assignments : AppScreen.Combinations,
                () =>
                {
                    if (target.Screen == NavigationScreen.Assignments)
                    {
                        form.Assignments.ShowRules();
                        _ = assignments.SelectAsync(target.Category);
                    }
                    else list.SelectCategory(target.Category);
                });
            list.ContextChanged += comparison.OnContextChanged;
            list.AssignmentNavigationRequested += (_, target) => form.OpenScreen(AppScreen.Assignments,
                () =>
                {
                    form.Assignments.ShowRules();
                    _ = assignments.SelectForCombinationAsync(target.Category, target.Combination);
                });
            form.Shown += (_, _) =>
            {
                _ = list.RefreshAsync();
                _ = status.RefreshAsync();
                _ = data.LoadAsync();

            };
            System.Windows.Forms.Application.Run(form);
        }
        catch (WorkspaceUnreadableException ex)
        {
            Log.Error(ex, "The workspace cannot be read: {Problems}", string.Join("; ", ex.Problems));
            MessageBox.Show(UiText.Format("Startup_Unreadable", string.Join(Environment.NewLine, ex.Problems)),
                UiText.Get("Startup_Title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (WorkspaceRecoveryRequiredException ex)
        {
            Log.Error(ex, "The workspace needs recovery");
            MessageBox.Show(UiText.Format("Startup_Recovery", ex.Message), UiText.Get("Startup_Title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show(UiText.Format("Startup_Failed", ex.Message), UiText.Get("Startup_Title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Log.Information("Application exiting ({Version})", Assembly.GetExecutingAssembly().GetName().Version);
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// ワークスペースが読めないとき、読込・検証できたバックアップ（設定ファイルだけ）と過去コミット（一式）を示し、選んだもので置き換える。
    /// 書き換える現ファイルは復旧素材へ退避する。復元した内容は未確定の変更（W）として開き、利用者に変更理由付きの確定保存を求める。
    /// 候補がない、利用者がやめた場合は false。候補は検証済みなので、復元の後の読込は失敗しない（繰り返し尋ねない）。
    /// </summary>
    private static bool RestoreFromBackup(WorkspaceBackups backups, WorkspaceUnreadableException problem)
    {
        IReadOnlyList<BackupCandidate> candidates = backups.FindCandidates();
        Log.Error(problem, "The workspace cannot be read; {Count} recovery candidates: {Problems}",
            candidates.Count, string.Join("; ", problem.Problems));
        if (candidates.Count == 0) return false;

        int? chosen = RecoveryDialog.Choose(string.Join(Environment.NewLine, problem.Problems), candidates.Select(Describe).ToArray());
        if (chosen is not { } index) return false;
        Guid operationId = Guid.NewGuid();
        backups.Restore(candidates[index], operationId);
        Log.Information("Restored the workspace from {Source} {Location} ({OperationId})",
            candidates[index].Source, candidates[index].Location, operationId);
        MessageBox.Show(UiText.Get("Recovery_Restored"), UiText.Get("App_Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    private static string Describe(BackupCandidate candidate)
    {
        string when = candidate.SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        return candidate.Source == BackupSource.Commit
            ? UiText.Format("Recovery_FromCommit", when, candidate.Location[..Math.Min(7, candidate.Location.Length)],
                candidate.Description?.Split('\n')[0].Trim() ?? "")
            : UiText.Format("Recovery_FromFile", when, candidate.Location);
    }

    // 「組み合わせと値」の上下の境界の位置と、値の表を隠しているか
    private static void RestoreLayout(MainForm form)
    {
        if (double.TryParse(AppSettingsFile.Get("valuesSplitRatio"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio))
            form.ValuesSplitRatio = ratio;
        form.ValuesHidden = AppSettingsFile.Get("valuesHidden") == "true";
    }

    private static void RememberLayout(MainForm form) => AppSettingsFile.Set(new Dictionary<string, string>
    {
        ["valuesSplitRatio"] = form.ValuesSplitRatio.ToString("0.###", CultureInfo.InvariantCulture),
        ["valuesHidden"] = form.ValuesHidden ? "true" : "false",
    });

    /// <summary>
    /// 予期しない例外は、スタックトレースを含めてローカルのログへ残し、画面には安全な概要と次の操作だけを示す。
    /// </summary>
    private static void HandleUnexpectedErrors()
    {
        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            Log.Error(e.Exception, "Unexpected error on the UI thread");
            MessageBox.Show(UiText.Get("Error_Unexpected"), UiText.Get("App_Title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unexpected error; the application is terminating");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved background error");
            e.SetObserved();
        };
    }
}
