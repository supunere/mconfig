using System.Reflection;
using Mconfig.Application.Assignments.Commands;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Persistence;
using Mconfig.Presentation.Assignments;
using Mconfig.Presentation.Common;
using Mconfig.Presentation.Combinations;
using Xunit;

namespace Mconfig.Presentation.Tests;

public sealed class AssignmentNavigationTests
{
    [Fact]
    public async Task FiltersChangeVisibleColumnsButKeepFullNavigationTargetAndSession()
    {
        using Fixture f = await Fixture.CreateAsync();
        CombinationView view = new();
        using CombinationListPresenter presenter = new(view, f.Session, new ImmediateRunner(), new ImmediateDispatcher(), new("ja"));
        AssignmentNavigationEventArgs? target = null;
        presenter.AssignmentNavigationRequested += (_, value) => target = value;
        var before = f.Session.Status;
        await presenter.RefreshAsync();
        Assert.Equal(3, view.Table!.SpecColumns.Count);
        Assert.Equal(27, view.Table.Rows.Count);

        view.Filter("usage", "オフィス");
        Assert.Equal(new[] { "formFactor", "productTier" }, view.Table.SpecColumns.Select(c => c.Key));
        Assert.Equal(9, view.Table.Rows.Count);
        Assert.Equal("オフィス", view.Filters.Single(f => f.Key == "usage").Selected);
        view.Filter("formFactor", "タワー");
        view.Filter("productTier", "スタンダード");
        Assert.Empty(view.Table.SpecColumns);
        Assert.Single(view.Table.Rows);
        Assert.Equal(3, view.Table.Rows[0].Values.Count);
        Assert.Equal(3, view.Table.CategoryColumns.Count);
        view.Select(0, "memory");
        view.Navigate();
        Assert.NotNull(target);
        Assert.Equal("memory", target.Category);
        Assert.Equal("オフィス", target.Combination["usage"]);
        Assert.Equal("タワー", target.Combination["formFactor"]);
        Assert.Equal("スタンダード", target.Combination["productTier"]);

        view.Filter("usage", null);
        Assert.Equal("usage", Assert.Single(view.Table.SpecColumns).Key);
        Assert.Equal(3, view.Table.Rows.Count);
        Assert.Null(view.Filters.Single(f => f.Key == "usage").Selected);
        view.Clear();
        Assert.Equal(3, view.Table.SpecColumns.Count);
        Assert.Equal(27, view.Table.Rows.Count);
        Assert.False(view.Table.Filtered);
        Assert.Equal(before, f.Session.Status);
    }

    private sealed class CombinationView : ICombinationListView
    {
        public event EventHandler<FilterChangedEventArgs>? FilterChanged;
        public event EventHandler<CellSelectedEventArgs>? CellSelected;
        public event EventHandler? FiltersCleared;
        public event EventHandler? AssignmentRequested;
        public CombinationTableDisplay? Table { get; private set; }
        public IReadOnlyList<FilterDisplay> Filters { get; private set; } = [];
        public void ShowFilters(IReadOnlyList<FilterDisplay> filters) => Filters = filters;
        public void ShowCombinations(CombinationTableDisplay table) => Table = table;
        public void Filter(string key, string? value) => FilterChanged?.Invoke(this, new(key, value));
        public void Select(int row, string category) => CellSelected?.Invoke(this, new(row, category));
        public void Clear() => FiltersCleared?.Invoke(this, EventArgs.Empty);
        public void Navigate() => AssignmentRequested?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public async Task NavigationFindsCurrentRuleAfterReorderingWithoutEditingData()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Presenter.SelectAsync("cpu");
        await f.NavigateAsync();
        Assert.Equal("memory", f.View.Table!.Category);
        Assert.Equal(3, f.View.Table.SelectedRow);
        Assert.True(f.View.Table.RuleNavigation);

        var result = f.Commands.Handle(new MoveAssignmentCommand("memory", 3, 0, f.Session.Status.Version));
        Assert.Equal(CommandStatus.Applied, result.Status);
        await f.NavigateAsync();
        Assert.Equal(0, f.View.Table.SelectedRow);
    }

    [Fact]
    public async Task DisabledRuleCanBeLocatedAndMissingRuleHasNoSelection()
    {
        using Fixture f = await Fixture.CreateAsync();
        var result = f.Commands.Handle(new ChangeAssignmentCommand("memory", 3,
            new AssignmentRow(new Dictionary<string, string> { ["productTier"] = "スタンダード" }, null),
            f.Session.Status.Version));
        Assert.Equal(CommandStatus.Applied, result.Status);
        await f.NavigateAsync();
        Assert.Equal(3, f.View.Table!.SelectedRow);
        Assert.Null(f.View.Table.Rows[3].Preset);
        for (int row = 4; row >= 0; row--)
            Assert.Equal(CommandStatus.Applied,
                f.Commands.Handle(new RemoveAssignmentCommand("memory", row, f.Session.Status.Version)).Status);
        await f.NavigateAsync();
        Assert.Null(f.View.Table.SelectedRow);
        Assert.True(f.View.Table.RuleNotFound);
    }

    private sealed class Fixture(string root, WorkspaceLock editLock, EditSession session) : IDisposable
    {
        public EditSession Session { get; } = session;
        public AssignmentCommandHandlers Commands { get; } = new(session);
        public RecordingView View { get; private set; } = null!;
        public AssignmentPresenter Presenter { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "mconfig-navigation-tests", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(AppContext.BaseDirectory, "TestData");
            foreach (string file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
            {
                string target = Path.Combine(root, "data", Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            WorkspaceLayout layout = new(root);
            WorkspaceLock editLock = WorkspaceLock.TryAcquire(layout)!;
            JsonWorkspaceStore store = new(layout, editLock, new GitDataRepository(layout));
            Fixture f = new(root, editLock, new(await store.LoadAsync(default)));
            IAssignmentView view = DispatchProxy.Create<IAssignmentView, RecordingView>();
            f.View = (RecordingView)view;
            f.Presenter = new(view, f.Session, new ImmediateRunner(), new ImmediateDispatcher(), new("ja"), TimeSpan.Zero);
            return f;
        }

        public async Task NavigateAsync()
        {
            var before = Session.Status;
            await Presenter.SelectForCombinationAsync("memory", new Dictionary<string, string>
            {
                ["usage"] = "オフィス", ["formFactor"] = "タワー", ["productTier"] = "スタンダード",
            });
            Assert.Equal(before, Session.Status);
        }

        public void Dispose()
        {
            Presenter.Dispose();
            editLock.Dispose();
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    public class RecordingView : DispatchProxy
    {
        public AssignmentTableDisplay? Table { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAssignmentView.ShowAssignments)) Table = (AssignmentTableDisplay)args![0]!;
            return null;
        }
    }

    private sealed class ImmediateRunner : IQueryRunner
    {
        public Task<T> RunAsync<T>(Func<T> query) => Task.FromResult(query());
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}
