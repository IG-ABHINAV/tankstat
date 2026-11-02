using System.Text.Json;

namespace Tankstat.Api.IntegrationTests;

/// <summary>Dashboard data over real GraphQL and a real database: summaries, chart data and saved/shared charts.</summary>
[Collection(ApiCollection.Name)]
public class DashboardApiTests : IDisposable
{
    private readonly TestApp _app = TestApp.Standalone();

    public void Dispose() => _app.Dispose();

    private async Task<(HttpClient Alice, HttpClient Bob, string BobId)> Users()
    {
        var people = await _app.Users();
        return (people.Alice, people.Bob, people.BobId);
    }

    private static async Task<string> CarWithLogs(HttpClient c)
    {
        var id = (await c.Gql("mutation { addVehicle(input: { name: \"Car\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        var today = DateTime.UtcNow;
        string D(int monthsAgo) => new DateTime(today.Year, today.Month, 1).AddMonths(-monthsAgo).ToString("yyyy-MM-dd");
        foreach (var (months, odo, vol, cost) in new[] { (2, 1000, 40, 20000), (1, 1500, 30, 18000), (0, 2000, 25, 17500) })
            await c.Gql("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }", new { i = new { vehicleId = id, date = D(months), volume = vol, totalCost = cost, currency = "HUF", odometer = odo, isFullTank = true } });
        await c.Gql("mutation($i: AddExpenseInput!) { addExpense(input: $i) { id } }", new { i = new { vehicleId = id, date = D(0), title = "Oil", category = "Service", amount = 35000, currency = "HUF" } });
        return id;
    }

    [Fact]
    public async Task TheSummary_AndChartData_ComeFromTheStoredLogs()
    {
        var (alice, _, _) = await Users();
        var id = await CarWithLogs(alice);

        var data = (await alice.Gql(@"query($id: UUID!, $c: ChartConfigInput!, $c2: ChartConfigInput!) {
              vehicle(id: $id) { summary { latestOdometer averageConsumption currency thisMonthSpend fillUpCount expenseCount spendTrend { month amount } spending { currency thisMonth lastMonth } } }
              total: vehicleChartData(vehicleId: $id, config: $c) { unit series { kind currency points { key value } } }
              byCategory: vehicleChartData(vehicleId: $id, config: $c2) { series { currency points { key value } } } }",
            new
            {
                id,
                c = new { metric = "TOTAL_SPEND", grouping = "MONTH", kind = "BAR", range = "LAST3_MONTHS", stacked = true },
                c2 = new { metric = "EXPENSE_COST", grouping = "CATEGORY", kind = "DONUT", range = "ALL", stacked = false },
            })).Data();

        var summary = data.GetProperty("vehicle").GetProperty("summary");
        Assert.Equal((2000L, "HUF", 3, 1), (summary.GetProperty("latestOdometer").GetInt64(), summary.GetProperty("currency").GetString(), summary.GetProperty("fillUpCount").GetInt32(), summary.GetProperty("expenseCount").GetInt32()));
        Assert.Equal(52500m, summary.GetProperty("thisMonthSpend").GetDecimal()); // 17500 fuel + 35000 oil
        Assert.Equal(5.5m, summary.GetProperty("averageConsumption").GetDecimal()); // the mean of 6 (30 l / 500 km) and 5 (25 l / 500 km)
        Assert.Equal(6, summary.GetProperty("spendTrend").GetArrayLength());
        var spent = Assert.Single(summary.GetProperty("spending").EnumerateArray());
        Assert.Equal(("HUF", 52500m), (spent.GetProperty("currency").GetString(), spent.GetProperty("thisMonth").GetDecimal()));

        var total = data.GetProperty("total");
        Assert.Equal("CURRENCY", total.GetProperty("unit").GetString());
        Assert.Equal(["expenses", "fuel"], total.GetProperty("series").EnumerateArray().Select(s => s.GetProperty("kind").GetString()!).Order());
        var category = data.GetProperty("byCategory").GetProperty("series")[0].GetProperty("points")[0];
        Assert.Equal(("Service", 35000m), (category.GetProperty("key").GetString(), category.GetProperty("value").GetDecimal()));
    }

    [Fact]
    public async Task SavedCharts_ArePrivateUntilShared_AndOnlyTheCreatorChangesThem()
    {
        var (alice, bob, bobId) = await Users();
        var id = await CarWithLogs(alice);
        await alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = id, userId = bobId, level = "EDIT" } });
        const string save = "mutation($i: SaveChartInput!) { saveVehicleChart(input: $i) { id title isShared metric kind range rangeFrom rangeTo canEdit createdBy { displayName } } }";
        object Input(string title, bool shared, string range = "LAST6_MONTHS", string? from = null, string? to = null, string? chartId = null) =>
            new { i = new { id = chartId, vehicleId = id, title, shared, config = new { metric = "FUEL_VOLUME", grouping = "MONTH", kind = "LINE", range, stacked = false, from, to } } };

        var mine = (await alice.Gql(save, Input("Mine", false, "CUSTOM", "2026-01-01", "2026-03-31"))).Data().GetProperty("saveVehicleChart");
        var shared = (await alice.Gql(save, Input("For everyone", true))).Data().GetProperty("saveVehicleChart");
        Assert.Equal(("CUSTOM", "2026-01-01", "2026-03-31", true, "alice"), (mine.GetProperty("range").GetString(), mine.GetProperty("rangeFrom").GetString(), mine.GetProperty("rangeTo").GetString(), mine.GetProperty("canEdit").GetBoolean(), mine.GetProperty("createdBy").GetProperty("displayName").GetString()));

        const string list = "query($id: UUID!) { vehicleCharts(vehicleId: $id) { id title canEdit } }";
        var bobsView = (await bob.Gql(list, new { id })).Data().GetProperty("vehicleCharts").EnumerateArray().ToList();
        Assert.Equal(["For everyone"], bobsView.Select(c => c.GetProperty("title").GetString()));
        Assert.False(bobsView[0].GetProperty("canEdit").GetBoolean());

        var denied = await bob.Gql(save, Input("Hijack", true, chartId: shared.GetProperty("id").GetString()));
        Assert.Equal("chart.notYours", denied.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        var invalid = await alice.Gql(save, Input("Bad", false, "CUSTOM"));
        Assert.Equal("chart.datesRequired", invalid.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());

        Assert.True((await alice.Gql("mutation($id: UUID!) { deleteVehicleChart(id: $id) }", new { id = mine.GetProperty("id").GetString() })).Data().GetProperty("deleteVehicleChart").GetBoolean());
        Assert.Single((await alice.Gql(list, new { id })).Data().GetProperty("vehicleCharts").EnumerateArray());
    }

    [Fact]
    public async Task ASavedChart_KeepsItsTrendOptions_AndTheDataCarriesTheirSeries()
    {
        var (alice, _, _) = await Users();
        var id = await CarWithLogs(alice);
        var aYearAgo = new DateTime(DateTime.UtcNow.Year - 1, DateTime.UtcNow.Month, 1).ToString("yyyy-MM-dd");
        await alice.Gql("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId = id, date = aYearAgo, volume = 30, totalCost = 15000, currency = "HUF", odometer = 500, isFullTank = true } });
        var config = new { metric = "TOTAL_SPEND", grouping = "MONTH", kind = "BAR", range = "LAST3_MONTHS", stacked = false, trend = "MOVING_AVERAGE", compareYearEarlier = true };

        var saved = (await alice.Gql("mutation($i: SaveChartInput!) { saveVehicleChart(input: $i) { trend runningTotal compareYearEarlier } }",
            new { i = new { vehicleId = id, title = "Trend", shared = false, config } })).Data().GetProperty("saveVehicleChart");
        var data = (await alice.Gql("query($id: UUID!, $c: ChartConfigInput!) { vehicleChartData(vehicleId: $id, config: $c) { series { kind role slope points { key } } } }",
            new { id, c = config })).Data().GetProperty("vehicleChartData").GetProperty("series").EnumerateArray().ToList();
        var olderClient = (await alice.Gql("query($id: UUID!) { vehicleChartData(vehicleId: $id, config: { metric: TOTAL_SPEND, grouping: MONTH, kind: BAR, range: LAST3_MONTHS, stacked: false }) { series { role } } }",
            new { id })).Data().GetProperty("vehicleChartData").GetProperty("series").EnumerateArray().Select(s => s.GetProperty("role").GetString()).ToList();

        Assert.Equal(("MOVING_AVERAGE", false, true), (saved.GetProperty("trend").GetString(), saved.GetProperty("runningTotal").GetBoolean(), saved.GetProperty("compareYearEarlier").GetBoolean()));
        Assert.Equal(["VALUE", "YEAR_EARLIER", "MOVING_AVERAGE"], data.Select(s => s.GetProperty("role").GetString()));
        Assert.Equal(data[0].GetProperty("points").EnumerateArray().Select(p => p.GetProperty("key").GetString()), data[1].GetProperty("points").EnumerateArray().Select(p => p.GetProperty("key").GetString()));
        Assert.Equal(["VALUE"], olderClient); // a request without the new fields still draws the values alone
    }

    [Fact]
    public async Task ASecondMeasure_IsSaved_AndItsSeriesSayTheirOwnMetricAndUnit()
    {
        var (alice, _, _) = await Users();
        var id = await CarWithLogs(alice);
        var config = new { metric = "DISTANCE", grouping = "MONTH", kind = "BAR", range = "LAST3_MONTHS", stacked = false, secondMetric = "AVERAGE_CONSUMPTION", secondKind = "LINE" };

        var saved = (await alice.Gql("mutation($i: SaveChartInput!) { saveVehicleChart(input: $i) { secondMetric secondKind } }",
            new { i = new { vehicleId = id, title = "Distance and consumption", shared = false, config } })).Data().GetProperty("saveVehicleChart");
        var data = (await alice.Gql("query($id: UUID!, $c: ChartConfigInput!) { vehicleChartData(vehicleId: $id, config: $c) { unit series { metric unit role } } }",
            new { id, c = config })).Data().GetProperty("vehicleChartData");
        var refused = await alice.Gql("query($id: UUID!) { vehicleChartData(vehicleId: $id, config: { metric: DISTANCE, grouping: MONTH, kind: BAR, range: ALL, stacked: false, secondMetric: DISTANCE, secondKind: LINE }) { unit } }", new { id });

        Assert.Equal(("AVERAGE_CONSUMPTION", "LINE"), (saved.GetProperty("secondMetric").GetString(), saved.GetProperty("secondKind").GetString()));
        Assert.Equal("DISTANCE", data.GetProperty("unit").GetString());
        Assert.Equal([("DISTANCE", "DISTANCE"), ("AVERAGE_CONSUMPTION", "CONSUMPTION")],
            data.GetProperty("series").EnumerateArray().Select(s => (s.GetProperty("metric").GetString(), s.GetProperty("unit").GetString())));
        Assert.Equal("chart.secondSameMetric", refused.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    [Fact]
    public async Task StrangersGetNothing()
    {
        var (alice, bob, _) = await Users();
        var id = await CarWithLogs(alice);

        var summary = (await bob.Gql("query($id: UUID!) { vehicle(id: $id) { id } }", new { id })).Data().GetProperty("vehicle");
        var chart = await bob.Gql("query($id: UUID!) { vehicleChartData(vehicleId: $id, config: { metric: TOTAL_SPEND, grouping: MONTH, kind: BAR, range: ALL, stacked: false }) { unit } }", new { id });

        Assert.Equal(JsonValueKind.Null, summary.ValueKind);
        Assert.Equal("vehicle.notFound", chart.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }
}
