using System.Text.Json.Nodes;
using Mconfig.Application.Presets.Queries;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Persistence;
using Mconfig.Presentation.Combinations;
using Mconfig.Presentation.Common;
using Xunit;

namespace Mconfig.Presentation.Tests;

public sealed class PresetComparisonSelectionTests
{
    [Fact]
    public async Task RowSelectionKeepsColumnsAndUpdatesBaselineIncludingCachedRows()
    {
        using Fixture f = await Fixture.CreateAsync();
        var before = f.Session.Status;
        await f.Presenter.SelectAsync("cpu");
        string[] order = f.View.Display!.Columns.Select(c => c.Name).ToArray();
        Assert.Equal(f.View.Display.AllPresets, order);
        Assert.All(f.View.Display.Rows.SelectMany(r => r.Cells), c => Assert.False(c.DiffersFromSelectedPreset));
        f.Select(order[2]);
        Assert.Equal(order, f.View.Display.Columns.Select(c => c.Name));
        Assert.True(f.View.Display.Columns[2].AssignedToSelection);
        Assert.True(f.View.Display.Rows[0].Cells[0].DiffersFromSelectedPreset);
        Assert.False(f.View.Display.Rows[0].Cells[2].DiffersFromSelectedPreset);
        f.Select(order[0]);
        Assert.False(f.View.Display.Rows[0].Cells[0].DiffersFromSelectedPreset);
        Assert.True(f.View.Display.Rows[0].Cells[2].DiffersFromSelectedPreset);
        f.Select(null);
        Assert.All(f.View.Display.Rows.SelectMany(r => r.Cells), c => Assert.False(c.DiffersFromSelectedPreset));
        Assert.Equal(before, f.Session.Status);
    }

    [Fact]
    public async Task HiddenSelectedPresetRemainsBaselineAndCanBeExplicitlyAdded()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Presenter.SelectAsync("cpu");
        string[] all = f.View.Display!.AllPresets.ToArray();
        f.View.Choose([all[0], all[1]]);
        f.Select(all[2]);
        Assert.Equal(all.Take(2), f.View.Display.Columns.Select(c => c.Name));
        Assert.DoesNotContain(f.View.Display.Columns, c => c.AssignedToSelection);
        Assert.All(f.View.Display.Rows[0].Cells, c => Assert.True(c.DiffersFromSelectedPreset));
        f.View.Choose([all[0], all[1], all[2]]);
        Assert.Equal(all.Take(3), f.View.Display.Columns.Select(c => c.Name));
        Assert.True(f.View.Display.Columns[2].AssignedToSelection);
        Assert.False(f.View.Display.Rows[0].Cells[2].DiffersFromSelectedPreset);
    }

    [Fact]
    public async Task AutomaticSubsetIsStableUntilExplicitResetOrCategoryChange()
    {
        using Fixture f = await Fixture.CreateAsync(extraPresets: 6);
        f.Select("CPU・基本構成");
        string[] order = f.View.Display!.Columns.Select(c => c.Name).ToArray();
        Assert.Equal(PresetComparisonPresenter.AutomaticLimit, order.Length);
        string hidden = f.View.Display.AllPresets.Last();
        f.Select(hidden);
        Assert.Equal(order, f.View.Display.Columns.Select(c => c.Name));
        Assert.Equal(hidden, f.View.Display.SelectedPreset);
        Assert.DoesNotContain(f.View.Display.Columns, c => c.AssignedToSelection);
        await f.Presenter.RefreshAsync();
        Assert.Equal(order, f.View.Display.Columns.Select(c => c.Name));
        f.View.Choose(null);
        Assert.Contains(f.View.Display.Columns, c => c.Name == hidden && c.AssignedToSelection);
        await f.Presenter.SelectAsync("memory");
        Assert.Equal("memory", f.View.Display.Category);
        Assert.DoesNotContain(f.View.Display.Columns, c => c.Name.StartsWith("CPU"));
    }

    private sealed class Fixture(string root, WorkspaceLock editLock, EditSession session) : IDisposable
    {
        public EditSession Session { get; } = session;
        public View View { get; } = new();
        public PresetComparisonPresenter Presenter { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(int extraPresets = 0)
        {
            string root = Path.Combine(Path.GetTempPath(), "mconfig-comparison-tests", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(AppContext.BaseDirectory, "TestData");
            foreach (string file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
            {
                string target = Path.Combine(root, "data", Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            if (extraPresets > 0)
            {
                string path = Path.Combine(root, "data", "settings_master.json");
                JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                JsonArray presets = json["cpu"]!["presets"]!.AsArray();
                for (int i = 0; i < extraPresets; i++)
                {
                    JsonNode preset = presets[0]!.DeepClone();
                    preset["name"] = $"CPU・追加構成{i}";
                    presets.Add(preset);
                }
                await File.WriteAllTextAsync(path, json.ToJsonString());
            }
            WorkspaceLayout layout = new(root);
            WorkspaceLock editLock = WorkspaceLock.TryAcquire(layout)!;
            JsonWorkspaceStore store = new(layout, editLock, new GitDataRepository(layout));
            Fixture f = new(root, editLock, new(await store.LoadAsync(default)));
            f.Presenter = new(f.View, f.Session, new Runner(), new Dispatcher(), new("ja"));
            return f;
        }
        public void Select(string? preset) => Presenter.OnContextChanged(this, new CombinationContext(
            Session.Status.Version, [new("cpu", "CPU")], null,
            preset is null ? new Dictionary<string, string>() : new() { ["cpu"] = preset },
            new Dictionary<string, IReadOnlyList<string>>()) { SelectedCategory = "cpu" });
        public void Dispose()
        {
            Presenter.Dispose();
            editLock.Dispose();
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
    private sealed class View : IPresetComparisonView
    {
        public ComparisonDisplay? Display { get; private set; }
        public event EventHandler<PresetsChosenEventArgs>? PresetsChosen;
#pragma warning disable CS0067
        public event EventHandler<CellEditStartingEventArgs>? CellEditStarting;
        public event EventHandler<CellEditedEventArgs>? CellEdited;
        public event EventHandler<PasteRequestedEventArgs>? PasteRequested;
        public event EventHandler<PresetOperationRequestedEventArgs>? PresetOperationRequested;
#pragma warning restore CS0067
        public void Choose(IReadOnlyList<string>? presets) => PresetsChosen?.Invoke(this, new(presets));
        public void ShowComparison(ComparisonDisplay comparison) => Display = comparison;
        public string? AskPresetName(PresetNamePrompt prompt) => null;
        public bool ConfirmDeletePreset(string preset) => false;
        public SharedEditAnswer ConfirmSharedEdit(SharedEditPrompt prompt) => new(false, false);
        public void ShowEditFeedback(EditFeedback feedback) { }
        public void ShowPasteOutOfRange(PasteOutOfRange range) { }
    }
    private sealed class Runner : IQueryRunner { public Task<T> RunAsync<T>(Func<T> query) => Task.FromResult(query()); }
    private sealed class Dispatcher : IUiDispatcher { public void Post(Action action) => action(); }
}
