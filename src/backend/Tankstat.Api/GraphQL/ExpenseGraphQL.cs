using Microsoft.Extensions.Options;
using Tankstat.Application.Access;
using Tankstat.Application.Expenses;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Access;
using Tankstat.Domain.Vehicles;
using Tankstat.Domain;

namespace Tankstat.Api.GraphQL;

/// <param name="Amount">May be omitted only while one of <paramref name="PhotoIds"/> is still being read: the reading fills it in later (<c>reviewState</c>).</param>
/// <param name="Currency">ISO 4217 code of the currency paid in; omit to use the instance default.</param>
/// <param name="Odometer">Omit when the odometer was not noted (a photo that is still being read may fill it in).</param>
/// <param name="PhotoIds">Photos uploaded for this expense beforehand (<c>PUT /media/vehicles/{id}/photo-drafts</c>); they become its photos.</param>
/// <param name="Id">An id the client chose for the new expense; the same add sent again (a lost answer, a replay after being offline) answers with what it created. Omit to let the server choose.</param>
/// <param name="Discount">What was taken off the amount, which is what was paid; needs the amount.</param>
public sealed record AddExpenseInput(
    Guid VehicleId, DateOnly Date, string Title, string? Category, decimal? Amount, string? Currency, long? Odometer, string? Note, IReadOnlyList<Guid>? PhotoIds = null,
    Guid? Id = null, decimal? Discount = null);

/// <param name="Amount">May be omitted only while a photo of the expense is still being read.</param>
/// <param name="Currency">Omit to keep the expense's currency.</param>
/// <param name="Discount">What was taken off the amount; omitted or null is none (an earlier one is cleared).</param>
public sealed record UpdateExpenseInput(
    Guid Id, DateOnly Date, string Title, string? Category, decimal? Amount, string? Currency, long? Odometer, string? Note, decimal? Discount = null);

/// <summary>The expense type exposes its odometer and amount as plain values; the linked reading and cost stay internal details.</summary>
public sealed class ExpenseType : ObjectType<Expense>
{
    protected override void Configure(IObjectTypeDescriptor<Expense> descriptor)
    {
        descriptor.Ignore(e => e.OdometerReading);
        descriptor.Ignore(e => e.SavedVersion); // the persistence layer's, not a client's
        descriptor.Ignore(e => e.OdometerReadingId);
        descriptor.Ignore(e => e.Cost);
        descriptor.Ignore(e => e.CostId);
        descriptor.Ignore(e => e.IsDeleted);
        descriptor.Ignore(e => e.Missing);
        descriptor.Ignore(e => e.Fillable);
        descriptor.Ignore(e => e.Update(default, default!, default, default, default, default, default, default));
        descriptor.Ignore(e => e.FillFromPhoto(default!));
        descriptor.Ignore(e => e.FinishReading(default));
        descriptor.Field(e => e.FilledFromPhoto)
            .Description("The values its photos filled in that nobody has checked yet.")
            .Type<NonNullType<ListType<NonNullType<EnumType<LogValue>>>>>()
            .Resolve(ctx => LogValueList.Of(ctx.Parent<Expense>().FilledFromPhoto));
    }
}

[ExtendObjectType<Expense>]
public sealed class ExpenseExtensions
{
    public async Task<bool> GetCanEdit([Parent] Expense expense, LogLevelByVehicleLoader levels, CancellationToken ct) =>
        await levels.LoadAsync(expense.VehicleId, ct) >= AccessLevel.Edit;

    public async Task<bool> GetCanDelete([Parent] Expense expense, LogLevelByVehicleLoader levels, CancellationToken ct) =>
        await levels.LoadAsync(expense.VehicleId, ct) >= AccessLevel.Delete;

    public Task<UserRef?> GetCreatedBy([Parent] Expense expense, UserRefLoader users, CancellationToken ct) => users.LoadAsync(expense.CreatedById, ct);

    /// <summary>Who made its last change that counted (see <c>changedAt</c>, <c>lastChange</c>); null when nobody did (a photo) or no longer exists.</summary>
    public async Task<UserRef?> GetChangedBy([Parent] Expense expense, UserRefLoader users, CancellationToken ct) =>
        expense.ChangedById is { } id ? await users.LoadAsync(id, ct) : null;

    public Task<Vehicle?> GetVehicle([Parent] Expense expense, VehicleIncludingDeletedLoader vehicles, CancellationToken ct) => vehicles.LoadAsync(expense.VehicleId, ct);
}

[ExtendObjectType<Vehicle>]
public sealed class VehicleExpenseExtensions
{
    public Task<int> GetExpenseCount([Parent] Vehicle vehicle, [Service] ExpenseService expenses, CancellationToken ct) =>
        expenses.CountAsync(vehicle.Id, ct);
}

[ExtendObjectType(OperationTypeNames.Query)]
public sealed class ExpenseQueries
{
    /// <summary>One page of a vehicle's expenses, sorted by the server (newest first by default).</summary>
    public Task<IReadOnlyList<Expense>> GetExpenses(
        Guid vehicleId, [Service] ExpenseService expenses, CancellationToken ct,
        ExpenseSortField orderBy = ExpenseSortField.Date, SortDirection direction = SortDirection.Desc,
        int skip = 0, int take = ExpenseQuery.DefaultTake) =>
        expenses.ListAsync(vehicleId, new ExpenseQuery(orderBy, direction, skip, take), ct);

    public Task<int> GetExpenseCount(Guid vehicleId, [Service] ExpenseService expenses, CancellationToken ct) => expenses.CountAsync(vehicleId, ct);

    public Task<Expense?> GetExpense(Guid id, [Service] ExpenseService expenses, CancellationToken ct) => expenses.FindAsync(id, ct);

    /// <summary>The categories already used on a vehicle's expenses, as suggestions for the form.</summary>
    public Task<IReadOnlyList<string>> GetExpenseCategories(Guid vehicleId, [Service] ExpenseService expenses, CancellationToken ct) =>
        expenses.CategoriesAsync(vehicleId, ct);

    public Task<IReadOnlyList<Expense>> GetExpenseTrash(
        [Service] ExpenseService expenses, CancellationToken ct,
        ExpenseSortField orderBy = ExpenseSortField.DeletedAt, SortDirection direction = SortDirection.Desc,
        int skip = 0, int take = ExpenseQuery.DefaultTake) =>
        expenses.ListTrashAsync(new ExpenseQuery(orderBy, direction, skip, take), ct);

    public Task<int> GetExpenseTrashCount([Service] ExpenseService expenses, CancellationToken ct) => expenses.CountTrashAsync(ct);

    public Task<int> GetExpenseTrashDeletableCount([Service] ExpenseService expenses, CancellationToken ct) => expenses.CountDeletableTrashAsync(ct);
}

[ExtendObjectType(OperationTypeNames.Mutation)]
public sealed class ExpenseMutations
{
    public Task<Expense> AddExpense(AddExpenseInput input, [Service] ExpenseService expenses, [Service] IOptionsSnapshot<VehicleDefaultsOptions> defaults, CancellationToken ct) =>
        expenses.AddAsync(input.VehicleId,
            new ExpenseInput(input.Date, input.Title, input.Category, input.Amount, input.Currency ?? defaults.Value.Currency, input.Odometer, input.Note, input.Discount), ct, input.PhotoIds, input.Id);

    public Task<Expense> UpdateExpense(UpdateExpenseInput input, [Service] ExpenseService expenses, CancellationToken ct) =>
        expenses.UpdateAsync(input.Id, new ExpenseInput(input.Date, input.Title, input.Category, input.Amount, input.Currency, input.Odometer, input.Note, input.Discount), ct);

    public Task<Expense> DeleteExpense(Guid id, [Service] ExpenseService expenses, CancellationToken ct) => expenses.DeleteAsync(id, ct);

    public Task<Expense> RestoreExpense(Guid id, [Service] ExpenseService expenses, CancellationToken ct) => expenses.RestoreAsync(id, ct);

    /// <summary>Permanently removes the trashed expenses the user has Delete access to; returns how many.</summary>
    public Task<int> EmptyExpenseTrash([Service] ExpenseService expenses, CancellationToken ct) => expenses.EmptyTrashAsync(ct);
}
