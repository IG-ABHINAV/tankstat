using Tankstat.Application.Access;
using Tankstat.Domain.Photos;

namespace Tankstat.Application.Photos;

/// <summary>
/// Whose uploads the current user may see (#313): a photo draft is its uploader's alone until it expires (<see cref="PhotoDraft.VisibleTo"/>),
/// and a photo added to a saved log is queued for reading for its uploader only. The one place that compares an upload with the current
/// user, as <see cref="LogAccessGuard"/> is for the logs: the use cases ask here instead of looking at <c>CreatedById</c> themselves.
/// </summary>
public sealed class PhotoDraftAccess(IPhotoDraftRepository drafts, AccessService access, TimeProvider clock)
{
    /// <summary>Whether <paramref name="draft"/> is the current user's and not expired.</summary>
    public async Task<bool> IsMineAsync(PhotoDraft draft, CancellationToken ct) =>
        draft.VisibleTo((await access.RequirePrincipalAsync(ct)).Id, clock.GetUtcNow());

    /// <summary>The current user's draft with this id; anyone else's, an expired one or an unknown id is null.</summary>
    public async Task<PhotoDraft?> MineAsync(Guid id, CancellationToken ct) =>
        await drafts.FindAsync(id, ct) is { } draft && await IsMineAsync(draft, ct) ? draft : null;

    /// <summary>The current user's drafts among <paramref name="ids"/> (in no particular order); the others are left out.</summary>
    public async Task<IReadOnlyList<PhotoDraft>> MineAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var me = (await access.RequirePrincipalAsync(ct)).Id;
        var now = clock.GetUtcNow();
        return [.. (await drafts.FindManyAsync(ids, ct)).Where(d => d.VisibleTo(me, now))];
    }

    /// <summary>Whether the current user uploaded <paramref name="photo"/> (a photo of a saved log).</summary>
    public async Task<bool> IsUploaderAsync(LogPhoto photo, CancellationToken ct) => photo.CreatedById == (await access.RequirePrincipalAsync(ct)).Id;
}
