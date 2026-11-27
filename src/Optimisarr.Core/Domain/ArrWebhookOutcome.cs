namespace Optimisarr.Core.Domain;

/// <summary>What Optimisarr did with the last webhook delivery from a Sonarr/Radarr connection.</summary>
public enum ArrWebhookOutcome
{
    /// <summary>The manager's Test button reached Optimisarr and was accepted.</summary>
    Test,

    /// <summary>The imported file was added to the queue.</summary>
    Queued,

    /// <summary>The file is in the inventory, but nothing was queued; the detail says why.</summary>
    NotQueued,

    /// <summary>The file was modified too recently; it is looked at again once it settles.</summary>
    Settling,

    /// <summary>The file is not inside any enabled library.</summary>
    NotInLibrary,

    /// <summary>An event Optimisarr has no use for, such as Grab.</summary>
    Ignored,

    /// <summary>The delivery was refused: a malformed body, an unsafe path, or a missing file.</summary>
    Refused,

    /// <summary>The connection is turned off in Optimisarr, so the delivery was not acted on.</summary>
    ConnectionOff,
}
