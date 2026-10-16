using System.IO.Compression;
using Mconfig.Application.Assignments.Commands;
using Mconfig.Application.History.Queries;
using Mconfig.Application.Ports;
using Mconfig.Application.Presets.Commands;
using Mconfig.Application.Releases.Commands;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Commands;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Infrastructure.History;
using Mconfig.Infrastructure.Persistence;
using Mconfig.Infrastructure.Releases;
using Xunit;

namespace Mconfig.Presentation.Tests;

public sealed class PublicationTests
{
    [Fact]
    public async Task PublishCreatesZipAndHistoryTogetherAndRepeatedClickDoesNotDuplicate()
    {
        using var f = await Fixture.CreateAsync();
        f.ChangeRate("3600");
        OperationResult result = await f.PublishAsync();
        Assert.True(result.Status == OperationStatus.Succeeded, result.Detail ?? result.Status.ToString());
        Assert.Equal(OperationStage.Exported, result.CompletedStage);
        Assert.False(f.Session.Status.HasUnsavedChanges);
        Assert.False(f.Session.Status.HasUncommittedChanges);
        Assert.Single(await f.Revisions.GetHistoryAsync(100, null, default));
        using (ZipArchive zip = ZipFile.OpenRead(result.Output!))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "settings_master.json");
            Assert.Contains(zip.Entries, e => e.FullName == "change_diff_report.txt");
            Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
        }
        Assert.Equal(OperationStatus.NoChanges, (await f.PublishAsync()).Status);
        Assert.Single(Directory.GetFiles(f.Layout.ExportsDirectory, "*.zip"));
        Assert.Single(await f.Revisions.GetHistoryAsync(100, null, default));
        f.ChangeRate("4000");
        OperationResult second = await f.PublishAsync();
        Assert.Equal(OperationStatus.Succeeded, second.Status);
        var history = await f.Revisions.GetHistoryAsync(100, null, default);
        Assert.Equal(2, history.Count);
        Assert.Equal(history[1].Id, history[0].Parent);
        Assert.Equal(2, Directory.GetFiles(f.Layout.ExportsDirectory, "*.zip").Length);
        var candidates = await f.Archive.GetReleaseCandidatesAsync(second.Revision, default);
        Assert.Equal(2, candidates.Count);
    }

    [Theory]
    [InlineData("Recorded", false)]
    [InlineData("ZipWritten", false)]
    [InlineData("Tagged", false)]
    [InlineData("Published", false)]
    [InlineData("BeforeHistory", false)]
    [InlineData("Recorded", true)]
    [InlineData("ZipWritten", true)]
    [InlineData("Tagged", true)]
    [InlineData("Published", true)]
    [InlineData("BeforeHistory", true)]
    public async Task FailedPublicationLeavesNoHistoryAndRetryReusesZip(string point, bool reload)
    {
        using var f = await Fixture.CreateAsync();
        f.Failure = point;
        f.ChangeRate("3600");
        OperationResult failed = await f.PublishAsync();
        Assert.Equal(OperationStatus.TechnicalFailure, failed.Status);
        Assert.Equal(OperationStage.DraftSaved, failed.CompletedStage);
        Assert.False(f.Session.Status.HasUnsavedChanges);
        Assert.True(f.Session.Status.HasUncommittedChanges);
        Assert.Empty(await f.Revisions.GetHistoryAsync(100, null, default));
        f.Failure = null;
        if (reload) await f.ReloadAsync();
        OperationResult result = await f.PublishAsync();
        Assert.True(result.Status == OperationStatus.Succeeded, result.Detail ?? result.Status.ToString());
        Assert.Single(await f.Revisions.GetHistoryAsync(100, null, default));
        Assert.Single(Directory.GetFiles(f.Layout.ExportsDirectory, "*.zip"));
        Assert.False(f.Session.Status.HasUncommittedChanges);
    }

    [Fact]
    public async Task FailedSecondPublicationKeepsPreviousHistoryAndChangesForRetry()
    {
        using var f = await Fixture.CreateAsync();
        var first = await f.PublishAsync();
        f.ChangeRate("3600");
        f.Failure = "ZipWritten";
        Assert.Equal(OperationStatus.TechnicalFailure, (await f.PublishAsync()).Status);
        Assert.Equal(first.Revision, Assert.Single(await f.Revisions.GetHistoryAsync(100, null, default)).Id);
        Assert.NotEmpty((await new HistoryQueryHandlers(f.Session, f.Revisions).HandleAsync(new GetUncommittedChangesQuery())).Changes);
        f.Failure = null;
        await f.ReloadAsync();
        Assert.Equal(OperationStatus.Succeeded, (await f.PublishAsync()).Status);
        Assert.Equal(2, (await f.Revisions.GetHistoryAsync(100, null, default)).Count);
        Assert.Equal(2, Directory.GetFiles(f.Layout.ExportsDirectory, "*.zip").Length);
    }

    [Fact]
    public async Task WarningsAndCanceledValidationCannotPublish()
    {
        using var f = await Fixture.CreateAsync();
        var assignments = new AssignmentCommandHandlers(f.Session);
        for (int i = 0; i < 2; i++)
            assignments.Handle(new AddAssignmentCommand("memory", new(new Dictionary<string, string>(), null), f.Session.Status.Version));
        Assert.Equal(OperationStatus.ValidationFailed, (await f.PublishAsync()).Status);
        Assert.Empty(await f.Revisions.GetHistoryAsync(100, null, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(OperationStatus.Canceled, (await f.PublishAsync(cancellation.Token)).Status);
        Assert.False(Directory.Exists(f.Layout.ExportsDirectory));
    }

    [Fact]
    public async Task BlankReasonAndStaleVersionDoNotWriteAnything()
    {
        using var f = await Fixture.CreateAsync();
        var handler = f.Handler();
        var version = f.Session.Status.Version;
        Assert.Equal(OperationStatus.ValidationFailed, (await handler.HandleAsync(new("", "test", version))).Status);
        f.ChangeRate("3600");
        Assert.Equal(OperationStatus.Conflict, (await handler.HandleAsync(new("Initial publication", "test", version))).Status);
        Assert.Empty(await f.Revisions.GetHistoryAsync(100, null, default));
        Assert.False(Directory.Exists(f.Layout.ExportsDirectory));
    }

    private sealed class Fixture : IDisposable
    {
        public WorkspaceLayout Layout { get; }
        private readonly WorkspaceLock _editLock;
        private readonly GitDataRepository _repository;
        private readonly JsonWorkspaceStore _store;
        public GitRevisionStore Revisions { get; }
        public ZipReleaseArchive Archive { get; }
        public EditSession Session { get; private set; } = null!;
        public string? Failure { get; set; }

        private Fixture(WorkspaceLayout layout, WorkspaceLock editLock)
        {
            Layout = layout;
            _editLock = editLock;
            _repository = new(layout);
            _store = new(layout, editLock, _repository);
            Revisions = new(_repository, editLock);
            Archive = new(_repository, editLock, "test")
            {
                Faults = point => { if (Failure == point.ToString()) throw new IOException("Injected publication failure"); },
            };
        }

        public PublishWorkspaceHandler Handler() => new(Session, _store, new GitPublicationStore(_repository, _editLock, Archive)
        {
            BeforeHistoryCommit = () => { if (Failure == "BeforeHistory") throw new IOException("Injected history failure"); },
        });
        public Task<OperationResult> PublishAsync(CancellationToken cancellation = default) =>
            Handler().HandleAsync(new("Publish tested configuration", "test", Session.Status.Version), cancellation);
        public async Task ReloadAsync() => Session = new(await _store.LoadAsync(default));
        public void ChangeRate(string value) => Assert.Equal(CommandStatus.Applied,
            new ChangePresetCellHandler(Session).Handle(new("memory", "メモリ・8GB構成", "modules", "moduleA",
                "transferRateMTs", CellValue.FromNumberLiteral(value), Session.Status.Version)).Status);

        public static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "mconfig-publication-tests", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(AppContext.BaseDirectory, "TestData");
            foreach (string file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
            {
                string target = Path.Combine(root, "data", Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            WorkspaceLayout layout = new(root);
            Fixture fixture = new(layout, WorkspaceLock.TryAcquire(layout)!);
            await fixture.ReloadAsync();
            return fixture;
        }

        public void Dispose()
        {
            _editLock.Dispose();
            foreach (string file in Directory.EnumerateFiles(Layout.Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Layout.Root, recursive: true);
        }
    }
}
