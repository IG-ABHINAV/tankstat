using Tankstat.Api.Media;
using Tankstat.Domain.Access;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Api.GraphQL;

/// <summary>The vehicle type hides the internal picture id; clients use <c>pictureUrl</c>.</summary>
public sealed class VehicleType : ObjectType<Vehicle>
{
    protected override void Configure(IObjectTypeDescriptor<Vehicle> descriptor)
    {
        descriptor.Ignore(v => v.PictureImageId);
        descriptor.Ignore(v => v.SavedVersion); // the persistence layer's, not a client's
    }
}

/// <summary>Fields of <c>Vehicle</c> that depend on who is asking, or that live in other aggregates.</summary>
[ExtendObjectType<Vehicle>]
public sealed class VehicleExtensions
{
    /// <summary>Whether the current user may edit (and trash) this vehicle itself; the UI hides the actions otherwise.</summary>
    /// <remarks>This field and the next ones are asked for every row of a list of vehicles, so each goes through a loader (#311).</remarks>
    public async Task<bool> GetCanEdit([Parent] Vehicle vehicle, AccessLevelByOwnerLoader levels, CancellationToken ct) =>
        await levels.LoadAsync(vehicle.OwnerId, ct) >= AccessLevel.Edit;

    /// <summary>What the current user may do with this vehicle's logs: view, add/change (Edit) or also delete permanently (Delete).</summary>
    public Task<AccessLevel> GetLogAccess([Parent] Vehicle vehicle, LogLevelByVehicleLoader levels, CancellationToken ct) =>
        levels.LoadAsync(vehicle.Id, ct);

    /// <summary>Null when authentication is off or the owner no longer exists.</summary>
    public Task<UserRef?> GetOwner([Parent] Vehicle vehicle, UserRefLoader users, CancellationToken ct) =>
        users.LoadAsync(vehicle.OwnerId, ct);

    /// <summary>Who made its last change that counted (see <c>changedAt</c>, <c>lastChange</c>); null when nobody did (a photo) or no longer exists.</summary>
    public async Task<UserRef?> GetChangedBy([Parent] Vehicle vehicle, UserRefLoader users, CancellationToken ct) =>
        vehicle.ChangedById is { } id ? await users.LoadAsync(id, ct) : null;

    /// <summary>Address of the vehicle's picture, if it has one.</summary>
    public string? GetPictureUrl([Parent] Vehicle vehicle) => MediaUrls.Image(vehicle.PictureImageId);

    public Task<int> GetRefuelingCount([Parent] Vehicle vehicle, RefuelingCountLoader counts, CancellationToken ct) =>
        counts.LoadAsync(vehicle.Id, ct);
}
