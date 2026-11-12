using Tankstat.Domain.Access;

namespace Tankstat.Domain.Photos;

/// <summary>
/// A photo uploaded for a log that is not saved yet (the add dialog sends each photo as soon as it is picked). Only its uploader sees
/// it; saving the log turns it into a <see cref="LogPhoto"/>. Drafts that were never used expire after <see cref="Lifetime"/>. The
/// picture is a <c>StoredImage</c> with the same id in the vehicle's draft folder, so it goes away with the vehicle.
/// </summary>
public sealed class PhotoDraft : IOwned
{
    /// <summary>Drafts one user may keep on one vehicle at a time: room for a full log plus an abandoned dialog.</summary>
    public const int MaxPerUserAndVehicle = 2 * LogPhoto.MaxPerLog;

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private PhotoDraft() { } // EF Core

    /// <summary>The id of the picture (<c>StoredImage</c>), which is also how the client names the draft.</summary>
    public Guid Id { get; private set; }

    /// <summary>The owner of the vehicle (a copy, like on the logs).</summary>
    public Guid OwnerId { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid CreatedById { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PhotoDraft Create(Guid imageId, Guid ownerId, Guid vehicleId, Guid createdById, DateTimeOffset now) =>
        new() { Id = imageId, OwnerId = ownerId, VehicleId = vehicleId, CreatedById = createdById, CreatedAt = now };

    public bool IsExpired(DateTimeOffset now) => now - CreatedAt >= Lifetime;

    /// <summary>A draft is shown to its uploader only, until it expires.</summary>
    public bool VisibleTo(Guid userId, DateTimeOffset now) => CreatedById == userId && !IsExpired(now);

    /// <summary>The uploader's draft for this vehicle, not expired: what may be made a photo or a picture of it.</summary>
    public bool UsableBy(Guid userId, Guid vehicleId, DateTimeOffset now) => VisibleTo(userId, now) && VehicleId == vehicleId;
}
