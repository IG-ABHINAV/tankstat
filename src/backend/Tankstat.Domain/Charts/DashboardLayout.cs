using System.Text.RegularExpressions;

namespace Tankstat.Domain.Charts;

/// <summary>A tile of a vehicle's dashboard: a key figure, a ready-made chart (<c>preset:&lt;id&gt;</c>), a saved widget (<c>chart:&lt;guid&gt;</c>, a
/// chart or a key figure) or a tile carrying its own recipe (<c>tile:&lt;guid&gt;</c>, #273), at its place in a grid of
/// <see cref="DashboardLayoutValues.Columns"/> columns. A pinned tile is never moved by dragging the others.</summary>
/// <param name="Period">An old key figure id's own period (#230); null shows the figure's default. Stored with the tiles as JSON, so a tile saved before
/// periods existed reads as null. A <c>tile:</c> keeps its period in its recipe.</param>
/// <param name="Recipe">A <c>tile:</c> tile's own recipe, and only such a tile's.</param>
public sealed record DashboardTile(string Id, int X, int Y, int W, int H, bool Pinned = false, TrendPeriod? Period = null, TileRecipe? Recipe = null);

/// <summary>
/// What a vehicle's dashboard shows: its tiles, and the saved charts taken off it. Ids are checked for shape, the places for their bounds
/// (never for overlaps: the UI packs the tiles when it draws them, and a phone saves its one column into the same grid); what a figure or a
/// ready-made chart is, the UI owns, and drops the ones it does not know (dashboard/widgets.ts <c>resolveLayout</c>), as for a grid's columns.
/// </summary>
public sealed partial record DashboardLayoutValues(IReadOnlyList<DashboardTile> Tiles, IReadOnlyList<string> Hidden)
{
    public const int Columns = 4;
    public const int MaxRows = 24;
    public const int MaxItems = 60;
    public const int MaxIdLength = 40;
    public const string PresetPrefix = "preset:";
    public const string ChartPrefix = "chart:";
    public const string TilePrefix = "tile:";

    /// <summary>
    /// Every tile and entry in shape and within bounds, duplicates dropped (the first wins), at most <see cref="MaxItems"/> per list. The
    /// shapes (#273): a <c>tile:&lt;guid&gt;</c> with its own recipe (<see cref="TileRecipe.Validated"/>, its period inside it), and every other
    /// id without one: a saved widget <c>chart:&lt;guid&gt;</c>, a <c>preset:</c> chart, or an old key figure id with or without its period. A
    /// period valid on <paramref name="today"/> (<see cref="TrendPeriod.Validated"/>). An old key figure id's period is a trend figure's, which
    /// always compares (<c>vehicleTrend</c> refuses <see cref="TrendComparison.None"/>): none is refused there, or the tile would be saved and
    /// then never load; a <c>tile:</c> figure's recipe may have none (<c>vehicleFigure</c> answers it).
    /// </summary>
    public DashboardLayoutValues Checked(DateOnly today)
    {
        var tiles = (Tiles ?? []).Select(t => t is not null && Shaped(t) && t.X >= 0 && t.W >= 1 && t.X + t.W <= Columns && t.Y >= 0 && t.H >= 1 && t.H <= MaxRows
                ? t with { Period = t.Period?.Validated(today), Recipe = t.Recipe?.Validated(today) }
                : throw new DomainException("dashboard.itemInvalid", "An entry of the dashboard is not valid."))
            .DistinctBy(t => t.Id, StringComparer.Ordinal).ToList();
        if (tiles.Count > MaxItems) throw new DomainException("dashboard.tooMany", $"A dashboard can list at most {MaxItems} of each.", new { Max = MaxItems });
        return new(tiles, Check(Hidden, e => ChartIdOf(e) is not null));
    }

    /// <summary>The saved widget (a chart or a key figure, #273) an entry names, or null for anything else.</summary>
    public static Guid? ChartIdOf(string entry) =>
        entry.StartsWith(ChartPrefix, StringComparison.Ordinal) && Guid.TryParseExact(entry[ChartPrefix.Length..], "D", out var id) ? id : null;

    /// <summary>The tile carrying its own recipe an id names (<c>tile:&lt;guid&gt;</c>, #273), or null for anything else.</summary>
    public static Guid? TileIdOf(string entry) =>
        entry.StartsWith(TilePrefix, StringComparison.Ordinal) && Guid.TryParseExact(entry[TilePrefix.Length..], "D", out var id) ? id : null;

    /// <summary>Whether an entry is in a built-in key figure id's shape (<c>spendingTrend</c>): also what a figure recipe's template must be.</summary>
    public static bool IsFigureId(string entry) => entry.Length <= MaxIdLength && IdPattern().IsMatch(entry);

    /// <summary>A tile's id and what it carries agree: a <c>tile:</c> its recipe and no period of its own, every other id no recipe (an old
    /// figure id a period that compares).</summary>
    private static bool Shaped(DashboardTile t) => t.Id is not null && (TileIdOf(t.Id) is not null
        ? t.Recipe is not null && t.Period is null
        : t.Recipe is null && IsWidget(t.Id) && (t.Period is null || IsFigureId(t.Id) && t.Period.Against != TrendComparison.None));

    private static bool IsWidget(string id) => IsFigureId(id) || IsPreset(id) || ChartIdOf(id) is not null;

    private static bool IsPreset(string entry) => entry.StartsWith(PresetPrefix, StringComparison.Ordinal) && IsFigureId(entry[PresetPrefix.Length..]);

    private static IReadOnlyList<string> Check(IReadOnlyList<string>? entries, Func<string, bool> valid)
    {
        var list = (entries ?? []).Select(e => e is not null && valid(e) ? e : throw new DomainException("dashboard.itemInvalid", "An entry of the dashboard is not valid."))
            .Distinct(StringComparer.Ordinal).ToList();
        if (list.Count > MaxItems) throw new DomainException("dashboard.tooMany", $"A dashboard can list at most {MaxItems} of each.", new { Max = MaxItems });
        return list;
    }

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*\z")] // \z, not $: $ also matches before a last newline
    private static partial Regex IdPattern();
}

/// <summary>A dashboard's arrangement as stored: the vehicle's default, or one person's own arrangement of it.</summary>
public abstract class DashboardLayout
{
    public Guid VehicleId { get; protected set; }
    public IReadOnlyList<DashboardTile> Tiles { get; protected set; } = [];
    public IReadOnlyList<string> Hidden { get; protected set; } = [];
    public DateTimeOffset UpdatedAt { get; protected set; }

    public DashboardLayoutValues Values => new(Tiles, Hidden);

    protected void Apply(DashboardLayoutValues values, DateTimeOffset now)
    {
        // The UTC date, as the statistics' "today" (StatsService.Today): a period this accepts is one the figures accept the same day.
        var checkedValues = values.Checked(DateOnly.FromDateTime(now.UtcDateTime));
        Tiles = checkedValues.Tiles;
        Hidden = checkedValues.Hidden;
        UpdatedAt = now;
    }
}

/// <summary>The dashboard everyone who sees the vehicle gets, unless they arranged one of their own (changed with Edit access to its logs).</summary>
public sealed class VehicleDashboardLayout : DashboardLayout
{
    private VehicleDashboardLayout() { } // EF Core

    public static VehicleDashboardLayout Create(Guid vehicleId, DashboardLayoutValues values, DateTimeOffset now)
    {
        var layout = new VehicleDashboardLayout { VehicleId = vehicleId };
        layout.Apply(values, now);
        return layout;
    }
}

/// <summary>One person's own arrangement of a vehicle's dashboard: it wins over the vehicle's for them alone.</summary>
public sealed class UserDashboardLayout : DashboardLayout
{
    private UserDashboardLayout() { } // EF Core

    public Guid UserId { get; private set; }

    public static UserDashboardLayout Create(Guid userId, Guid vehicleId, DashboardLayoutValues values, DateTimeOffset now)
    {
        var layout = new UserDashboardLayout { UserId = userId, VehicleId = vehicleId };
        layout.Apply(values, now);
        return layout;
    }
}
