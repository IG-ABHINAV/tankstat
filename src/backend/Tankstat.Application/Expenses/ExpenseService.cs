using Microsoft.Extensions.Logging;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Odometers;
using Tankstat.Application.Photos;
using Tankstat.Application.Recognition;
using Tankstat.Application.Sync;
using Tankstat.Application.Tyres;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Photos;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Expenses;

/// <param name="Amount">Null only while a photo of the expense is still being read: it fills the amount in later.</param>
/// <param name="Currency">Required with an amount when adding; when updating, omit it to keep the expense's currency.</param>
/// <param name="Odometer">Null when the odometer was not noted (a photo that is still being read may fill it in).</param>
/// <param name="Discount">What was taken off <c>Amount</c>, which is what was paid; on an update null keeps the expense's (a build from before discounts sends none) and 0 is none.</param>
public sealed record ExpenseInput(DateOnly Date, string Title, string? Category, decimal? Amount, string? Currency, long? Odometer, string? Note, decimal? Discount = null);

/// <summary>
/// Expenses of a vehicle (service, insurance, ...). They follow the access rules of the vehicle's logs exactly like refuelings:
/// Edit may add, change, trash and restore; only Delete may delete permanently.
/// </summary>
public sealed class ExpenseService(
    LogAccessGuard guard, IExpenseRepository expenses, AccessService access, OdometerService odometer, LogPhotoService photos, LogPhotoFiller filler,
    TyreService tyres, TimeProvider clock, ILogger<ExpenseService> logger)
{
    private DateOnly LatestAllowedDate => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(1);

    public async Task<IReadOnlyList<Expense>> ListAsync(Guid vehicleId, ExpenseQuery query, CancellationToken ct) =>
        await VisibleVehicleAsync(vehicleId, ct) is null ? [] : await expenses.ListForVehicleAsync(vehicleId, query.Normalized(), ct);

    public async Task<int> CountAsync(Guid vehicleId, CancellationToken ct) =>
        await VisibleVehicleAsync(vehicleId, ct) is null ? 0 : await expenses.CountForVehicleAsync(vehicleId, ct);

    public async Task<IReadOnlyList<string>> CategoriesAsync(Guid vehicleId, CancellationToken ct) =>
        await VisibleVehicleAsync(vehicleId, ct) is null ? [] : await expenses.CategoriesAsync(vehicleId, ct);

    public async Task<Expense?> FindAsync(Guid id, CancellationToken ct)
    {
        var expense = await expenses.FindAsync(id, ct);
        return expense is not null && await VisibleVehicleAsync(expense.VehicleId, ct) is not null ? expense : null;
    }

    /// <param name="photoDraftIds">Photos the user uploaded for this expense before saving it (see <see cref="PhotoDraftService"/>).</param>
    /// <param name="id">The id the client chose; an expense it already created with this id is answered as it is (see <see cref="ClientIds"/>).</param>
    public async Task<Expense> AddAsync(Guid vehicleId, ExpenseInput input, CancellationToken ct, IReadOnlyCollection<Guid>? photoDraftIds = null, Guid? id = null)
    {
        var vehicle = await EditableVehicleAsync(vehicleId, ct);
        var creator = await access.RequirePrincipalAsync(ct);
        // Before any rule: what the first try saved may itself be a neighbour of this expense now.
        if (id is { } given && await expenses.FindIncludingDeletedAsync(given, ct) is { } existing)
            return await FinishRepeatedAsync(Repeated(existing, vehicle.Id, creator.Id), photoDraftIds, ct);
        await ValidateAsync(vehicle.Id, input, exceptReadingId: null, ct);
        var drafts = await photos.RequireDraftsAsync(vehicle.Id, photoDraftIds, ct);
        var readingPhotos = (input.Amount is null || input.Odometer is null) && await filler.MayWaitForDraftsAsync([.. drafts.Select(d => d.Id)], ct);

        var expense = Build(vehicle, creator.Id, input, readingPhotos, id);
        if (!await expenses.AddAsync(expense, ct)) // the same add, sent twice at once: the other one saved it
            return Repeated(await expenses.FindIncludingDeletedAsync(expense.Id, ct) ?? expense, vehicle.Id, creator.Id);
        await photos.AttachDraftsAsync(LogType.Expense, expense.Id, drafts, ct);
        logger.LogDebug("User {UserId} added expense {ExpenseId} for vehicle {VehicleId} with {Photos} photos", creator.Id, expense.Id, vehicle.Id, drafts.Count);
        // Readings that finished before the save are taken now; a draft that could not be attached leaves nothing to wait for.
        if (expense.ReviewState == ReviewState.AwaitingPhotos) await filler.FillAsync(LogType.Expense, expense.Id, ct);
        return await expenses.FindAsync(expense.Id, ct) ?? expense;
    }

    /// <summary>Builds (without saving) an expense whose input was already validated; used by <see cref="AddAsync"/> and by imports.</summary>
    /// <param name="readingPhotos">A photo of the expense is still being read (only then may the amount be empty).</param>
    public static Expense Build(Vehicle vehicle, Guid createdById, ExpenseInput input, bool readingPhotos = false, Guid? id = null)
    {
        Cost.RequireAmountForDiscount(input.Amount, input.Discount); // without an amount there is no cost to carry it: refused, never dropped
        var cost = input.Amount is { } amount ? Cost.Create(vehicle.OwnerId, vehicle.Id, input.Date, amount, input.Currency, input.Discount) : null;
        var reading = input.Odometer is { } value ? OdometerReading.Create(vehicle.OwnerId, vehicle.Id, input.Date, value) : null;
        return Expense.Create(vehicle.OwnerId, createdById, vehicle.Id, input.Date, input.Title, input.Category, cost, reading, input.Note, readingPhotos, id);
    }

    /// <param name="expectedVersion">The version the change was made from; refused when the expense was saved since (see <see cref="VersionCheck"/>).</param>
    public async Task<Expense> UpdateAsync(Guid id, ExpenseInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var expense = await EditableAsync(id, includeDeleted: false, ct);
        // An expense a tyre log line made (#288) shares the line's day and reading: the two change together.
        if (await tyres.LineOfExpenseAsync(id, ct) is { } line)
        {
            var before = expense.ReviewState;
            var saved = await tyres.UpdateExpenseAsync(line, id, input, ct, expectedVersion);
            // A person saved it: what they were told about its photos counts as read, as for any expense.
            if (before != ReviewState.None && saved.ReviewState == ReviewState.None) await filler.ReviewedAsync(LogType.Expense, id, saved.CreatedById, ct);
            return saved;
        }
        VersionCheck.Require(expectedVersion, expense.Version);
        await ValidateAsync(expense.VehicleId, input, exceptReadingId: expense.OdometerReadingId, ct);
        var readingPhotos = (input.Amount is null || input.Odometer is null) && await filler.MayWaitForLogPhotosAsync(LogType.Expense, id, ct);
        var waited = expense.ReviewState;

        var changes = expense.Update(input.Date, input.Title, input.Category, input.Amount, input.Currency ?? expense.Currency, input.Odometer, input.Note, readingPhotos, (await access.RequirePrincipalAsync(ct)).Id,
            input.Discount ?? expense.Discount); // left out is kept (a build from before discounts knows none), 0 is none
        await expenses.UpdateAsync(expense, changes, ct);
        if (waited != ReviewState.None && expense.ReviewState == ReviewState.None)
            await filler.ReviewedAsync(LogType.Expense, id, expense.CreatedById, ct);
        logger.LogDebug("Expense {ExpenseId} of vehicle {VehicleId} updated", id, expense.VehicleId);
        if (expense.ReviewState != ReviewState.AwaitingPhotos) return expense;
        // Readings that finished before the save are taken now (the worker's round may have passed while the log did not wait yet).
        await filler.FillAsync(LogType.Expense, id, ct);
        return await expenses.FindAsync(id, ct) ?? expense;
    }

    /// <summary>Moves the expense to the trash; it can be restored until it is deleted permanently.</summary>
    public async Task<Expense> DeleteAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var expense = await EditableAsync(id, includeDeleted: false, ct);
        if (await tyres.LineOfExpenseAsync(id, ct) is { } line) return await tyres.TrashExpenseAsync(line, id, ct, expectedVersion); // its line keeps the reading
        VersionCheck.Require(expectedVersion, expense.Version);
        expense.MarkDeleted(clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
        await expenses.UpdateAsync(expense, LinkedChanges.None, ct);
        logger.LogDebug("Expense {ExpenseId} of vehicle {VehicleId} moved to the trash", id, expense.VehicleId);
        return expense;
    }

    /// <summary>The expense when it is in the trash and the user may edit it (restore it), else null (see <see cref="RefuelingService.InTrashAsync"/>).</summary>
    public async Task<Expense?> InTrashAsync(Guid id, CancellationToken ct) =>
        await expenses.FindIncludingDeletedAsync(id, ct) is { IsDeleted: true } expense
        && await guard.ForVehicleAsync(expense.VehicleId, ct) is { Level: >= AccessLevel.Edit } ? expense : null;

    public async Task<Expense> RestoreAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var expense = await EditableAsync(id, includeDeleted: true, ct);
        VersionCheck.Require(expectedVersion, expense.Version);
        if (expense.Odometer is { } value) await odometer.ValidateAsync(expense.VehicleId, expense.Date, value, exceptReadingId: expense.OdometerReadingId, ct);
        if (await tyres.LineOfExpenseAsync(id, ct) is { } line) return await tyres.RestoreExpenseAsync(line, id, ct, expectedVersion);
        expense.Restore((await access.RequirePrincipalAsync(ct)).Id);
        await expenses.UpdateAsync(expense, LinkedChanges.None, ct);
        logger.LogDebug("Expense {ExpenseId} of vehicle {VehicleId} restored from the trash", id, expense.VehicleId);
        if (expense.ReviewState == ReviewState.AwaitingPhotos)
        {
            await filler.FillAsync(LogType.Expense, id, ct); // its photos may have been read while it was in the trash
            return await expenses.FindAsync(id, ct) ?? expense;
        }
        return expense;
    }

    public async Task<IReadOnlyList<Expense>> ListTrashAsync(ExpenseQuery query, CancellationToken ct) =>
        await expenses.ListDeletedAsync(await access.LogScopeAsync(AccessLevel.Edit, ct), query.Normalized(), ct);

    public async Task<int> CountTrashAsync(CancellationToken ct) =>
        await expenses.CountDeletedAsync(await access.LogScopeAsync(AccessLevel.Edit, ct), ct);

    public async Task<int> CountDeletableTrashAsync(CancellationToken ct) =>
        await expenses.CountDeletedAsync(await access.LogScopeAsync(AccessLevel.Delete, ct), ct);

    /// <summary>Permanently removes the trashed expenses the user has Delete access to. Returns how many were removed.</summary>
    public async Task<int> EmptyTrashAsync(CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        var purged = await expenses.PurgeAsync(await access.LogScopeAsync(AccessLevel.Delete, ct), ct);
        await photos.DeleteFilesAsync(LogType.Expense, purged, ct); // their photos go with them
        if (purged.Count > 0) logger.LogInformation("User {UserId} emptied the expense trash: {Count} expenses deleted for good ({WithPhotos} with photos)", user.Id, purged.Count, purged.WithPhotos.Count);
        return purged.Count;
    }

    /// <summary>
    /// The add came again: the first try saved the expense but may have failed after that, and the caller cannot tell. The steps after the
    /// save are safe to repeat, so they run again: the drafts still waiting are attached and a reading that finished is taken. An expense in
    /// the trash stays as it is (a visit's retry restores it itself, see <c>RecurringExpenseService.MarkDoneAsync</c>).
    /// </summary>
    private async Task<Expense> FinishRepeatedAsync(Expense existing, IReadOnlyCollection<Guid>? photoDraftIds, CancellationToken ct)
    {
        if (existing.IsDeleted) return existing;
        await photos.AttachDraftsAsync(LogType.Expense, existing.Id, await photos.DraftsStillWaitingAsync(existing.VehicleId, photoDraftIds, ct), ct);
        if (existing.ReviewState == ReviewState.AwaitingPhotos) await filler.FillAsync(LogType.Expense, existing.Id, ct);
        return await expenses.FindAsync(existing.Id, ct) ?? existing;
    }

    /// <summary>An expense this add already created (same vehicle, same creator) is the answer; another one's id is refused.</summary>
    private Expense Repeated(Expense existing, Guid vehicleId, Guid creatorId)
    {
        var same = existing.VehicleId == vehicleId && existing.CreatedById == creatorId;
        if (same) logger.LogDebug("Expense {ExpenseId} of vehicle {VehicleId} was already added; the add came again", existing.Id, vehicleId);
        return ClientIds.Repeat(existing, same, existing.Id);
    }

    private async Task<Vehicle?> VisibleVehicleAsync(Guid vehicleId, CancellationToken ct) => (await guard.ForVehicleAsync(vehicleId, ct))?.Vehicle;

    private async Task<Vehicle> EditableVehicleAsync(Guid vehicleId, CancellationToken ct) =>
        LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(vehicleId, ct),
            () => new NotFoundException("vehicle.notFound", $"Vehicle {vehicleId} does not exist.", new { Id = vehicleId })).Vehicle;

    private async Task<Expense> EditableAsync(Guid id, bool includeDeleted, CancellationToken ct)
    {
        var expense = includeDeleted ? await expenses.FindIncludingDeletedAsync(id, ct) : await expenses.FindAsync(id, ct);
        LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(expense?.VehicleId, ct),
            () => new NotFoundException("expense.notFound", $"Expense {id} does not exist.", new { Id = id }));
        return expense!;
    }

    private async Task ValidateAsync(Guid vehicleId, ExpenseInput input, Guid? exceptReadingId, CancellationToken ct)
    {
        if (input.Date > LatestAllowedDate) throw new DomainException("refueling.dateInFuture", "The date cannot be in the future.");
        if (input.Odometer is { } value) await odometer.ValidateAsync(vehicleId, input.Date, value, exceptReadingId, ct);
    }
}
