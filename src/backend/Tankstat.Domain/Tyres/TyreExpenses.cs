using Tankstat.Domain.Vehicles;

namespace Tankstat.Domain.Tyres;

/// <summary>
/// A tyre log line and the expense its amount made (#288) hold one odometer reading between them, on one day. The reading is live while a
/// live holder has it, and goes to the trash with the last of them; it is deleted only when neither holds it any more (in code: the purges).
/// </summary>
public static class TyreExpenses
{
    /// <summary>Puts the shared reading in or out of the trash by who still holds it live; true when it changed.</summary>
    public static bool Settle(TyreEntry line, Expense? expense, DateTimeOffset now)
    {
        if (line.OdometerReading is not { } reading) return false;
        var held = !line.IsDeleted || (expense is { IsDeleted: false } && expense.OdometerReadingId == reading.Id);
        if (held == (reading.DeletedAt is null)) return false;
        if (held) reading.Restore();
        else reading.MarkDeleted(now);
        return true;
    }
}
