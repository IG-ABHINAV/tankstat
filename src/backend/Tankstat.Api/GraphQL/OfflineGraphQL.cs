using GreenDonut;
using Tankstat.Application.Recurring;
using Tankstat.Application.Settings;
using Tankstat.Application.Sync;
using Tankstat.Domain.Settings;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Api.GraphQL;

/// <param name="From">A first, full download: the logs dated from this day (omit for all of them). Ignored with <paramref name="Since"/>.</param>
/// <param name="Since">A later download: the <c>watermark</c> of the one before; every row saved since then comes, whatever its date.</param>
/// <param name="After">Continues a download: the <c>next</c> of the page before (the other values are then taken from it).</param>
/// <param name="Take">At most this many refuelings and this many expenses per page (1 to 200).</param>
public sealed record OfflineChangesInput(Guid VehicleId, DateOnly? From, DateTimeOffset? Since, string? After, int Take);

/// <summary>One page of what a device downloads of a vehicle to show it offline.</summary>
/// <param name="Recurring">All of the vehicle's schedules with today's status, on the first page; empty on the others.</param>
/// <param name="Removed">What was removed for good since <c>since</c> (a later download's first page only). A vehicle's removal stands for everything below it.</param>
/// <param name="Next">Pass it as <c>after</c> for the next page; null when the download is complete.</param>
/// <param name="Watermark">Pass it as <c>since</c> for the next download of this vehicle.</param>
/// <param name="Resync">The last download is too old for what the server remembers: start afresh with a full download (nothing else came).</param>
/// <param name="Tyres">The vehicle's tyres whole, with the server's figures, on the first page (kept as the <c>vehicleTyres</c> answer); null on the others.</param>
public sealed record OfflineChanges(
    Vehicle Vehicle, IReadOnlyList<Refueling> Refuelings, IReadOnlyList<Expense> Expenses, IReadOnlyList<RecurringExpenseInfo> Recurring,
    IReadOnlyList<RemovedEntity> Removed, string? Next, DateTimeOffset Watermark, bool Resync, Tankstat.Application.Tyres.TyreOverview? Tyres = null);

/// <summary>A vehicle whose offline window differs from the user's default.</summary>
public sealed record OfflineVehicleWindow(Guid VehicleId, string Window);

/// <summary>
/// What the user's devices download for offline use: a window rule (<c>none</c>, <c>all</c>, <c>thisYear</c>, <c>thisAndLastYear</c>,
/// <c>from:yyyy-MM-dd</c> or <c>span:</c> an ISO 8601 duration such as <c>P2Y6M</c>) as the default and per vehicle. A device turns a rule
/// into a start date on its own clock.
/// </summary>
public sealed record OfflineSettingsInfo(string DefaultWindow, IReadOnlyList<OfflineVehicleWindow> Vehicles)
{
    public static OfflineSettingsInfo From(OfflineSettingsView v) => new(v.DefaultWindow, v.Vehicles.Select(s => new OfflineVehicleWindow(s.VehicleId, s.Window)).ToList());
}

public sealed record OfflineVehicleWindowInput(Guid VehicleId, string Window);

/// <param name="Vehicles">The whole set of vehicles with a window of their own; the others follow the default.</param>
public sealed record UpdateOfflineSettingsInput(string DefaultWindow, IReadOnlyList<OfflineVehicleWindowInput> Vehicles);

/// <summary>How many logs a download would bring, the trash included.</summary>
public sealed record LogCountSince(int Refuelings, int Expenses);

[ExtendObjectType(OperationTypeNames.Query)]
public sealed class OfflineQueries
{
    /// <summary>
    /// A page of what a device downloads of a vehicle to show it offline (View on the vehicle's logs): a first, full download takes the
    /// logs dated from <c>from</c>; a later one (<c>since</c>) every row saved since, in the trash too, plus what was removed for good.
    /// </summary>
    public async Task<OfflineChanges> GetOfflineChanges(OfflineChangesInput input, [Service] OfflineFeedService feed, CancellationToken ct)
    {
        var page = await feed.ChangesAsync(input.VehicleId, input.From, input.Since, input.After, input.Take, ct);
        return new OfflineChanges(page.Vehicle, page.Refuelings, page.Expenses, page.Recurring.Select(RecurringExpenseInfo.From).ToList(),
            page.Removed, page.Next, page.Watermark, page.Resync, page.Tyres);
    }

    /// <summary>What the current user's devices download for offline use (with authentication off: everyone's, the anonymous user's).</summary>
    public async Task<OfflineSettingsInfo> GetOfflineSettings([Service] OfflineSettingsService settings, CancellationToken ct) =>
        OfflineSettingsInfo.From(await settings.GetAsync(ct));
}

[ExtendObjectType(OperationTypeNames.Mutation)]
public sealed class OfflineMutations
{
    /// <summary>Replaces the user's offline windows as a whole. A rule that is not valid is <c>settings.offlineWindowInvalid</c>.</summary>
    public async Task<OfflineSettingsInfo> UpdateOfflineSettings(UpdateOfflineSettingsInput input, [Service] OfflineSettingsService settings, CancellationToken ct) =>
        OfflineSettingsInfo.From(await settings.SetAsync(input.DefaultWindow, input.Vehicles.Select(v => (v.VehicleId, v.Window)).ToList(), ct));
}

/// <summary>The log counts of the vehicles of a response for one start date, one query per date.</summary>
public sealed class LogCountSinceLoader(OfflineFeedService feed, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<(Guid VehicleId, DateOnly? From), LogCountSince>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<(Guid VehicleId, DateOnly? From), LogCountSince>> LoadBatchAsync(
        IReadOnlyList<(Guid VehicleId, DateOnly? From)> keys, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, DateOnly?), LogCountSince>();
        foreach (var group in keys.GroupBy(k => k.From))
        {
            var counts = await feed.CountSinceAsync([.. group.Select(k => k.VehicleId)], group.Key, ct);
            foreach (var key in group) result[key] = counts.TryGetValue(key.VehicleId, out var c) ? new LogCountSince(c.Refuelings, c.Expenses) : new LogCountSince(0, 0);
        }
        return result;
    }
}

[ExtendObjectType<Vehicle>]
public sealed class VehicleOfflineExtensions
{
    /// <summary>How many logs a download from this day (omit for all) would bring, for the estimates on the Offline data page.</summary>
    public async Task<LogCountSince> GetLogCountSince(DateOnly? from, [Parent] Vehicle vehicle, LogCountSinceLoader loader, CancellationToken ct) =>
        await loader.LoadAsync((vehicle.Id, from), ct) ?? new LogCountSince(0, 0);
}
