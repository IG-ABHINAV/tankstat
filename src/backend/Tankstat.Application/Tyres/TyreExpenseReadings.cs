using Tankstat.Domain.Odometers;

namespace Tankstat.Application.Tyres;

/// <summary>
/// A photo of a tyre log line's expense filled in the odometer the two had none of (#288): the line takes that very reading.
/// Apart from <see cref="TyreService"/> because the photo worker runs it with no user: nothing here may need the access layer
/// (<c>ICurrentUser</c> is the Api's, and the worker's services resolve without it: <c>RecognitionSetupTests</c>).
/// </summary>
public sealed class TyreExpenseReadings(ITyreRepository tyres)
{
    public async Task FilledAsync(Guid expenseId, OdometerReading reading, CancellationToken ct)
    {
        if ((await tyres.ListEntriesByExpenseAsync([expenseId], ct)).FirstOrDefault() is not { IsDeleted: false } found) return;
        var book = await tyres.LoadAsync(found.VehicleId, ct);
        var line = book.Entries.First(e => e.Id == found.Id);
        if (!line.TakeReading(reading)) return;
        var save = new TyreSave();
        save.Change(line);
        await tyres.SaveAsync(save, ct); // one line changed, nothing added: no id to clash on
    }
}
