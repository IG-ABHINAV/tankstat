using GreenDonut;
using Tankstat.Application.Access;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Access;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Api.GraphQL;

// What every row of a list of logs (refuelings, expenses, both trashes) asks about: loaded for the whole response at once. Asked row by row,
// a page of a hundred rows ran several hundred queries in parallel, which can exhaust a database's connection pool (the request then failed
// on whichever row was last in line, "Unexpected Execution Error").

/// <summary>What the user may do with the logs of each vehicle of a response.</summary>
public sealed class LogLevelByVehicleLoader(RefuelingService refuelings, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, AccessLevel>(scheduler, options)
{
    protected override Task<IReadOnlyDictionary<Guid, AccessLevel>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct) =>
        refuelings.LevelsForVehiclesAsync(keys, ct);
}

/// <summary>Who logged each row (none for a user that is gone).</summary>
public sealed class UserRefLoader(IUserRepository users, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, UserRef?>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, UserRef?>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var found = (await users.ListByIdsAsync(keys, ct)).ToDictionary(u => u.Id);
        return keys.ToDictionary(k => k, k => found.TryGetValue(k, out var user) ? UserRef.From(user) : null);
    }
}

/// <summary>What the user may do with each owner's data (#311: a list of vehicles says for every row whether the user may edit it).</summary>
public sealed class AccessLevelByOwnerLoader(AccessService access, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, AccessLevel>(scheduler, options)
{
    protected override Task<IReadOnlyDictionary<Guid, AccessLevel>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct) =>
        access.LevelsAsync(keys, ct);
}

/// <summary>Each vehicle's number of refuellings not in the trash (0 for one the user may not see), for a list of vehicles (#311).</summary>
public sealed class RefuelingCountLoader(RefuelingService refuelings, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, int>(scheduler, options)
{
    protected override Task<IReadOnlyDictionary<Guid, int>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct) =>
        refuelings.CountsAsync(keys, ct);
}

/// <summary>Each row's vehicle, trashed ones too (a log in the trash shows its vehicle's name and units). Only for already authorised rows.</summary>
public sealed class VehicleIncludingDeletedLoader(IVehicleRepository vehicles, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, Vehicle?>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, Vehicle?>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var found = (await vehicles.ListByIdsIncludingDeletedAsync(keys, ct)).ToDictionary(v => v.Id);
        return keys.ToDictionary(k => k, k => found.GetValueOrDefault(k));
    }
}
