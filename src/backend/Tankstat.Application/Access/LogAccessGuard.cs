using Tankstat.Application.Auth;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Access;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Access;

/// <summary>A live vehicle with what the current user may do with its logs (at least View).</summary>
public sealed record LogContext(Vehicle Vehicle, AccessLevel Level);

/// <summary>
/// The one place that decides who may see or change the logs of a vehicle (refuelings, expenses and their photos): the vehicle must be
/// live and visible, View shows, Edit changes. Everything that is not visible looks missing, a view-only user is forbidden.
/// </summary>
public sealed class LogAccessGuard(IVehicleRepository vehicles, AccessService access)
{
    /// <summary>The vehicle's logs context, or null when the vehicle is missing, trashed or may not be seen.</summary>
    public async Task<LogContext?> ForVehicleAsync(Guid? vehicleId, CancellationToken ct)
    {
        if (vehicleId is null || await vehicles.FindAsync(vehicleId.Value, ct) is not { } vehicle) return null;
        var level = await access.LogLevelAsync(vehicle, ct);
        return level >= AccessLevel.View ? new LogContext(vehicle, level) : null;
    }

    /// <summary>
    /// <see cref="ForVehicleAsync"/> for many vehicles in a fixed number of queries (#311: a page of vehicles): the contexts of those that are
    /// live and visible, keyed by vehicle id; the others are missing.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, LogContext>> ForVehiclesAsync(IReadOnlyCollection<Guid> vehicleIds, CancellationToken ct)
    {
        var live = await vehicles.ListByIdsAsync(vehicleIds, ct);
        var levels = await access.LogLevelsAsync(live, ct);
        return live.Where(v => levels.GetValueOrDefault(v.Id) >= AccessLevel.View)
            .ToDictionary(v => v.Id, v => new LogContext(v, levels[v.Id]));
    }

    /// <summary>The context for changing logs: unseen is <paramref name="notFound"/>, view-only is forbidden.</summary>
    public static LogContext RequireEdit(LogContext? context, Func<NotFoundException> notFound)
    {
        if (context is null) throw notFound();
        if (context.Level < AccessLevel.Edit) throw new ForbiddenException("vehicle.viewOnly", "You may only view this vehicle.");
        return context;
    }
}
