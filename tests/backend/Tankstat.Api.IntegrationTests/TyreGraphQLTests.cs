using System.Net.Http.Json;
using System.Text.Json;

namespace Tankstat.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class TyreGraphQLTests(ApiFixture api)
{
    private async Task<JsonElement> Send(string query, object? variables = null)
    {
        var response = await api.Factory.CreateClient().PostAsJsonAsync("/graphql", new { query, variables });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> AddVehicle()
    {
        var added = await Send("mutation($i: AddVehicleInput!) { addVehicle(input: $i) { id } }", new { i = new { name = "Tyres " + Guid.NewGuid().ToString("N")[..6], fuelType = "PETROL" } });
        return added.GetProperty("data").GetProperty("addVehicle").GetProperty("id").GetString()!;
    }

    private const string Tyres = """
        vehicleId hasReadings
        sets { onTheCar set { id name season roughDistance roughUntil version } figures { distance approximate pairOneFront pairOneRear pairsUnknown suggestion { distance until } }
               installations { installation { id from until pairOneFront version } distance { distance approximate measure } } }
        entries { id kind setId installationId day odometer note version }
        """;

    private async Task<JsonElement> AddSet(string vehicle, string name, bool fitted, string day = "2026-03-28", long rough = 0, Guid? id = null)
    {
        var body = await Send($"mutation($i: AddTyreSetInput!) {{ addTyreSet(input: $i) {{ {Tyres} }} }}", new
        {
            i = new { vehicleId = vehicle, name, season = "SUMMER", since = "2023-03-25", fitted, day, roughDistance = rough, pairOneFront = fitted ? true : (bool?)null, id = id ?? Guid.NewGuid() },
        });
        return body.GetProperty("data").GetProperty("addTyreSet");
    }

    private static JsonElement SetNamed(JsonElement tyres, string name) =>
        tyres.GetProperty("sets").EnumerateArray().Single(s => s.GetProperty("set").GetProperty("name").GetString() == name);

    [Fact]
    public async Task Sets_AreAdded_Swapped_WithTheDaysReading_AndListedWithTheirDistances()
    {
        var vehicle = await AddVehicle();
        await AddSet(vehicle, "Michelin", fitted: true, rough: 32_900);
        var both = await AddSet(vehicle, "Continental", fitted: false);
        var winter = SetNamed(both, "Continental").GetProperty("set").GetProperty("id").GetString();

        var swapped = (await Send($"mutation($i: SwapTyresInput!) {{ swapTyres(input: $i) {{ {Tyres} }} }}", new
        {
            i = new { vehicleId = vehicle, setId = winter, day = "2026-09-20", pairOneFront = true, odometer = 148_200, note = "At the garage", installationId = Guid.NewGuid() },
        })).GetProperty("data").GetProperty("swapTyres");

        Assert.True(SetNamed(swapped, "Continental").GetProperty("onTheCar").GetBoolean());
        var summer = SetNamed(swapped, "Michelin");
        Assert.Equal("2026-09-20", summer.GetProperty("installations")[0].GetProperty("installation").GetProperty("until").GetString());
        Assert.Equal(32_900, summer.GetProperty("figures").GetProperty("distance").GetInt64()); // no reading before 20 Sep: nothing measured yet
        Assert.Equal("NOT_MEASURED", summer.GetProperty("installations")[0].GetProperty("distance").GetProperty("measure").GetString());
        var line = swapped.GetProperty("entries")[0]; // newest first: the swap's line, before the fitted set's
        Assert.Equal(("SWAP", 148_200L, "At the garage"), (line.GetProperty("kind").GetString(), line.GetProperty("odometer").GetInt64(), line.GetProperty("note").GetString()));
        Assert.Equal(2, swapped.GetProperty("entries").GetArrayLength());
        Assert.True(swapped.GetProperty("hasReadings").GetBoolean());

        var listed = (await Send($"query($id: UUID!) {{ vehicleTyres(vehicleId: $id) {{ {Tyres} }} }}", new { id = vehicle })).GetProperty("data").GetProperty("vehicleTyres");
        Assert.Equal(2, listed.GetProperty("sets").GetArrayLength());
    }

    [Fact]
    public async Task ASecondSwapOnADay_IsRefusedWithItsKey_AndTheTrashListsWhatReplacingMoved()
    {
        var vehicle = await AddVehicle();
        await AddSet(vehicle, "Michelin", fitted: true, day: "2026-09-20");
        var both = await AddSet(vehicle, "Continental", fitted: false);
        var winter = SetNamed(both, "Continental").GetProperty("set").GetProperty("id").GetString();
        const string swap = "mutation($i: SwapTyresInput!) { swapTyres(input: $i) { vehicleId } }";

        var refused = await Send(swap, new { i = new { vehicleId = vehicle, setId = winter, day = "2026-09-20" } });
        var error = refused.GetProperty("errors")[0].GetProperty("extensions");
        Assert.Equal(("VALIDATION_FAILED", "tyres.swapExists", "Michelin"),
            (error.GetProperty("code").GetString(), error.GetProperty("key").GetString(), error.GetProperty("args").GetProperty("set").GetString()));

        Assert.False((await Send(swap, new { i = new { vehicleId = vehicle, setId = winter, day = "2026-09-20", replace = true } })).TryGetProperty("errors", out _));
        var trash = (await Send("{ tyreTrash { kind entryKind setName day until canDelete vehicle { id } } tyreTrashCount }")).GetProperty("data");
        var row = trash.GetProperty("tyreTrash").EnumerateArray().Single(r => r.GetProperty("vehicle").GetProperty("id").GetString() == vehicle);
        Assert.Equal(("ENTRY", "SWAP", "Michelin", "2026-09-20"),
            (row.GetProperty("kind").GetString(), row.GetProperty("entryKind").GetString(), row.GetProperty("setName").GetString(), row.GetProperty("day").GetString()));
        Assert.True(row.GetProperty("canDelete").GetBoolean());
    }

    [Fact]
    public async Task PastSwaps_AndAInstallation_GoIntoTheHistory_AndASetIsRetired()
    {
        var vehicle = await AddVehicle();
        var first = await AddSet(vehicle, "Michelin", fitted: true, day: "2026-03-28", rough: 32_900);
        var summer = SetNamed(first, "Michelin").GetProperty("set").GetProperty("id").GetString();
        var both = await AddSet(vehicle, "Continental", fitted: false);
        var winter = SetNamed(both, "Continental").GetProperty("set").GetProperty("id").GetString();

        var past = (await Send($"mutation($i: AddPastTyreSwapsInput!) {{ addPastTyreSwaps(input: $i) {{ {Tyres} }} }}", new
        {
            i = new { vehicleId = vehicle, swaps = new object[] { new { day = "2025-11-15", setId = winter }, new { day = "2025-03-29", setId = summer, odometer = 128_150 } } },
        })).GetProperty("data").GetProperty("addPastTyreSwaps");
        Assert.Equal(["2025-03-29", "2026-03-28"], SetNamed(past, "Michelin").GetProperty("installations").EnumerateArray()
            .Select(s => s.GetProperty("installation").GetProperty("from").GetString()).Order());

        var retired = await Send($"mutation($id: UUID!) {{ retireTyreSet(id: $id, day: \"2026-10-01\") {{ {Tyres} }} }}", new { id = winter });
        Assert.False(retired.TryGetProperty("errors", out _));
        var onCar = await Send("mutation($id: UUID!) { retireTyreSet(id: $id, day: \"2026-10-01\") { vehicleId } }", new { id = summer });
        Assert.Equal("tyres.retireOnCar", onCar.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    [Fact]
    public async Task EveryOtherMutation_ChangesWhatItSays()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true, day: "2026-03-28", rough: 30_000), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        async Task<JsonElement> Ok(string mutation, object variables)
        {
            var body = await Send(mutation, variables);
            Assert.False(body.TryGetProperty("errors", out var errors), errors.ToString());
            return body.GetProperty("data");
        }

        await Ok("mutation($i: UpdateTyreSetInput!) { updateTyreSet(input: $i) { vehicleId } }", new { i = new { id = summer, name = "Michelin Primacy 4", season = "SUMMER", since = "2023-03-25" } });
        await Ok("mutation($i: SetTyreRoughDistanceInput!) { setTyreRoughDistance(input: $i) { vehicleId } }", new { i = new { id = summer, distance = 31_000 } });
        await Ok("mutation($i: SwapTyresInput!) { changeTyrePositions(input: $i) { vehicleId } }", new { i = new { vehicleId = vehicle, setId = summer, day = "2026-06-01", pairOneFront = false } });
        var added = await Ok($"mutation($i: AddTyreInstallationInput!) {{ addTyreInstallation(input: $i) {{ {Tyres} }} }}", new { i = new { setId = summer, from = "2025-03-29", until = "2025-11-15", odometerFrom = 128_150 } });
        var installation = SetNamed(added.GetProperty("addTyreInstallation"), "Michelin Primacy 4").GetProperty("installations").EnumerateArray()
            .Single(x => x.GetProperty("installation").GetProperty("from").GetString() == "2025-03-29").GetProperty("installation").GetProperty("id").GetString();
        await Ok("mutation($i: UpdateTyreInstallationInput!) { updateTyreInstallation(input: $i) { vehicleId } }", new { i = new { id = installation, from = "2025-04-01", until = "2025-11-15" } });
        await Ok("mutation($id: UUID!) { deleteTyreInstallation(id: $id) { vehicleId } }", new { id = installation });
        await Ok("mutation($id: UUID!) { restoreTyreInstallation(id: $id) { vehicleId } }", new { id = installation });
        var moved = await Ok($"query($id: UUID!) {{ vehicleTyres(vehicleId: $id) {{ {Tyres} }} }}", new { id = vehicle });
        var swapLine = moved.GetProperty("vehicleTyres").GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == installation);
        Assert.Equal(("2025-04-01", 128_150L), (swapLine.GetProperty("day").GetString(), swapLine.GetProperty("odometer").GetInt64())); // the line and its reading moved with it
        var entry = Guid.NewGuid().ToString();
        var added2 = await Ok($"mutation($i: AddTyreEntryInput!) {{ addTyreEntry(input: $i) {{ {Tyres} }} }}",
            new { i = new { vehicleId = vehicle, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2025-06-01", odometer = 131_000, note = "From the receipt", id = entry } });
        Assert.Contains(added2.GetProperty("addTyreEntry").GetProperty("entries").EnumerateArray(), e => e.GetProperty("id").GetString() == entry && e.GetProperty("kind").GetString() == "PUNCTURE_REPAIRED");
        var updated = await Ok($"mutation($i: UpdateTyreEntryInput!) {{ updateTyreEntry(input: $i) {{ {Tyres} }} }}",
            new { i = new { id = entry, kind = "WHEEL_BALANCING", day = "2025-06-02", note = "From the receipt" } });
        var changed = updated.GetProperty("updateTyreEntry").GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == entry);
        Assert.Equal(("WHEEL_BALANCING", JsonValueKind.Null, JsonValueKind.Null), (changed.GetProperty("kind").GetString(), changed.GetProperty("setId").ValueKind, changed.GetProperty("odometer").ValueKind)); // left out clears them
        Assert.Equal("tyres.entryKindNotAllowed", (await Send("mutation($i: UpdateTyreEntryInput!) { updateTyreEntry(input: $i) { vehicleId } }",
            new { i = new { id = entry, kind = "SWAP", day = "2025-06-02" } })).GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        await Ok("mutation($id: UUID!) { deleteTyreEntry(input: { id: $id }) { vehicleId } }", new { id = entry });
        await Ok("mutation($id: UUID!) { restoreTyreEntry(id: $id) { vehicleId } }", new { id = entry });

        await Ok("mutation($i: SwapTyresInput!) { swapTyres(input: $i) { vehicleId } }",
            new { i = new { vehicleId = vehicle, setId = SetNamed(await AddSet(vehicle, "Continental", fitted: false), "Continental").GetProperty("set").GetProperty("id").GetString(), day = "2026-09-20" } });
        await Ok("mutation($id: UUID!) { retireTyreSet(id: $id, day: \"2026-10-01\") { vehicleId } }", new { id = summer });
        await Ok("mutation($id: UUID!) { unretireTyreSet(id: $id) { vehicleId } }", new { id = summer });
        await Ok("mutation($id: UUID!) { deleteTyreSet(id: $id) { vehicleId } }", new { id = summer });
        var restored = await Ok($"mutation($id: UUID!) {{ restoreTyreSet(id: $id) {{ {Tyres} }} }}", new { id = summer });
        Assert.Equal(31_000, SetNamed(restored.GetProperty("restoreTyreSet"), "Michelin Primacy 4").GetProperty("set").GetProperty("roughDistance").GetInt64());

        await Ok("mutation($id: UUID!) { deleteTyreSet(id: $id) { vehicleId } }", new { id = summer });
        Assert.True((await Ok("mutation { emptyTyreTrash }", new { })).GetProperty("emptyTyreTrash").GetInt32() >= 1);
    }

    [Fact]
    public async Task ALinesAmount_IsAnExpenseThatNamesItsLine_AndTheLineCanGoToTheTrashWithoutIt()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true, day: "2026-03-28"), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        var (line, expense) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        const string fields = "entries { id kind expenseId amount currency category expenseTrashed } expenseCategory";

        var added = (await Send($"mutation($i: AddTyreEntryInput!) {{ addTyreEntry(input: $i) {{ {fields} }} }}", new
        {
            i = new { vehicleId = vehicle, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", odometer = 148_200, id = line,
                fee = new { amount = 25.5m, currency = "EUR", category = "Tyres", title = "Puncture repaired: Michelin", expenseId = expense } },
        })).GetProperty("data").GetProperty("addTyreEntry");
        var shown = added.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == line);
        Assert.Equal((expense, 25.5m, "EUR", "Tyres", false),
            (shown.GetProperty("expenseId").GetString(), shown.GetProperty("amount").GetDecimal(), shown.GetProperty("currency").GetString(), shown.GetProperty("category").GetString(), shown.GetProperty("expenseTrashed").GetBoolean()));
        Assert.Equal("Tyres", added.GetProperty("expenseCategory").GetString());
        var costs = (await Send("query($id: UUID!) { vehicleTyres(vehicleId: $id) { spentAll { currency amount } spentNoSet { amount } sets { set { name } spent { currency amount } repairs } } }",
            new { id = vehicle })).GetProperty("data").GetProperty("vehicleTyres");
        var michelin = costs.GetProperty("sets").EnumerateArray().Single(x => x.GetProperty("set").GetProperty("name").GetString() == "Michelin");
        Assert.Equal(("EUR", 25.5m, 1), (michelin.GetProperty("spent")[0].GetProperty("currency").GetString(), michelin.GetProperty("spent")[0].GetProperty("amount").GetDecimal(),
            michelin.GetProperty("repairs").GetInt32()));
        Assert.Equal((25.5m, 0), (costs.GetProperty("spentAll")[0].GetProperty("amount").GetDecimal(), costs.GetProperty("spentNoSet").GetArrayLength()));

        var linked = (await Send("query($id: UUID!) { expense(id: $id) { title amount odometer date tyreEntry { id kind setName trashed } } }", new { id = expense }))
            .GetProperty("data").GetProperty("expense");
        Assert.Equal(("Puncture repaired: Michelin", 148_200L, "2026-09-20"), (linked.GetProperty("title").GetString(), linked.GetProperty("odometer").GetInt64(), linked.GetProperty("date").GetString()));
        var tyreEntry = linked.GetProperty("tyreEntry");
        Assert.Equal((line, "PUNCTURE_REPAIRED", "Michelin", false),
            (tyreEntry.GetProperty("id").GetString(), tyreEntry.GetProperty("kind").GetString(), tyreEntry.GetProperty("setName").GetString(), tyreEntry.GetProperty("trashed").GetBoolean()));

        // Unticked: the line goes, its expense stays, naming its line in the trash.
        Assert.False((await Send("mutation($id: UUID!) { deleteTyreEntry(input: { id: $id, withExpense: false }) { vehicleId } }", new { id = line })).TryGetProperty("errors", out _));
        var after = (await Send("query($id: UUID!) { expense(id: $id) { tyreEntry { trashed } } }", new { id = expense })).GetProperty("data").GetProperty("expense");
        Assert.True(after.GetProperty("tyreEntry").GetProperty("trashed").GetBoolean());
    }

    [Fact]
    public async Task TheLineAndItsExpense_OnARealDatabase_ShareOneDayAndReading_FromEitherSide_AndPartWhenTheExpenseWasEditedAlone()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true, day: "2026-03-28"), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        var (line, expense) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        async Task<JsonElement> Ok(string q, object v)
        {
            var body = await Send(q, v);
            Assert.False(body.TryGetProperty("errors", out var errors), errors.ToString());
            return body.GetProperty("data");
        }
        async Task<JsonElement> Line() => (await Ok("query($v: UUID!) { vehicleTyres(vehicleId: $v) { entries { id day odometer amount expenseId expenseTrashed } readings { date value } } }", new { v = vehicle }))
            .GetProperty("vehicleTyres");
        JsonElement Of(JsonElement tyres) => tyres.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == line);
        async Task<JsonElement> Expense() => (await Ok("query($id: UUID!) { expense(id: $id) { date odometer amount tyreEntry { id } } }", new { id = expense }))
            .GetProperty("expense");
        const string updateLine = "mutation($i: UpdateTyreEntryInput!) { updateTyreEntry(input: $i) { vehicleId } }";
        const string updateExpense = "mutation($i: UpdateExpenseInput!) { updateExpense(input: $i) { id } }";
        object Fee(decimal? amount) => new { amount, currency = "EUR", category = "Tyres", title = "Puncture", expenseId = expense };

        await Ok("mutation($i: AddTyreEntryInput!) { addTyreEntry(input: $i) { vehicleId } }",
            new { i = new { vehicleId = vehicle, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", odometer = 148_200, id = line, fee = Fee(25) } });

        // The odometer cleared on the Expenses tab clears the line's; typed on the line it is the expense's again; cleared there, gone from both.
        await Ok(updateExpense, new { i = new { id = expense, date = "2026-09-20", title = "Puncture", amount = 25m, currency = "EUR", odometer = (long?)null } });
        Assert.Equal(JsonValueKind.Null, Of(await Line()).GetProperty("odometer").ValueKind);
        await Ok(updateLine, new { i = new { id = line, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", odometer = 148_300, fee = Fee(25) } });
        Assert.Equal(148_300, (await Expense()).GetProperty("odometer").GetInt64());
        await Ok(updateLine, new { i = new { id = line, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", fee = Fee(25) } });
        Assert.Equal(JsonValueKind.Null, (await Expense()).GetProperty("odometer").ValueKind);
        Assert.Empty((await Line()).GetProperty("readings").EnumerateArray());

        // The amount emptied: the expense to the trash; typed again: back, with the new amount.
        await Ok(updateLine, new { i = new { id = line, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", odometer = 148_300, fee = Fee(null) } });
        Assert.True(Of(await Line()).GetProperty("expenseTrashed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await Expense()).ValueKind);
        await Ok(updateLine, new { i = new { id = line, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", odometer = 148_300, fee = Fee(40) } });
        Assert.Equal((40m, 148_300L), ((await Expense()).GetProperty("amount").GetDecimal(), (await Expense()).GetProperty("odometer").GetInt64()));
        Assert.Single((await Line()).GetProperty("readings").EnumerateArray()); // one reading row for the two

        // Edited on its own while its line is in the trash, the expense gets a day and a reading of its own; restoring the line parts the two.
        await Ok("mutation($i: DeleteTyreEntryInput!) { deleteTyreEntry(input: $i) { vehicleId } }", new { i = new { id = line, withExpense = false } });
        await Ok(updateExpense, new { i = new { id = expense, date = "2026-09-25", title = "Puncture", amount = 40m, currency = "EUR", odometer = 148_900 } });
        await Ok("mutation($id: UUID!) { restoreTyreEntry(id: $id) { vehicleId } }", new { id = line });
        var parted = await Line();
        Assert.Equal((JsonValueKind.Null, 148_300L), (Of(parted).GetProperty("expenseId").ValueKind, Of(parted).GetProperty("odometer").GetInt64()));
        Assert.Equal(JsonValueKind.Null, (await Expense()).GetProperty("tyreEntry").ValueKind);
        Assert.Equal([148_300L, 148_900L], parted.GetProperty("readings").EnumerateArray().Select(r => r.GetProperty("value").GetInt64()).Order());
    }

    [Fact]
    public async Task ASwapAJoinMovesOnARealDatabase_TakesItsFeeToTheNewDay_AndLeavesItsReadingOnItsOldDay()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true, day: "2026-03-28"), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        var winter = SetNamed(await AddSet(vehicle, "Continental", fitted: false), "Continental").GetProperty("set").GetProperty("id").GetString()!;
        const string swap = "mutation($i: SwapTyresInput!) { swapTyres(input: $i) { vehicleId } }";
        var (later, expense) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        Assert.False((await Send(swap, new { i = new { vehicleId = vehicle, setId = winter, day = "2026-06-01", pairOneFront = true, installationId = Guid.NewGuid() } })).TryGetProperty("errors", out _));
        Assert.False((await Send(swap, new { i = new { vehicleId = vehicle, setId = summer, day = "2026-08-01", pairOneFront = true, installationId = Guid.NewGuid() } })).TryGetProperty("errors", out _));
        Assert.False((await Send(swap, new { i = new { vehicleId = vehicle, setId = winter, day = "2026-09-10", pairOneFront = true, odometer = 147_500, installationId = later,
            fee = new { amount = 70m, currency = "EUR", title = "Tyre swap", expenseId = expense } } })).TryGetProperty("errors", out _));

        Assert.False((await Send(swap, new { i = new { vehicleId = vehicle, setId = winter, day = "2026-08-20", pairOneFront = true, installationId = Guid.NewGuid() } })).TryGetProperty("errors", out _));

        var moved = (await Send("query($id: UUID!) { expense(id: $id) { date odometer } }", new { id = expense })).GetProperty("data").GetProperty("expense");
        Assert.Equal(("2026-08-20", JsonValueKind.Null), (moved.GetProperty("date").GetString(), moved.GetProperty("odometer").ValueKind));
        var lines = (await Send($"query($id: UUID!) {{ vehicleTyres(vehicleId: $id) {{ {Tyres} }} }}", new { id = vehicle })).GetProperty("data").GetProperty("vehicleTyres").GetProperty("entries");
        Assert.Contains(lines.EnumerateArray(), e => e.GetProperty("kind").GetString() == "NOTE" && e.GetProperty("day").GetString() == "2026-09-10" && e.GetProperty("odometer").GetInt64() == 147_500);
    }

    private const string SingleTyres = """
        sets { onTheCar set { id name brokenUpOn version }
               tyres { onTheCar corner tyre { id label version retiredWhy } figures { distance front rear unknown } places { place { id corner from until version } distance { distance } } } }
        """;

    private async Task<JsonElement> Mutate(string field, string inputType, object input, string selection = SingleTyres)
    {
        var body = await Send($"mutation($i: {inputType}!) {{ {field}(input: $i) {{ {selection} }} }}", new { i = input });
        Assert.False(body.TryGetProperty("errors", out var errors), errors.ToString());
        return body.GetProperty("data").GetProperty(field);
    }

    private async Task<JsonElement> ById(string field, string id) =>
        (await Send($"mutation($id: UUID!) {{ {field}(id: $id) {{ {SingleTyres} }} }}", new { id })).GetProperty("data").GetProperty(field);

    private static JsonElement TyreLabelled(JsonElement tyres, string label) => tyres.GetProperty("sets").EnumerateArray()
        .SelectMany(s => s.GetProperty("tyres").EnumerateArray()).Single(t => t.GetProperty("tyre").GetProperty("label").GetString() == label);

    [Fact]
    public async Task ASetIsBrokenUp_ATyreRelabelled_ATimeOnTheCarAddedChangedTrashedAndRestored_AndASwapPlacesEachTyre()
    {
        var vehicle = await AddVehicle();
        var summerSet = SetNamed(await AddSet(vehicle, "Michelin", fitted: true, rough: 10_000), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        var winterSet = SetNamed(await AddSet(vehicle, "Continental", fitted: false), "Continental").GetProperty("set").GetProperty("id").GetString()!;
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        string[] corners = ["FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT"];

        var broken = await Mutate("breakUpTyreSet", "BreakUpTyreSetInput", new
        {
            setId = summerSet, day = "2026-09-01", tyres = ids.Select((id, n) => new { id, label = $"S{n + 1}", corner = corners[n] }),
        });
        var summer = SetNamed(broken, "Michelin");
        Assert.Equal("2026-09-01", summer.GetProperty("set").GetProperty("brokenUpOn").GetString());
        Assert.Equal(["S1", "S2", "S3", "S4"], summer.GetProperty("tyres").EnumerateArray().Select(t => t.GetProperty("tyre").GetProperty("label").GetString()));
        Assert.Equal((10_000L, "REAR_LEFT", true), (TyreLabelled(broken, "S3").GetProperty("figures").GetProperty("distance").GetInt64(),
            TyreLabelled(broken, "S3").GetProperty("corner").GetString(), TyreLabelled(broken, "S3").GetProperty("onTheCar").GetBoolean()));

        Assert.Equal("S1x", TyreLabelled(await Mutate("updateTyre", "UpdateTyreInput", new { id = ids[0], label = "S1x" }), "S1x").GetProperty("tyre").GetProperty("label").GetString());
        var refused = await Send("mutation($i: UpdateTyreInput!) { updateTyre(input: $i) { vehicleId } }", new { i = new { id = ids[1], label = "s3" } });
        Assert.Equal("tyres.labelTaken", refused.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());

        // S4's own time on the car: it came off in June and went back on in July.
        var s4Place = TyreLabelled(broken, "S4").GetProperty("places")[0].GetProperty("place").GetProperty("id").GetString()!;
        await Mutate("updateTyrePlace", "UpdateTyrePlaceInput", new { id = s4Place, from = "2026-03-28", until = "2026-06-01", corner = "REAR_RIGHT" });
        var placeId = Guid.NewGuid();
        var added = await Mutate("addTyrePlace", "AddTyrePlaceInput", new { tyreId = ids[3], from = "2026-07-01", corner = "REAR_RIGHT", id = placeId });
        Assert.Equal(2, TyreLabelled(added, "S4").GetProperty("places").GetArrayLength());
        Assert.Equal(1, TyreLabelled(await ById("deleteTyrePlace", placeId.ToString()), "S4").GetProperty("places").GetArrayLength());
        Assert.Equal(2, TyreLabelled(await ById("restoreTyrePlace", placeId.ToString()), "S4").GetProperty("places").GetArrayLength());

        // Off the car, and back on with S1 and S3 crossed.
        await Mutate("swapTyres", "SwapTyresInput", new { vehicleId = vehicle, setId = winterSet, day = "2026-09-20", installationId = Guid.NewGuid() });
        var back = await Mutate("swapTyres", "SwapTyresInput", new
        {
            vehicleId = vehicle, setId = summerSet, day = "2026-09-25", installationId = Guid.NewGuid(),
            corners = new object[] { new { tyreId = ids[0], corner = "REAR_LEFT" }, new { tyreId = ids[2], corner = "FRONT_LEFT" }, new { tyreId = ids[1], corner = "FRONT_RIGHT" } },
        });
        Assert.Equal(("REAR_LEFT", "FRONT_LEFT", (string?)null),
            (TyreLabelled(back, "S1x").GetProperty("corner").GetString(), TyreLabelled(back, "S3").GetProperty("corner").GetString(),
                TyreLabelled(back, "S4").GetProperty("corner").GetString()));

        // A tyre to the trash, and its row on the Trash page.
        var trashed = await ById("deleteTyre", ids[1].ToString());
        Assert.DoesNotContain(SetNamed(trashed, "Michelin").GetProperty("tyres").EnumerateArray(), t => t.GetProperty("tyre").GetProperty("label").GetString() == "S2");
        var trash = (await Send("query { tyreTrash(take: 100) { kind id label corner setName day until } }")).GetProperty("data").GetProperty("tyreTrash");
        Assert.Contains(trash.EnumerateArray(), r => r.GetProperty("kind").GetString() == "TYRE" && r.GetProperty("label").GetString() == "S2");
        Assert.Equal("S2", TyreLabelled(await ById("restoreTyre", ids[1].ToString()), "S2").GetProperty("tyre").GetProperty("label").GetString());
    }

    [Fact]
    public async Task AFlatOnTwoTyresOfAWholeSet_BreaksItUpOnSave_OneExpenseNamesBothLines_AndTheSetCountsItOnce()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        string[] corners = ["FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT"];
        var (s3, s4, expense) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var added = await Mutate("addTyreEntry", "AddTyreEntryInput", new
        {
            vehicleId = vehicle, kind = "PUNCTURE_REPAIRED", day = "2026-09-20", setId = summer, note = "Screws in the tread",
            fee = new { amount = 13_000, currency = "HUF", category = "Tyres", title = "Puncture repaired: Michelin", expenseId = expense },
            tyres = new object[] { new { tyreId = tyres[2], id = s3 }, new { tyreId = tyres[3], id = s4 } },
            breakUp = new { setId = summer, day = "2026-09-20", tyres = tyres.Select((id, n) => new { id, label = $"S{n + 1}", corner = corners[n] }) },
        }, "sets { set { name brokenUpOn } spent { amount } repairs tyres { tyre { label } spent { amount } repairs } } entries { id tyreId groupId amount }");

        var set = SetNamed(added, "Michelin");
        Assert.Equal(("2026-09-20", 13_000m, 2), (set.GetProperty("set").GetProperty("brokenUpOn").GetString(), set.GetProperty("spent")[0].GetProperty("amount").GetDecimal(),
            set.GetProperty("repairs").GetInt32()));
        Assert.Equal(6_500m, TyreLabelled(added, "S3").GetProperty("spent")[0].GetProperty("amount").GetDecimal());
        var lines = added.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("groupId").ValueKind != JsonValueKind.Null).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(s3.ToString(), l.GetProperty("groupId").GetString()));

        var names = (await Send("query($v: UUID!) { expenses(vehicleId: $v, take: 10) { id tyreEntry { id } tyreEntries { id tyreLabel groupId } } }", new { v = vehicle }))
            .GetProperty("data").GetProperty("expenses").EnumerateArray().Single(e => e.GetProperty("id").GetString() == expense.ToString());
        Assert.Equal(["S3", "S4"], names.GetProperty("tyreEntries").EnumerateArray().Select(e => e.GetProperty("tyreLabel").GetString()));
        Assert.Equal(s3.ToString(), names.GetProperty("tyreEntry").GetProperty("id").GetString()); // the first, for an older app

        // S3's day of its own parts it from the expense; S4 keeps it.
        var parted = await Mutate("updateTyreEntry", "UpdateTyreEntryInput", new { id = s3, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-21", note = "Screws in the tread" },
            "entries { id groupId amount }");
        var byId = parted.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, 13_000m), (byId[s3.ToString()].GetProperty("amount").ValueKind, byId[s3.ToString()].GetProperty("groupId").ValueKind,
            byId[s4.ToString()].GetProperty("amount").GetDecimal()));
    }

    [Fact]
    public async Task ALineThatPartsAndEmptiesItsOdometerInOneChange_WhileTheDialogStillSendsTheAmount_SavesWithoutASecondExpense()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        string[] corners = ["FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT"];
        var (s3, s4) = (Guid.NewGuid(), Guid.NewGuid());
        object Fee() => new { amount = 13_000, currency = "HUF", category = "Tyres", title = "Puncture repaired: Michelin", expenseId = Guid.NewGuid() };
        await Mutate("addTyreEntry", "AddTyreEntryInput", new
        {
            vehicleId = vehicle, kind = "PUNCTURE_REPAIRED", day = "2026-09-20", setId = summer, odometer = 148_200, fee = Fee(),
            tyres = new object[] { new { tyreId = tyres[2], id = s3 }, new { tyreId = tyres[3], id = s4 } },
            breakUp = new { setId = summer, day = "2026-09-20", tyres = tyres.Select((id, n) => new { id, label = $"S{n + 1}", corner = corners[n] }) },
        }, "vehicleId");

        var parted = await Mutate("updateTyreEntry", "UpdateTyreEntryInput", new { id = s3, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", fee = Fee() },
            "entries { id odometer amount } spentAll { amount }");

        var byId = parted.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (byId[s3.ToString()].GetProperty("odometer").ValueKind, byId[s3.ToString()].GetProperty("amount").ValueKind));
        Assert.Equal((148_200L, 13_000m), (byId[s4.ToString()].GetProperty("odometer").GetInt64(), byId[s4.ToString()].GetProperty("amount").GetDecimal()));
        Assert.Equal(13_000m, parted.GetProperty("spentAll")[0].GetProperty("amount").GetDecimal()); // no second expense
    }

    [Fact]
    public async Task AlsoChangeItFor_EmptiesTheSharedOdometerForBoth_ThenTrashTakesTheExpenseFromEveryLine()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        string[] corners = ["FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT"];
        var (s3, s4) = (Guid.NewGuid(), Guid.NewGuid());
        await Mutate("addTyreEntry", "AddTyreEntryInput", new
        {
            vehicleId = vehicle, kind = "PUNCTURE_REPAIRED", day = "2026-09-20", setId = summer, odometer = 148_200,
            fee = new { amount = 13_000, currency = "HUF", category = "Tyres", title = "Puncture repaired: Michelin", expenseId = Guid.NewGuid() },
            tyres = new object[] { new { tyreId = tyres[2], id = s3 }, new { tyreId = tyres[3], id = s4 } },
            breakUp = new { setId = summer, day = "2026-09-20", tyres = tyres.Select((id, n) => new { id, label = $"S{n + 1}", corner = corners[n] }) },
        }, "vehicleId");

        var emptied = await Mutate("updateTyreEntry", "UpdateTyreEntryInput", new { id = s3, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", also = new[] { s4 } },
            "entries { id odometer amount groupId } readings { date value }");
        var lines = emptied.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("groupId").ValueKind != JsonValueKind.Null).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal((JsonValueKind.Null, 13_000m), (l.GetProperty("odometer").ValueKind, l.GetProperty("amount").GetDecimal())));
        Assert.DoesNotContain(emptied.GetProperty("readings").EnumerateArray(), r => r.GetProperty("value").GetInt64() == 148_200);

        var trashed = await Mutate("updateTyreEntry", "UpdateTyreEntryInput", new
        {
            id = s3, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", fee = new { amount = (decimal?)null }, sharedExpense = "TRASH",
        }, "entries { id amount expenseTrashed } spentAll { amount }");
        var byId = trashed.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.All([byId[s3.ToString()], byId[s4.ToString()]], l => Assert.True(l.GetProperty("expenseTrashed").GetBoolean()));
        Assert.Empty(trashed.GetProperty("spentAll").EnumerateArray());
    }

    [Fact]
    public async Task ReplacingTwoTyres_RetiresThem_PutsTheNewOnesOnAtTheirCorners_EachWithHalfThePrice()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        string[] corners = ["FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT"];
        var (s5, s6, expense) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var replaced = await Mutate("replaceTyres", "ReplaceTyresInput", new
        {
            setId = summer, day = "2026-07-09", why = "DAMAGED", odometer = 145_500,
            tyres = new object[]
            {
                new { tyreId = tyres[0], id = s5, label = "S5", madeWeek = 20, madeYear = 2026, lineId = Guid.NewGuid() },
                new { tyreId = tyres[1], id = s6, label = "S6", name = "Pilot Sport 5", lineId = Guid.NewGuid() },
            },
            price = new { amount = 78_000, currency = "HUF", category = "Tyres", title = "Bought: Michelin", expenseId = expense },
            breakUp = new { setId = summer, day = "2026-07-09", tyres = tyres.Select((id, n) => new { id, label = $"S{n + 1}", corner = corners[n] }) },
        }, "sets { set { name } spent { amount } tyres { tyre { label isRetired retiredOn retiredWhy replacedById } corner onTheCar spent { amount } figures { distance } } } entries { kind tyreId groupId amount }");

        var set = SetNamed(replaced, "Michelin");
        Assert.Equal(78_000m, set.GetProperty("spent")[0].GetProperty("amount").GetDecimal());
        var s1 = TyreLabelled(replaced, "S1");
        Assert.Equal((true, "2026-07-09", "DAMAGED", s5.ToString()), (s1.GetProperty("tyre").GetProperty("isRetired").GetBoolean(), s1.GetProperty("tyre").GetProperty("retiredOn").GetString(),
            s1.GetProperty("tyre").GetProperty("retiredWhy").GetString(), s1.GetProperty("tyre").GetProperty("replacedById").GetString()));
        var new5 = TyreLabelled(replaced, "S5");
        Assert.Equal(("FRONT_LEFT", true, 39_000m), (new5.GetProperty("corner").GetString(), new5.GetProperty("onTheCar").GetBoolean(), new5.GetProperty("spent")[0].GetProperty("amount").GetDecimal()));
        Assert.Equal(2, replaced.GetProperty("entries").EnumerateArray().Count(e => e.GetProperty("kind").GetString() == "BOUGHT" && e.GetProperty("tyreId").ValueKind != JsonValueKind.Null));
    }

    [Fact]
    public async Task AReplacementDatedBeforeALaterInstallation_HandsThatTimeOnTheCarOverToTheNewTyre_OnARealDatabase()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        var winter = SetNamed(await AddSet(vehicle, "Continental", fitted: false), "Continental").GetProperty("set").GetProperty("id").GetString()!;
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        string[] corners = ["FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT"];
        await Mutate("breakUpTyreSet", "BreakUpTyreSetInput", new { setId = summer, day = "2026-04-01", tyres = tyres.Select((id, n) => new { id, label = $"S{n + 1}", corner = corners[n] }) }, "vehicleId");
        await Mutate("swapTyres", "SwapTyresInput", new { vehicleId = vehicle, setId = winter, day = "2026-06-01", installationId = Guid.NewGuid() }, "vehicleId");
        await Mutate("swapTyres", "SwapTyresInput", new { vehicleId = vehicle, setId = summer, day = "2026-08-01", installationId = Guid.NewGuid() }, "vehicleId");
        var s5 = Guid.NewGuid();

        await Mutate("replaceTyres", "ReplaceTyresInput", new { setId = summer, day = "2026-05-01", why = "DAMAGED", tyres = new[] { new { tyreId = tyres[0], id = s5, label = "S5", lineId = Guid.NewGuid() } } },
            "vehicleId");

        var after = await Mutate("updateTyre", "UpdateTyreInput", new { id = s5, label = "S5" }, "sets { set { name } tyres { tyre { label } corner places { place { from until corner } } } }");
        string[] Days(string label) => [.. TyreLabelled(after, label).GetProperty("places").EnumerateArray().Select(p => $"{p.GetProperty("place").GetProperty("from").GetString()}..{p.GetProperty("place").GetProperty("until").GetString()}")];
        Assert.Equal(["2026-03-28..2026-05-01"], Days("S1"));
        Assert.Equal(["2026-08-01..", "2026-05-01..2026-06-01"], Days("S5")); // newest first: the later one handed over whole
        Assert.Equal("FRONT_LEFT", TyreLabelled(after, "S5").GetProperty("corner").GetString());
    }

    [Fact]
    public async Task OneTyresLineOfAGroupWithoutAnAmount_MovedAlone_TakesARowOfItsOwn_TheOtherStaysOnASavedRow()
    {
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        string[] corners = ["FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT"];
        var (s3, s4) = (Guid.NewGuid(), Guid.NewGuid());
        await Mutate("addTyreEntry", "AddTyreEntryInput", new
        {
            vehicleId = vehicle, kind = "PUNCTURE_REPAIRED", day = "2026-09-20", setId = summer, odometer = 148_200,
            tyres = new object[] { new { tyreId = tyres[2], id = s3 }, new { tyreId = tyres[3], id = s4 } },
            breakUp = new { setId = summer, day = "2026-09-20", tyres = tyres.Select((id, n) => new { id, label = $"S{n + 1}", corner = corners[n] }) },
        }, "vehicleId");

        var moved = await Mutate("updateTyreEntry", "UpdateTyreEntryInput", new { id = s3, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-22", odometer = 148_400 },
            "entries { id day odometer groupId } readings { date value }");

        var byId = moved.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal(("2026-09-22", 148_400L), (byId[s3.ToString()].GetProperty("day").GetString(), byId[s3.ToString()].GetProperty("odometer").GetInt64()));
        Assert.Equal(("2026-09-20", 148_200L), (byId[s4.ToString()].GetProperty("day").GetString(), byId[s4.ToString()].GetProperty("odometer").GetInt64()));
        var readings = moved.GetProperty("readings").EnumerateArray().Select(r => (r.GetProperty("date").GetString(), r.GetProperty("value").GetInt64())).ToList();
        Assert.Contains(("2026-09-20", 148_200L), readings);
        Assert.Contains(("2026-09-22", 148_400L), readings);
    }

    [Fact]
    public async Task AnExpenseWithTyreDetails_IsOnTheTyreLog_ItsKindChangedFromTheExpense_AndAPlainOneGetsItsLineInAnEdit()
    {
        // #290: addExpense and updateExpense take `tyre`.
        var vehicle = await AddVehicle();
        var summer = SetNamed(await AddSet(vehicle, "Michelin", fitted: true), "Michelin").GetProperty("set").GetProperty("id").GetString()!;
        async Task<JsonElement> Ok(string mutation, object variables)
        {
            var body = await Send(mutation, variables);
            Assert.False(body.TryGetProperty("errors", out var errors), errors.ToString());
            return body.GetProperty("data");
        }
        async Task<JsonElement> LineOf(string expenseId) =>
            (await Ok("query($v: UUID!) { vehicleTyres(vehicleId: $v) { entries { id kind setId day odometer expenseId amount } } }", new { v = vehicle }))
                .GetProperty("vehicleTyres").GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("expenseId").GetString() == expenseId);
        var (expense, line) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        var added = (await Ok("mutation($i: AddExpenseInput!) { addExpense(input: $i) { id tyreEntry { id kind setName } } }", new
        {
            i = new
            {
                vehicleId = vehicle, id = expense, date = "2026-09-20", title = "Puncture repaired: Michelin", category = "Tyres", amount = 25, currency = "EUR", odometer = 148_200,
                tyre = new { kind = "PUNCTURE_REPAIRED", setId = summer, lineId = line },
            },
        })).GetProperty("addExpense");
        Assert.Equal((line, "PUNCTURE_REPAIRED", "Michelin"), (added.GetProperty("tyreEntry").GetProperty("id").GetString(),
            added.GetProperty("tyreEntry").GetProperty("kind").GetString(), added.GetProperty("tyreEntry").GetProperty("setName").GetString()));
        var shown = await LineOf(expense);
        Assert.Equal((line, "2026-09-20", 148_200L, 25m), (shown.GetProperty("id").GetString(), shown.GetProperty("day").GetString(), shown.GetProperty("odometer").GetInt64(), shown.GetProperty("amount").GetDecimal()));

        await Ok("mutation($i: UpdateExpenseInput!) { updateExpense(input: $i) { id } }", new
        {
            i = new { id = expense, date = "2026-09-20", title = "Valve: Michelin", category = "Tyres", amount = 25, odometer = 148_200, tyre = new { kind = "VALVE_REPLACED" } },
        });
        Assert.Equal(("VALVE_REPLACED", JsonValueKind.Null), (( await LineOf(expense)).GetProperty("kind").GetString(), (await LineOf(expense)).GetProperty("setId").ValueKind)); // no set: a whole set's line may go to none

        var plain = (await Ok("mutation($i: AddExpenseInput!) { addExpense(input: $i) { id } }", new
        {
            i = new { vehicleId = vehicle, id = Guid.NewGuid(), date = "2026-09-01", title = "Tyre hotel", category = "Tyres", amount = 60, currency = "EUR" },
        })).GetProperty("addExpense").GetProperty("id").GetString()!;
        await Ok("mutation($i: UpdateExpenseInput!) { updateExpense(input: $i) { id } }", new
        {
            i = new { id = plain, date = "2026-09-01", title = "Tyre hotel", category = "Tyres", amount = 60, tyre = new { kind = "STORAGE_FEE", setId = summer, lineId = Guid.NewGuid() } },
        });
        Assert.Equal("STORAGE_FEE", (await LineOf(plain)).GetProperty("kind").GetString());

        var note = await Send("mutation($i: AddExpenseInput!) { addExpense(input: $i) { id } }", new
        {
            i = new { vehicleId = vehicle, id = Guid.NewGuid(), date = "2026-09-21", title = "Note", amount = 5, currency = "EUR", tyre = new { kind = "NOTE", setId = summer } },
        });
        Assert.Equal("tyres.noteTakesNoAmount", note.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }
}
