using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Service;

/// <summary>Interactive local users may read status and pause claims, never submit paths or processes.</summary>
[SupportedOSPlatform("windows")]
public sealed class MonitorServer(SidecarSession session, WorkerMonitor monitor, HostShutdown shutdown, ILogger<MonitorServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var load = new MachineLoadSampler();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreatePipe(MonitorProtocol.PipeName);
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await ExchangeAsync(pipe, session, command =>
                {
                    if (command == MonitorProtocol.ReadPreview) monitor.RequestPreviews();
                    if (command == MonitorProtocol.EndPreview) monitor.EndPreviews();
                    if (command == MonitorProtocol.ArmShutdown) shutdown.Arm();
                    if (command == MonitorProtocol.CancelShutdown) shutdown.Cancel();
                    var (jobs, last) = monitor.Read(command == MonitorProtocol.ReadPreview);
                    var status = session.Status;
                    var ending = shutdown.Read();
                    var snapshot = new MonitorSnapshot(Environment.MachineName, status.State.ToString(), status.Detail,
                        session.IsPaused, MonitorProtocol.PublicServerAddress(session.ServerAddress), load.Sample(), Program.FreeScratchBytes(Program.ScratchDirectory()),
                        jobs, last, SidecarBuild.Version, ending.Armed, ending.Detail, ending.Seconds, ending.CanCancel);
                    return snapshot;
                }, timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(250, stoppingToken); }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Local monitor pipe unavailable; the worker continues without a tray connection.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
    internal static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 1024, 65536, security);
    }

    internal static async Task ExchangeAsync(Stream pipe, SidecarSession session, Func<byte, MonitorSnapshot> snapshot, CancellationToken token)
    {
        var command = new byte[1];
        await pipe.ReadExactlyAsync(command, token);
        switch (command[0])
        {
            case MonitorProtocol.Read: break;
            case MonitorProtocol.Pause: session.SetPaused(true); break;
            case MonitorProtocol.Resume: session.SetPaused(false); break;
            case MonitorProtocol.ReadPreview:
            case MonitorProtocol.EndPreview:
            case MonitorProtocol.ArmShutdown:
            case MonitorProtocol.CancelShutdown: break;
            default: return;
        }
        var response = JsonSerializer.SerializeToUtf8Bytes(snapshot(command[0]));
        if (response.Length > MonitorProtocol.MaximumResponseBytes) throw new InvalidDataException("Monitor response exceeded its bound.");
        await pipe.WriteAsync(BitConverter.GetBytes(response.Length), token);
        await pipe.WriteAsync(response, token);
        await pipe.FlushAsync(token);
        // Acknowledging the frame keeps Windows from discarding an unread pipe buffer on close.
        await pipe.ReadExactlyAsync(command, token);
    }

}
