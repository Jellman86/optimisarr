using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Workers;

/// <summary>Peak native-process allowances, shared by placement and worker admission.</summary>
public static class PerceptualResourceBudget
{
    // Inventory dimensions cannot lower this: source qualification itself must decode an
    // original whose header may differ from inventory, within the supported 16 MP bound.
    public const long ImageMemoryBytes = 3584L * 1024 * 1024;
    public const long AudioMemoryBytes = 512L * 1024 * 1024;

    public static long? MemoryBytes(MediaKind kind, long scratchBytes, bool? scratchUsesMemory)
    {
        if (scratchBytes <= 0 || scratchUsesMemory is null || kind is not (MediaKind.Image or MediaKind.Audio)) return null;
        try
        {
            var native = kind == MediaKind.Image ? ImageMemoryBytes : AudioMemoryBytes;
            return checked(native + (scratchUsesMemory.Value ? scratchBytes : 0));
        }
        catch (OverflowException) { return null; }
    }
}
