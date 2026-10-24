namespace Optimisarr.Api.Deduplication;

internal sealed class FileCleanupDeferredException() : Exception(
    "Cleanup is deferred while another file operation or unresolved duplicate reservation is active. Files and diagnostics are retained; retry after that operation or reservation is resolved.");
