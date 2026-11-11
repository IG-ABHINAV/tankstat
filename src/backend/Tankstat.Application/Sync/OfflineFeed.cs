using System.Globalization;
using System.Text;
using Tankstat.Application.Recurring;
using Tankstat.Domain;
using Tankstat.Domain.Recurring;
using Tankstat.Domain.Sync;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Sync;

/// <summary>Where a table's paging stands: the last row sent, in the database's own order of (UpdatedAt, Id).</summary>
public sealed record OfflineKey(DateTimeOffset UpdatedAt, Guid Id);

/// <summary>
/// Which rows of a table one request reads: a full download takes the logs dated from <see cref="From"/> (null: all of them); a later one
/// takes every row saved since <see cref="Since"/>, whatever its date (an edit can move a log out of the window, and the device must hear
/// of it). Both include rows in the trash. <see cref="After"/> continues a download that took more than one page.
/// </summary>
public sealed record OfflineRows(Guid VehicleId, DateOnly? From, DateTimeOffset? Since, OfflineKey? After, int Take);

public sealed record RemovedEntity(OfflineEntityType Type, Guid Id);

/// <summary>How many logs of a vehicle a download would bring (the trash included), for the estimates on the Offline data page.</summary>
public sealed record LogCounts(int Refuelings, int Expenses);

public interface IOfflineFeedRepository
{
    /// <summary>The rows, ordered by (UpdatedAt, Id) the way the database orders them, at most <c>Take</c>.</summary>
    Task<IReadOnlyList<Refueling>> RefuelingsAsync(OfflineRows rows, CancellationToken ct);

    Task<IReadOnlyList<Expense>> ExpensesAsync(OfflineRows rows, CancellationToken ct);

    /// <summary>What of the vehicle was removed for good since the moment, and does not exist again (a device may have added it anew).</summary>
    Task<IReadOnlyList<RemovedEntity>> RemovedSinceAsync(Guid vehicleId, DateTimeOffset since, CancellationToken ct);

    Task<int> SweepTombstonesAsync(DateTimeOffset before, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, LogCounts>> CountSinceAsync(IReadOnlyCollection<Guid> vehicleIds, DateOnly? from, CancellationToken ct);
}

/// <summary>
/// The state of one download of one vehicle, handed to the device as an opaque string and back: the vehicle, the window (a full
/// download's start date, or a later one's moment), the watermark (the server's time when the download began, which the device sends as
/// the moment of its next download) and, per table, the last row sent or that the table is done. Refuelings and expenses are paged each
/// by its own key: rows of the two tables can share a time, and only the database knows its order of ids.
/// </summary>
public sealed record OfflineCursor(Guid VehicleId, DateOnly? From, DateTimeOffset? Since, DateTimeOffset Watermark, OfflineKey? Refuelings, bool RefuelingsDone, OfflineKey? Expenses, bool ExpensesDone)
{
    private const string Version = "1";

    public string Encode()
    {
        static string Key(OfflineKey? key, bool done) => done ? "x" : key is null ? "-" : $"{key.UpdatedAt.UtcTicks}:{key.Id:N}";
        var text = string.Join('|', Version, VehicleId.ToString("N"), From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-",
            Since?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-", Watermark.UtcTicks.ToString(CultureInfo.InvariantCulture),
            Key(Refuelings, RefuelingsDone), Key(Expenses, ExpensesDone));
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>A cursor this server made for this vehicle, or <c>sync.cursorInvalid</c>.</summary>
    public static OfflineCursor Decode(string encoded, Guid vehicleId)
    {
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='))).Split('|');
            if (parts.Length != 7 || parts[0] != Version || Guid.ParseExact(parts[1], "N") != vehicleId) throw Invalid();
            var (refuelings, refuelingsDone) = ParseKey(parts[5]);
            var (expenses, expensesDone) = ParseKey(parts[6]);
            return new OfflineCursor(vehicleId,
                parts[2] == "-" ? null : DateOnly.ParseExact(parts[2], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                parts[3] == "-" ? null : Ticks(parts[3]), Ticks(parts[4]), refuelings, refuelingsDone, expenses, expensesDone);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            throw Invalid();
        }
    }

    private static (OfflineKey?, bool) ParseKey(string part)
    {
        if (part == "x") return (null, true);
        if (part == "-") return (null, false);
        var pieces = part.Split(':');
        if (pieces.Length != 2) throw Invalid();
        return (new OfflineKey(Ticks(pieces[0]), Guid.ParseExact(pieces[1], "N")), false);
    }

    private static DateTimeOffset Ticks(string text) => new(long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture), TimeSpan.Zero);

    private static DomainException Invalid() => new("sync.cursorInvalid", "This download cannot be continued; start it again.");
}

/// <summary>One page of a vehicle's download (see <see cref="OfflineFeedService"/>).</summary>
/// <param name="Recurring">The vehicle's schedules with their status as of now: all of them, on the first page only.</param>
/// <param name="Next">Continues this download; null when it is complete.</param>
/// <param name="Watermark">The moment to send as <c>since</c> next time.</param>
/// <param name="Resync">The device's last download is too old for what the server remembers: it starts afresh, nothing else came.</param>
/// <param name="Tyres">The vehicle's tyres (#287) whole, with the server's figures, on the first page (the device keeps them as they come; it never
/// works a distance out); null on the others.</param>
public sealed record OfflinePage(
    Vehicle Vehicle, IReadOnlyList<Refueling> Refuelings, IReadOnlyList<Expense> Expenses, IReadOnlyList<RecurringItem> Recurring,
    IReadOnlyList<RemovedEntity> Removed, string? Next, DateTimeOffset Watermark, bool Resync, Tyres.TyreOverview? Tyres = null);
