using Microsoft.Extensions.Logging;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Images;
using Tankstat.Domain;
using Tankstat.Domain.Images;
using Tankstat.Domain.Photos;

namespace Tankstat.Application.Photos;

/// <summary>
/// Photos picked for a log that is not saved yet: the add dialog uploads each one straight away (so nothing is lost if saving fails,
/// and the server already has it), and saving the log attaches them (<see cref="LogPhotoService.AttachDraftsAsync"/>). Uploading
/// needs Edit on the vehicle's logs; a draft is only ever visible to the user who uploaded it. Expired drafts (anyone's, on any
/// vehicle) are cleaned up whenever someone uploads a new one, so no background job is needed.
/// </summary>
public sealed class PhotoDraftService(
    LogAccessGuard guard, IPhotoDraftRepository drafts, PhotoDraftAccess own, ImageService images, AccessService access, TimeProvider clock,
    ILogger<PhotoDraftService> logger)
{
    /// <summary>Stores the picture (JPEG, PNG or WebP) as a draft for a new log of the vehicle and returns its id.</summary>
    public async Task<Guid> UploadAsync(Guid vehicleId, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var vehicle = LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(vehicleId, ct),
            () => new NotFoundException("vehicle.notFound", $"Vehicle {vehicleId} does not exist.", new { Id = vehicleId })).Vehicle;
        var me = await access.RequirePrincipalAsync(ct);

        await RemoveExpiredAsync(ct);
        if (await drafts.CountAsync(vehicle.Id, me.Id, ct) >= PhotoDraft.MaxPerUserAndVehicle)
            throw new DomainException("photo.tooManyDrafts", $"At most {PhotoDraft.MaxPerUserAndVehicle} photos can wait for a log to be saved.",
                new { Max = PhotoDraft.MaxPerUserAndVehicle });

        var imageId = await images.StoreAsync(data, ImageFolders.PhotoDrafts(vehicle.Id), ct);
        try
        {
            await drafts.AddAsync(PhotoDraft.Create(imageId, vehicle.OwnerId, vehicle.Id, me.Id, clock.GetUtcNow()), ct);
        }
        catch
        {
            await images.DeleteAsync([imageId], CancellationToken.None);
            throw;
        }
        logger.LogDebug("User {UserId} uploaded draft photo {ImageId} for vehicle {VehicleId}", me.Id, imageId, vehicle.Id);
        return imageId;
    }

    /// <summary>Removes one of the current user's drafts; anyone else's (or an unknown id) looks missing.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        var draft = await own.MineAsync(id, ct) ?? throw NotFound(id);
        await drafts.RemoveAsync([draft.Id], ct);
        await images.DeleteDraftPicturesAsync([draft], ct);
        logger.LogDebug("Draft photo {ImageId} removed", draft.Id);
    }

    /// <summary>
    /// The drafts to attach to a new log of the vehicle: each must be the current user's (or <paramref name="uploadedBy"/>'s: a parked
    /// change applied by someone else takes its sender's), for that vehicle and not expired, and together they must fit on one log.
    /// Checked before the log is saved, so a bad list saves nothing.
    /// </summary>
    public async Task<IReadOnlyList<PhotoDraft>> RequireAttachableAsync(Guid vehicleId, IReadOnlyCollection<Guid>? ids, CancellationToken ct, Guid? uploadedBy = null)
    {
        if (ids is null || ids.Count == 0) return [];
        var wanted = ids.Distinct().ToList();
        if (wanted.Count > LogPhoto.MaxPerLog)
            throw new DomainException("photo.tooMany", $"A log can have at most {LogPhoto.MaxPerLog} photos.", new { Max = LogPhoto.MaxPerLog });

        var uploader = uploadedBy ?? (await access.RequirePrincipalAsync(ct)).Id;
        var now = clock.GetUtcNow();
        var found = (await drafts.FindManyAsync(wanted, ct)).ToDictionary(d => d.Id);
        return wanted.Select(id => found.GetValueOrDefault(id) is { } d && d.UsableBy(uploader, vehicleId, now)
            ? d
            : throw Expired(id)).ToList();
    }

    /// <summary>
    /// Of the given drafts, the ones still waiting to be attached (the current user's, for the vehicle, not expired); the others were
    /// attached already or are gone. For an add that is sent again: it attaches what the first try did not get to.
    /// </summary>
    public async Task<IReadOnlyList<PhotoDraft>> StillWaitingAsync(Guid vehicleId, IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        if (ids is null || ids.Count == 0) return [];
        var me = await access.RequirePrincipalAsync(ct);
        var now = clock.GetUtcNow();
        return (await drafts.FindManyAsync(ids.Distinct().ToList(), ct)).Where(d => d.UsableBy(me.Id, vehicleId, now)).ToList();
    }

    /// <summary>The drafts were attached to a log: only their rows go, the pictures live on as its photos.</summary>
    internal Task ForgetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => drafts.RemoveAsync(ids, ct);

    private async Task RemoveExpiredAsync(CancellationToken ct)
    {
        var expired = await drafts.ListCreatedBeforeAsync(clock.GetUtcNow() - PhotoDraft.Lifetime, ct);
        if (expired.Count == 0) return;
        var ids = expired.Select(d => d.Id).ToList();
        await drafts.RemoveAsync(ids, ct);
        await images.DeleteDraftPicturesAsync(expired, ct);
        logger.LogDebug("Removed {Count} expired draft photos", ids.Count);
    }

    /// <summary>A draft that cannot be used (gone, expired, someone else's or another vehicle's): the user adds the photo again.</summary>
    internal static DomainException Expired(Guid id) => new("photo.draftExpired", "A photo is no longer available; add it again.", new { Id = id });

    private static NotFoundException NotFound(Guid id) => new("photo.notFound", $"The photo {id} does not exist.", new { Id = id });
}
