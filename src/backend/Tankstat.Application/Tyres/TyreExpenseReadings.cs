using Tankstat.Domain.Odometers;

namespace Tankstat.Application.Tyres;

/// <summary>
/// A photo of a tyre log line's expense filled in the odometer the two had none of (#288): the line takes that very reading, every live
/// line of the expense when several share it (#289).
/// Apart from <see cref="TyreService"/> because the photo worker runs it with no user: nothing here may need the access layer
/// (<c>ICurrentUser</c> is the Api's, and the worker's services resolve without it: <c>RecognitionSetupTests</c>).
/// </summary>
public sealed class TyreExpenseReadings(ITyreRepository tyres)
{
    public async Task FilledAsync(Guid expenseId, OdometerReading reading, CancellationToken ct)
    {
        if ((await tyres.ListEntriesByExpenseAsync([expenseId], ct)).FirstOrDefault(e => !e.IsDeleted) is not { } found) return;
        var book = await tyres.LoadAsync(found.VehicleId, ct);
        // Every live line holding the expense takes it (#289: the lines made together hold one reading).
        var save = new TyreSave();
        foreach (var line in book.Entries.Where(e => e.ExpenseId == expenseId && book.IsLive(e)))
            if (line.TakeReading(reading)) save.Change(line);
        if (save.ChangedEntries.Count == 0) return;
        await tyres.SaveAsync(save, ct); // lines changed, nothing added: no id to clash on
    }
}
