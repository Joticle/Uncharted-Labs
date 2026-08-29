namespace UnchartedOptions.Core;

/// <summary>What the competition calendar permits at a given moment.</summary>
public enum TradingPermission
{
    /// <summary>Before the competition opens. No orders of any kind on the judged account.</summary>
    BeforeCompetitionOpens,

    /// <summary>Normal operation: may open new positions and manage existing ones.</summary>
    OpenAndManage,

    /// <summary>Past the last entry window. Manage and close only, no new positions.</summary>
    ManageOnly,

    /// <summary>Everything must be flat, except contracts being held to expiry.</summary>
    FlattenAll,

    /// <summary>Competition is over.</summary>
    Closed,
}

/// <summary>
/// Contest-specific timing rules, deliberately separate from the trading strategy.
/// </summary>
/// <remarks>
/// <para>
/// This is an overlay, not doctrine. The strategy does not care that a competition ends on
/// a Thursday; these constraints exist only because P&amp;L is scored at a fixed instant.
/// Keeping them in their own type means the strategy stays portable once the contest ends.
/// </para>
/// <para>
/// Boundaries are expressed as fixed UTC instants rather than looked up in a timezone
/// database. US Eastern is UTC-4 for the whole competition window (daylight saving does not
/// end until November), so the conversion is unambiguous -- and the agent runs on both
/// Windows and Linux CI, where timezone identifiers differ and
/// <c>InvariantGlobalization</c> is enabled.
/// </para>
/// </remarks>
public sealed record CompetitionCalendar
{
    /// <summary>Trading opens Monday 31 Aug 2026, 09:30 ET.</summary>
    public DateTimeOffset TradingOpens { get; init; } = new(2026, 8, 31, 13, 30, 0, TimeSpan.Zero);

    /// <summary>Last close at which a new position may be opened: Wednesday 2 Sep, 16:00 ET.</summary>
    public DateTimeOffset LastEntryClose { get; init; } = new(2026, 9, 2, 20, 0, 0, TimeSpan.Zero);

    /// <summary>Everything flat by Thursday 3 Sep, 16:00 ET, when P&amp;L is measured.</summary>
    public DateTimeOffset FlatBy { get; init; } = new(2026, 9, 3, 20, 0, 0, TimeSpan.Zero);

    /// <summary>Expiry date whose contracts may be held through settlement.</summary>
    public DateOnly HoldThroughExpiry { get; init; } = new(2026, 9, 3);

    public TradingPermission PermissionAt(DateTimeOffset now) =>
        now < TradingOpens ? TradingPermission.BeforeCompetitionOpens
        : now < LastEntryClose ? TradingPermission.OpenAndManage
        : now < FlatBy ? TradingPermission.ManageOnly
        : now < FlatBy.AddHours(4) ? TradingPermission.FlattenAll
        : TradingPermission.Closed;

    public bool MayOpenNewPositions(DateTimeOffset now) =>
        PermissionAt(now) == TradingPermission.OpenAndManage;

    /// <summary>
    /// Whether a position expiring on <paramref name="expiration"/> may be left to settle
    /// rather than closed. Exercises and assignments for the final expiry count toward the
    /// scored equity, so closing them early is not required.
    /// </summary>
    public bool MayHoldToExpiry(DateOnly expiration) => expiration == HoldThroughExpiry;

    public string Describe(DateTimeOffset now) => PermissionAt(now) switch
    {
        TradingPermission.BeforeCompetitionOpens =>
            $"Competition opens {TradingOpens:yyyy-MM-dd HH:mm} UTC. No orders until then.",
        TradingPermission.OpenAndManage =>
            $"Open and manage. Last entry {LastEntryClose:yyyy-MM-dd HH:mm} UTC.",
        TradingPermission.ManageOnly =>
            $"Past the last entry window. Manage and close only, no new positions.",
        TradingPermission.FlattenAll =>
            $"Flatten everything except contracts expiring {HoldThroughExpiry:yyyy-MM-dd}.",
        _ => "Competition closed.",
    };
}
