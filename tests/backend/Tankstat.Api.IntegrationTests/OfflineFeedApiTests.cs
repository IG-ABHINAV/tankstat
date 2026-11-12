using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Tankstat.Api.IntegrationTests;

/// <summary>What a device downloads of a vehicle to show it offline (the feed), and the offline windows, over real GraphQL and a real database.</summary>
[Collection(ApiCollection.Name)]
public class OfflineFeedApiTests : IDisposable
{
    // Everything a device keeps of a row: the grids' columns, the details, the trash, the version and when it was saved.
    private const string Feed = """
        query($i: OfflineChangesInput!) { offlineChanges(input: $i) {
          vehicle { id name version updatedAt logAccess }
          refuelings { id vehicleId date volume totalCost currency pricePerUnit consumption odometer isFullTank missedPreviousFillUp note
            reviewState filledFromPhoto canEdit canDelete deletedAt version updatedAt createdBy { id displayName avatarUrl } photos { id url } }
          expenses { id vehicleId date title category amount currency odometer note reviewState filledFromPhoto canEdit canDelete deletedAt
            version updatedAt createdBy { id displayName avatarUrl } schedules { id title } photos { id url } }
          recurring { id title version updatedAt status { state dueDate } }
          removed { type id }
          next watermark resync } }
        """;

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly TestApp _app;

    public OfflineFeedApiTests() => _app = TestApp.Standalone(services: s => s.AddSingleton<TimeProvider>(_clock));

    public void Dispose() => _app.Dispose();

    private static string Day(int daysAgo) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-daysAgo)).ToString("yyyy-MM-dd");

    private static async Task<string> Car(HttpClient c, string name = "Golf") =>
        (await c.Gql("mutation($n: String!) { addVehicle(input: { name: $n, fuelType: PETROL }) { id } }", new { n = name })).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;

    [Fact]
    public async Task TheFirstPage_CarriesTheVehiclesTyresWhole_AndNoTyreTombstones()
    {
        var people = await _app.Users();
        var car = await Car(people.Alice);
        var set = Guid.NewGuid();
        await people.Alice.Gql("mutation($i: AddTyreSetInput!) { addTyreSet(input: $i) { vehicleId } }",
            new { i = new { vehicleId = car, id = set, name = "Michelin", season = "SUMMER", since = "2023-03-25", fitted = false, day = Day(0) } });
        const string tyres = "query($i: OfflineChangesInput!) { offlineChanges(input: $i) { tyres { sets { set { id name } } } watermark removed { type } } }";

        var page = (await people.Alice.Gql(tyres, new { i = new { vehicleId = car, take = 50 } })).Data().GetProperty("offlineChanges");
        Assert.Equal(set.ToString(), page.GetProperty("tyres").GetProperty("sets")[0].GetProperty("set").GetProperty("id").GetString());

        await people.Alice.Gql("mutation($id: UUID!) { deleteTyreSet(id: $id) { vehicleId } }", new { id = set });
        await people.Alice.Gql("mutation { emptyTyreTrash }");
        var later = (await people.Alice.Gql(tyres, new { i = new { vehicleId = car, since = page.GetProperty("watermark").GetString(), take = 50 } })).Data().GetProperty("offlineChanges");
        Assert.Empty(later.GetProperty("tyres").GetProperty("sets").EnumerateArray());
        Assert.Empty(later.GetProperty("removed").EnumerateArray()); // the tyres came whole: no tombstone of theirs
    }

    private static async Task<string> Refuel(HttpClient c, string vehicleId, int daysAgo, long odometer) =>
        (await c.Gql("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId, date = Day(daysAgo), volume = 40m, totalCost = 20000m, currency = "HUF", odometer, isFullTank = true } }))
        .Data().GetProperty("logRefueling").GetProperty("id").GetString()!;

    private static async Task<string> Expense(HttpClient c, string vehicleId, int daysAgo, string title = "Oil") =>
        (await c.Gql("mutation($i: AddExpenseInput!) { addExpense(input: $i) { id } }",
            new { i = new { vehicleId, date = Day(daysAgo), title, amount = 1000m, currency = "HUF" } }))
        .Data().GetProperty("addExpense").GetProperty("id").GetString()!;

    private static async Task<JsonElement> Page(HttpClient c, object input) =>
        (await c.Gql(Feed, new { i = input })).Data().GetProperty("offlineChanges");

    private static IEnumerable<string> Ids(JsonElement page, string list) => page.GetProperty(list).EnumerateArray().Select(r => r.GetProperty("id").GetString()!);

    /// <summary>Every page of one download: the rows of each table and the last page.</summary>
    private static async Task<(List<string> Refuelings, List<string> Expenses, List<JsonElement> Pages)> Download(HttpClient c, string vehicleId, object first)
    {
        var pages = new List<JsonElement> { await Page(c, first) };
        while (pages[^1].GetProperty("next").GetString() is { } next) pages.Add(await Page(c, new { vehicleId, after = next, take = 2 }));
        return (pages.SelectMany(p => Ids(p, "refuelings")).ToList(), pages.SelectMany(p => Ids(p, "expenses")).ToList(), pages);
    }

    [Fact]
    public async Task AFirstDownload_BringsTheWindowPageByPage_EveryRowOnce_WithItsSchedulesOnTheFirstPage()
    {
        var people = await _app.Users();
        var golf = await Car(people.Alice);
        var refuelings = new List<string>();
        for (var i = 0; i < 5; i++) refuelings.Add(await Refuel(people.Alice, golf, 50 - i * 10, 1000 + i * 500));
        var expenses = new List<string> { await Expense(people.Alice, golf, 40), await Expense(people.Alice, golf, 5), await Expense(people.Alice, golf, 1) };
        await people.Alice.Gql("mutation($i: AddRecurringExpenseInput!) { addRecurringExpense(input: $i) { id } }",
            new { i = new { vehicleId = golf, title = "Insurance", kind = "TIME", intervalMonths = 12, lastDoneDate = Day(100) } });
        await people.Alice.Gql($"mutation {{ deleteRefueling(id: \"{refuelings[4]}\") {{ id }} }}"); // the trash comes too

        var (gotRefuelings, gotExpenses, pages) = await Download(people.Alice, golf, new { vehicleId = golf, take = 2 });

        Assert.Equal(refuelings.Order(), gotRefuelings.Order());
        Assert.Equal(expenses.Order(), gotExpenses.Order());
        Assert.Equal(3, pages.Count); // 5 refuelings in pages of 2
        Assert.Single(pages[0].GetProperty("recurring").EnumerateArray());
        Assert.All(pages.Skip(1), p => Assert.Empty(p.GetProperty("recurring").EnumerateArray()));
        Assert.All(pages, p => Assert.Equal(pages[0].GetProperty("watermark").GetString(), p.GetProperty("watermark").GetString()));
        var trashed = pages.SelectMany(p => p.GetProperty("refuelings").EnumerateArray()).Single(r => r.GetProperty("id").GetString() == refuelings[4]);
        Assert.Equal(JsonValueKind.String, trashed.GetProperty("deletedAt").ValueKind);
        var row = pages.SelectMany(p => p.GetProperty("refuelings").EnumerateArray()).Single(r => r.GetProperty("id").GetString() == refuelings[0]);
        Assert.True(row.GetProperty("canEdit").GetBoolean());
        Assert.Equal("alice", row.GetProperty("createdBy").GetProperty("displayName").GetString());
        Assert.Equal(1, row.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task AFirstDownload_TakesTheLogsFromTheWindowsStart_ALaterOne_EveryRowSavedSince_WhateverItsDate()
    {
        var people = await _app.Users();
        var golf = await Car(people.Alice);
        var old = await Refuel(people.Alice, golf, 90, 1000);
        var recent = await Refuel(people.Alice, golf, 10, 2000);
        var oldExpense = await Expense(people.Alice, golf, 80);
        _clock.Advance(TimeSpan.FromMinutes(3)); // saved before the download, beyond its overlap (and within the access cookie's 15 minutes)

        var first = await Page(people.Alice, new { vehicleId = golf, from = Day(30), take = 50 });
        Assert.Equal([recent], Ids(first, "refuelings"));
        Assert.Empty(Ids(first, "expenses"));

        _clock.Advance(TimeSpan.FromMinutes(3)); // past the overlap
        var watermark = first.GetProperty("watermark").GetString();
        await people.Alice.Gql("mutation($i: UpdateExpenseInput!) { updateExpense(input: $i) { id } }",
            new { i = new { id = oldExpense, date = Day(80), title = "Tyres", amount = 1000m, currency = "HUF" } });
        var added = await Refuel(people.Alice, golf, 5, 3000);

        var later = await Page(people.Alice, new { vehicleId = golf, from = Day(30), since = watermark, take = 50 });

        Assert.Equal([added], Ids(later, "refuelings")); // not the old one, not the recent one: neither changed
        Assert.Equal([oldExpense], Ids(later, "expenses")); // dated before the window, but changed: the device must hear of it
        Assert.DoesNotContain(old, Ids(later, "refuelings"));
    }

    [Fact]
    public async Task ALaterDownload_TellsWhatWasRemovedForGood_AndConsumptionMovesBringTheRowsAgain()
    {
        var people = await _app.Users();
        var golf = await Car(people.Alice);
        var a = await Refuel(people.Alice, golf, 30, 1000);
        var b = await Refuel(people.Alice, golf, 20, 1500);
        var schedule = (await people.Alice.Gql("mutation($i: AddRecurringExpenseInput!) { addRecurringExpense(input: $i) { id } }",
            new { i = new { vehicleId = golf, title = "Insurance", kind = "TIME", intervalMonths = 12, lastDoneDate = Day(100) } }))
            .Data().GetProperty("addRecurringExpense").GetProperty("id").GetString()!;
        _clock.Advance(TimeSpan.FromMinutes(3));
        var watermark = (await Page(people.Alice, new { vehicleId = golf, take = 50 })).GetProperty("watermark").GetString();
        _clock.Advance(TimeSpan.FromMinutes(3));

        await people.Alice.Gql($"mutation {{ deleteRefueling(id: \"{a}\") {{ id }} }}");
        await people.Alice.Gql("mutation { emptyRefuelingTrash }");
        await people.Alice.Gql($"mutation {{ deleteRecurringExpense(id: \"{schedule}\") }}");
        var later = await Page(people.Alice, new { vehicleId = golf, since = watermark, take = 50 });

        var removed = later.GetProperty("removed").EnumerateArray().Select(r => (r.GetProperty("type").GetString(), r.GetProperty("id").GetString())).ToList();
        Assert.Contains(("REFUELING", a), removed);
        Assert.Contains(("RECURRING_EXPENSE", schedule), removed);
        Assert.Equal([b], Ids(later, "refuelings")); // its consumption changed when the one before it went
    }

    [Fact]
    public async Task OnlyThoseWhoMaySeeTheLogsDownloadThem_AndAnOldDownloadStartsAfresh()
    {
        var people = await _app.Users();
        var golf = await Car(people.Alice);
        await Refuel(people.Alice, golf, 10, 1000);

        Assert.Equal("NOT_FOUND", (await people.Bob.Gql(Feed, new { i = new { vehicleId = golf, take = 10 } })).ErrorCode());
        (await people.Alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = golf, userId = people.BobId, level = "EDIT" } })).Data();
        var shared = await Page(people.Bob, new { vehicleId = golf, take = 10 });
        Assert.Single(Ids(shared, "refuelings"));
        Assert.True(shared.GetProperty("refuelings")[0].GetProperty("canEdit").GetBoolean());
        Assert.False(shared.GetProperty("refuelings")[0].GetProperty("canDelete").GetBoolean());

        var tooOld = await Page(people.Alice, new { vehicleId = golf, since = DateTimeOffset.UtcNow.AddDays(-91).ToString("O"), take = 10 });
        Assert.True(tooOld.GetProperty("resync").GetBoolean());
        Assert.Empty(Ids(tooOld, "refuelings"));

        var foreign = await people.Alice.Gql(Feed, new { i = new { vehicleId = golf, after = "not-a-cursor", take = 10 } });
        Assert.Equal("sync.cursorInvalid", foreign.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        var tooMany = await people.Alice.Gql(Feed, new { i = new { vehicleId = golf, take = 201 } });
        Assert.Equal("sync.takeInvalid", tooMany.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    [Fact]
    public async Task WhoMayOnlyViewTheLogs_GetsTheTrashAsRemoved_NeverItsValues()
    {
        var people = await _app.Users();
        var golf = await Car(people.Alice);
        var kept = await Refuel(people.Alice, golf, 20, 1000);
        var trashedLog = await Refuel(people.Alice, golf, 10, 1500);
        var trashedExpense = await Expense(people.Alice, golf, 5);
        await people.Alice.Gql($"mutation {{ deleteRefueling(id: \"{trashedLog}\") {{ id }} }}");
        await people.Alice.Gql($"mutation {{ deleteExpense(id: \"{trashedExpense}\") {{ id }} }}");
        await people.Admin.Gql("mutation { setDefaultAccess(level: VIEW) { defaultLevelForOthers } }"); // Bob may only view the logs

        var page = await Page(people.Bob, new { vehicleId = golf, take = 50 });

        Assert.Equal([kept], Ids(page, "refuelings"));
        Assert.Empty(Ids(page, "expenses"));
        var removed = page.GetProperty("removed").EnumerateArray().Select(r => (r.GetProperty("type").GetString(), r.GetProperty("id").GetString())).ToList();
        Assert.Contains(("REFUELING", trashedLog), removed);
        Assert.Contains(("EXPENSE", trashedExpense), removed);
        Assert.Equal(2, Ids(await Page(people.Alice, new { vehicleId = golf, take = 50 }), "refuelings").Count()); // the owner keeps the trash
    }

    /// <summary>HotChocolate weighs a query by its shape, not by the rows it returns: an empty vehicle shows whether 200 full rows may be asked.</summary>
    [Fact]
    public async Task AskingForAFullPageOfTwoHundredRows_IsWithinTheCostLimit()
    {
        var people = await _app.Users();
        var golf = await Car(people.Alice);

        var page = await people.Alice.Gql(Feed, new { i = new { vehicleId = golf, take = 200 } });

        Assert.False(page.TryGetProperty("errors", out var errors), errors.ToString());
    }

    [Fact]
    public async Task TheOfflineWindows_FollowTheUser_ReplaceAsAWhole_AndOnlyNameVisibleVehicles()
    {
        var people = await _app.Users();
        var golf = await Car(people.Alice);
        var bobs = await Car(people.Bob, "Bobs");
        const string Get = "{ offlineSettings { defaultWindow vehicles { vehicleId window } } }";
        const string Set = "mutation($i: UpdateOfflineSettingsInput!) { updateOfflineSettings(input: $i) { defaultWindow vehicles { vehicleId window } } }";

        Assert.Equal("span:P2M", (await people.Alice.Gql(Get)).Data().GetProperty("offlineSettings").GetProperty("defaultWindow").GetString());

        await people.Alice.Gql(Set, new { i = new { defaultWindow = "thisAndLastYear", vehicles = new[] { new { vehicleId = golf, window = "span:P2Y6M4DT2H48M12S" } } } });
        var otherDevice = _app.NewClient();
        await otherDevice.LoginAs("alice@example.com", "alice-password-1");
        var settings = (await otherDevice.Gql(Get)).Data().GetProperty("offlineSettings");
        Assert.Equal("thisAndLastYear", settings.GetProperty("defaultWindow").GetString());
        Assert.Equal("span:P2Y6M4DT2H48M12S", settings.GetProperty("vehicles")[0].GetProperty("window").GetString());

        var invalid = await people.Alice.Gql(Set, new { i = new { defaultWindow = "span:P0D", vehicles = Array.Empty<object>() } });
        Assert.Equal("settings.offlineWindowInvalid", invalid.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        var notHers = await people.Alice.Gql(Set, new { i = new { defaultWindow = "all", vehicles = new[] { new { vehicleId = bobs, window = "all" } } } });
        Assert.Equal("NOT_FOUND", notHers.ErrorCode());
        Assert.Equal("thisAndLastYear", (await people.Alice.Gql(Get)).Data().GetProperty("offlineSettings").GetProperty("defaultWindow").GetString());
        Assert.Equal("span:P2M", (await people.Bob.Gql(Get)).Data().GetProperty("offlineSettings").GetProperty("defaultWindow").GetString());
    }

    [Fact]
    public async Task TheEstimates_CountTheLogsADownloadWouldBring()
    {
        var people = await _app.Users();
        var golf = await Car(people.Alice);
        await Refuel(people.Alice, golf, 90, 1000);
        await Refuel(people.Alice, golf, 10, 2000);
        await Expense(people.Alice, golf, 5);

        var counts = (await people.Alice.Gql($$"""{ vehicle(id: "{{golf}}") { all: logCountSince { refuelings expenses } recent: logCountSince(from: "{{Day(30)}}") { refuelings expenses } } }"""))
            .Data().GetProperty("vehicle");

        Assert.Equal(2, counts.GetProperty("all").GetProperty("refuelings").GetInt32());
        Assert.Equal(1, counts.GetProperty("recent").GetProperty("refuelings").GetInt32());
        Assert.Equal(1, counts.GetProperty("recent").GetProperty("expenses").GetInt32());
    }
}
