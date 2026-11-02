using Microsoft.Extensions.Logging;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Stats;

/// <summary>The dashboard a person sees of a vehicle: their own arrangement if they made one, else the vehicle's, else the Standard preset.</summary>
/// <param name="CanChangeShared">Whether they may change the vehicle's dashboard (Edit access to its logs).</param>
public sealed record DashboardView(VehicleDashboardLayout? Shared, UserDashboardLayout? Personal, bool CanChangeShared);

/// <summary>
/// How a vehicle's dashboard is arranged (#211). The vehicle has a default everyone who sees it gets, changed with Edit access to its
/// logs; anyone who sees the vehicle may arrange one of their own and go back to the vehicle's. What a figure or a ready-made chart is,
/// is the UI's business; a saved chart in a layout must be one of the vehicle's that the layout's audience can see.
/// </summary>
public sealed class DashboardService(IVehicleRepository vehicles, IDashboardLayoutRepository layouts, IVehicleChartRepository charts, AccessService access, TimeProvider clock, ILogger<DashboardService> logger)
{
    /// <summary>Null when the vehicle is not visible.</summary>
    public async Task<DashboardView?> GetAsync(Guid vehicleId, CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        var vehicle = await vehicles.FindAsync(vehicleId, ct);
        var level = vehicle is null ? AccessLevel.None : await access.LogLevelAsync(vehicle, ct);
        if (level < AccessLevel.View) return null;
        return new DashboardView(await layouts.FindSharedAsync(vehicleId, ct), await layouts.FindPersonalAsync(user.Id, vehicleId, ct), level >= AccessLevel.Edit);
    }

    /// <summary>
    /// Saves the vehicle's layout (<paramref name="personal"/> false; Edit access) or the user's own (View access). A saved chart an entry
    /// names is kept only when it is one of the vehicle's charts its audience sees: a shared one for the vehicle's layout, a shared one or
    /// the user's own for theirs. Anything else (deleted meanwhile, another vehicle's, someone's private chart) is dropped, not refused.
    /// </summary>
    public async Task SaveAsync(Guid vehicleId, bool personal, DashboardLayoutValues values, CancellationToken ct)
    {
        var user = await RequireMayChangeAsync(vehicleId, personal, ct);
        var checkedValues = values.Checked();
        var visible = await charts.ListVisibleAsync(vehicleId, user.Id, ct); // shared, plus the user's own
        var allowed = visible.Where(c => personal || c.IsShared).Select(c => c.Id).ToHashSet();
        bool Keep(string entry) => DashboardLayoutValues.ChartIdOf(entry) is not { } id || allowed.Contains(id);
        var kept = checkedValues with { Tiles = checkedValues.Tiles.Where(t => Keep(t.Id)).ToList(), Hidden = checkedValues.Hidden.Where(Keep).ToList() };

        var now = clock.GetUtcNow();
        if (personal) await layouts.SavePersonalAsync(UserDashboardLayout.Create(user.Id, vehicleId, kept, now), ct);
        else await layouts.SaveSharedAsync(VehicleDashboardLayout.Create(vehicleId, kept, now), ct);
        logger.LogDebug("User {UserId} saved the {Scope} dashboard of vehicle {VehicleId}", user.Id, personal ? "personal" : "shared", vehicleId);
    }

    /// <summary>Forgets a layout: the vehicle's goes back to the Standard preset, a person's to the vehicle's.</summary>
    public async Task ResetAsync(Guid vehicleId, bool personal, CancellationToken ct)
    {
        var user = await RequireMayChangeAsync(vehicleId, personal, ct);
        if (personal) await layouts.RemovePersonalAsync(user.Id, vehicleId, ct);
        else await layouts.RemoveSharedAsync(vehicleId, ct);
        logger.LogDebug("User {UserId} reset the {Scope} dashboard of vehicle {VehicleId}", user.Id, personal ? "personal" : "shared", vehicleId);
    }

    private async Task<Principal> RequireMayChangeAsync(Guid vehicleId, bool personal, CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        var vehicle = await vehicles.FindAsync(vehicleId, ct);
        var level = vehicle is null ? AccessLevel.None : await access.LogLevelAsync(vehicle, ct);
        if (level < AccessLevel.View) throw new NotFoundException("vehicle.notFound", $"Vehicle {vehicleId} does not exist.", new { Id = vehicleId });
        if (!personal && level < AccessLevel.Edit)
            throw new ForbiddenException("dashboard.needEditToChange", "You need edit access to the vehicle's logs to change its dashboard.");
        return user;
    }
}
