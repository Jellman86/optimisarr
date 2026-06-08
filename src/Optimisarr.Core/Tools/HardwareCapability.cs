namespace Optimisarr.Core.Tools;

public sealed record EncoderCapability(
    string Name,
    string Codec,
    string Mode,
    bool Available);

public sealed record HardwareCapabilityResult(
    IReadOnlyList<string> HardwareAccelerators,
    IReadOnlyList<EncoderCapability> Encoders,
    bool NvidiaRuntimeAvailable,
    bool DriDeviceAvailable,
    string? Error);

