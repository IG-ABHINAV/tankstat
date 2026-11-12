using Microsoft.Extensions.Options;
using Tankstat.Application.Auth;
using Tankstat.Domain.Access;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Access;

/// <summary>
/// The access layer above the raw entities: every use case asks it who is acting and what they may touch,
/// instead of looking at owners itself. Works for any <see cref="IOwned"/> entity.
/// </summary>
public sealed class AccessService(
    ICurrentUser current, IOptions<AuthOptions> auth, IAccessGrantRepository grants, IAccessSettingsRepository settings,
    IResourceGrantRepository resourceGrants)
{
    public async Task<Principal> RequirePrincipalAsync(CancellationToken ct)
    {
        if (await current.GetPrincipalAsync(ct) is { } principal) return principal;
        if (auth.Value.Mode == AuthMode.None) return Principal.Anonymous;
        throw new UnauthenticatedException();
    }

    /// <summary>Administrators only; never available when authentication is off (there is no real administrator then).</summary>
    public async Task<Principal> RequireAdminAsync(CancellationToken ct)
    {
        var principal = await RequirePrincipalAsync(ct);
        if (principal.IsAnonymous || !principal.IsAdmin) throw new ForbiddenException("auth.adminRequired", "Administrator rights are required.");
        return principal;
    }

    public async Task<AccessLevel> LevelAsync(Guid ownerId, CancellationToken ct)
    {
        var principal = await RequirePrincipalAsync(ct);
        if (principal.IsAdmin || principal.Id == ownerId) return AccessLevel.Delete;

        var defaults = (await settings.GetAsync(ct)).DefaultLevelForOthers;
        var granted = await grants.ListForGranteeAsync(principal.Id, ct);
        return AccessPolicy.Resolve(principal.Id, principal.IsAdmin, ownerId, defaults, granted);
    }

    public async Task<bool> CanAsync(Guid ownerId, AccessLevel level, CancellationToken ct) =>
        await LevelAsync(ownerId, ct) >= level;

    /// <summary>
    /// <see cref="LevelAsync"/> for many owners at once, with a fixed number of queries (the settings and the user's grants, once each) however
    /// many there are (#311: a list of vehicles says for every row whether the user may edit it).
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, AccessLevel>> LevelsAsync(IReadOnlyCollection<Guid> ownerIds, CancellationToken ct)
    {
        var distinct = ownerIds.Distinct().ToList();
        if (distinct.Count == 0) return new Dictionary<Guid, AccessLevel>();
        var principal = await RequirePrincipalAsync(ct);
        if (principal.IsAdmin || distinct.All(o => o == principal.Id)) return distinct.ToDictionary(o => o, _ => AccessLevel.Delete);

        var defaults = (await settings.GetAsync(ct)).DefaultLevelForOthers;
        var granted = await grants.ListForGranteeAsync(principal.Id, ct);
        return distinct.ToDictionary(o => o, o => AccessPolicy.Resolve(principal.Id, principal.IsAdmin, o, defaults, granted));
    }

    /// <summary>The owners whose data the current user may access at (at least) <paramref name="level"/>.</summary>
    public Task<OwnerScope> ScopeAsync(AccessLevel level, CancellationToken ct) => ScopeAsync(level, administration: true, ct);

    /// <param name="administration">
    /// Whether an administrator's right to everything counts. Without it, an administrator gets what any other user would (their own data,
    /// grants and the instance default), e.g. for what concerns them personally; the anonymous user (authentication off) keeps everything.
    /// </param>
    private async Task<OwnerScope> ScopeAsync(AccessLevel level, bool administration, CancellationToken ct)
    {
        var principal = await RequirePrincipalAsync(ct);
        if (principal.IsAdmin && (administration || principal.IsAnonymous)) return OwnerScope.All;

        var defaults = (await settings.GetAsync(ct)).DefaultLevelForOthers;
        if (defaults >= level) return OwnerScope.All;

        var owners = (await grants.ListForGranteeAsync(principal.Id, ct))
            .Where(g => g.Level >= level).Select(g => g.OwnerId).Append(principal.Id);
        return OwnerScope.Of(owners);
    }

    // ---- Vehicles and their logs -----------------------------------------------------------------------------

    /// <summary>
    /// Access to the vehicle itself. A grant on its logs lets the user see the vehicle (so it shows up in their list and
    /// they can open it) but never change it.
    /// </summary>
    public async Task<AccessLevel> VehicleLevelAsync(Vehicle vehicle, CancellationToken ct)
    {
        var level = await LevelAsync(vehicle.OwnerId, ct);
        if (level >= AccessLevel.View) return level;
        return await LogGrantAsync(vehicle.Id, ct) is not AccessLevel.None ? AccessLevel.View : AccessLevel.None;
    }

    /// <summary>Access to the logs (refuelings, ...) of a vehicle: the owner-based access or the vehicle's log grant, whichever is more.</summary>
    public async Task<AccessLevel> LogLevelAsync(Vehicle vehicle, CancellationToken ct)
    {
        var level = await LevelAsync(vehicle.OwnerId, ct);
        return level == AccessLevel.Delete ? level : (AccessLevel)Math.Max((int)level, (int)await LogGrantAsync(vehicle.Id, ct));
    }

    /// <summary>
    /// <see cref="LogLevelAsync"/> for many vehicles at once, with a fixed number of queries (the settings, the user's grants and their log
    /// grants, once each) however many there are: lists of logs say for every row what the user may do, and a page of a hundred rows asked
    /// each on its own would run hundreds of queries at once.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, AccessLevel>> LogLevelsAsync(IReadOnlyCollection<Vehicle> vehicles, CancellationToken ct)
    {
        var distinct = vehicles.DistinctBy(v => v.Id).ToList();
        if (distinct.Count == 0) return new Dictionary<Guid, AccessLevel>();
        var principal = await RequirePrincipalAsync(ct);
        if (principal.IsAdmin || distinct.All(v => v.OwnerId == principal.Id)) return distinct.ToDictionary(v => v.Id, _ => AccessLevel.Delete);

        var defaults = (await settings.GetAsync(ct)).DefaultLevelForOthers;
        var granted = await grants.ListForGranteeAsync(principal.Id, ct);
        var logGrants = (await resourceGrants.ListForGranteeAsync(principal.Id, ResourceType.Vehicle, GrantedFeature.Logs, ct))
            .GroupBy(g => g.ResourceId).ToDictionary(g => g.Key, g => g.Max(x => x.Level));
        return distinct.ToDictionary(v => v.Id, v =>
        {
            var level = AccessPolicy.Resolve(principal.Id, principal.IsAdmin, v.OwnerId, defaults, granted);
            return level == AccessLevel.Delete ? level : (AccessLevel)Math.Max((int)level, (int)logGrants.GetValueOrDefault(v.Id));
        });
    }

    private async Task<AccessLevel> LogGrantAsync(Guid vehicleId, CancellationToken ct)
    {
        var principal = await RequirePrincipalAsync(ct);
        var grant = await resourceGrants.FindAsync(ResourceType.Vehicle, vehicleId, principal.Id, GrantedFeature.Logs, ct);
        return grant?.Level ?? AccessLevel.None;
    }

    /// <summary>Vehicles the user may see at <paramref name="level"/>: owner-based, plus (for viewing) the vehicles whose logs they were granted.</summary>
    public async Task<OwnerScope> VehicleScopeAsync(AccessLevel level, CancellationToken ct)
    {
        var owners = await ScopeAsync(level, ct);
        if (owners.IsAll || level != AccessLevel.View) return owners;
        return OwnerScope.Of(owners.Owners, await GrantedVehiclesAsync(AccessLevel.Edit, ct));
    }

    /// <summary>Logs the user may access at <paramref name="level"/>: owner-based, plus the vehicles whose log grant is at least that level.</summary>
    public Task<OwnerScope> LogScopeAsync(AccessLevel level, CancellationToken ct) => LogScopeAsync(level, administration: true, ct);

    /// <summary>
    /// Like <see cref="LogScopeAsync(AccessLevel, CancellationToken)"/>, but an administrator's right to everything does not count: the logs
    /// that concern the user personally (theirs, granted, or open to everyone), e.g. what they are notified about.
    /// </summary>
    public Task<OwnerScope> PersonalLogScopeAsync(AccessLevel level, CancellationToken ct) => LogScopeAsync(level, administration: false, ct);

    private async Task<OwnerScope> LogScopeAsync(AccessLevel level, bool administration, CancellationToken ct)
    {
        var owners = await ScopeAsync(level, administration, ct);
        if (owners.IsAll) return owners;
        return OwnerScope.Of(owners.Owners, await GrantedVehiclesAsync(level, ct));
    }

    private async Task<IReadOnlyList<Guid>> GrantedVehiclesAsync(AccessLevel atLeast, CancellationToken ct)
    {
        var principal = await RequirePrincipalAsync(ct);
        var granted = await resourceGrants.ListForGranteeAsync(principal.Id, ResourceType.Vehicle, GrantedFeature.Logs, ct);
        return granted.Where(g => g.Level >= atLeast).Select(g => g.ResourceId).ToList();
    }
}
