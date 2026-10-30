using Filee.Core.Conversion;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Documents;

/// <summary>Desktop shutdown cancels work; the CLI's normal queue disposal still drains its work to completion.</summary>
public static class DocumentShutdown
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Called on the UI thread after input triggers stop. Queue disposal runs entirely on the thread pool so its
    /// continuations never require the UI thread, which an application Exit event cannot asynchronously keep alive.
    /// Returns false when cleanup faults or an engine does not stop within the bounded wait.
    /// </summary>
    public static bool Stop(ConversionService conversions, JobQueue queue, TimeSpan? timeout = null, ILogger? log = null)
    {
        // Jobs is a UI collection: snapshot it here, before doing any work on a background thread. Cancel queued
        // work first so releasing an active engine does not let the next document begin converting.
        var jobs = conversions.Jobs.Select(vm => vm.Job)
            .Where(job => job.State is JobState.Queued or JobState.Running)
            .OrderBy(job => job.State == JobState.Running ? 1 : 0).ToArray();
        var drain = Task.Run(async () =>
        {
            foreach (var job in jobs)
            {
                try { job.Cancel(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    log?.LogWarning(ex, "An engine cancellation callback failed during shutdown");
                }
            }
            try
            {
                await queue.DisposeAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Observe faults even if the UI has already reached its timeout and stopped waiting.
                log?.LogWarning(ex, "The conversion queue could not finish shutdown");
                return false;
            }
        });
        try
        {
            return drain.WaitAsync(timeout ?? DefaultTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            log?.LogWarning("Conversion shutdown exceeded {Timeout}; cancellation was requested", timeout ?? DefaultTimeout);
            return false;
        }
        finally
        {
            conversions.Dispose();
        }
    }
}
