using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// A list of vehicles costs a fixed number of database contexts, however many rows it has (#311): who is asking is checked once per
/// request, and what every row says about itself (what the user may do, its owner, its counts, its summary) is loaded for the whole page.
/// Standalone, never mode None: there the anonymous principal costs nothing, which would hide the first.
/// </summary>
public class VehicleListQueriesTests(VehicleListQueriesTests.Data data) : IClassFixture<VehicleListQueriesTests.Data>
{
    [Fact]
    public async Task WhoIsAsking_IsCheckedOncePerRequest_NotForEveryRow()
    {
        var plain = await data.CountAsync(data.Users.Alice, "{ myVehicles(skip: 0, take: 24) { id name } }");
        var withLevels = await data.CountAsync(data.Users.Alice, "{ myVehicles(skip: 0, take: 24) { id canEdit } }");
        Assert.InRange(withLevels.Contexts, 1, plain.Contexts);
        Assert.All(Rows(withLevels.Body, "myVehicles"), v => Assert.True(v.GetProperty("canEdit").GetBoolean()));

        const string grid = "vehicles(orderBy: NAME, direction: ASC, skip: 0, take: 100)";
        var adminPlain = await data.CountAsync(data.Users.Admin, $"{{ {grid} {{ id name }} }}");
        var adminWithLevels = await data.CountAsync(data.Users.Admin, $"{{ {grid} {{ id canEdit }} }}");
        Assert.Equal(100, Rows(adminWithLevels.Body, "vehicles").Count);
        Assert.InRange(adminWithLevels.Contexts, 1, adminPlain.Contexts);
    }

    [Fact]
    public async Task EveryRowsOwnerLevelsAndCounts_AreLoadedForTheWholePage()
    {
        var grid = await PageAsync(data.Users.Admin, "vehicles", take => $"{{ vehicles(orderBy: NAME, direction: ASC, skip: 0, take: {take}) {{ id canEdit logAccess owner {{ id }} refuelingCount }} }}", 100);
        Assert.Equal(100, grid.Count);
        Assert.All(grid, v =>
        {
            Assert.True(v.GetProperty("canEdit").GetBoolean());
            Assert.Equal("DELETE", v.GetProperty("logAccess").GetString());
            Assert.Equal(data.Users.AliceId, v.GetProperty("owner").GetProperty("id").GetString());
            Assert.Equal(1, v.GetProperty("refuelingCount").GetInt32());
        });

        var trash = await PageAsync(data.Users.Admin, "trash", take => $"{{ trash(take: {take}) {{ id owner {{ id }} }} }}", 100);
        Assert.Equal(20, trash.Count);
        Assert.All(trash, v => Assert.Equal(data.Users.AliceId, v.GetProperty("owner").GetProperty("id").GetString()));

        var granted = await PageAsync(data.Users.Bob, "myVehicles", take => $"{{ myVehicles(skip: 0, take: {take}) {{ id canEdit logAccess refuelingCount }} }}", 24);
        Assert.Equal(24, granted.Count);
        Assert.All(granted, v =>
        {
            Assert.False(v.GetProperty("canEdit").GetBoolean());
            Assert.Equal("VIEW", v.GetProperty("logAccess").GetString());
            Assert.Equal(1, v.GetProperty("refuelingCount").GetInt32());
        });

        var importTargets = await PageAsync(data.Users.Alice, "myVehicles", take => $"{{ myVehicles(take: {take}) {{ id canEdit logAccess }} }}", 200);
        Assert.Equal(110, importTargets.Count);
    }

    /// <summary>
    /// A page of <paramref name="take"/> rows, which must take the very number of database contexts that a page of three takes (a fixed number,
    /// whatever the rows), and a small one.
    /// </summary>
    private async Task<List<JsonElement>> PageAsync(HttpClient client, string list, Func<int, string> query, int take)
    {
        var few = await data.CountAsync(client, query(3));
        var page = await data.CountAsync(client, query(take));
        Assert.Equal(3, Rows(few.Body, list).Count);
        Assert.Equal(few.Contexts, page.Contexts);
        Assert.InRange(page.Contexts, 1, 25);
        return Rows(page.Body, list);
    }

    private static List<JsonElement> Rows(JsonElement body, string list) => [.. body.Data().GetProperty(list).EnumerateArray()];

    /// <summary>
    /// One Standalone host for the class: Alice owns 130 vehicles, 20 of them in the trash; Bob holds a View grant on Alice's data; the
    /// administrator sees everything. Each vehicle has a refuelling, so the counts and the summaries have something to say.
    /// </summary>
    public sealed class Data : IAsyncLifetime
    {
        private readonly ContextCounter _contexts = new();
        private TestApp? _app;

        internal TwoUsers Users { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _app = new TestApp(TestApp.StandaloneSettings(), host: b => b.ConfigureTestServices(_contexts.Install));
            Users = await _app.Users();
            for (var i = 0; i < 130; i++)
            {
                var id = (await Users.Alice.Gql("mutation($n: String!) { addVehicle(input: { name: $n, fuelType: PETROL }) { id } }", new { n = $"Car {i:000}" }))
                    .Data().GetProperty("addVehicle").GetProperty("id").GetString();
                (await Users.Alice.Gql("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
                    new { i = new { vehicleId = id, date = "2026-01-10", volume = 40, totalCost = 60, currency = "EUR", odometer = 1000 + i, isFullTank = true } })).Data();
                if (i % 13 == 0 || i % 13 == 7) (await Users.Alice.Gql("mutation($id: UUID!) { deleteVehicle(id: $id) { id } }", new { id })).Data();
            }
            (await Users.Admin.Gql("mutation($i: SetAccessGrantInput!) { setAccessGrant(input: $i) }",
                new { i = new { ownerId = Users.AliceId, granteeId = Users.BobId, level = "VIEW" } })).Data();
        }

        /// <summary>The answer to <paramref name="query"/> and the database contexts it took.</summary>
        public async Task<(JsonElement Body, int Contexts)> CountAsync(HttpClient client, string query)
        {
            var body = default(JsonElement);
            var contexts = await _contexts.CountAsync(async () => body = await client.Gql(query));
            return (body, contexts);
        }

        public Task DisposeAsync()
        {
            _app?.Dispose();
            return Task.CompletedTask;
        }
    }
}
