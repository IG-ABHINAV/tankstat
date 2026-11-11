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
        await Ok("mutation($id: UUID!) { deleteTyreEntry(id: $id) { vehicleId } }", new { id = entry });
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
}
