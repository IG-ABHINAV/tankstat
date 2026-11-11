using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tankstat.Domain.Vehicles;
using Tankstat.Infrastructure.Persistence;

namespace Tankstat.Seeder;

/// <param name="Expenses">Every expense: the visits of the recurring expenses and the one-off ones.</param>
public sealed record SeedResult(int Vehicles, int Trashed, int Refuelings, int Recurring = 0, int Notifications = 0, int Expenses = 0, int TyreSets = 0);

/// <summary>Deletes, creates and migrates the database, then inserts the generated data in batches.</summary>
public sealed class DatabaseSeeder(IDbContextFactory<AppDbContext> dbFactory, DataGenerator generator)
{
    private const int BatchVehicles = 500;
    private const int NotifiedVehicles = 20; // the vehicles the access-change notifications are about

    public async Task<SeedResult> RecreateAndSeedAsync(SeedOptions options, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.Database.EnsureDeletedAsync(ct);
            SqliteConnection.ClearAllPools(); // release the file so it can be recreated at once
            await db.Database.MigrateAsync(ct);
        }

        int vehicles = 0, trashed = 0, refuelings = 0, recurring = 0, expenses = 0, tyreSets = 0;
        var notified = new List<Vehicle>();
        foreach (var batch in generator.Generate(options).Chunk(BatchVehicles))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.Vehicles.AddRange(batch.Select(b => b.Vehicle));
            db.Refuelings.AddRange(batch.SelectMany(b => b.Refuelings));
            db.RecurringExpenses.AddRange(batch.SelectMany(b => b.Recurring));
            db.Expenses.AddRange(batch.SelectMany(b => b.Expenses));
            db.RecurringCompletions.AddRange(batch.SelectMany(b => b.Completions));
            db.TyreSets.AddRange(batch.SelectMany(b => b.TyreSets ?? []));
            db.TyreInstallations.AddRange(batch.SelectMany(b => b.TyreInstallations ?? []));
            await db.SaveChangesAsync(ct);

            vehicles += batch.Count(b => !b.Vehicle.IsDeleted);
            trashed += batch.Count(b => b.Vehicle.IsDeleted);
            refuelings += batch.Sum(b => b.Refuelings.Count);
            recurring += batch.Sum(b => b.Recurring.Count);
            expenses += batch.Sum(b => b.Expenses.Count);
            tyreSets += batch.Sum(b => b.TyreSets?.Count ?? 0);
            notified.AddRange(batch.Select(b => b.Vehicle).Where(v => !v.IsDeleted).Take(NotifiedVehicles - notified.Count));
        }

        var notifications = generator.Notifications(options, notified);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Notifications.AddRange(notifications);
            await db.SaveChangesAsync(ct);
        }
        return new SeedResult(vehicles, trashed, refuelings, recurring, notifications.Count, expenses, tyreSets);
    }
}
