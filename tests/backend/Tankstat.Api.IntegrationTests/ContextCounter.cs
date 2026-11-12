using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tankstat.Infrastructure.Persistence;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// Counts the database contexts a host asks for (each one a connection while it runs a query), so a test can say that a list costs a fixed
/// number of them however many rows it has. Installed over the host's <see cref="IDbContextFactory{TContext}"/>.
/// </summary>
internal sealed class ContextCounter
{
    private int _created;

    public int Created => Volatile.Read(ref _created);

    public void Install(IServiceCollection services)
    {
        var original = services.Single(d => d.ServiceType == typeof(IDbContextFactory<AppDbContext>));
        services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(IDbContextFactory<AppDbContext>), sp => new Counting(
            (IDbContextFactory<AppDbContext>)(original.ImplementationInstance
                ?? original.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)), this), original.Lifetime));
    }

    /// <summary>The contexts <paramref name="work"/> asked for.</summary>
    public async Task<int> CountAsync(Func<Task> work)
    {
        var before = Created;
        await work();
        return Created - before;
    }

    private void Add() => Interlocked.Increment(ref _created);

    private sealed class Counting(IDbContextFactory<AppDbContext> inner, ContextCounter counter) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            counter.Add();
            return inner.CreateDbContext();
        }

        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            counter.Add();
            return await inner.CreateDbContextAsync(cancellationToken);
        }
    }
}
