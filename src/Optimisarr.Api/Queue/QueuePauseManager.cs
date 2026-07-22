using Optimisarr.Api.Library;

namespace Optimisarr.Api.Queue;

public interface IQueuePauseStateStore
{
    Task<bool> GetPausedAsync(CancellationToken cancellationToken);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken);
}

/// <summary>Resolves the scoped settings store without making the singleton pause manager scoped.</summary>
public sealed class QueuePauseStateStore(IServiceScopeFactory scopeFactory) : IQueuePauseStateStore
{
    public async Task<bool> GetPausedAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        return await settings.GetQueuePausedAsync(cancellationToken);
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        await settings.SetQueuePausedAsync(paused, cancellationToken);
    }
}

public sealed record AutomaticPauseGuardResult<T>(bool Started, T? Value) where T : class;

/// <summary>
/// Serializes durable pause transitions with automatic replacement. A completed pause request
/// therefore means no automatic replacement is still in flight or can start until Resume succeeds.
/// </summary>
public sealed class QueuePauseManager(
    QueuePauseControl control,
    IQueuePauseStateStore stateStore,
    ILogger<QueuePauseManager> logger)
{
    private readonly SemaphoreSlim _transition = new(1, 1);

    public bool IsPaused => control.IsPaused;

    public QueuePauseSnapshot Snapshot => control.Snapshot;

    public void OnEncodeStarted(int pid) => control.OnEncodeStarted(pid);

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            if (await stateStore.GetPausedAsync(cancellationToken))
            {
                control.Pause();
                logger.LogInformation("Queue remains paused from the previous session (manual pause).");
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            // Once the transition starts it must finish even if the HTTP client disconnects. Persist
            // first: a crash between these steps restores the conservative paused state on restart.
            await stateStore.SetPausedAsync(true, CancellationToken.None);
            control.Pause();
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task<QueueResumeResult> ResumeAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            if (!control.IsPaused)
            {
                return QueueResumeResult.Success;
            }

            var result = control.Resume();
            if (!result.Resumed)
            {
                return result;
            }

            try
            {
                await stateStore.SetPausedAsync(false, CancellationToken.None);
            }
            catch
            {
                // Keep memory/process state aligned with the still-durable pause. A subsequent
                // successful Resume can retry without a restart unexpectedly changing the answer.
                control.Pause();
                throw;
            }

            return result;
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task<AutomaticPauseGuardResult<T>> TryRunAutomaticActionAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
        where T : class
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            if (control.IsPaused)
            {
                return new AutomaticPauseGuardResult<T>(false, null);
            }

            return new AutomaticPauseGuardResult<T>(true, await action());
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task<QueueResumeResult> ReleaseProcessesForShutdownAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            var result = control.ResumeProcessesForShutdown();
            if (control.IsPaused)
            {
                logger.LogInformation(
                    "Released suspended encodes for shutdown drain; the durable manual pause remains set for restart.");
            }
            return result;
        }
        finally
        {
            _transition.Release();
        }
    }
}
