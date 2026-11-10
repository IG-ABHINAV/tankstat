using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tankstat.Application.Access;
using Tankstat.Application.Recurring;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Sync;

namespace Tankstat.Application.Sync;

/// <summary>
/// What a device downloads of a vehicle to show it offline, a page at a time. The first download of a vehicle (no <c>since</c>) brings
/// the logs dated from the device's window (<c>from</c>, null for all), the trash included; every later one brings only the rows saved since
/// the watermark of the one before, whatever their date, and what was removed for good meanwhile. The vehicle comes with every page, its
/// schedules (with today's status) and its tyres (whole, with their figures) with the first. The vehicle's logs are seen with the View access to them.
/// </summary>
public sealed class OfflineFeedService(
    LogAccessGuard guard, IOfflineFeedRepository feed, RecurringExpenseService recurring, Tyres.TyreService tyres, IOptionsSnapshot<SyncOptions> options,
    TimeProvider clock, ILogger<OfflineFeedService> logger)
{
    public const int MaxTake = 200;

    /// <summary>
    /// How far back a later download looks before the watermark it was given: a save that began before the previous download read the
    /// table may commit after it, with an earlier time. The device keeps what it already has, so a row that comes twice does no harm.
    /// </summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    private static long _lastSweepTicks;

    public async Task<OfflinePage> ChangesAsync(Guid vehicleId, DateOnly? from, DateTimeOffset? since, string? after, int take, CancellationToken ct)
    {
        if (take is < 1 or > MaxTake)
            throw new DomainException("sync.takeInvalid", $"A page holds 1 to {MaxTake} rows.", new { Max = MaxTake });
        var context = await guard.ForVehicleAsync(vehicleId, ct) ?? throw new NotFoundException("vehicle.notFound", "Vehicle not found.", new { Id = vehicleId });
        var now = clock.GetUtcNow();
        await SweepAsync(now, ct);

        var first = after is null;
        var cursor = first ? new OfflineCursor(vehicleId, since is null ? from : null, since, now, null, false, null, false) : OfflineCursor.Decode(after!, vehicleId);
        // The tombstones are read from the overlap before `since`: those must still be kept, or a removal could be missed.
        if (cursor.Since is { } old && old - Overlap < now - Retention)
        {
            logger.LogDebug("The last download of vehicle {VehicleId} is older than the tombstones kept: the device starts afresh", vehicleId);
            return new OfflinePage(context.Vehicle, [], [], [], [], null, now, Resync: true);
        }

        var lookFrom = cursor.Since - Overlap;
        var refuelings = cursor.RefuelingsDone ? [] : await feed.RefuelingsAsync(new OfflineRows(vehicleId, cursor.From, lookFrom, cursor.Refuelings, take + 1), ct);
        var expenses = cursor.ExpensesDone ? [] : await feed.ExpensesAsync(new OfflineRows(vehicleId, cursor.From, lookFrom, cursor.Expenses, take + 1), ct);
        var moreRefuelings = refuelings.Count > take;
        var moreExpenses = expenses.Count > take;
        var sentRefuelings = refuelings.Take(take).ToList();
        var sentExpenses = expenses.Take(take).ToList();

        var next = moreRefuelings || moreExpenses
            ? (cursor with
            {
                Refuelings = sentRefuelings.LastOrDefault() is { } r ? new OfflineKey(r.UpdatedAt, r.Id) : cursor.Refuelings,
                RefuelingsDone = cursor.RefuelingsDone || !moreRefuelings,
                Expenses = sentExpenses.LastOrDefault() is { } e ? new OfflineKey(e.UpdatedAt, e.Id) : cursor.Expenses,
                ExpensesDone = cursor.ExpensesDone || !moreExpenses,
            }).Encode()
            : null;
        var schedules = first ? await recurring.ListAsync(vehicleId, ct) : [];
        var tyreOverview = first ? await tyres.OverviewAsync(vehicleId, ct) : null;
        var removed = first && cursor.Since is { } moment ? await feed.RemovedSinceAsync(vehicleId, moment - Overlap, ct) : [];
        // The trash is shown to those who may change the logs (Edit), like online: to anyone else a log in the trash is gone, so it is
        // sent as removed (a device that had it drops it), never with its values. Paging above went by every row, the trash included.
        if (context.Level < AccessLevel.Edit)
        {
            removed = [.. removed,
                .. sentRefuelings.Where(r => r.IsDeleted).Select(r => new RemovedEntity(OfflineEntityType.Refueling, r.Id)),
                .. sentExpenses.Where(e => e.IsDeleted).Select(e => new RemovedEntity(OfflineEntityType.Expense, e.Id))];
            sentRefuelings = [.. sentRefuelings.Where(r => !r.IsDeleted)];
            sentExpenses = [.. sentExpenses.Where(e => !e.IsDeleted)];
        }

        logger.LogDebug("Sent {Refuelings} refuelings, {Expenses} expenses and {Removed} removals of vehicle {VehicleId} ({Kind} download, {More})",
            sentRefuelings.Count, sentExpenses.Count, removed.Count, vehicleId, cursor.Since is null ? "full" : "later", next is null ? "complete" : "more to come");
        return new OfflinePage(context.Vehicle, sentRefuelings, sentExpenses, schedules, removed, next, cursor.Watermark, Resync: false, tyreOverview);
    }

    /// <summary>For vehicles the caller already got through an access check (a resolver of the vehicle).</summary>
    public Task<IReadOnlyDictionary<Guid, LogCounts>> CountSinceAsync(IReadOnlyCollection<Guid> vehicleIds, DateOnly? from, CancellationToken ct) =>
        feed.CountSinceAsync(vehicleIds, from, ct);

    private TimeSpan Retention => TimeSpan.FromDays(options.Value.TombstoneRetentionDays);

    /// <summary>Old tombstones go on the same lazy path, at most once an hour (no background job).</summary>
    private async Task SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < TimeSpan.TicksPerHour || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last) return;
        var swept = await feed.SweepTombstonesAsync(now - Retention, ct);
        if (swept > 0) logger.LogDebug("Removed {Count} tombstones older than {Days} days", swept, options.Value.TombstoneRetentionDays);
    }
}
