using Tankstat.Application.Access;
using Tankstat.Application.Expenses;
using Tankstat.Application.Recurring;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Access;
using Tankstat.Domain.Sync;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Sync;

/// <summary>What a change concerns.</summary>
public readonly record struct SyncTargetKey(OfflineEntityType Type, Guid Id);

/// <summary>
/// What a parked change concerns, as it is on the server now: the entity (<see cref="Vehicle"/>, <see cref="Refueling"/>,
/// <see cref="Expense"/>, a tyre set or installation, or a schedule with its status), and whether it is in the trash.
/// </summary>
public sealed record SyncTarget(ISynced Entity, bool Trashed, RecurringItem? Schedule = null);

/// <summary>
/// Tells whoever decides about a parked change what is on the server now. Only what the reader may see is told, whoever sent the change (a
/// sender whose access ended learns nothing new): View on the vehicle's logs (on the vehicle for a vehicle) for what is live, Edit for what
/// is in the trash, as the trash lists. A log of a vehicle in the trash, something purged or a deleted schedule is not told at all.
/// </summary>
public sealed class SyncTargetService(
    IVehicleRepository vehicles, IRefuelingRepository refuelings, IExpenseRepository expenses, IRecurringExpenseRepository schedules,
    RecurringExpenseService recurring, Tyres.ITyreRepository tyres, AccessService access)
{
    /// <summary>What a kind of change concerns, when it concerns one thing that a person could have changed meanwhile (not adds, visits or photos).</summary>
    public static OfflineEntityType? TargetOf(SyncChangeKind kind) => kind switch
    {
        SyncChangeKind.UpdateRefueling or SyncChangeKind.DeleteRefueling or SyncChangeKind.RestoreRefueling => OfflineEntityType.Refueling,
        SyncChangeKind.UpdateExpense or SyncChangeKind.DeleteExpense or SyncChangeKind.RestoreExpense => OfflineEntityType.Expense,
        SyncChangeKind.UpdateRecurringExpense or SyncChangeKind.DeleteRecurringExpense => OfflineEntityType.RecurringExpense,
        SyncChangeKind.UpdateVehicle or SyncChangeKind.DeleteVehicle or SyncChangeKind.RestoreVehicle => OfflineEntityType.Vehicle,
        SyncChangeKind.UpdateTyreSet or SyncChangeKind.SetTyreRoughDistance or SyncChangeKind.RetireTyreSet or SyncChangeKind.UnretireTyreSet
            or SyncChangeKind.DeleteTyreSet or SyncChangeKind.RestoreTyreSet => OfflineEntityType.TyreSet,
        SyncChangeKind.UpdateTyreInstallation or SyncChangeKind.DeleteTyreInstallation or SyncChangeKind.RestoreTyreInstallation => OfflineEntityType.TyreInstallation,
        SyncChangeKind.UpdateTyreEntry or SyncChangeKind.DeleteTyreEntry or SyncChangeKind.RestoreTyreEntry => OfflineEntityType.TyreEntry,
        SyncChangeKind.BreakUpTyreSet => OfflineEntityType.TyreSet,
        SyncChangeKind.UpdateTyre or SyncChangeKind.DeleteTyre or SyncChangeKind.RestoreTyre => OfflineEntityType.Tyre,
        SyncChangeKind.UpdateTyrePlace or SyncChangeKind.DeleteTyrePlace or SyncChangeKind.RestoreTyrePlace => OfflineEntityType.TyrePlace,
        _ => null,
    };

    /// <summary>The targets as they are now, those the reader may see, with a fixed number of queries per kind of target.</summary>
    public async Task<IReadOnlyDictionary<SyncTargetKey, SyncTarget>> CurrentAsync(IReadOnlyCollection<SyncTargetKey> keys, CancellationToken ct)
    {
        var result = new Dictionary<SyncTargetKey, SyncTarget>();
        Guid[] Ids(OfflineEntityType type) => [.. keys.Where(k => k.Type == type).Select(k => k.Id).Distinct()];

        // Logs: of a live vehicle the reader may see; one in the trash only for whoever could restore it.
        var refuelingRows = await refuelings.ListByIdsIncludingDeletedAsync(Ids(OfflineEntityType.Refueling), ct);
        var expenseRows = await expenses.ListByIdsIncludingDeletedAsync(Ids(OfflineEntityType.Expense), ct);
        var logVehicles = await vehicles.ListByIdsAsync([.. refuelingRows.Select(r => r.VehicleId).Concat(expenseRows.Select(e => e.VehicleId)).Distinct()], ct);
        var logLevels = await access.LogLevelsAsync(logVehicles, ct);
        bool MaySee(Guid vehicleId, bool trashed) => logLevels.GetValueOrDefault(vehicleId) >= (trashed ? AccessLevel.Edit : AccessLevel.View);
        foreach (var r in refuelingRows.Where(r => MaySee(r.VehicleId, r.IsDeleted)))
            result[new SyncTargetKey(OfflineEntityType.Refueling, r.Id)] = new SyncTarget(r, r.IsDeleted);
        foreach (var e in expenseRows.Where(e => MaySee(e.VehicleId, e.IsDeleted)))
            result[new SyncTargetKey(OfflineEntityType.Expense, e.Id)] = new SyncTarget(e, e.IsDeleted);

        // Tyres follow the logs' rules; an installation or a line of a set in the trash is in the trash with it.
        var sets = await tyres.ListSetsAsync(Ids(OfflineEntityType.TyreSet), ct);
        var installations = await tyres.ListInstallationsAsync(Ids(OfflineEntityType.TyreInstallation), ct);
        var entries = await tyres.ListEntriesAsync(Ids(OfflineEntityType.TyreEntry), ct);
        // Single tyres and their places (#289): a place of a tyre in the trash, a tyre of a set in the trash, is in the trash with it.
        var places = await tyres.ListPlacesAsync(Ids(OfflineEntityType.TyrePlace), ct);
        var singleTyres = await tyres.ListTyresAsync([.. Ids(OfflineEntityType.Tyre).Concat(places.Select(p => p.TyreId)).Distinct()], ct);
        var setsOfInstallations = await tyres.ListSetsAsync(
            [.. installations.Select(s => s.SetId).Concat(entries.Where(e => e.SetId is not null).Select(e => e.SetId!.Value)).Concat(singleTyres.Select(t => t.SetId)).Distinct()], ct);
        var tyreVehicles = await vehicles.ListByIdsAsync(
            [.. sets.Select(s => s.VehicleId).Concat(installations.Select(s => s.VehicleId)).Concat(entries.Select(e => e.VehicleId)).Concat(singleTyres.Select(t => t.VehicleId)).Distinct()], ct);
        var tyreLevels = await access.LogLevelsAsync(tyreVehicles, ct);
        bool MaySeeTyres(Guid vehicleId, bool trashed) => tyreLevels.GetValueOrDefault(vehicleId) >= (trashed ? AccessLevel.Edit : AccessLevel.View);
        foreach (var set in sets.Where(s => MaySeeTyres(s.VehicleId, s.IsDeleted)))
            result[new SyncTargetKey(OfflineEntityType.TyreSet, set.Id)] = new SyncTarget(set, set.IsDeleted);
        foreach (var installation in installations)
        {
            var trashed = installation.IsDeleted || setsOfInstallations.Any(s => s.Id == installation.SetId && s.IsDeleted);
            if (MaySeeTyres(installation.VehicleId, trashed)) result[new SyncTargetKey(OfflineEntityType.TyreInstallation, installation.Id)] = new SyncTarget(installation, trashed);
        }
        foreach (var entry in entries)
        {
            var trashed = entry.IsDeleted || setsOfInstallations.Any(s => s.Id == entry.SetId && s.IsDeleted);
            if (MaySeeTyres(entry.VehicleId, trashed)) result[new SyncTargetKey(OfflineEntityType.TyreEntry, entry.Id)] = new SyncTarget(entry, trashed);
        }
        bool TyreTrashed(Domain.Tyres.Tyre tyre) => tyre.IsDeleted || setsOfInstallations.Any(s => s.Id == tyre.SetId && s.IsDeleted);
        foreach (var tyre in singleTyres.Where(t => Ids(OfflineEntityType.Tyre).Contains(t.Id)))
            if (MaySeeTyres(tyre.VehicleId, TyreTrashed(tyre))) result[new SyncTargetKey(OfflineEntityType.Tyre, tyre.Id)] = new SyncTarget(tyre, TyreTrashed(tyre));
        foreach (var place in places)
        {
            var trashed = place.IsDeleted || singleTyres.FirstOrDefault(t => t.Id == place.TyreId) is not { } of || TyreTrashed(of);
            if (MaySeeTyres(place.VehicleId, trashed)) result[new SyncTargetKey(OfflineEntityType.TyrePlace, place.Id)] = new SyncTarget(place, trashed);
        }

        // Schedules (deleted for good, never in a trash): with their status, through the list that checks the vehicles' logs.
        var scheduleIds = Ids(OfflineEntityType.RecurringExpense);
        if (scheduleIds.Length > 0)
        {
            var found = await schedules.FindManyAsync(scheduleIds, ct);
            var listed = await recurring.ListForVehiclesAsync(await vehicles.ListByIdsAsync([.. found.Select(s => s.VehicleId).Distinct()], ct), ct);
            foreach (var item in listed.Values.SelectMany(items => items).Where(i => scheduleIds.Contains(i.Item.Id)))
                result[new SyncTargetKey(OfflineEntityType.RecurringExpense, item.Item.Id)] = new SyncTarget(item.Item, false, item);
        }

        // Vehicles: a log grant shows the vehicle; one in the trash only for whoever may restore the vehicle itself.
        var vehicleRows = await vehicles.ListByIdsIncludingDeletedAsync(Ids(OfflineEntityType.Vehicle), ct);
        var seen = await access.LogLevelsAsync(vehicleRows, ct);
        foreach (var v in vehicleRows)
        {
            var visible = v.IsDeleted ? await access.VehicleLevelAsync(v, ct) >= AccessLevel.Edit : seen.GetValueOrDefault(v.Id) >= AccessLevel.View;
            if (visible) result[new SyncTargetKey(OfflineEntityType.Vehicle, v.Id)] = new SyncTarget(v, v.IsDeleted);
        }
        return result;
    }
}
