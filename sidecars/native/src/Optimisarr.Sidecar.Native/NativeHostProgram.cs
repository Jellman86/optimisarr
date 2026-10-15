namespace Optimisarr.Sidecar.Native;

internal static class NativeHostProgram
{
    public static async Task<int> RunAsync(Stream input, Stream output, CancellationToken token,
        Func<NativeHostRequest, Action<NativeHostEvent>, CancellationToken, Task<NativeHostEvent>>? execute = null)
    {
        NativeHostRequest request;
        try
        {
            using var initial = CancellationTokenSource.CreateLinkedTokenSource(token);
            initial.CancelAfter(TimeSpan.FromSeconds(10));
            request = NativeHostProtocol.Parse(await NativeHostProtocol.ReadRequestAsync(input, initial.Token));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        {
            var refused = new NativeHostEvents(output, () => { }, null, default);
            try { await refused.FinishAsync(new("refused", Detail: "The native host request is invalid, incomplete or unsupported.")); }
            catch (IOException) { }
            return 2;
        }

        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var finished = new CancellationTokenSource();
        var events = new NativeHostEvents(output, () => NativeHostProtocol.CancelOwnedWork(work), request.Pairing?.Credential, default);
        var parent = NativeHostProtocol.WatchParentAsync(input, work, finished.Token);
        try
        {
            NativeHostEvent result;
            try
            {
                result = await (execute ?? NativeHostExecutor.RunAsync)(request, events.Report, work.Token);
            }
            catch (OperationCanceledException) when (work.IsCancellationRequested)
            {
                result = new("refused", Detail: "Native work was cancelled. Check the server for its recorded outcome.");
            }
            catch (Exception)
            {
                NativeHostProtocol.CancelOwnedWork(work);
                result = new("refused", Detail: "Native work did not return a complete result. Check the server for its recorded outcome.");
            }
            try { await events.FinishAsync(result); }
            catch (IOException) { return 2; }
            return result.Type is "capabilities" or "requirements" or "recovered" || result.Outcome?.Delivered == true ? 0 : 1;
        }
        finally
        {
            NativeHostProtocol.CancelOwnedWork(finished);
            await parent;
        }
    }
}
