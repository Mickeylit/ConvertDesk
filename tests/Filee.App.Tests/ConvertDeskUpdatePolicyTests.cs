using Filee.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class ConvertDeskUpdatePolicyTests
{
    [Fact]
    public async Task Upstream_update_entrypoints_cannot_download_or_launch_an_installer()
    {
        var network = new UnexpectedNetwork();
        var launches = 0;
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network)
        {
            InstallsItself = true,
            StartProcess = _ => { launches++; return null; },
            LatestVersion = "999.0.0",
        };
        Assert.False(UpdateService.UpdatesEnabled);
        Assert.Null(await updates.CheckAsync(TestContext.Current.CancellationToken));
        Assert.False(await updates.RefreshAsync(true, TestContext.Current.CancellationToken));
        await updates.UpdateAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => updates.DownloadInstallerAsync(Path.GetTempPath(), null));
        Assert.Equal(0, network.Requests);
        Assert.Equal(0, launches);
        Assert.Equal(UpdateStep.Idle, updates.Step);
    }

    private sealed class UnexpectedNetwork : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("ConvertDesk must not access the upstream update feed.");
        }
    }
}
