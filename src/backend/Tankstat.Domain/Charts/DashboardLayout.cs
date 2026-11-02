using System.Text.RegularExpressions;

namespace Tankstat.Domain.Charts;

/// <summary>
/// What a vehicle's dashboard shows, in order: the key figures, then the charts (a ready-made one as <c>preset:&lt;id&gt;</c>, a saved one as
/// <c>chart:&lt;guid&gt;</c>), and the saved charts taken off it. Ids are checked for shape and count only: the UI owns what a figure or a
/// ready-made chart is, and drops the ones it does not know (dashboard/widgets.ts <c>resolveLayout</c>), as it does for a grid's columns.
/// </summary>
public sealed partial record DashboardLayoutValues(IReadOnlyList<string> Figures, IReadOnlyList<string> Charts, IReadOnlyList<string> Hidden)
{
    public const int MaxItems = 60;
    public const int MaxIdLength = 40;
    public const string PresetPrefix = "preset:";
    public const string ChartPrefix = "chart:";

    /// <summary>Every entry in shape, duplicates dropped (the first wins), at most <see cref="MaxItems"/> per list.</summary>
    public DashboardLayoutValues Checked() => new(Check(Figures, IsFigure), Check(Charts, e => IsPreset(e) || ChartIdOf(e) is not null), Check(Hidden, e => ChartIdOf(e) is not null));

    /// <summary>The saved chart an entry names, or null for anything else.</summary>
    public static Guid? ChartIdOf(string entry) =>
        entry.StartsWith(ChartPrefix, StringComparison.Ordinal) && Guid.TryParseExact(entry[ChartPrefix.Length..], "D", out var id) ? id : null;

    private static bool IsFigure(string entry) => entry.Length <= MaxIdLength && IdPattern().IsMatch(entry);

    private static bool IsPreset(string entry) => entry.StartsWith(PresetPrefix, StringComparison.Ordinal) && IsFigure(entry[PresetPrefix.Length..]);

    private static IReadOnlyList<string> Check(IReadOnlyList<string>? entries, Func<string, bool> valid)
    {
        var list = (entries ?? []).Select(e => e is not null && valid(e) ? e : throw new DomainException("dashboard.itemInvalid", "An entry of the dashboard is not valid."))
            .Distinct(StringComparer.Ordinal).ToList();
        if (list.Count > MaxItems) throw new DomainException("dashboard.tooMany", $"A dashboard can list at most {MaxItems} of each.", new { Max = MaxItems });
        return list;
    }

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*$")]
    private static partial Regex IdPattern();
}

/// <summary>A dashboard's arrangement as stored: the vehicle's default, or one person's own arrangement of it.</summary>
public abstract class DashboardLayout
{
    public Guid VehicleId { get; protected set; }
    public IReadOnlyList<string> Figures { get; protected set; } = [];
    public IReadOnlyList<string> Charts { get; protected set; } = [];
    public IReadOnlyList<string> Hidden { get; protected set; } = [];
    public DateTimeOffset UpdatedAt { get; protected set; }

    public DashboardLayoutValues Values => new(Figures, Charts, Hidden);

    protected void Apply(DashboardLayoutValues values, DateTimeOffset now)
    {
        var checkedValues = values.Checked();
        Figures = checkedValues.Figures;
        Charts = checkedValues.Charts;
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
