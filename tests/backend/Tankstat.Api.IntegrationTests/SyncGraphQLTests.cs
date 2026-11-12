using System.Net.Http.Json;
using System.Text.Json;

namespace Tankstat.Api.IntegrationTests;

/// <summary>Changes a device kept offline, sent with <c>syncChanges</c>, over real GraphQL and a real database.</summary>
[Collection(ApiCollection.Name)]
public class SyncGraphQLTests : IDisposable
{
    private const string Sync = """
        mutation($i: SyncChangesInput!) { syncChanges(input: $i) { applied parked results { id status entityId version reason { key args { name value } } } } }
        """;

    private readonly TestApp _app = TestApp.Standalone();

    public void Dispose() => _app.Dispose();

    private static async Task<JsonElement> Send(HttpClient c, params object[] changes) =>
        (await c.Gql(Sync, new { i = new { changes } })).Data().GetProperty("syncChanges");

    private static async Task<int> Count(HttpClient c, string vehicleId) =>
        (await c.Gql("query($v: UUID!) { refuelingCount(vehicleId: $v) }", new { v = vehicleId })).Data().GetProperty("refuelingCount").GetInt32();

    [Fact]
    public async Task AVehicleAddedOffline_AndALogOfIt_AreApplied_AndTheSameBatchAgainAppliesNothing()
    {
        var people = await _app.Users();
        var car = Guid.NewGuid().ToString();
        var log = Guid.NewGuid().ToString();
        object[] batch =
        [
            new { id = Guid.NewGuid(), addVehicle = new { id = car, name = "Golf", fuelType = "PETROL" } },
            new { id = Guid.NewGuid(), logRefueling = new { id = log, vehicleId = car, date = "2026-09-01", volume = 40, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } },
        ];

        var first = await Send(people.Alice, batch);
        var again = await Send(people.Alice, batch);

        Assert.Equal(2, first.GetProperty("applied").GetInt32());
        Assert.Equal(car, first.GetProperty("results")[0].GetProperty("entityId").GetString());
        Assert.Equal(log, first.GetProperty("results")[1].GetProperty("entityId").GetString());
        Assert.Equal(first.GetProperty("results").ToString(), again.GetProperty("results").ToString());
        Assert.Equal(1, await Count(people.Alice, car));
    }

    [Fact]
    public async Task TyreChangesKeptOffline_AreApplied_OnceEach_AndAStaleEditIsParkedWithTheSetAsItIsNow()
    {
        var people = await _app.Users();
        var car = Guid.NewGuid().ToString();
        var summer = Guid.NewGuid().ToString();
        var winter = Guid.NewGuid().ToString();
        object[] batch =
        [
            new { id = Guid.NewGuid(), addVehicle = new { id = car, name = "Octavia", fuelType = "PETROL" } },
            new { id = Guid.NewGuid(), addTyreSet = new { id = summer, installationId = Guid.NewGuid(), vehicleId = car, name = "Michelin", season = "SUMMER", since = "2023-03-25", fitted = true, day = "2026-03-28" } },
            new { id = Guid.NewGuid(), addTyreSet = new { id = winter, vehicleId = car, name = "Continental", season = "WINTER", since = "2022-11-19", fitted = false, day = "2026-03-28" } },
            new { id = Guid.NewGuid(), swapTyres = new { vehicleId = car, setId = winter, day = "2026-09-20", pairOneFront = true, odometer = 148_200, installationId = Guid.NewGuid() } },
        ];

        var first = await Send(people.Alice, batch);
        var again = await Send(people.Alice, batch);

        Assert.Equal(4, first.GetProperty("applied").GetInt32());
        Assert.Equal(summer, first.GetProperty("results")[1].GetProperty("entityId").GetString());
        Assert.Equal(first.GetProperty("results").ToString(), again.GetProperty("results").ToString());
        var tyres = (await people.Alice.Gql("query($v: UUID!) { vehicleTyres(vehicleId: $v) { sets { onTheCar set { id version } installations { installation { from } } } entries { kind odometer } } }", new { v = car }))
            .Data().GetProperty("vehicleTyres");
        var sets = tyres.GetProperty("sets").EnumerateArray().ToDictionary(x => x.GetProperty("set").GetProperty("id").GetString()!);
        Assert.True(sets[winter].GetProperty("onTheCar").GetBoolean());
        Assert.Equal(1, sets[winter].GetProperty("installations").GetArrayLength()); // swapped once
        Assert.Equal(("SWAP", 148_200L), (tyres.GetProperty("entries")[0].GetProperty("kind").GetString(), tyres.GetProperty("entries")[0].GetProperty("odometer").GetInt64()));

        // An edit made from an older version of the set is parked; whoever decides sees the set as it is now.
        var version = sets[summer].GetProperty("set").GetProperty("version").GetInt32();
        await Send(people.Alice, new { id = Guid.NewGuid(), expectedVersion = version, updateTyreSet = new { id = summer, name = "Michelin Primacy 4", season = "SUMMER", since = "2023-03-25" } });
        var stale = await Send(people.Alice, new { id = Guid.NewGuid(), expectedVersion = version, updateTyreSet = new { id = summer, name = "Michelin P4", season = "SUMMER", since = "2023-03-25" } });
        Assert.Equal("sync.versionMismatch", stale.GetProperty("results")[0].GetProperty("reason").GetProperty("key").GetString());
        var parked = (await people.Alice.Gql("{ parkedChanges { kind current { state tyreSet { name } } } }")).Data().GetProperty("parkedChanges");
        var mine = parked.EnumerateArray().Single(p => p.GetProperty("kind").GetString() == "UPDATE_TYRE_SET");
        Assert.Equal(("LIVE", "Michelin Primacy 4"), (mine.GetProperty("current").GetProperty("state").GetString(), mine.GetProperty("current").GetProperty("tyreSet").GetProperty("name").GetString()));
    }

    [Fact]
    public async Task EveryKindOfTyreChange_IsApplied_ARepeatedTrashToo()
    {
        var people = await _app.Users();
        var (car, summer, winter, installation, entry) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        object Change(object operation) => operation;
        var batch = new List<object>
        {
            new { id = Guid.NewGuid(), addVehicle = new { id = car, name = "Octavia", fuelType = "PETROL" } },
            new { id = Guid.NewGuid(), addTyreSet = new { id = summer, installationId = Guid.NewGuid(), vehicleId = car, name = "Michelin", season = "SUMMER", since = "2023-03-25", fitted = true, day = "2026-03-28", roughDistance = 30_000 } },
            new { id = Guid.NewGuid(), addTyreSet = new { id = winter, vehicleId = car, name = "Continental", season = "WINTER", since = "2022-11-19", fitted = false, day = "2026-03-28" } },
            new { id = Guid.NewGuid(), updateTyreSet = new { id = winter, name = "Continental TS 870", season = "WINTER", since = "2022-11-19" } },
            new { id = Guid.NewGuid(), setTyreRoughDistance = new { id = summer, distance = 31_000 } },
            new { id = Guid.NewGuid(), swapTyres = new { vehicleId = car, setId = winter, day = "2026-09-20", installationId = Guid.NewGuid() } },
            new { id = Guid.NewGuid(), changeTyrePositions = new { vehicleId = car, setId = winter, day = "2026-10-01", pairOneFront = false, installationId = Guid.NewGuid() } },
            new { id = Guid.NewGuid(), addPastTyreSwaps = new { vehicleId = car, swaps = new object[] { new { day = "2025-11-15", setId = winter, installationId = Guid.NewGuid() } } } },
            new { id = Guid.NewGuid(), addTyreInstallation = new { id = installation, setId = summer, from = "2025-03-29", until = "2025-11-15" } },
            new { id = Guid.NewGuid(), updateTyreInstallation = new { id = installation, from = "2025-04-01", until = "2025-11-15", pairOneFront = true } },
            new { id = Guid.NewGuid(), deleteTyreInstallation = installation },
            new { id = Guid.NewGuid(), deleteTyreInstallation = installation }, // in the trash already: nothing left to do
            new { id = Guid.NewGuid(), restoreTyreInstallation = installation },
            new { id = Guid.NewGuid(), addTyreEntry = new { vehicleId = car, kind = "STORAGE_FEE", day = "2026-09-20", odometer = 148_200, note = "At the garage", id = entry } },
            new { id = Guid.NewGuid(), updateTyreEntry = new { id = entry, kind = "STORAGE_FEE", setId = summer, day = "2026-09-20", odometer = 148_200, note = "Garage" } },
            new { id = Guid.NewGuid(), deleteTyreEntry = new { id = entry } },
            new { id = Guid.NewGuid(), deleteTyreEntry = new { id = entry } }, // in the trash already: nothing left to do
            new { id = Guid.NewGuid(), restoreTyreEntry = entry },
            new { id = Guid.NewGuid(), retireTyreSet = new { id = summer, day = "2026-10-02" } },
            new { id = Guid.NewGuid(), unretireTyreSet = summer },
            new { id = Guid.NewGuid(), deleteTyreSet = summer },
            new { id = Guid.NewGuid(), deleteTyreSet = summer },
            new { id = Guid.NewGuid(), restoreTyreSet = summer },
        };

        var result = await Send(people.Alice, [.. batch.Select(Change)]);

        Assert.Equal(batch.Count, result.GetProperty("applied").GetInt32());
        Assert.Equal(0, result.GetProperty("parked").GetInt32());
        var tyres = (await people.Alice.Gql("query($v: UUID!) { vehicleTyres(vehicleId: $v) { sets { set { name roughDistance retiredOn } installations { installation { from pairOneFront } } } entries { kind note } readings { date value } } }", new { v = car }))
            .Data().GetProperty("vehicleTyres");
        var sets = tyres.GetProperty("sets").EnumerateArray().ToDictionary(x => x.GetProperty("set").GetProperty("name").GetString()!);
        Assert.Equal(31_000, sets["Michelin"].GetProperty("set").GetProperty("roughDistance").GetInt64());
        Assert.Equal(JsonValueKind.Null, sets["Michelin"].GetProperty("set").GetProperty("retiredOn").ValueKind);
        Assert.Contains(sets["Michelin"].GetProperty("installations").EnumerateArray(), x => x.GetProperty("installation").GetProperty("from").GetString() == "2025-04-01");
        Assert.Equal(3, sets["Continental TS 870"].GetProperty("installations").GetArrayLength()); // past swap, the swap, the change of positions
        Assert.Equal((148_200L, "2026-09-20"), (tyres.GetProperty("readings")[0].GetProperty("value").GetInt64(), tyres.GetProperty("readings")[0].GetProperty("date").GetString()));
        Assert.Contains(tyres.GetProperty("entries").EnumerateArray(), e => e.GetProperty("kind").GetString() == "STORAGE_FEE" && e.GetProperty("note").GetString() == "Garage");
    }

    [Fact]
    public async Task ATyreSwapWithoutTheIdOfWhatItCreates_IsRefused_AsAnAddWithoutItsId()
    {
        var people = await _app.Users();
        var car = Guid.NewGuid().ToString();
        var winter = Guid.NewGuid().ToString();
        await Send(people.Alice,
            new { id = Guid.NewGuid(), addVehicle = new { id = car, name = "Octavia", fuelType = "PETROL" } },
            new { id = Guid.NewGuid(), addTyreSet = new { id = winter, vehicleId = car, name = "Continental", season = "WINTER", since = "2022-11-19", fitted = false, day = "2026-03-28" } });

        var refused = await people.Alice.Gql(Sync, new { i = new { changes = new object[] { new { id = Guid.NewGuid(), swapTyres = new { vehicleId = car, setId = winter, day = "2026-09-20" } } } } });

        Assert.Equal("sync.entityIdRequired", refused.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    [Fact]
    public async Task ADiscountKeptOffline_IsAppliedWithItsLog_KeptByAnOlderBuildsUpdate_AndClearedByZero()
    {
        var people = await _app.Users();
        var car = Guid.NewGuid().ToString();
        var log = Guid.NewGuid().ToString();
        const string read = "query($id: UUID!) { refueling(id: $id) { totalCost discount pricePerUnit pricePerUnitBeforeDiscount } }";

        await Send(people.Alice,
            new { id = Guid.NewGuid(), addVehicle = new { id = car, name = "Golf", fuelType = "PETROL" } },
            new { id = Guid.NewGuid(), logRefueling = new { id = log, vehicleId = car, date = "2026-09-01", volume = 43, totalCost = 34744, discount = 430, currency = "HUF", odometer = 1000, isFullTank = true } });
        var logged = (await people.Alice.Gql(read, new { id = log })).Data().GetProperty("refueling");
        Assert.Equal((34744m, 430m, 808m, 818m),
            (logged.GetProperty("totalCost").GetDecimal(), logged.GetProperty("discount").GetDecimal(), logged.GetProperty("pricePerUnit").GetDecimal(), logged.GetProperty("pricePerUnitBeforeDiscount").GetDecimal()));

        // A change an older build kept offline knows no discount: it is kept. One made with the field emptied sends 0: it goes.
        var old = await Send(people.Alice, new { id = Guid.NewGuid(), expectedVersion = 1, updateRefueling = new { id = log, date = "2026-09-01", volume = 43, totalCost = 34744, currency = "HUF", odometer = 1000, isFullTank = true } });
        Assert.Equal("APPLIED", old.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(430m, (await people.Alice.Gql(read, new { id = log })).Data().GetProperty("refueling").GetProperty("discount").GetDecimal());

        var update = await Send(people.Alice, new { id = Guid.NewGuid(), expectedVersion = 2, updateRefueling = new { id = log, date = "2026-09-01", volume = 43, totalCost = 34744, discount = 0, currency = "HUF", odometer = 1000, isFullTank = true } });
        Assert.Equal("APPLIED", update.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, (await people.Alice.Gql(read, new { id = log })).Data().GetProperty("refueling").GetProperty("discount").ValueKind);
    }

    [Fact]
    public async Task AnExpensesAndAVisitsDiscount_KeptOffline_AreApplied_AndAnOlderBuildsUpdateKeepsIt()
    {
        var people = await _app.Users();
        var (car, expense, schedule, visit) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        const string read = "query($e: UUID!, $v: UUID!) { expense(id: $e) { amount discount } visit: expense(id: $v) { amount discount } }";

        await Send(people.Alice,
            new { id = car, addVehicle = new { id = car, name = "Golf", fuelType = "PETROL" } },
            new { id = expense, addExpense = new { id = expense, vehicleId = car, date = "2026-09-05", title = "Tyres", amount = 18800, discount = 1200, currency = "HUF" } },
            new { id = schedule, addRecurringExpense = new { id = schedule, vehicleId = car, title = "Service", kind = "TIME", intervalMonths = 12, lastDoneDate = "2026-01-01" } },
            new { id = visit, markRecurringExpensesDone = new { ids = new[] { schedule }, date = "2026-09-10", amount = 33000, discount = 2000, currency = "HUF", expenseId = visit } });
        var both = (await people.Alice.Gql(read, new { e = expense, v = visit })).Data();
        Assert.Equal((1200m, 2000m), (both.GetProperty("expense").GetProperty("discount").GetDecimal(), both.GetProperty("visit").GetProperty("discount").GetDecimal()));

        var old = await Send(people.Alice, new { id = Guid.NewGuid(), expectedVersion = 1, updateExpense = new { id = expense, date = "2026-09-05", title = "Tyres", amount = 18800, currency = "HUF" } });
        Assert.Equal("APPLIED", old.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(1200m, (await people.Alice.Gql(read, new { e = expense, v = visit })).Data().GetProperty("expense").GetProperty("discount").GetDecimal());
    }

    [Fact]
    public async Task AChangeMadeFromAnOldVersion_IsParked_AndChangesNothing()
    {
        var people = await _app.Users();
        var car = (await people.Alice.Gql("mutation { addVehicle(input: { name: \"Golf\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        await people.Alice.Gql("mutation($i: UpdateVehicleInput!) { updateVehicle(input: $i) { id } }", new { i = new { id = car, name = "Golf GTI", fuelType = "PETROL" } }); // version 2 now

        var result = await Send(people.Alice, new { id = Guid.NewGuid(), expectedVersion = 1, updateVehicle = new { id = car, name = "Polo", fuelType = "PETROL" } });

        var parked = result.GetProperty("results")[0];
        Assert.Equal("PARKED", parked.GetProperty("status").GetString());
        Assert.Equal("sync.versionMismatch", parked.GetProperty("reason").GetProperty("key").GetString());
        Assert.Equal("Golf GTI", (await people.Alice.Gql($"{{ vehicle(id: \"{car}\") {{ name }} }}")).Data().GetProperty("vehicle").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ARefusalOfTheServer_IsParkedWithItsKey_AndAccessStillApplies()
    {
        var people = await _app.Users();
        var car = (await people.Alice.Gql("mutation { addVehicle(input: { name: \"Golf\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;

        var bobs = await Send(people.Bob, new { id = Guid.NewGuid(), logRefueling = new { id = Guid.NewGuid(), vehicleId = car, date = "2026-09-01", volume = 40, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } });

        Assert.Equal("PARKED", bobs.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal("vehicle.notFound", bobs.GetProperty("results")[0].GetProperty("reason").GetProperty("key").GetString());
        Assert.Equal(0, await Count(people.Alice, car));
    }

    [Fact]
    public async Task AVehicleAddTheServerRefuses_AndALogOfAVehicleThatIsGone_AreParked_AndTheRestOfTheBatchGoesOn()
    {
        var people = await _app.Users();
        var refused = Guid.NewGuid().ToString(); // an add the server refuses: that vehicle never exists
        var purged = Guid.NewGuid().ToString(); // as if purged meanwhile
        var car = Guid.NewGuid().ToString();

        var result = await Send(people.Alice,
            new { id = Guid.NewGuid(), addVehicle = new { id = refused, name = "", fuelType = "PETROL" } },
            new { id = Guid.NewGuid(), logRefueling = new { id = Guid.NewGuid(), vehicleId = purged, date = "2026-09-01", volume = 40, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } },
            new { id = Guid.NewGuid(), addVehicle = new { id = car, name = "Golf", fuelType = "PETROL" } });

        var statuses = result.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString()).ToList();
        Assert.Equal(["PARKED", "PARKED", "APPLIED"], statuses); // filed with the sender: there is no vehicle row to file them under
    }

    [Fact]
    public async Task AChangeWithoutExactlyOneOperation_OrAnAddWithoutItsId_RefusesTheBatch()
    {
        var people = await _app.Users();
        var none = await people.Alice.Gql(Sync, new { i = new { changes = new object[] { new { id = Guid.NewGuid() } } } });
        var noId = await people.Alice.Gql(Sync, new { i = new { changes = new object[] { new { id = Guid.NewGuid(), addVehicle = new { name = "Golf", fuelType = "PETROL" } } } } });
        var signedOut = await _app.NewClient().Gql(Sync, new { i = new { changes = new object[] { new { id = Guid.NewGuid(), deleteVehicle = Guid.NewGuid() } } } });

        Assert.Equal("sync.oneOperationRequired", none.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        Assert.Equal("sync.entityIdRequired", noId.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        Assert.Equal("UNAUTHENTICATED", signedOut.ErrorCode());
    }

    [Fact]
    public async Task WhatWasChangedLast_SaysWhoDidItWhatAndWhen()
    {
        var people = await _app.Users();
        var car = (await people.Alice.Gql("mutation { addVehicle(input: { name: \"Golf\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        var log = (await people.Alice.Gql("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId = car, date = "2026-09-01", volume = 40, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } })).Data().GetProperty("logRefueling").GetProperty("id").GetString()!;
        await people.Alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = car, userId = people.BobId, level = "EDIT" } });
        const string Last = "query($id: UUID!) { refueling(id: $id) { lastChange changedAt changedBy { displayName } } }";
        var logged = (await people.Alice.Gql(Last, new { id = log })).Data().GetProperty("refueling");
        Assert.Equal(("CREATED", "alice"), (logged.GetProperty("lastChange").GetString(), logged.GetProperty("changedBy").GetProperty("displayName").GetString()));
        Assert.NotEqual(JsonValueKind.Null, logged.GetProperty("changedAt").ValueKind);

        await people.Bob.Gql("mutation($i: UpdateRefuelingInput!) { updateRefueling(input: $i) { id } }",
            new { i = new { id = log, date = "2026-09-01", volume = 41, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } });
        var edited = (await people.Alice.Gql(Last, new { id = log })).Data().GetProperty("refueling");
        Assert.Equal(("EDITED", "bob"), (edited.GetProperty("lastChange").GetString(), edited.GetProperty("changedBy").GetProperty("displayName").GetString()));

        await Send(people.Alice, new { id = Guid.NewGuid(), deleteVehicle = car }); // through a sync too
        var trashed = (await people.Alice.Gql("{ trash { lastChange changedBy { displayName } } }")).Data().GetProperty("trash").EnumerateArray().Single();
        Assert.Equal(("TRASHED", "alice"), (trashed.GetProperty("lastChange").GetString(), trashed.GetProperty("changedBy").GetProperty("displayName").GetString()));
    }

    private const string Parked = "{ parkedChanges { id kind status change reason { key } submittedBy { displayName } canResolve vehicleId } }";
    private const string Resolve = "mutation($i: ResolveSyncChangeInput!) { resolveSyncChange(input: $i) { id status entityId version } }";

    /// <summary>Bob (an editor of Alice's car) renames it from an old version on a device: parked, filed with the car.</summary>
    private static async Task<(TwoUsers People, string Car, string ChangeId)> ParkedByBob(TestApp app)
    {
        var people = await app.Users();
        var car = (await people.Alice.Gql("mutation { addVehicle(input: { name: \"Golf\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        var log = (await people.Alice.Gql("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId = car, date = "2026-09-01", volume = 40, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } })).Data().GetProperty("logRefueling").GetProperty("id").GetString()!;
        await people.Alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = car, userId = people.BobId, level = "EDIT" } });
        await people.Alice.Gql("mutation($i: UpdateRefuelingInput!) { updateRefueling(input: $i) { id } }",
            new { i = new { id = log, date = "2026-09-01", volume = 41, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } }); // version 2
        var changeId = Guid.NewGuid().ToString();
        await Send(people.Bob, new { id = changeId, expectedVersion = 1, updateRefueling = new { id = log, date = "2026-09-01", volume = 45, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } });
        return (people, car, changeId);
    }

    [Fact]
    public async Task AParkedChange_IsListedForTheOwnerAndTheSender_CountedOnTheVehicle_AndNotified()
    {
        var (people, car, changeId) = await ParkedByBob(_app);

        var alices = (await people.Alice.Gql(Parked)).Data().GetProperty("parkedChanges");
        var only = Assert.Single(alices.EnumerateArray());
        Assert.Equal((changeId, "UPDATE_REFUELING", "sync.versionMismatch", "bob", true, car),
            (only.GetProperty("id").GetString(), only.GetProperty("kind").GetString(), only.GetProperty("reason").GetProperty("key").GetString(),
             only.GetProperty("submittedBy").GetProperty("displayName").GetString(), only.GetProperty("canResolve").GetBoolean(), only.GetProperty("vehicleId").GetString()));
        Assert.Contains("\"volume\":45", only.GetProperty("change").GetString());
        Assert.Single((await people.Bob.Gql(Parked)).Data().GetProperty("parkedChanges").EnumerateArray());
        Assert.Equal(1, (await people.Alice.Gql($"{{ vehicle(id: \"{car}\") {{ parkedChangeCount }} }}")).Data().GetProperty("vehicle").GetProperty("parkedChangeCount").GetInt32());

        var inbox = (await people.Alice.Gql("{ notifications(unreadOnly: true) { kind subject { type id } args { name value } } }")).Data().GetProperty("notifications");
        var note = inbox.EnumerateArray().Single(n => n.GetProperty("kind").GetString() == "SYNC_CHANGE_PARKED");
        Assert.Equal(("SYNC_CHANGE", changeId), (note.GetProperty("subject").GetProperty("type").GetString(), note.GetProperty("subject").GetProperty("id").GetString()));
    }

    [Fact]
    public async Task AParkedChange_IsAppliedAnywayAsEdited_OrDiscarded_OnceOnly()
    {
        var (people, car, changeId) = await ParkedByBob(_app);
        var log = (await people.Alice.Gql($"{{ refuelings(vehicleId: \"{car}\", orderBy: DATE, direction: DESC, skip: 0, take: 5) {{ id }} }}"))
            .Data().GetProperty("refuelings")[0].GetProperty("id").GetString()!;

        var applied = (await people.Alice.Gql(Resolve, new { i = new { id = changeId, action = "APPLY", change = new { id = changeId,
            updateRefueling = new { id = log, date = "2026-09-01", volume = 44, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } } } })).Data().GetProperty("resolveSyncChange");
        var twice = await people.Alice.Gql(Resolve, new { i = new { id = changeId, action = "DISCARD" } });

        Assert.Equal(("APPLIED", 3), (applied.GetProperty("status").GetString(), applied.GetProperty("version").GetInt32()));
        Assert.Equal("sync.notParked", twice.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        Assert.Empty((await people.Alice.Gql(Parked)).Data().GetProperty("parkedChanges").EnumerateArray());
        Assert.Equal(44, (await people.Alice.Gql($"{{ refueling(id: \"{log}\") {{ volume }} }}")).Data().GetProperty("refueling").GetProperty("volume").GetDecimal());
    }

    [Fact]
    public async Task OnlyThoseWhoMaySeeIt_FindAParkedChange_AndAnEditMustDoTheSame()
    {
        var (people, _, changeId) = await ParkedByBob(_app);
        var stranger = _app.NewClient();
        await people.Admin.Gql("mutation($i: CreateUserInput!) { createUser(input: $i) { user { id } } }", new { i = new { email = "eve@example.com", displayName = "eve", isAdmin = false } });

        var otherKind = await people.Alice.Gql(Resolve, new { i = new { id = changeId, action = "APPLY", change = new { id = changeId, deleteRefueling = Guid.NewGuid() } } });
        var discarded = (await people.Bob.Gql(Resolve, new { i = new { id = changeId, action = "DISCARD" } })).Data().GetProperty("resolveSyncChange");

        Assert.Equal("sync.kindMismatch", otherKind.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
        Assert.Equal("DISCARDED", discarded.GetProperty("status").GetString());
        Assert.Equal("UNAUTHENTICATED", (await stranger.Gql(Parked)).ErrorCode());
    }

    private static async Task<string> UploadDraft(HttpClient c, string vehicleId)
    {
        var content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        var response = await c.PutAsync($"/media/vehicles/{vehicleId}/photo-drafts", content);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string[]> Photos(HttpClient c, string refuelingId) =>
        [.. (await c.Gql($"{{ refueling(id: \"{refuelingId}\") {{ photos {{ id }} }} }}")).Data().GetProperty("refueling").GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("id").GetString()!)];

    [Fact]
    public async Task APhotoAddedOrRemovedOffline_OnASavedLog_IsAppliedOnce_AndARemovedOneIsParked()
    {
        var people = await _app.Users();
        var car = (await people.Alice.Gql("mutation { addVehicle(input: { name: \"Golf\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        var log = (await people.Alice.Gql("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId = car, date = "2026-09-01", volume = 40, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } })).Data().GetProperty("logRefueling").GetProperty("id").GetString()!;
        var draft = await UploadDraft(people.Alice, car);
        var add = new { id = Guid.NewGuid(), addRefuelingPhoto = new { logId = log, draftId = draft } };

        var added = await Send(people.Alice, add);
        var again = await Send(people.Alice, add);
        var removed = await Send(people.Alice, new { id = Guid.NewGuid(), removeRefuelingPhoto = new { logId = log, imageId = draft } });
        var gone = await Send(people.Alice, new { id = Guid.NewGuid(), removeRefuelingPhoto = new { logId = log, imageId = draft } });

        Assert.Equal(("APPLIED", draft), (added.GetProperty("results")[0].GetProperty("status").GetString(), added.GetProperty("results")[0].GetProperty("entityId").GetString()));
        Assert.Equal(added.GetRawText(), again.GetRawText());
        Assert.Equal("APPLIED", removed.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(("PARKED", "photo.notFound"), (gone.GetProperty("results")[0].GetProperty("status").GetString(), gone.GetProperty("results")[0].GetProperty("reason").GetProperty("key").GetString()));
        Assert.Empty(await Photos(people.Alice, log));
        var parked = Assert.Single((await people.Alice.Gql("{ parkedChanges { kind vehicleId } }")).Data().GetProperty("parkedChanges").EnumerateArray());
        Assert.Equal(("REMOVE_REFUELING_PHOTO", car), (parked.GetProperty("kind").GetString(), parked.GetProperty("vehicleId").GetString()));
    }

    [Fact]
    public async Task APhotoParkedForItsSender_IsAttachedFromTheSendersDraft_WhenSomeoneWhoMayEditTheLogsAppliesIt()
    {
        var (people, car, log) = await SharedLog(_app);
        var draft = await UploadDraft(people.Bob, car);
        await people.Alice.Gql("mutation($id: UUID!) { deleteRefueling(id: $id) { id } }", new { id = log });
        var change = Guid.NewGuid();
        var sent = await Send(people.Bob, new { id = change, addRefuelingPhoto = new { logId = log, draftId = draft } }); // to a log in the trash
        Assert.Equal("PARKED", sent.GetProperty("results")[0].GetProperty("status").GetString());
        await people.Alice.Gql("mutation($id: UUID!) { restoreRefueling(id: $id) { id } }", new { id = log });

        var applied = await people.Alice.Gql(Resolve, new { i = new { id = change, action = "APPLY" } });

        Assert.Equal("APPLIED", applied.Data().GetProperty("resolveSyncChange").GetProperty("status").GetString());
        Assert.Equal([draft], await Photos(people.Alice, log));
    }

    [Fact]
    public async Task EveryKindOfChange_IsAppliedThroughSync_InTheOrderItWasMade()
    {
        var alice = (await _app.Users()).Alice;
        var (car, refueling, expense, schedule, visit) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var fill = new { date = "2026-09-01", volume = 40, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true };
        var cost = new { date = "2026-09-05", title = "Parking", amount = 5, currency = "EUR" };
        var cycle = new { title = "Service", kind = "TIME", intervalMonths = 12, lastDoneDate = "2026-01-01" };

        // Each change as the device sends it: its own id and one operation (an add's id is the id of what it adds).
        var vehicle = await Send(alice, new { id = car, addVehicle = new { id = car, name = "Golf", fuelType = "PETROL" } });
        var (refuelingPhoto, expensePhoto) = (await UploadDraft(alice, car.ToString()), await UploadDraft(alice, car.ToString()));
        var rest = await Send(alice,
            new { id = refueling, logRefueling = new { id = refueling, vehicleId = car, fill.date, fill.volume, fill.totalCost, fill.currency, fill.odometer, fill.isFullTank } },
            new { id = expense, addExpense = new { id = expense, vehicleId = car, cost.date, cost.title, cost.amount, cost.currency } },
            new { id = schedule, addRecurringExpense = new { id = schedule, vehicleId = car, cycle.title, cycle.kind, cycle.intervalMonths, cycle.lastDoneDate } },
            new { id = Guid.NewGuid(), updateRefueling = new { id = refueling, fill.date, volume = 41, fill.totalCost, fill.currency, fill.odometer, fill.isFullTank } },
            new { id = Guid.NewGuid(), updateExpense = new { id = expense, cost.date, title = "Parking at the station", cost.amount, cost.currency } },
            new { id = Guid.NewGuid(), updateRecurringExpense = new { id = schedule, title = "Yearly service", cycle.kind, cycle.intervalMonths, cycle.lastDoneDate } },
            new { id = Guid.NewGuid(), updateVehicle = new { id = car, name = "Golf GTI", fuelType = "PETROL" } },
            new { id = visit, markRecurringExpensesDone = new { ids = new[] { schedule }, date = "2026-09-10", odometer = 1100, amount = 120, currency = "EUR", expenseId = visit } },
            new { id = Guid.NewGuid(), addRefuelingPhoto = new { logId = refueling, draftId = refuelingPhoto } },
            new { id = Guid.NewGuid(), removeRefuelingPhoto = new { logId = refueling, imageId = refuelingPhoto } },
            new { id = Guid.NewGuid(), addExpensePhoto = new { logId = expense, draftId = expensePhoto } },
            new { id = Guid.NewGuid(), removeExpensePhoto = new { logId = expense, imageId = expensePhoto } },
            new { id = Guid.NewGuid(), deleteRefueling = refueling },
            new { id = Guid.NewGuid(), restoreRefueling = refueling },
            new { id = Guid.NewGuid(), deleteExpense = expense },
            new { id = Guid.NewGuid(), restoreExpense = expense },
            new { id = Guid.NewGuid(), deleteRecurringExpense = schedule },
            new { id = Guid.NewGuid(), deleteVehicle = car },
            new { id = Guid.NewGuid(), restoreVehicle = car });

        var results = vehicle.GetProperty("results").EnumerateArray().Concat(rest.GetProperty("results").EnumerateArray()).ToList();
        Assert.Equal(20, results.Count);
        Assert.All(results, r => Assert.Equal("APPLIED", r.GetProperty("status").GetString()));
        var now = (await alice.Gql($"{{ vehicle(id: \"{car}\") {{ name }} refueling(id: \"{refueling}\") {{ volume deletedAt photos {{ id }} }} expense(id: \"{visit}\") {{ amount }} }}")).Data();
        Assert.Equal(("Golf GTI", 41m, true, 0, 120m),
            (now.GetProperty("vehicle").GetProperty("name").GetString(), now.GetProperty("refueling").GetProperty("volume").GetDecimal(),
             now.GetProperty("refueling").GetProperty("deletedAt").ValueKind == JsonValueKind.Null, now.GetProperty("refueling").GetProperty("photos").GetArrayLength(),
             now.GetProperty("expense").GetProperty("amount").GetDecimal()));
    }

    /// <summary>Alice's car with one log (version 1), Bob an editor of its logs.</summary>
    private static async Task<(TwoUsers People, string Car, string Log)> SharedLog(TestApp app)
    {
        var people = await app.Users();
        var car = (await people.Alice.Gql("mutation { addVehicle(input: { name: \"Golf\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        var log = (await people.Alice.Gql("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId = car, date = "2026-09-01", volume = 40, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true } })).Data().GetProperty("logRefueling").GetProperty("id").GetString()!;
        await people.Alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = car, userId = people.BobId, level = "EDIT" } });
        return (people, car, log);
    }

    private static object Edit(string log, decimal volume, string? note = null) =>
        new { id = log, date = "2026-09-01", volume, totalCost = 60, currency = "EUR", odometer = 1000, isFullTank = true, note };

    /// <summary>The selection the app's ParkedChangeFields makes of it (graphql/offline.graphql, conflicts.graphql), so the cost analysis is the app's.</summary>
    private const string Current = """
        { parkedChanges { id kind status vehicleId targetId change base receivedAt reason { key args { name value } } vehicle { id name distanceUnit volumeUnit }
          submittedBy { id displayName } canResolve
          current { state version changedAt lastChange changedBy { id displayName }
            vehicle { version name licensePlate fuelType units { distance volume } }
            refueling { version date volume totalCost currency odometer isFullTank missedPreviousFillUp note }
            expense { version date title category amount currency odometer note }
            schedule { version title category note kind intervalMonths intervalDistance lastDoneDate lastDoneOdometer warnDays warnDistance } } } }
        """;

    private static string? Key(JsonElement response) =>
        response.TryGetProperty("errors", out var errors) ? errors[0].GetProperty("extensions").GetProperty("key").GetString() : null;

    [Fact]
    public async Task AParkedEdit_CarriesTheValuesItWasMadeFrom_AndWhatIsOnTheServerNow_ForWhoeverMaySeeIt()
    {
        var (people, car, log) = await SharedLog(_app);
        await people.Alice.Gql("mutation($i: UpdateRefuelingInput!) { updateRefueling(input: $i) { id } }", new { i = Edit(log, 41, "Alice's") }); // version 2
        var made = """{"volume":40,"note":null}""";
        var change = Guid.NewGuid().ToString();

        var sent = await Send(people.Bob, new { id = change, expectedVersion = 1, updateRefueling = Edit(log, 45), @base = made });

        Assert.Equal("PARKED", sent.GetProperty("results")[0].GetProperty("status").GetString());
        var parked = (await people.Alice.Gql(Current)).Data().GetProperty("parkedChanges")[0];
        Assert.Equal(made, parked.GetProperty("base").GetString());
        var now = parked.GetProperty("current");
        Assert.Equal(("LIVE", 2, "EDITED", "alice", 41m, "Alice's"),
            (now.GetProperty("state").GetString(), now.GetProperty("version").GetInt32(), now.GetProperty("lastChange").GetString(),
             now.GetProperty("changedBy").GetProperty("displayName").GetString(), now.GetProperty("refueling").GetProperty("volume").GetDecimal(),
             now.GetProperty("refueling").GetProperty("note").GetString()));
        Assert.Equal(JsonValueKind.Null, now.GetProperty("vehicle").ValueKind);
        Assert.DoesNotContain("base", (await people.Alice.Gql(Parked)).Data().GetProperty("parkedChanges")[0].GetProperty("change").GetString()); // kept apart

        // Bob's access ends: his change is still listed for him, but nothing of what is there now.
        await people.Alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = car, userId = people.BobId, level = "NONE" } });
        var his = Assert.Single((await people.Bob.Gql(Current)).Data().GetProperty("parkedChanges").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, his.GetProperty("current").ValueKind);
    }

    [Fact]
    public async Task AMergedEdit_IsAppliedAgainstTheVersionItWasMergedWith_AndParkedAgainWhenItWasChangedOnceMore()
    {
        var (people, _, log) = await SharedLog(_app);
        await people.Alice.Gql("mutation($i: UpdateRefuelingInput!) { updateRefueling(input: $i) { id } }", new { i = Edit(log, 41) }); // version 2
        var change = Guid.NewGuid().ToString();
        await Send(people.Bob, new { id = change, expectedVersion = 1, updateRefueling = Edit(log, 45) });
        await people.Alice.Gql("mutation($i: UpdateRefuelingInput!) { updateRefueling(input: $i) { id } }", new { i = Edit(log, 42) }); // version 3, after Bob looked

        var stale = await people.Bob.Gql(Resolve, new { i = new { id = change, action = "APPLY", change = new { id = change, expectedVersion = 2, updateRefueling = Edit(log, 43) } } });
        var merged = await people.Bob.Gql(Resolve, new { i = new { id = change, action = "APPLY", change = new { id = change, expectedVersion = 3, updateRefueling = Edit(log, 43) } } });

        Assert.Equal("sync.versionMismatch", Key(stale));
        Assert.Equal(("APPLIED", 4), (merged.Data().GetProperty("resolveSyncChange").GetProperty("status").GetString(), merged.Data().GetProperty("resolveSyncChange").GetProperty("version").GetInt32()));
        Assert.Equal(43, (await people.Alice.Gql($"{{ refueling(id: \"{log}\") {{ volume }} }}")).Data().GetProperty("refueling").GetProperty("volume").GetDecimal());
    }

    [Fact]
    public async Task AnEditOfWhatWasTrashedMeanwhile_CanBeRestoredWithIt_AndARefusedEditPutsItBack()
    {
        var (people, _, log) = await SharedLog(_app);
        await people.Alice.Gql("mutation($id: UUID!) { deleteRefueling(id: $id) { id } }", new { id = log }); // version 2, in the trash
        var change = Guid.NewGuid().ToString();
        var sent = await Send(people.Bob, new { id = change, expectedVersion = 1, updateRefueling = Edit(log, 45) });
        Assert.Equal("refueling.notFound", sent.GetProperty("results")[0].GetProperty("reason").GetProperty("key").GetString());
        Assert.Equal("TRASHED", (await people.Bob.Gql(Current)).Data().GetProperty("parkedChanges")[0].GetProperty("current").GetProperty("state").GetString());

        var refused = await people.Bob.Gql(Resolve, new { i = new { id = change, action = "APPLY", restoreFirst = true, change = new { id = change, expectedVersion = 2, updateRefueling = Edit(log, -1) } } });
        Assert.NotNull(Key(refused));
        Assert.Equal(JsonValueKind.Null, (await people.Alice.Gql($"{{ refueling(id: \"{log}\") {{ id }} }}")).Data().GetProperty("refueling").ValueKind); // back in the trash

        var trashedAgain = (await people.Bob.Gql(Current)).Data().GetProperty("parkedChanges")[0].GetProperty("current").GetProperty("version").GetInt32();
        var restored = await people.Bob.Gql(Resolve, new { i = new { id = change, action = "APPLY", restoreFirst = true, change = new { id = change, expectedVersion = trashedAgain, updateRefueling = Edit(log, 45) } } });
        Assert.Equal("APPLIED", restored.Data().GetProperty("resolveSyncChange").GetProperty("status").GetString());
        Assert.Equal(45, (await people.Alice.Gql($"{{ refueling(id: \"{log}\") {{ volume }} }}")).Data().GetProperty("refueling").GetProperty("volume").GetDecimal());

        var notAnEdit = Guid.NewGuid().ToString();
        await people.Alice.Gql("mutation($id: UUID!) { deleteRefueling(id: $id) { id } }", new { id = log });
        await Send(people.Bob, new { id = notAnEdit, expectedVersion = 1, restoreRefueling = log }); // parked: changed meanwhile
        Assert.Equal("sync.cannotRestoreFirst", Key(await people.Bob.Gql(Resolve, new { i = new { id = notAnEdit, action = "APPLY", restoreFirst = true } })));
    }

    [Fact]
    public async Task ATrashOfWhatIsAlreadyInTheTrash_IsApplied_ButNotForWhoeverMayNoLongerChangeIt()
    {
        var (people, car, log) = await SharedLog(_app);
        await people.Alice.Gql("mutation($id: UUID!) { deleteRefueling(id: $id) { id } }", new { id = log });

        var bobs = await Send(people.Bob, new { id = Guid.NewGuid(), expectedVersion = 1, deleteRefueling = log });
        Assert.Equal("APPLIED", bobs.GetProperty("results")[0].GetProperty("status").GetString());

        await people.Alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = car, userId = people.BobId, level = "NONE" } });
        var outsider = await Send(people.Bob, new { id = Guid.NewGuid(), expectedVersion = 1, deleteRefueling = log });
        Assert.Equal(("PARKED", "refueling.notFound"),
            (outsider.GetProperty("results")[0].GetProperty("status").GetString(), outsider.GetProperty("results")[0].GetProperty("reason").GetProperty("key").GetString()));

        var vehicle = await Send(people.Alice, new { id = Guid.NewGuid(), deleteVehicle = car }, new { id = Guid.NewGuid(), expectedVersion = 1, deleteVehicle = car });
        Assert.Equal(2, vehicle.GetProperty("applied").GetInt32());
    }

    [Fact]
    public async Task AVehiclesPictureChosenOffline_IsSetFromItsDraftOnce_RemovedLater_AndParkedForWhoeverMayNotChangeTheVehicle_UntilTheOwnerAppliesIt()
    {
        var people = await _app.Users();
        var car = (await people.Alice.Gql("mutation { addVehicle(input: { name: \"Golf\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        async Task<string?> Picture() =>
            (await people.Alice.Gql($"{{ vehicle(id: \"{car}\") {{ pictureUrl }} }}")).Data().GetProperty("vehicle").GetProperty("pictureUrl").GetString();
        var draft = await UploadDraft(people.Alice, car);
        object set = new { id = Guid.NewGuid(), setVehiclePicture = new { vehicleId = car, draftId = draft } };

        var first = await Send(people.Alice, set);
        var again = await Send(people.Alice, set); // the answer never arrived: sent again

        Assert.Equal("APPLIED", first.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(first.GetProperty("results").ToString(), again.GetProperty("results").ToString());
        Assert.Equal($"/media/{Guid.Parse(draft):N}", await Picture());
        Assert.Equal(System.Net.HttpStatusCode.OK, (await people.Alice.GetAsync($"/media/{draft}")).StatusCode); // the picture, no longer a draft

        // Bob may edit the car's logs (and so upload drafts), not the car: his picture is parked with the reason.
        await people.Alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = car, userId = people.BobId, level = "EDIT" } });
        var (bobsChange, bobsDraft) = (Guid.NewGuid(), await UploadDraft(people.Bob, car));
        var bobs = await Send(people.Bob, new { id = bobsChange, setVehiclePicture = new { vehicleId = car, draftId = bobsDraft } });
        Assert.Equal(("PARKED", "vehicle.viewOnly"),
            (bobs.GetProperty("results")[0].GetProperty("status").GetString(), bobs.GetProperty("results")[0].GetProperty("reason").GetProperty("key").GetString()));

        // Alice, who may change the car, applies it anyway: it takes Bob's draft.
        var applied = await people.Alice.Gql(Resolve, new { i = new { id = bobsChange, action = "APPLY" } });
        Assert.Equal("APPLIED", applied.Data().GetProperty("resolveSyncChange").GetProperty("status").GetString());
        Assert.Equal($"/media/{Guid.Parse(bobsDraft):N}", await Picture());

        var removed = await Send(people.Alice, new { id = Guid.NewGuid(), removeVehiclePicture = car });
        Assert.Equal("APPLIED", removed.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Null(await Picture());
    }
}
