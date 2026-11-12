using Microsoft.Extensions.Logging;
using Tankstat.Application.Access;
using Tankstat.Application.Images;
using Tankstat.Domain;
using Tankstat.Application.Odometers;
using Tankstat.Application.Tyres;
using Tankstat.Domain.Access;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Sync;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Vehicles;

public sealed class VehicleService(
    IVehicleRepository vehicles, IRefuelingRepository refuelings, ITyreRepository tyres, AccessService access, OdometerService odometer, ImageService images, TimeProvider clock,
    ILogger<VehicleService> logger)
{
    /// <summary>One page of the full vehicle list: an administrator feature, like the rest of the administration.</summary>
    public async Task<IReadOnlyList<Vehicle>> ListAsync(VehicleQuery query, CancellationToken ct)
    {
        await access.RequireAdminAsync(ct);
        return await vehicles.ListAsync(await access.VehicleScopeAsync(AccessLevel.View, ct), query.Normalized(), ct);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await access.RequireAdminAsync(ct);
        return await vehicles.CountAsync(await access.VehicleScopeAsync(AccessLevel.View, ct), null, ct);
    }

    /// <summary>
    /// One page of the vehicles the user may see (their own and the ones shared with them), in their own order (the ones they arranged
    /// first, the rest by name), optionally only those whose name or license plate contains <paramref name="search"/>: what the home page
    /// shows. Everyone may ask.
    /// </summary>
    public async Task<IReadOnlyList<Vehicle>> ListMineAsync(string? search, int skip, int take, CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        return await vehicles.ListAsync(await access.VehicleScopeAsync(AccessLevel.View, ct), new VehicleQuery(Skip: skip, Take: take, Search: search, OrderedFor: user.Id), ct);
    }

    public Task<IReadOnlyList<Vehicle>> ListMineAsync(CancellationToken ct) => ListMineAsync(null, 0, VehicleQuery.MaxTake, ct);

    /// <summary>How many vehicles <see cref="ListMineAsync(string?, int, int, CancellationToken)"/> would return in all (for the pager).</summary>
    public async Task<int> CountMineAsync(string? search, CancellationToken ct) =>
        await vehicles.CountAsync(await access.VehicleScopeAsync(AccessLevel.View, ct), new VehicleQuery(Search: search).Normalized().Search, ct);

    /// <summary>Null when the vehicle does not exist or the user may not see it (existence is not revealed).</summary>
    public async Task<Vehicle?> FindAsync(Guid id, CancellationToken ct)
    {
        var vehicle = await vehicles.FindAsync(id, ct);
        return vehicle is not null && await access.VehicleLevelAsync(vehicle, ct) >= AccessLevel.View ? vehicle : null;
    }

    /// <param name="id">The id the client chose; a vehicle it already added with this id is answered as it is (see <see cref="ClientIds"/>).</param>
    public async Task<Vehicle> AddAsync(string name, string? licensePlate, FuelType fuelType, MeasurementUnits units, CancellationToken ct, Guid? id = null)
    {
        var owner = await access.RequirePrincipalAsync(ct);
        if (id is { } given && await vehicles.FindIncludingDeletedAsync(given, ct) is { } existing) return Repeated(existing, owner.Id);
        var vehicle = Vehicle.Create(owner.Id, name, licensePlate, fuelType, units, id);
        if (!await vehicles.AddAsync(vehicle, ct)) // the same add, sent twice at once: the other one saved it
            return Repeated(await vehicles.FindIncludingDeletedAsync(vehicle.Id, ct) ?? vehicle, owner.Id);
        logger.LogDebug("User {UserId} added vehicle {VehicleId}", owner.Id, vehicle.Id);
        return vehicle;
    }

    /// <param name="expectedVersion">The version the change was made from; refused when the vehicle was saved since (see <see cref="VersionCheck"/>).</param>
    public async Task<Vehicle> UpdateAsync(
        Guid id, string name, string? licensePlate, FuelType fuelType, MeasurementUnits? newUnits, CancellationToken ct, int? expectedVersion = null)
    {
        var vehicle = await EditableAsync(id, includeDeleted: false, ct);
        VersionCheck.Require(expectedVersion, vehicle.Version);
        // Numbers are stored as entered, so the units may only change while the vehicle has no logs (otherwise old numbers would silently change meaning).
        var units = newUnits ?? vehicle.Units;
        // A tyre set's rough figure is a number in the distance unit too (#287); a tyre log's reading is one of the readings.
        if (units != vehicle.Units && (await odometer.HasReadingsAsync(vehicle.Id, ct) || await refuelings.AnyForVehicleAsync(vehicle.Id, ct) || await tyres.HasRoughFiguresAsync(vehicle.Id, ct)))
            throw new DomainException("vehicle.unitsLocked", "The units cannot be changed once the vehicle has logs.");
        vehicle.Update(name, licensePlate, fuelType, units, (await access.RequirePrincipalAsync(ct)).Id);
        await vehicles.UpdateAsync(vehicle, ct);
        logger.LogDebug("Vehicle {VehicleId} updated", vehicle.Id);
        return vehicle;
    }

    /// <summary>Moves the vehicle to the trash; it can be restored until it is purged.</summary>
    public async Task<Vehicle> DeleteAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var vehicle = await EditableAsync(id, includeDeleted: false, ct);
        VersionCheck.Require(expectedVersion, vehicle.Version);
        vehicle.MarkDeleted(clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
        await vehicles.UpdateAsync(vehicle, ct);
        logger.LogDebug("Vehicle {VehicleId} moved to the trash", vehicle.Id);
        return vehicle;
    }

    /// <summary>The vehicle when it is in the trash and the user may edit it (restore it), else null (see <see cref="Refuelings.RefuelingService.InTrashAsync"/>).</summary>
    public async Task<Vehicle?> InTrashAsync(Guid id, CancellationToken ct) =>
        await vehicles.FindIncludingDeletedAsync(id, ct) is { IsDeleted: true } vehicle && await access.VehicleLevelAsync(vehicle, ct) >= AccessLevel.Edit ? vehicle : null;

    public async Task<Vehicle> RestoreAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var vehicle = await EditableAsync(id, includeDeleted: true, ct);
        VersionCheck.Require(expectedVersion, vehicle.Version);
        vehicle.Restore((await access.RequirePrincipalAsync(ct)).Id);
        await vehicles.UpdateAsync(vehicle, ct);
        logger.LogDebug("Vehicle {VehicleId} restored from the trash", vehicle.Id);
        return vehicle;
    }

    /// <summary>The trashed vehicles the user could restore (those they may edit).</summary>
    public async Task<IReadOnlyList<Vehicle>> ListTrashAsync(VehicleQuery query, CancellationToken ct) =>
        await vehicles.ListDeletedAsync(await access.ScopeAsync(AccessLevel.Edit, ct), query.Normalized(), ct);

    public async Task<int> CountTrashAsync(CancellationToken ct) =>
        await vehicles.CountDeletedAsync(await access.ScopeAsync(AccessLevel.Edit, ct), ct);

    /// <summary>How many trashed vehicles the user may delete permanently (only owners, administrators and Delete grants may).</summary>
    public async Task<int> CountDeletableTrashAsync(CancellationToken ct) =>
        await vehicles.CountDeletedAsync(await access.ScopeAsync(AccessLevel.Delete, ct), ct);

    /// <summary>Permanently removes the trashed vehicles (and their logs) the user has Delete access to. Returns how many vehicles were removed.</summary>
    public async Task<int> EmptyTrashAsync(CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        var purged = await vehicles.PurgeAsync(await access.ScopeAsync(AccessLevel.Delete, ct), ct);
        await images.DeleteVehicleFilesAsync(purged.VehicleIds, purged.ImageIds, ct); // their pictures and photos go with them
        if (purged.Count > 0) logger.LogInformation("User {UserId} emptied the vehicle trash: {Count} vehicles deleted for good ({VehicleIds})", user.Id, purged.Count, Listed(purged.VehicleIds));
        return purged.Count;
    }

    /// <summary>The first ids of a list and how many more there are: a line of the log stays a line, however much was deleted at once.</summary>
    private static string Listed(IReadOnlyList<Guid> ids) => string.Join(", ", ids.Take(20)) + (ids.Count > 20 ? $" and {ids.Count - 20} more" : "");

    /// <summary>A vehicle this user already added with the id is the answer; another one's id is refused.</summary>
    private Vehicle Repeated(Vehicle existing, Guid ownerId)
    {
        var same = existing.OwnerId == ownerId;
        if (same) logger.LogDebug("Vehicle {VehicleId} was already added; the add came again", existing.Id);
        return ClientIds.Repeat(existing, same, existing.Id);
    }

    /// <summary>Loads a vehicle for changing; one the user cannot see looks missing, one they can only view is forbidden.</summary>
    private async Task<Vehicle> EditableAsync(Guid id, bool includeDeleted, CancellationToken ct)
    {
        var vehicle = includeDeleted ? await vehicles.FindIncludingDeletedAsync(id, ct) : await vehicles.FindAsync(id, ct);
        var level = vehicle is null ? AccessLevel.None : await access.VehicleLevelAsync(vehicle, ct);
        if (vehicle is null || level < AccessLevel.View) throw new NotFoundException("vehicle.notFound", $"Vehicle {id} does not exist.", new { Id = id });
        if (level < AccessLevel.Edit) throw new Auth.ForbiddenException("vehicle.viewOnly", "You may only view this vehicle.");
        return vehicle;
    }
}
