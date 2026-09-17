namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// What the machine is doing right now, for the server to show beside this worker.
///
/// Both figures are optional and separately so: a machine can report its CPU while having no
/// readable accelerator, and either can be momentarily unmeasurable. Absent means "no answer",
/// which the Workers tab shows as such rather than as zero — "idle" and "cannot say" are different
/// things to tell someone deciding where work should go.
/// </summary>
public sealed record MachineLoad(double? CpuBusyFraction, double? GpuBusyFraction)
{
    public bool IsEmpty => CpuBusyFraction is null && GpuBusyFraction is null;
}
