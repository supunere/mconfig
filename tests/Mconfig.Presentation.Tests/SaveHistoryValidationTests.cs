using System.Reflection;
using Mconfig.Application.History.Commands;
using Mconfig.Application.History.Queries;
using Mconfig.Application.Presets.Commands;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Commands;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Application.Workspaces.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Persistence;
using Mconfig.Presentation.Common;
using Mconfig.Presentation.DataManagement;
using Xunit;

namespace Mconfig.Presentation.Tests;

public sealed class SaveHistoryValidationTests
{
    [Fact]
    public async Task CommittingValidatedContentKeepsValidationCurrent()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Presenter.ValidateAsync();
        Assert.False(f.View.Validation!.Stale);

        var result = await f.Presenter.CommitAsync("Record initial validated sample");

        Assert.Equal(OperationStatus.Succeeded, result!.Status);
        Assert.False(f.Session.Status.HasUncommittedChanges);
        Assert.All(f.View.Validations, validation => Assert.False(validation.Stale));
    }

    [Fact]
    public async Task EditingInvalidatesValidationAndReturningToValidatedContentRestoresIt()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Presenter.ValidateAsync();

        f.ChangeRate("3600");
        Assert.True(f.View.Validation!.Stale);
        f.ChangeRate("3200");
        Assert.False(f.View.Validation!.Stale);

        f.ChangeRate("3600");
        await f.Presenter.ValidateAsync();
        var result = await f.Presenter.SaveDraftAsync();
        Assert.Equal(OperationStatus.Succeeded, result!.Status);
        Assert.False(f.View.Validation!.Stale);

        f.ChangeRate("4000");
        Assert.True(f.View.Validation!.Stale);
        await f.Presenter.SaveDraftAsync();
        Assert.True(f.View.Validation!.Stale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationCompletingAfterSessionChangeChecksContent(bool editContent)
    {
        using Fixture f = await Fixture.CreateAsync();
        f.ChangeRate("3600");
        // Simulate a state change after the query captured its result, before the UI receives it.
        f.Runner.AfterValidation = () =>
        {
            if (editContent) f.ChangeRate("4000");
            else f.SaveDraft.HandleAsync(new SaveDraftCommand(f.Session.Status.Version)).GetAwaiter().GetResult();
        };

        await f.Presenter.ValidateAsync();

        Assert.Equal(editContent, f.View.Validation!.Stale);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly WorkspaceLock _editLock;
        private readonly DataOperationGate _gate;
        public EditSession Session { get; }
        public SaveDraftHandler SaveDraft { get; }
        public SaveHistoryPresenter Presenter { get; }
        public RecordingView View { get; }
        public ImmediateRunner Runner { get; } = new();

        private Fixture(string root, WorkspaceLock editLock, JsonWorkspaceStore store, EditSession session, GitRevisionStore revisions)
        {
            _root = root;
            _editLock = editLock;
            Session = session;
            SaveDraft = new(session, store);
            ISaveHistoryView view = DispatchProxy.Create<ISaveHistoryView, RecordingView>();
            View = (RecordingView)view;
            ImmediateDispatcher ui = new();
            _gate = new(session, Runner, ui);
            Presenter = new(view, session, new SaveHistoryHandlers(new(session), SaveDraft, new(session, store),
                new(session, store, revisions), new(session, revisions), new(session, store, revisions)),
                _gate, Runner, ui, new DisplayLanguage("ja"), "test");
        }

        public static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "mconfig-validation-tests", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(AppContext.BaseDirectory, "TestData");
            foreach (string file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
            {
                string target = Path.Combine(root, "data", Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            WorkspaceLayout layout = new(root);
            WorkspaceLock editLock = WorkspaceLock.TryAcquire(layout)!;
            GitDataRepository repository = new(layout);
            JsonWorkspaceStore store = new(layout, editLock, repository);
            return new(root, editLock, store, new EditSession(await store.LoadAsync(default)), new(repository, editLock));
        }

        public void ChangeRate(string value)
        {
            var result = new ChangePresetCellHandler(Session).Handle(new("memory", "メモリ・8GB構成", "modules", "moduleA",
                "transferRateMTs", CellValue.FromNumberLiteral(value), Session.Status.Version));
            Assert.Equal(CommandStatus.Applied, result.Status);
        }

        public void Dispose()
        {
            Presenter.Dispose();
            _gate.Dispose();
            _editLock.Dispose();
            // Git marks object files read-only on Windows.
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
    }

    public class RecordingView : DispatchProxy
    {
        public ValidationDisplay? Validation { get; private set; }
        public List<ValidationDisplay> Validations { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ISaveHistoryView.ShowValidation))
            {
                Validation = (ValidationDisplay)args![0]!;
                Validations.Add(Validation);
            }
            return method.ReturnType == typeof(bool) ? false : null;
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class ImmediateRunner : IQueryRunner
    {
        public Action? AfterValidation { get; set; }

        public Task<T> RunAsync<T>(Func<T> query)
        {
            T result = query();
            if (result is WorkspaceValidationReadModel && AfterValidation is { } after)
            {
                AfterValidation = null;
                after();
            }
            return Task.FromResult(result);
        }
    }
}
