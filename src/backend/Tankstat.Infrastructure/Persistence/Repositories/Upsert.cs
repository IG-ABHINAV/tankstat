using Microsoft.EntityFrameworkCore;

namespace Tankstat.Infrastructure.Persistence.Repositories;

/// <summary>Adding or replacing a row whose key is never generated (the settings, the dashboard layouts).</summary>
internal static class Upsert
{
    /// <summary>
    /// Adds the row or replaces it. Two first saves at the same time both see no row; the one that loses the insert becomes an update, so
    /// the later value wins instead of an error. A row deleted between the check and the update (a reset in another tab) is inserted instead.
    /// </summary>
    public static async Task UpsertAsync<T>(this IDbContextFactory<AppDbContext> dbFactory, T row, Func<AppDbContext, Task<bool>> exists, CancellationToken ct) where T : class
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var isNew = !await exists(db);
        db.Entry(row).State = isNew ? EntityState.Added : EntityState.Modified;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (!isNew)
        {
            db.ChangeTracker.Clear();
            db.Entry(row).State = EntityState.Added;
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (isNew)
        {
            db.ChangeTracker.Clear();
            db.Entry(row).State = EntityState.Modified;
            await db.SaveChangesAsync(ct);
        }
    }
}
