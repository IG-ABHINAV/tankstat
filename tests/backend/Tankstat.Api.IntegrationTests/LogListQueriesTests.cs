using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// What every row of a list of logs says about itself (what the user may do, who logged it, its vehicle) is loaded for the whole page at
/// once. Asked row by row, a page of a hundred rows opened several hundred database connections in parallel, more than PostgreSQL allows
/// by default, and the whole page failed ("Unexpected Execution Error" on one row's canDelete).
/// </summary>
public class LogListQueriesTests
{
    private const string Rows = "id canEdit canDelete createdBy { id } vehicle { id }";

    [Fact]
    public async Task AFullPageOfLogs_TakesAFewDatabaseContexts_NotSomeForEveryRow()
    {
        var contexts = new ContextCounter();
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" }, host: b => b.ConfigureTestServices(contexts.Install));
        var client = app.NewClient();
        var vehicle = (await Data(client, "mutation { addVehicle(input: { name: \"Busy car\", fuelType: PETROL }) { id } }"))
            .GetProperty("addVehicle").GetProperty("id").GetString();
        for (var i = 0; i < 100; i++)
        {
            var date = new DateOnly(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd");
            await Data(client, "mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } } ",
                new { i = new { vehicleId = vehicle, date, volume = 40, totalCost = 60, currency = "EUR", odometer = 1000 + i * 100, isFullTank = true } });
            await Data(client, "mutation($i: AddExpenseInput!) { addExpense(input: $i) { id } }",
                new { i = new { vehicleId = vehicle, date, title = "Parking", category = "Parking", amount = 5, currency = "EUR" } });
        }

        foreach (var list in new[] { "refuelings", "expenses" })
        {
            var page = default(JsonElement);
            var used = await contexts.CountAsync(async () =>
                page = await Data(client, $"query($id: UUID!) {{ {list}(vehicleId: $id, take: 100) {{ {Rows} }} }}", new { id = vehicle }));

            var rows = page.GetProperty(list).EnumerateArray().ToList();
            Assert.Equal(100, rows.Count);
            Assert.All(rows, r => Assert.True(r.GetProperty("canEdit").GetBoolean() && r.GetProperty("canDelete").GetBoolean()));
            Assert.All(rows, r => Assert.Equal(vehicle, r.GetProperty("vehicle").GetProperty("id").GetString()));
            Assert.InRange(used, 1, 15);
        }
    }

    private static async Task<JsonElement> Data(HttpClient client, string query, object? variables = null)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query, variables });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("errors", out var errors), errors.ToString());
        return body.GetProperty("data");
    }
}
