using Microsoft.EntityFrameworkCore;
using Tankstat.Application.Sync;
using Tankstat.Infrastructure.Persistence;
using Tankstat.Infrastructure.Persistence.Repositories;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>
/// The offline feed pages by (UpdatedAt, Id). Each provider orders uuids its own way, which is fine as long as the database both orders and
/// compares them: these check that every provider translates the comparison into SQL (never evaluated in memory) next to the ORDER BY.
/// </summary>
public class OfflineFeedTranslationTests
{
    public static TheoryData<DatabaseProvider> Providers => new(Enum.GetValues<DatabaseProvider>());

    private static AppDbContext Context(DatabaseProvider provider)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        _ = provider switch
        {
            DatabaseProvider.Sqlite => builder.UseSqlite("Data Source=x.db"),
            DatabaseProvider.PostgreSql => builder.UseNpgsql("Host=x"),
            DatabaseProvider.SqlServer => builder.UseSqlServer("Server=x"),
            DatabaseProvider.MySql => builder.UseMySQL("Server=x"),
            _ => throw new NotSupportedException(provider.ToString()),
        };
        return new AppDbContext(builder.Options);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void APageAfterAKey_ComparesAndOrdersTheIdsInTheDatabase(DatabaseProvider provider)
    {
        using var db = Context(provider);
        var rows = new OfflineRows(Guid.NewGuid(), null, DateTimeOffset.UtcNow, new OfflineKey(DateTimeOffset.UtcNow, Guid.NewGuid()), 201);

        foreach (var sql in new[] { OfflineFeedRepository.RefuelingRows(db, rows).ToQueryString(), OfflineFeedRepository.ExpenseRows(db, rows).ToQueryString() })
        {
            Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"""?Id[""`\]]?\s*>\s*@", sql); // the id comparison is SQL, against the key's parameter
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TheTyreTrash_IsOneQuery_OverSetsAndInstallations_PagedInTheDatabase(DatabaseProvider provider)
    {
        using var db = Context(provider);
        var scope = Tankstat.Application.Access.OwnerScope.Of([Guid.NewGuid()], [Guid.NewGuid()]);

        var sql = TyreRepository.Trash(db, scope).OrderByDescending(r => r.DeletedAt).ThenBy(r => r.Id).Skip(10).Take(50).ToQueryString();

        Assert.Contains("UNION ALL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TyreInstallations", sql);
    }
}
