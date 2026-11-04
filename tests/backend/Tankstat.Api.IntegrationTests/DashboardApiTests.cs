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

    private static async Task<string> AddVehicle(HttpClient c) =>
        (await c.Gql("mutation { addVehicle(input: { name: \"Car\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;

    private static Task<JsonElement> Share(HttpClient c, string vehicleId, string userId, string level) =>
        c.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId, userId, level } });

    private static async Task<bool> DeleteChart(HttpClient c, string? chartId) =>
        (await c.Gql("mutation($id: UUID!) { deleteVehicleChart(id: $id) }", new { id = chartId })).Data().GetProperty("deleteVehicleChart").GetBoolean();

    private static async Task<string> CarWithLogs(HttpClient c)
    {
        var id = await AddVehicle(c);
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
              vehicle(id: $id) { summary { latestOdometer averageConsumption currency thisMonthSpend fillUpCount expenseCount spendTrend { month amount } spending { currency thisMonth lastMonth } trends { spending { currency recent earlier } distance { recent earlier } consumption { recent earlier recentFillUps earlierFillUps } } } }
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
        var trends = summary.GetProperty("trends");
        Assert.Equal(38000m, Assert.Single(trends.GetProperty("spending").EnumerateArray()).GetProperty("recent").GetDecimal()); // the two earlier months; this month is left out
        var consumption = trends.GetProperty("consumption");
        Assert.Equal(2, consumption.GetProperty("recentFillUps").GetInt32()); // the mean of the two there are, and nothing before them to compare with
        Assert.Equal(JsonValueKind.Null, consumption.GetProperty("earlier").ValueKind);

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
        await Share(alice, id, bobId, "EDIT");
        const string save = "mutation($i: SaveChartInput!) { saveVehicleChart(input: $i) { id title isShared metric kind range rangeFrom rangeTo canEdit createdBy { displayName } } }";
        object Input(string title, bool shared, string range = "LAST6_MONTHS", string? from = null, string? to = null, string? chartId = null) =>
            new { i = new { id = chartId, vehicleId = id, title, shared, config = new { metric = "FUEL_VOLUME", grouping = "MONTH", kind = "LINE", range, stacked = false, from, to } } };

        var mine = (await alice.Gql(save, Input("Mine", false, "CUSTOM", "2026-01-01", "2026-03-31"))).Data().GetProperty("saveVehicleChart");
        var shared = (await alice.Gql(save, Input("For everyone", true))).Data().GetProperty("saveVehicleChart");
        Assert.Equal(("CUSTOM", "2026-01-01", "2026-03-31", true, "alice"), (mine.GetProperty("range").GetString(), mine.GetProperty("rangeFrom").GetString(), mine.GetProperty("rangeTo").GetString(), mine.GetProperty("canEdit").GetBoolean(), mine.GetProperty("createdBy").GetProperty("displayName").GetString()));

        const string list = "query($id: UUID!) { vehicleCharts(vehicleId: $id) { id title canEdit canDelete } }";
        var bobsView = (await bob.Gql(list, new { id })).Data().GetProperty("vehicleCharts").EnumerateArray().ToList();
        Assert.Equal(["For everyone"], bobsView.Select(c => c.GetProperty("title").GetString()));
        Assert.Equal((false, false), (bobsView[0].GetProperty("canEdit").GetBoolean(), bobsView[0].GetProperty("canDelete").GetBoolean()));

        var denied = await bob.Gql(save, Input("Hijack", true, chartId: shared.GetProperty("id").GetString()));
        Assert.Equal("chart.notYours", denied.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        var invalid = await alice.Gql(save, Input("Bad", false, "CUSTOM"));
        Assert.Equal("chart.datesRequired", invalid.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());

        Assert.True(await DeleteChart(alice, mine.GetProperty("id").GetString()));
        Assert.Single((await alice.Gql(list, new { id })).Data().GetProperty("vehicleCharts").EnumerateArray());
    }

    // #210: whoever holds Delete access to the logs without having made the chart (the owner, an administrator, a Delete grant) is told
    // `canEdit false, canDelete true`, has a save refused, and may remove it.
    [Fact]
    public async Task DeleteAccessWithoutHavingMadeAChart_MayRemoveIt_NeverChangeIt_AndTheFlagsSaySo()
    {
        var people = await _app.Users();
        var (admin, alice, bob, bobId) = (people.Admin, people.Alice, people.Bob, people.BobId);
        var vehicleId = await AddVehicle(alice);
        Task Grant(string level) => Share(alice, vehicleId, bobId, level);
        const string save = "mutation($i: SaveChartInput!) { saveVehicleChart(input: $i) { id } }";
        object Input(string title, string? id = null) =>
            new { i = new { id, vehicleId, title, shared = true, config = new { metric = "FUEL_VOLUME", grouping = "MONTH", kind = "LINE", range = "LAST6_MONTHS", stacked = false } } };
        async Task<(bool CanEdit, bool CanDelete)> Flags(HttpClient c, string chartId)
        {
            var chart = (await c.Gql("query($id: UUID!) { vehicleCharts(vehicleId: $id) { id canEdit canDelete } }", new { id = vehicleId })).Data().GetProperty("vehicleCharts")
                .EnumerateArray().Single(x => x.GetProperty("id").GetString() == chartId);
            return (chart.GetProperty("canEdit").GetBoolean(), chart.GetProperty("canDelete").GetBoolean());
        }
        async Task<string?> SaveKey(HttpClient c, string chartId) => (await c.Gql(save, Input("Changed", chartId))).ErrorKey();

        // Bob (Edit) shares a chart: the owner and an administrator are Delete-level non-creators; the creator keeps both rights.
        await Grant("EDIT");
        var bobs = (await bob.Gql(save, Input("Bob's"))).Data().GetProperty("saveVehicleChart").GetProperty("id").GetString()!;
        Assert.Equal((false, true), await Flags(alice, bobs));
        Assert.Equal((false, true), await Flags(admin, bobs));
        Assert.Equal((true, true), await Flags(bob, bobs));
        Assert.Equal("chart.notYours", await SaveKey(alice, bobs));
        Assert.Equal("chart.notYours", await SaveKey(admin, bobs));
        Assert.Null(await SaveKey(bob, bobs));
        Assert.True(await DeleteChart(admin, bobs));

        // Bob holds a Delete grant on a chart Alice shared.
        await Grant("DELETE");
        var alices = (await alice.Gql(save, Input("Alice's"))).Data().GetProperty("saveVehicleChart").GetProperty("id").GetString()!;
        Assert.Equal((false, true), await Flags(bob, alices));
        Assert.Equal("chart.notYours", await SaveKey(bob, alices));
        Assert.True(await DeleteChart(bob, alices));
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
    public async Task ADashboardsLayouts_TheVehiclesForEveryone_APersonalOneForItsOwner_AndAViewerCannotChangeTheVehicles()
    {
        var people = await _app.Users();
        var (alice, bob) = (people.Alice, people.Bob);
        var id = await CarWithLogs(alice);
        // A viewer: the administrator grants Bob View on Alice's data (a vehicle's logs are shared with Edit or more).
        await people.Admin.Gql("mutation($i: SetAccessGrantInput!) { setAccessGrant(input: $i) }", new { i = new { ownerId = people.AliceId, granteeId = people.BobId, level = "VIEW" } });
        var shared = (await alice.Gql("mutation($i: SaveChartInput!) { saveVehicleChart(input: $i) { id } }",
            new { i = new { vehicleId = id, title = "Shared", shared = true, config = new { metric = "FUEL_VOLUME", grouping = "MONTH", kind = "BAR", range = "LAST6_MONTHS", stacked = false } } }))
            .Data().GetProperty("saveVehicleChart").GetProperty("id").GetString();
        const string save = "mutation($i: SaveDashboardLayoutInput!) { saveDashboardLayout(input: $i) }";
        const string read = "query($id: UUID!) { vehicle(id: $id) { dashboard { canChangeShared shared { tiles { id x y w h pinned } hidden updatedAt } personal { tiles { id } } } } }";
        object Layout(bool personal, params string[] charts) => new
        {
            i = new { vehicleId = id, personal, tiles = charts.Select((c, k) => new { id = c, x = 0, y = k * 8, w = 2, h = 8, pinned = k == 0 }).Prepend(new { id = "odometer", x = 2, y = 0, w = 1, h = 3, pinned = false }).ToArray(), hidden = Array.Empty<string>() },
        };
        static IEnumerable<string?> Ids(JsonElement layout) => layout.GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("id").GetString());

        var standard = (await alice.Gql(read, new { id })).Data().GetProperty("vehicle").GetProperty("dashboard");
        Assert.Equal((JsonValueKind.Null, true), (standard.GetProperty("shared").ValueKind, standard.GetProperty("canChangeShared").GetBoolean()));
        Assert.True((await alice.Gql(save, Layout(false, "preset:distance", $"chart:{shared}"))).Data().GetProperty("saveDashboardLayout").GetBoolean());
        var refused = await bob.Gql(save, Layout(false, "preset:consumption"));
        await bob.Gql(save, Layout(true, "preset:consumption"));

        var bobs = (await bob.Gql(read, new { id })).Data().GetProperty("vehicle").GetProperty("dashboard");
        Assert.Equal("dashboard.needEditToChange", refused.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        Assert.False(bobs.GetProperty("canChangeShared").GetBoolean());
        Assert.Equal(["odometer", "preset:distance", $"chart:{shared}"], Ids(bobs.GetProperty("shared")));
        Assert.True(bobs.GetProperty("shared").GetProperty("tiles")[1].GetProperty("pinned").GetBoolean());
        Assert.Equal(["odometer", "preset:consumption"], Ids(bobs.GetProperty("personal")));
        Assert.Equal(JsonValueKind.Null, (await alice.Gql(read, new { id })).Data().GetProperty("vehicle").GetProperty("dashboard").GetProperty("personal").ValueKind);

        await bob.Gql("mutation($id: UUID!) { resetDashboardLayout(vehicleId: $id, personal: true) }", new { id });
        Assert.Equal(JsonValueKind.Null, (await bob.Gql(read, new { id })).Data().GetProperty("vehicle").GetProperty("dashboard").GetProperty("personal").ValueKind);
        var invalid = await alice.Gql(save, Layout(true, "not a chart"));
        Assert.Equal("dashboard.itemInvalid", invalid.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    [Fact]
    public async Task ATrendFigure_OverAPeriodOfItsOwn_GivesItsWindowsAndValues_AndABadPeriodIsRefused()
    {
        var (alice, bob, _) = await Users();
        var id = await CarWithLogs(alice);
        const string trend = "query($id: UUID!, $f: TrendFigure!, $p: TrendPeriodInput!) { vehicleTrend(vehicleId: $id, figure: $f, period: $p) { recent { start end fillUps } earlier { start end fillUps } currencies { currency recent earlier } consumption { recent recentFillUps } distance { recent earlier } } }";

        // The last two fill-ups against the two before: the latest two months' 18 000 and 17 500, the oil change of this month too.
        var spending = (await alice.Gql(trend, new { id, f = "SPENDING", p = new { kind = "LAST", unit = "FILL_UPS", count = 2 } })).Data().GetProperty("vehicleTrend");
        Assert.Equal(2, spending.GetProperty("recent").GetProperty("fillUps").GetInt32());
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), spending.GetProperty("recent").GetProperty("end").GetString());
        var huf = Assert.Single(spending.GetProperty("currencies").EnumerateArray());
        Assert.Equal(("HUF", 70500m), (huf.GetProperty("currency").GetString(), huf.GetProperty("recent").GetDecimal()));
        // This year so far against last year to the same day: the defaults' fourth.
        var distance = (await alice.Gql(trend, new { id, f = "DISTANCE", p = new { kind = "THIS_YEAR", against = "YEAR_EARLIER" } })).Data().GetProperty("vehicleTrend");
        Assert.NotEqual(JsonValueKind.Null, distance.GetProperty("distance").ValueKind);

        Assert.Equal("trend.countRange", (await alice.Gql(trend, new { id, f = "SPENDING", p = new { kind = "LAST", unit = "DAYS", count = 0 } })).ErrorKey());
        Assert.Equal("trend.sinceRange", (await alice.Gql(trend, new { id, f = "SPENDING", p = new { kind = "SINCE", since = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd") } })).ErrorKey());
        Assert.Equal("vehicle.notFound", (await bob.Gql(trend, new { id, f = "SPENDING", p = new { kind = "THIS_YEAR" } })).ErrorKey());
    }

    [Fact]
    public async Task ATilesPeriod_IsSavedAndReadBack_AndATileWithoutOne_AsAnOlderClientSendsIt_StillSaves()
    {
        var (alice, _, _) = await Users();
        var id = await AddVehicle(alice);
        const string save = "mutation($i: SaveDashboardLayoutInput!) { saveDashboardLayout(input: $i) }";
        const string read = "query($id: UUID!) { vehicle(id: $id) { dashboard { personal { tiles { id period { kind unit count since whole against } } } } } }";
        var period = new { kind = "LAST", unit = "WEEKS", count = 8, whole = true, against = "YEAR_EARLIER" };

        await alice.Gql(save, new { i = new { vehicleId = id, personal = true, hidden = Array.Empty<string>(), tiles = new object[] { new { id = "spendingTrend", x = 0, y = 0, w = 1, h = 4, period }, new { id = "odometer", x = 1, y = 0, w = 1, h = 4 } } } });

        var tiles = (await alice.Gql(read, new { id })).Data().GetProperty("vehicle").GetProperty("dashboard").GetProperty("personal").GetProperty("tiles");
        var saved = tiles[0].GetProperty("period");
        Assert.Equal(("LAST", "WEEKS", 8, true, "YEAR_EARLIER"), (saved.GetProperty("kind").GetString(), saved.GetProperty("unit").GetString(), saved.GetProperty("count").GetInt32(), saved.GetProperty("whole").GetBoolean(), saved.GetProperty("against").GetString()));
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("since").ValueKind);
        Assert.Equal(JsonValueKind.Null, tiles[1].GetProperty("period").ValueKind); // the figure's default
        // A period on a chart, or one that does not hold, is refused whole.
        Assert.Equal("dashboard.itemInvalid", (await alice.Gql(save, new { i = new { vehicleId = id, personal = true, hidden = Array.Empty<string>(), tiles = new object[] { new { id = "preset:monthlyCosts", x = 0, y = 0, w = 2, h = 8, period } } } })).ErrorKey());
        Assert.Equal("trend.wholeNeedsUnit", (await alice.Gql(save, new { i = new { vehicleId = id, personal = true, hidden = Array.Empty<string>(), tiles = new object[] { new { id = "spendingTrend", x = 0, y = 0, w = 1, h = 4, period = new { kind = "LAST", unit = "DAYS", count = 8, whole = true } } } } })).ErrorKey());
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
