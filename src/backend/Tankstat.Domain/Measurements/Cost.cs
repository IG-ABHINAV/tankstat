using Tankstat.Domain.Access;

namespace Tankstat.Domain.Measurements;

/// <summary>
/// An amount of money together with the currency it was paid in, at a date. Entities that cost something (a refuelling today,
/// an inspection or service fee later) do not carry an amount of their own: they link to a cost. It shares the lifecycle of
/// the entity that owns it (trash, restore, delete), and all costs of a vehicle live in one table, so spending can later be
/// summed across kinds of entities.
/// </summary>
public sealed class Cost : IOwned, ISoftDeletable
{
    private Cost() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>The owner of the vehicle (denormalized, like on the logs).</summary>
    public Guid OwnerId { get; private set; }
    public Guid VehicleId { get; private set; }

    /// <summary>When it was paid.</summary>
    public DateOnly Date { get; private set; }
    /// <summary>What was paid, after <see cref="Discount"/>.</summary>
    public decimal Amount { get; private set; }

    /// <summary>
    /// What was taken off at the till (a loyalty or fuel-card discount, a coupon), in <see cref="Currency"/>; null when none. The receipt's
    /// line before it is <see cref="AmountBeforeDiscount"/>; every sum of spending stays on <see cref="Amount"/>, what was paid.
    /// </summary>
    public decimal? Discount { get; private set; }

    /// <summary>The amount before the discount was taken off: <see cref="Amount"/> plus <see cref="Discount"/>.</summary>
    public decimal AmountBeforeDiscount => Amount + (Discount ?? 0);

    /// <summary>ISO 4217 code of the currency it was paid in (can differ from cost to cost, e.g. when travelling).</summary>
    public string Currency { get; private set; } = "";
    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public static Cost Create(Guid ownerId, Guid vehicleId, DateOnly date, decimal amount, string? currency, decimal? discount = null)
    {
        var cost = new Cost { Id = Guid.NewGuid(), OwnerId = ownerId, VehicleId = vehicleId };
        cost.Update(date, amount, currency, discount);
        return cost;
    }

    /// <param name="discount">What was taken off, kept to the cent (as the column holds it); none, or what rounds to 0, is kept as none.</param>
    public void Update(DateOnly date, decimal amount, string? currency, decimal? discount = null)
    {
        if (amount < 0) throw new DomainException("cost.negative", "A cost cannot be negative.");
        if (discount < 0) throw new DomainException("cost.discountNegative", "A discount cannot be negative.");
        Date = date;
        Amount = amount;
        Discount = Normalised(discount);
        Currency = CurrencyCode.Normalize(currency);
    }

    /// <summary>
    /// A discount belongs to an amount it was taken off: one without an amount (a log still waiting for its photo's total) is refused. A
    /// negative one says so first; one that is none (0, or what rounds to 0) needs no amount.
    /// </summary>
    public static void RequireAmountForDiscount(decimal? amount, decimal? discount)
    {
        if (discount < 0) throw new DomainException("cost.discountNegative", "A discount cannot be negative.");
        if (Normalised(discount) is not null && amount is null)
            throw new DomainException("cost.discountWithoutAmount", "A discount needs the amount it was taken off.");
    }

    /// <summary>To the cent, as every provider's column keeps it (SQLite would keep more), and none when that is nothing.</summary>
    private static decimal? Normalised(decimal? discount) =>
        discount is { } value && Math.Round(value, 2, MidpointRounding.AwayFromZero) is var cents && cents > 0 ? cents : null;

    public void MarkDeleted(DateTimeOffset now) => DeletedAt = now;

    public void Restore() => DeletedAt = null;
}
