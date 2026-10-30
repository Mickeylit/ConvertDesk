using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;

namespace Filee.App.Tests;

public class DocumentWorkspaceTests
{
    [AvaloniaFact]
    public async Task Unsupported_inputs_and_folders_are_rejected_while_html_aliases_are_staged()
    {
        await using var f = new Fixture();
        var html = f.Write("report.HTM");
        var xhtml = f.Write("report.xhtml");
        var script = f.Write("run.py");
        var image = f.Write("scan.png");
        f.Workspace.AddFiles([html, xhtml, script, image, f.Directory, html]);

        Assert.Equal(new[] { html, xhtml }, f.Workspace.Files.Select(x => x.Path));
        Assert.Contains("run.py", f.Workspace.Message);
        Assert.Contains("scan.png", f.Workspace.Message);
        Assert.Empty(f.Conversions.Jobs);
        Assert.Equal(0, f.Converter.Calls);
    }

    [AvaloniaFact]
    public async Task Adding_files_and_switching_formats_never_starts_a_job()
    {
        await using var f = new Fixture();
        f.Workspace.AddFiles([f.Write("notes.txt")]);
        f.Workspace.SelectedTarget = f.Workspace.Targets.Single(t => t.Id == "html");
        f.Workspace.SelectedTarget = f.Workspace.Targets.Single(t => t.Id == "pdf");

        Assert.True(f.Workspace.CanConvert);
        Assert.True(f.Workspace.ConvertCommand.CanExecute(null));
        Assert.Empty(f.Conversions.Jobs);
        Assert.Equal(0, f.Converter.Calls);
        Assert.Single(f.Workspace.Files);
    }

    [AvaloniaFact]
    public async Task A_mixed_batch_cannot_silently_drop_a_file_already_in_the_target_format()
    {
        await using var f = new Fixture();
        var text = f.Write("notes.txt");
        var pdf = f.Write("original.pdf");
        f.Workspace.AddFiles([text, pdf]);

        Assert.False(f.Workspace.CanConvert);
        Assert.False(f.Workspace.Files.Single(x => x.Path == text).IsBlocked);
        var blocked = f.Workspace.Files.Single(x => x.Path == pdf);
        Assert.True(blocked.IsBlocked);
        Assert.Contains("Already", blocked.Status);
        Assert.False(f.Workspace.ConvertCommand.CanExecute(null));
        f.Workspace.ConvertCommand.Execute(null); // The command body revalidates even if invoked directly.
        Assert.Empty(f.Conversions.Jobs);
        Assert.Equal(0, f.Converter.Calls);

        blocked.RemoveCommand.Execute(null);
        Assert.True(f.Workspace.CanConvert);
        Assert.Single(f.Workspace.Files);
    }

    [AvaloniaFact]
    public async Task A_missing_engine_is_named_before_a_conversion_can_start()
    {
        await using var f = new Fixture(available: false);
        f.Workspace.AddFiles([f.Write("notes.txt")]);

        Assert.True(f.Workspace.NeedsPackage);
        Assert.False(f.Workspace.CanConvert);
        Assert.Contains("Test engine", Assert.Single(f.Workspace.Files).Status);
        Assert.Empty(f.Conversions.Jobs);
    }

    [AvaloniaFact]
    public async Task The_explicit_action_queues_all_files_and_preserves_originals_and_existing_outputs()
    {
        await using var f = new Fixture();
        var first = f.Write("notes.txt", "first original");
        var second = f.Write("other.txt", "second original");
        var existing = f.Write("notes.pdf", "existing result");
        f.Workspace.AddFiles([first, second]);
        f.Workspace.ConvertCommand.Execute(null);
        var job = Assert.Single(f.Conversions.Jobs);
        Assert.Equal(new[] { first, second }, job.Job.Sources);
        Assert.Empty(f.Workspace.Files);
        Assert.Single(f.Workspace.Jobs);

        var finished = await f.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(2, f.Converter.Calls);
        Assert.Equal(2, finished.Outputs.Count());
        Assert.Equal("first original", File.ReadAllText(first));
        Assert.Equal("second original", File.ReadAllText(second));
        Assert.Equal("existing result", File.ReadAllText(existing));
        Assert.DoesNotContain(existing, finished.Outputs);
        Assert.False(job.IsRunning);
    }

    [AvaloniaFact]
    public async Task A_file_deleted_after_staging_is_rechecked_at_submit_time()
    {
        await using var f = new Fixture();
        var input = f.Write("notes.txt");
        f.Workspace.AddFiles([input]);
        Assert.True(f.Workspace.CanConvert);
        File.Delete(input);
        f.Workspace.ConvertCommand.Execute(null);

        Assert.False(f.Workspace.CanConvert);
        Assert.True(Assert.Single(f.Workspace.Files).IsBlocked);
        Assert.Empty(f.Conversions.Jobs);
        Assert.Equal(0, f.Converter.Calls);
    }

    [AvaloniaFact]
    public async Task Pptx_choice_explains_the_two_distinct_output_behaviours()
    {
        await using var f = new Fixture();
        f.Workspace.SelectedTarget = f.Workspace.Targets.Single(t => t.Id == "pptx");
        Assert.True(f.Workspace.ShowsPptxHint);
        Assert.Contains("image slide", f.Workspace.PptxHint);
        Assert.Contains("editable slides", f.Workspace.PptxHint);
        Assert.Empty(f.Conversions.Jobs);
    }

    [AvaloniaFact]
    public async Task Visiting_engine_settings_and_returning_keeps_the_staged_files_and_target()
    {
        await using var f = new Fixture();
        var input = f.Write("notes.txt");
        f.Workspace.AddFiles([input]);
        f.Workspace.SelectedTarget = f.Workspace.Targets.Single(t => t.Id == "html");
        f.ReopenWorkspace();

        Assert.Equal(input, Assert.Single(f.Workspace.Files).Path);
        Assert.Equal("html", f.Workspace.SelectedTarget.Id);
        Assert.True(f.Workspace.CanConvert);
        Assert.Empty(f.Conversions.Jobs);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly JobQueue _queue;
        private readonly Localizer _loc = new();
        private readonly UserDataStore _store;
        private readonly ConverterCatalog _catalog;
        private readonly PresetAvailability _availability;
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("convertdesk-workspace-").FullName;
        public TestConverter Converter { get; }
        public ConversionService Conversions { get; }
        public DocumentWorkspaceViewModel Workspace { get; private set; }
        public TaskCompletionSource<ConversionJob> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture(bool available = true)
        {
            _store = new UserDataStore(Path.Combine(Directory, "settings"));
            _store.Load();
            Converter = new TestConverter(available);
            _catalog = new ConverterCatalog([Converter]);
            _queue = new JobQueue(_catalog, enforceProductFormats: true);
            Conversions = new ConversionService(_queue, _store, _loc, new NullPlatformServices());
            _queue.JobFinished += (_, job) => Finished.TrySetResult(job);
            var package = new EnginePackageState(new EnginePackage("test", [], 0, [Converter.Id]), _loc);
            _availability = new PresetAvailability(_catalog, _loc, [package]);
            Workspace = new DocumentWorkspaceViewModel(_loc, _store, Conversions, _availability, _catalog);
        }

        public void ReopenWorkspace()
        {
            Workspace.Dispose();
            Workspace = new DocumentWorkspaceViewModel(_loc, _store, Conversions, _availability, _catalog);
        }

        public string Write(string name, string contents = "document")
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, contents);
            return path;
        }

        public async ValueTask DisposeAsync()
        {
            Workspace.Dispose();
            await _queue.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
            Conversions.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class TestConverter(bool available) : IConverter
    {
        public int Calls;
        public string Id => "test-document-engine";
        public string DisplayName => "Test engine";
        public IReadOnlyList<ConversionEdge> Edges { get; } = [new("txt", "pdf"), new("txt", "html")];
        public int MaxParallelism => 1;
        public EngineStatus GetStatus() => available ? EngineStatus.Available() : EngineStatus.Unavailable("missing");
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var output = step.Output.Allocate(step.To)!;
            File.WriteAllText(output, "converted document");
            return Task.FromResult<IReadOnlyList<string>>([output]);
        }
    }

    private sealed class Localizer : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key switch
        {
            "engines.package.test.name" => "Test engine",
            "engine.reason.needs_package" => "Needs {0}",
            _ => key,
        };
        public string Format(string key, params object[] args) => string.Format(this[key], args);
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
