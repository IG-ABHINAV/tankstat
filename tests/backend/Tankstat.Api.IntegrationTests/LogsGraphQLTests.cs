using System.Net.Http.Json;
using System.Text.Json;

namespace Tankstat.Api.IntegrationTests;

/// <summary>Refuelling logs end to end with authentication off: CRUD, sorting/paging, odometer rules, units, trash.</summary>
[Collection(ApiCollection.Name)]
public class LogsGraphQLTests(ApiFixture api)
{
    private async Task<JsonElement> Send(string query, object? variables = null)
    {
        var response = await api.Factory.CreateClient().PostAsJsonAsync("/graphql", new { query, variables });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> AddVehicle(string? name = null, string? units = null)
    {
        var input = units is null
            ? new { name = name ?? "Logs car " + Guid.NewGuid().ToString("N")[..6], fuelType = "PETROL", units = (object?)null }
            : new { name = name ?? "Logs car", fuelType = "PETROL", units = (object?)JsonSerializer.Deserialize<object>(units) };
        var body = await Send("mutation($i: AddVehicleInput!) { addVehicle(input: $i) { id } }", new { i = input });
        return body.GetProperty("data").GetProperty("addVehicle").GetProperty("id").GetString()!;
    }

    private Task<JsonElement> Log(string vehicleId, string date, long odometer, double volume = 40, double cost = 60, string? currency = "EUR", string? note = null) =>
        Send("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId, date, volume, totalCost = cost, currency, odometer, isFullTank = true, note } });

    private static string? Code(JsonElement body) =>
        body.TryGetProperty("errors", out var e) ? e[0].GetProperty("extensions").GetProperty("key").GetString() : null;

    private static JsonElement Data(JsonElement body)
    {
        Assert.False(body.TryGetProperty("errors", out var errors), errors.ToString());
        return body.GetProperty("data");
    }

    private async Task<(string Odometer, decimal? Consumption)[]> Consumptions(string vehicleId)
    {
        var data = Data(await Send("query($id: UUID!) { refuelings(vehicleId: $id, orderBy: ODOMETER, direction: ASC) { odometer consumption } }", new { id = vehicleId }));
        return data.GetProperty("refuelings").EnumerateArray()
            .Select(r => (r.GetProperty("odometer").GetInt64().ToString(), r.GetProperty("consumption").ValueKind == JsonValueKind.Null ? (decimal?)null : r.GetProperty("consumption").GetDecimal()))
            .ToArray();
    }

    [Fact]
    public async Task AVehicleCountsItsExpenses_AndSuggestsTheCategoriesAlreadyUsed()
    {
        var id = await AddVehicle();
        foreach (var (title, category) in new[] { ("Wash", "Care"), ("Toll", "Road"), ("Wax", "Care"), ("Gift", (string?)null) })
            Data(await Send("mutation($i: AddExpenseInput!) { addExpense(input: $i) { id } }",
                new { i = new { vehicleId = id, date = "2026-08-01", title, category, amount = 5, currency = "EUR" } }));

        var data = Data(await Send("query($id: UUID!) { vehicle(id: $id) { expenseCount } expenseCategories(vehicleId: $id) }", new { id }));

        Assert.Equal(4, data.GetProperty("vehicle").GetProperty("expenseCount").GetInt32());
        Assert.Equal(["Care", "Road"], data.GetProperty("expenseCategories").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task ADiscount_IsStoredWithTheCost_GivesBothPrices_AndGoesWhenAnUpdateLeavesItOut()
    {
        var id = await AddVehicle();
        const string fields = "id totalCost discount pricePerUnit pricePerUnitBeforeDiscount";

        var logged = Data(await Send($"mutation($i: LogRefuelingInput!) {{ logRefueling(input: $i) {{ {fields} }} }}",
            new { i = new { vehicleId = id, date = "2026-10-20", volume = 43, totalCost = 34744, discount = 430, currency = "HUF", odometer = 1000, isFullTank = true } })).GetProperty("logRefueling");
        Assert.Equal((34744m, 430m, 808m, 818m),
            (logged.GetProperty("totalCost").GetDecimal(), logged.GetProperty("discount").GetDecimal(), logged.GetProperty("pricePerUnit").GetDecimal(), logged.GetProperty("pricePerUnitBeforeDiscount").GetDecimal()));

        var updated = Data(await Send($"mutation($i: UpdateRefuelingInput!) {{ updateRefueling(input: $i) {{ {fields} }} }}",
            new { i = new { id = logged.GetProperty("id").GetString(), date = "2026-10-20", volume = 43, totalCost = 34744, currency = "HUF", odometer = 1000, isFullTank = true } })).GetProperty("updateRefueling");
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("discount").ValueKind);
        Assert.Equal(808m, updated.GetProperty("pricePerUnitBeforeDiscount").GetDecimal());

        Assert.Equal("cost.discountWithoutAmount", Code(await Send("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId = id, date = "2026-10-21", volume = 40, discount = 5, currency = "HUF", odometer = 1100, isFullTank = true } })));
    }

    [Fact]
    public async Task TheConsumptionBetweenFullFillUps_IsStored_AndFollowsEveryChange()
    {
        var id = await AddVehicle();
        await Log(id, "2026-08-01", 1000, volume: 40);
        var second = Data(await Log(id, "2026-08-11", 1500, volume: 30)).GetProperty("logRefueling").GetProperty("id").GetString();
        await Log(id, "2026-08-21", 2000, volume: 25);
        Assert.Equal([("1000", (decimal?)null), ("1500", 6m), ("2000", 5m)], await Consumptions(id));

        Data(await Send("mutation($i: UpdateRefuelingInput!) { updateRefueling(input: $i) { consumption } }",
            new { i = new { id = second, date = "2026-08-11", volume = 50, totalCost = 60, currency = "EUR", odometer = 1500, isFullTank = true } }));
        Assert.Equal([("1000", (decimal?)null), ("1500", 10m), ("2000", 5m)], await Consumptions(id));

        Data(await Send("mutation($id: UUID!) { deleteRefueling(id: $id) { id } }", new { id = second }));
        Assert.Equal([("1000", (decimal?)null), ("2000", 2.5m)], await Consumptions(id)); // 25 l over 1000 km

        Data(await Send("mutation($id: UUID!) { restoreRefueling(id: $id) { consumption } }", new { id = second }));
        Assert.Equal([("1000", (decimal?)null), ("1500", 10m), ("2000", 5m)], await Consumptions(id));
    }

    [Fact]
    public async Task LogEditTrashRestoreAndEmptyTheTrash()
    {
        var id = await AddVehicle();
        var logged = Data(await Log(id, "2026-09-01", 12_000, volume: 41.5, cost: 79.9, currency: "huf", note: " road trip "));
        var logId = logged.GetProperty("logRefueling").GetProperty("id").GetString();

        var list = Data(await Send("query($id: UUID!) { refuelings(vehicleId: $id) { id date volume totalCost currency odometer note isFullTank pricePerUnit canEdit canDelete createdBy { id } } refuelingCount(vehicleId: $id) }", new { id }));
        var row = list.GetProperty("refuelings")[0];
        Assert.Equal(("2026-09-01", "HUF", 12_000L, "road trip"), (row.GetProperty("date").GetString(), row.GetProperty("currency").GetString(), row.GetProperty("odometer").GetInt64(), row.GetProperty("note").GetString()));
        Assert.InRange(row.GetProperty("pricePerUnit").GetDecimal(), 1.92m, 1.93m); // 79.9 / 41.5
        Assert.True(row.GetProperty("canEdit").GetBoolean() && row.GetProperty("canDelete").GetBoolean());
        Assert.Equal(1, list.GetProperty("refuelingCount").GetInt32());

        Data(await Send("mutation($i: UpdateRefuelingInput!) { updateRefueling(input: $i) { id } }",
            new { i = new { id = logId, date = "2026-09-02", volume = 30, totalCost = 50, odometer = 12_500, isFullTank = false } })); // currency omitted: kept
        var updated = Data(await Send("query($id: UUID!) { refueling(id: $id) { volume currency odometer isFullTank date } }", new { id = logId })).GetProperty("refueling");
        Assert.Equal(("HUF", 12_500L, false, "2026-09-02"), (updated.GetProperty("currency").GetString(), updated.GetProperty("odometer").GetInt64(), updated.GetProperty("isFullTank").GetBoolean(), updated.GetProperty("date").GetString()));

        Data(await Send("mutation($id: UUID!) { deleteRefueling(id: $id) { deletedAt } }", new { id = logId }));
        Assert.Equal(0, Data(await Send("query($id: UUID!) { refuelingCount(vehicleId: $id) }", new { id })).GetProperty("refuelingCount").GetInt32());
        var trash = Data(await Send("{ refuelingTrash { id vehicle { name units { distance volume } } deletedAt } refuelingTrashCount refuelingTrashDeletableCount }"));
        Assert.Contains(trash.GetProperty("refuelingTrash").EnumerateArray(), t => t.GetProperty("id").GetString() == logId);
        Assert.True(trash.GetProperty("refuelingTrashDeletableCount").GetInt32() >= 1);

        Data(await Send("mutation($id: UUID!) { restoreRefueling(id: $id) { id } }", new { id = logId }));
        Assert.Equal(1, Data(await Send("query($id: UUID!) { refuelingCount(vehicleId: $id) }", new { id })).GetProperty("refuelingCount").GetInt32());

        Data(await Send("mutation($id: UUID!) { deleteRefueling(id: $id) { id } }", new { id = logId }));
        Assert.True(Data(await Send("mutation { emptyRefuelingTrash }")).GetProperty("emptyRefuelingTrash").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.Null, Data(await Send("query($id: UUID!) { refueling(id: $id) { id } }", new { id = logId })).GetProperty("refueling").ValueKind);
    }

    [Fact]
    public async Task Logs_AreSortedAndPagedByTheServer()
    {
        var id = await AddVehicle();
        await Log(id, "2026-08-01", 1000, volume: 50, cost: 100);
        await Log(id, "2026-08-10", 1500, volume: 20, cost: 70);
        await Log(id, "2026-08-20", 2000, volume: 40, cost: 40);
        const string Query = "query($id: UUID!, $o: RefuelingSortField!, $d: SortDirection!, $skip: Int!, $take: Int!) { refuelings(vehicleId: $id, orderBy: $o, direction: $d, skip: $skip, take: $take) { odometer } refuelingCount(vehicleId: $id) }";

        async Task<long[]> Odometers(string order, string dir, int skip = 0, int take = 50) =>
            Data(await Send(Query, new { id, o = order, d = dir, skip, take })).GetProperty("refuelings").EnumerateArray().Select(x => x.GetProperty("odometer").GetInt64()).ToArray();

        Assert.Equal(new long[] { 2000L, 1500L, 1000L }, await Odometers("DATE", "DESC"));
        Assert.Equal(new long[] { 1000L, 1500L, 2000L }, await Odometers("ODOMETER", "ASC"));
        Assert.Equal(new long[] { 1500L, 2000L, 1000L }, await Odometers("VOLUME", "ASC"));
        Assert.Equal(new long[] { 2000L, 1000L, 1500L }, await Odometers("PRICE_PER_UNIT", "ASC")); // 1.0, 2.0, 3.5
        Assert.Equal(new long[] { 1500L }, await Odometers("DATE", "DESC", skip: 1, take: 1));
        Assert.Equal(3, Data(await Send(Query, new { id, o = "DATE", d = "ASC", skip = 0, take = 1 })).GetProperty("refuelingCount").GetInt32()); // count ignores paging
    }

    [Fact]
    public async Task OdometerRules_ReturnTranslatableErrors()
    {
        var id = await AddVehicle();
        await Log(id, "2026-08-01", 1000);
        await Log(id, "2026-09-01", 2000);

        var below = await Log(id, "2026-08-15", 900);
        var above = await Log(id, "2026-08-15", 2100);
        var negative = await Log(id, "2026-09-02", -1);
        var future = await Log(id, "2999-01-01", 3000);
        var inBetween = await Log(id, "2026-08-15", 1500);

        Assert.Equal("odometer.belowPrevious", Code(below));
        var args = below.GetProperty("errors")[0].GetProperty("extensions").GetProperty("args");
        Assert.Equal((1000L, "2026-08-01"), (args.GetProperty("previous").GetInt64(), args.GetProperty("date").GetString()));
        Assert.Equal("odometer.aboveNext", Code(above));
        Assert.Equal("odometer.negative", Code(negative));
        Assert.Equal("refueling.dateInFuture", Code(future));
        Assert.Null(Code(inBetween));
    }

    [Fact]
    public async Task InvalidValues_AreRejectedWithKeys()
    {
        var id = await AddVehicle();

        Assert.Equal("refueling.volumePositive", Code(await Log(id, "2026-09-01", 100, volume: 0)));
        Assert.Equal("cost.negative", Code(await Log(id, "2026-09-01", 100, cost: -1)));
        Assert.Equal("money.currencyInvalid", Code(await Log(id, "2026-09-01", 100, currency: "EURO")));
        Assert.Equal("refueling.noteTooLong", Code(await Log(id, "2026-09-01", 100, note: new string('x', 501))));
        Assert.Equal("vehicle.notFound", Code(await Log(Guid.NewGuid().ToString(), "2026-09-01", 100)));
    }

    [Fact]
    public async Task VeryLargeOdometerValues_AreAccepted()
    {
        var id = await AddVehicle();

        Assert.Null(Code(await Log(id, "2026-09-01", 9_000_000_000_000)));
    }

    [Fact]
    public async Task Units_AreChosenPerVehicle_AndLockedOnceItHasLogs()
    {
        var id = await AddVehicle("US truck", units: "{\"distance\":\"MILES\",\"volume\":\"US_GALLONS\"}");
        var units = Data(await Send("query($id: UUID!) { vehicle(id: $id) { units { distance volume } } }", new { id })).GetProperty("vehicle").GetProperty("units");
        Assert.Equal(("MILES", "US_GALLONS"), (units.GetProperty("distance").GetString(), units.GetProperty("volume").GetString()));

        // changeable while there are no logs ...
        Data(await Send("mutation($i: UpdateVehicleInput!) { updateVehicle(input: $i) { id } }",
            new { i = new { id, name = "US truck", fuelType = "DIESEL", units = new { distance = "MILES", volume = "IMPERIAL_GALLONS" } } }));
        await Log(id, "2026-09-01", 100);

        // ... then locked
        var locked = await Send("mutation($i: UpdateVehicleInput!) { updateVehicle(input: $i) { id } }",
            new { i = new { id, name = "US truck", fuelType = "DIESEL", units = new { distance = "KILOMETERS", volume = "LITERS" } } });
        Assert.Equal("vehicle.unitsLocked", Code(locked));
        Data(await Send("mutation($i: UpdateVehicleInput!) { updateVehicle(input: $i) { id } }", new { i = new { id, name = "Renamed truck", fuelType = "DIESEL" } })); // units omitted: fine
    }

    [Fact]
    public async Task NewVehicles_GetTheInstanceDefaultUnits_AndTheDefaultsAreExposed()
    {
        var id = await AddVehicle();

        var data = Data(await Send("query($id: UUID!) { vehicle(id: $id) { units { distance volume } } vehicleDefaults { distanceUnit volumeUnit currency } }", new { id }));

        Assert.Equal(("KILOMETERS", "LITERS"), (data.GetProperty("vehicle").GetProperty("units").GetProperty("distance").GetString(), data.GetProperty("vehicle").GetProperty("units").GetProperty("volume").GetString()));
        Assert.Equal("EUR", data.GetProperty("vehicleDefaults").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task LogDefaults_SuggestTheLatestOdometerAndLastCurrency()
    {
        var id = await AddVehicle();
        var empty = Data(await Send("query($id: UUID!) { logDefaults(vehicleId: $id) { lastOdometer lastDate currency } }", new { id })).GetProperty("logDefaults");
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastOdometer").ValueKind);
        Assert.Equal("EUR", empty.GetProperty("currency").GetString()); // the instance default

        await Log(id, "2026-08-01", 1000, currency: "EUR");
        await Log(id, "2026-09-01", 2000, currency: "HUF");

        var defaults = Data(await Send("query($id: UUID!) { logDefaults(vehicleId: $id) { lastOdometer lastDate currency } }", new { id })).GetProperty("logDefaults");
        Assert.Equal((2000L, "2026-09-01", "HUF"), (defaults.GetProperty("lastOdometer").GetInt64(), defaults.GetProperty("lastDate").GetString(), defaults.GetProperty("currency").GetString()));
    }

    [Fact]
    public async Task TrashedLogs_DoNotCountForTheOdometerRules_UntilRestored()
    {
        var id = await AddVehicle();
        var high = Data(await Log(id, "2026-09-01", 5000)).GetProperty("logRefueling").GetProperty("id").GetString();
        Data(await Send("mutation($id: UUID!) { deleteRefueling(id: $id) { id } }", new { id = high }));

        Assert.Null(Code(await Log(id, "2026-09-10", 100)));
        Assert.Equal("odometer.aboveNext", Code(await Send("mutation($id: UUID!) { restoreRefueling(id: $id) { id } }", new { id = high })));
    }

    [Fact]
    public async Task DeletingAVehicleForGood_RemovesItsLogsToo()
    {
        var id = await AddVehicle();
        await Log(id, "2026-09-01", 100);
        Data(await Send("mutation($id: UUID!) { deleteVehicle(id: $id) { id } }", new { id }));

        Assert.True(Data(await Send("mutation { emptyTrash }")).GetProperty("emptyTrash").GetInt32() >= 1);

        Assert.Equal(0, Data(await Send("query($id: UUID!) { refuelingCount(vehicleId: $id) }", new { id })).GetProperty("refuelingCount").GetInt32());
    }

    [Fact]
    public async Task AFillUpAfterAMissedOne_HasNoConsumption_AndKeepsTheMarkUnlessAnUpdateSaysOtherwise()
    {
        var id = await AddVehicle();
        await Log(id, "2026-08-01", 1000, volume: 40);
        var logged = Data(await Send("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id missedPreviousFillUp consumption } }",
            new { i = new { vehicleId = id, date = "2026-08-11", volume = 30, totalCost = 60, currency = "EUR", odometer = 2000, isFullTank = true, missedPreviousFillUp = true } }))
            .GetProperty("logRefueling");
        await Log(id, "2026-08-21", 2500, volume: 30);

        Assert.True(logged.GetProperty("missedPreviousFillUp").GetBoolean());
        Assert.Equal(JsonValueKind.Null, logged.GetProperty("consumption").ValueKind);
        Assert.Equal([("1000", (decimal?)null), ("2000", null), ("2500", 6m)], await Consumptions(id));

        var update = "mutation($i: UpdateRefuelingInput!) { updateRefueling(input: $i) { missedPreviousFillUp } }";
        var same = new { id = logged.GetProperty("id").GetString(), date = "2026-08-11", volume = 30, totalCost = 60, odometer = 2000, isFullTank = true };
        Assert.True(Data(await Send(update, new { i = same })).GetProperty("updateRefueling").GetProperty("missedPreviousFillUp").GetBoolean()); // omitted: kept
        Assert.False(Data(await Send(update, new { i = new { same.id, same.date, same.volume, same.totalCost, same.odometer, same.isFullTank, missedPreviousFillUp = false } }))
            .GetProperty("updateRefueling").GetProperty("missedPreviousFillUp").GetBoolean());
        Assert.Equal([("1000", (decimal?)null), ("2000", 3m), ("2500", 6m)], await Consumptions(id));
    }
}
