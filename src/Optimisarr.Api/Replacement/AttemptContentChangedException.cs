namespace Optimisarr.Api.Replacement;

internal sealed class AttemptContentChangedException(string detail) : Exception(
    detail + " Files and evidence are retained; create a fresh verified attempt.");
