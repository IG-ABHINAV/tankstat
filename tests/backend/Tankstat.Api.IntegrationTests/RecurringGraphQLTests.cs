using System.Net.Http.Json;
using System.Text.Json;

namespace Tankstat.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class RecurringGraphQLTests(ApiFixture api)
{
    private async Task<JsonElement> Send(string query, object? variables = null)
    {
        var response = await api.Factory.CreateClient().PostAsJsonAsync("/graphql", new { query, variables });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> AddVehicle()
    {
        var added = await Send("mutation($i: AddVehicleInput!) { addVehicle(input: $i) { id } }", new { i = new { name = "Recurring " + Guid.NewGuid().ToString("N")[..6], fuelType = "PETROL" } });
        return added.GetProperty("data").GetProperty("addVehicle").GetProperty("id").GetString()!;
    }

    private const string Fields = "id title createdAt kind intervalMonths intervalDistance lastDoneDate lastDoneOdometer warnDays warnDistance status { state limit dueDate dueOdometer daysLeft distanceLeft }";

    private async Task<JsonElement> AddItem(string vehicleId, object? over = null)
    {
        var input = new Dictionary<string, object?>
        {
            ["vehicleId"] = vehicleId, ["title"] = "Oil change", ["category"] = "Service", ["kind"] = "COMBINED", ["intervalMonths"] = 12,
            ["intervalDistance"] = 15000, ["lastDoneDate"] = "2026-01-15", ["lastDoneOdometer"] = 50000,
        };
        if (over is not null) foreach (var p in over.GetType().GetProperties()) input[p.Name] = p.GetValue(over);
        var body = await Send($"mutation($i: AddRecurringExpenseInput!) {{ addRecurringExpense(input: $i) {{ {Fields} }} }}", new { i = input });
        return body;
    }

    [Fact]
    public async Task ASchedule_IsAdded_ListedOnTheVehicle_WithItsStatus()
    {
        var vehicle = await AddVehicle();

        var added = (await AddItem(vehicle)).GetProperty("data").GetProperty("addRecurringExpense");

        Assert.Equal(("COMBINED", 30, 500), (added.GetProperty("kind").GetString(), added.GetProperty("warnDays").GetInt32(), (int)added.GetProperty("warnDistance").GetInt64()));
        Assert.Equal("2027-01-15", added.GetProperty("status").GetProperty("dueDate").GetString());
        Assert.True(DateTimeOffset.UtcNow - added.GetProperty("createdAt").GetDateTimeOffset() < TimeSpan.FromMinutes(5)); // recorded when it was added
        Assert.Equal(65000, added.GetProperty("status").GetProperty("dueOdometer").GetInt64());

        var listed = (await Send($"query($id: UUID!) {{ vehicle(id: $id) {{ recurring {{ {Fields} }} }} }}", new { id = vehicle })).GetProperty("data").GetProperty("vehicle").GetProperty("recurring");
        Assert.Equal(["Oil change"], listed.EnumerateArray().Select(i => i.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task TheDefaultWarnings_ComeFromTheConfiguration_AndAreExposed()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None", ["Defaults:RecurringWarnDays"] = "14", ["Defaults:RecurringWarnDistance"] = "1000" });
        var client = app.NewClient();
        var vehicle = (await client.Gql("mutation { addVehicle(input: { name: \"Car\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString();

        var defaults = (await client.Gql("{ vehicleDefaults { recurringWarnDays recurringWarnDistance } }")).Data().GetProperty("vehicleDefaults");
        var added = (await client.Gql("mutation($i: AddRecurringExpenseInput!) { addRecurringExpense(input: $i) { warnDays warnDistance } }",
            new { i = new { vehicleId = vehicle, title = "Oil change", kind = "ODOMETER", intervalDistance = 15000, lastDoneOdometer = 1000 } })).Data().GetProperty("addRecurringExpense");

        Assert.Equal((14, 1000L), (defaults.GetProperty("recurringWarnDays").GetInt32(), defaults.GetProperty("recurringWarnDistance").GetInt64()));
        Assert.Equal((14, 1000L), (added.GetProperty("warnDays").GetInt32(), added.GetProperty("warnDistance").GetInt64()));
    }

    [Fact]
    public async Task AddingWithoutAStart_CountsFromTodayAtTheCurrentOdometer()
    {
        var vehicle = await AddVehicle();
        await Send("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId = vehicle, date = "2026-10-01", volume = 40, totalCost = 60, odometer = 71500, isFullTank = true } });
        var input = new { vehicleId = vehicle, title = "Oil change", kind = "ODOMETER", intervalDistance = 15000 };

        var added = (await Send($"mutation($i: AddRecurringExpenseInput!) {{ addRecurringExpense(input: $i) {{ {Fields} }} }}", new { i = input })).GetProperty("data").GetProperty("addRecurringExpense");

        Assert.Equal(71500, added.GetProperty("lastDoneOdometer").GetInt64());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), added.GetProperty("lastDoneDate").GetString());
        Assert.Equal(86500, added.GetProperty("status").GetProperty("dueOdometer").GetInt64());

        var bare = await AddVehicle(); // no reading at all: the odometer has to be given
        var refused = await Send($"mutation($i: AddRecurringExpenseInput!) {{ addRecurringExpense(input: $i) {{ {Fields} }} }}", new { i = input with { vehicleId = bare } });
        Assert.Equal("recurring.odometerRequired", refused.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    private const string MarkDone = $"mutation($i: MarkRecurringExpensesDoneInput!) {{ markRecurringExpensesDone(input: $i) {{ schedules {{ {Fields} }} expense {{ id title category amount photos {{ id }} }} }} }}";

    private static string Id(JsonElement added) => added.GetProperty("data").GetProperty("addRecurringExpense").GetProperty("id").GetString()!;

    [Fact]
    public async Task ADiscount_GoesWithTheExpenseLoggedForTheVisit_AndAnExpensesOwnIsClearedByAnUpdateWithoutOne()
    {
        var vehicle = await AddVehicle();
        var id = Id(await AddItem(vehicle));

        var done = await Send("mutation($i: MarkRecurringExpensesDoneInput!) { markRecurringExpensesDone(input: $i) { expense { id amount discount } } }",
            new { i = new { ids = new[] { id }, date = "2026-09-20", odometer = 62000, amount = 33000, discount = 2000 } });
        var expense = done.GetProperty("data").GetProperty("markRecurringExpensesDone").GetProperty("expense");
        Assert.Equal((33000m, 2000m), (expense.GetProperty("amount").GetDecimal(), expense.GetProperty("discount").GetDecimal()));

        var updated = await Send("mutation($i: UpdateExpenseInput!) { updateExpense(input: $i) { amount discount } }",
            new { i = new { id = expense.GetProperty("id").GetString(), date = "2026-09-20", title = "Oil change", amount = 33000, odometer = 62000 } });
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("data").GetProperty("updateExpense").GetProperty("discount").ValueKind);

        var refused = await Send("mutation($i: AddExpenseInput!) { addExpense(input: $i) { id } }",
            new { i = new { vehicleId = vehicle, date = "2026-09-21", title = "Wash", discount = 300 } });
        Assert.Equal("cost.discountWithoutAmount", refused.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    [Fact]
    public async Task MarkingItDone_LogsTheExpenseAndStartsTheNextInterval()
    {
        var vehicle = await AddVehicle();
        var id = Id(await AddItem(vehicle));

        var done = await Send(MarkDone, new { i = new { ids = new[] { id }, date = "2026-09-20", odometer = 62000, amount = 35000 } });

        var payload = done.GetProperty("data").GetProperty("markRecurringExpensesDone");
        var item = Assert.Single(payload.GetProperty("schedules").EnumerateArray());
        Assert.Equal(("2026-09-20", 62000L), (item.GetProperty("lastDoneDate").GetString(), item.GetProperty("lastDoneOdometer").GetInt64()));
        Assert.Equal("2027-09-20", item.GetProperty("status").GetProperty("dueDate").GetString());
        Assert.Equal(("Oil change", "Service", 35000m), (payload.GetProperty("expense").GetProperty("title").GetString(), payload.GetProperty("expense").GetProperty("category").GetString(), payload.GetProperty("expense").GetProperty("amount").GetDecimal()));
        var expenses = (await Send("query($v: UUID!) { expenses(vehicleId: $v) { title category amount currency odometer schedules { id title } } }", new { v = vehicle })).GetProperty("data").GetProperty("expenses");
        var expense = Assert.Single(expenses.EnumerateArray());
        Assert.Equal(("Oil change", 62000L), (expense.GetProperty("title").GetString(), expense.GetProperty("odometer").GetInt64()));
        Assert.False(string.IsNullOrEmpty(expense.GetProperty("currency").GetString())); // the instance default
        Assert.Equal([id], expense.GetProperty("schedules").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task OneVisit_MarksSeveralDone_WithOneExpenseThatNamesThemAll()
    {
        var vehicle = await AddVehicle();
        var oil = Id(await AddItem(vehicle));
        var filter = Id(await AddItem(vehicle, new { title = "Oil filter" }));
        var fuel = Id(await AddItem(vehicle, new { title = "Fuel filter" }));

        var done = await Send(MarkDone, new { i = new { ids = new[] { oil, filter }, date = "2026-09-20", odometer = 62000, amount = 48000 } });

        Assert.False(done.TryGetProperty("errors", out var errors), errors.ToString());
        var payload = done.GetProperty("data").GetProperty("markRecurringExpensesDone");
        Assert.Equal([oil, filter], payload.GetProperty("schedules").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal(("Oil change, Oil filter", 48000m), (payload.GetProperty("expense").GetProperty("title").GetString(), payload.GetProperty("expense").GetProperty("amount").GetDecimal()));
        var expense = Assert.Single((await Send("query($v: UUID!) { expenses(vehicleId: $v) { schedules { title } } }", new { v = vehicle })).GetProperty("data").GetProperty("expenses").EnumerateArray());
        Assert.Equal(["Oil change", "Oil filter"], expense.GetProperty("schedules").EnumerateArray().Select(s => s.GetProperty("title").GetString()));
        var list = (await Send($"query($v: UUID!) {{ vehicle(id: $v) {{ recurring {{ id lastDoneDate }} }} }}", new { v = vehicle })).GetProperty("data").GetProperty("vehicle").GetProperty("recurring");
        Assert.Equal("2026-01-15", list.EnumerateArray().Single(r => r.GetProperty("id").GetString() == fuel).GetProperty("lastDoneDate").GetString()); // not at this visit
    }

    [Fact]
    public async Task WithoutAnAmount_OnlyTheSchedulesMoveOn()
    {
        var vehicle = await AddVehicle();
        var id = Id(await AddItem(vehicle));

        var done = await Send(MarkDone, new { i = new { ids = new[] { id }, date = "2026-09-20", odometer = 62000 } });

        Assert.Equal(JsonValueKind.Null, done.GetProperty("data").GetProperty("markRecurringExpensesDone").GetProperty("expense").ValueKind);
        Assert.Empty((await Send("query($v: UUID!) { expenses(vehicleId: $v) { id } }", new { v = vehicle })).GetProperty("data").GetProperty("expenses").EnumerateArray());
    }

    [Fact]
    public async Task OneScheduleThatRefuses_LeavesEverythingAsItWas_AndTheErrorNamesIt()
    {
        var vehicle = await AddVehicle();
        var oil = Id(await AddItem(vehicle));
        var tyres = Id(await AddItem(vehicle, new { title = "Tyres", kind = "ODOMETER", intervalMonths = (int?)null, intervalDistance = 40000, lastDoneOdometer = 63000 }));

        var refused = await Send(MarkDone, new { i = new { ids = new[] { oil, tyres }, date = "2026-09-20", odometer = 62000, amount = 48000 } });

        var error = refused.GetProperty("errors")[0].GetProperty("extensions");
        Assert.Equal(("VALIDATION_FAILED", "recurring.odometerBelowLast", "Tyres"), (error.GetProperty("code").GetString(), error.GetProperty("key").GetString(), error.GetProperty("args").GetProperty("title").GetString()));
        Assert.Empty((await Send("query($v: UUID!) { expenses(vehicleId: $v) { id } }", new { v = vehicle })).GetProperty("data").GetProperty("expenses").EnumerateArray());
        var list = (await Send("query($v: UUID!) { vehicle(id: $v) { recurring { lastDoneDate } } }", new { v = vehicle })).GetProperty("data").GetProperty("vehicle").GetProperty("recurring");
        Assert.All(list.EnumerateArray(), r => Assert.Equal("2026-01-15", r.GetProperty("lastDoneDate").GetString()));
    }

    [Fact]
    public async Task MarkingDone_WithPhotos_AttachesThemToTheLoggedExpense()
    {
        var vehicle = await AddVehicle();
        var id = Id(await AddItem(vehicle));
        var content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7]);
        var upload = await api.Factory.CreateClient().PutAsync($"/media/vehicles/{vehicle}/photo-drafts?form=expense&locale=en", content);
        var draft = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var done = await Send(MarkDone, new { i = new { ids = new[] { id }, date = "2026-09-20", odometer = 62000, amount = 35000, photoIds = new[] { draft } } });

        Assert.False(done.TryGetProperty("errors", out var errors), errors.ToString());
        var photos = done.GetProperty("data").GetProperty("markRecurringExpensesDone").GetProperty("expense").GetProperty("photos");
        Assert.Equal([draft], photos.EnumerateArray().Select(p => p.GetProperty("id").GetString())); // the dialog counts them
    }

    [Fact]
    public async Task Updating_AndDeleting_AndErrorsCarryTranslationKeys()
    {
        var vehicle = await AddVehicle();
        var id = (await AddItem(vehicle)).GetProperty("data").GetProperty("addRecurringExpense").GetProperty("id").GetString();

        var updated = await Send($"mutation($i: UpdateRecurringExpenseInput!) {{ updateRecurringExpense(input: $i) {{ {Fields} }} }}",
            new { i = new { id, title = "Insurance", kind = "TIME", intervalMonths = 12, lastDoneDate = "2026-03-01", warnDays = 14 } });
        var item = updated.GetProperty("data").GetProperty("updateRecurringExpense");
        Assert.Equal(("Insurance", "TIME", 14), (item.GetProperty("title").GetString(), item.GetProperty("kind").GetString(), item.GetProperty("warnDays").GetInt32()));

        var invalid = await AddItem(vehicle, new { kind = "ODOMETER", intervalDistance = (long?)null });
        var error = invalid.GetProperty("errors")[0].GetProperty("extensions");
        Assert.Equal(("VALIDATION_FAILED", "recurring.distanceInvalid"), (error.GetProperty("code").GetString(), error.GetProperty("key").GetString()));

        var gone = await Send("mutation($id: UUID!) { deleteRecurringExpense(id: $id) }", new { id });
        Assert.True(gone.GetProperty("data").GetProperty("deleteRecurringExpense").GetBoolean());
        var missing = await Send("mutation($id: UUID!) { deleteRecurringExpense(id: $id) }", new { id });
        Assert.Equal("NOT_FOUND", missing.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
    }
}
