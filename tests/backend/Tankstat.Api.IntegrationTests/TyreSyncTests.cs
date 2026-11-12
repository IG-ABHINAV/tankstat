using System.Text.Json;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// Tyre changes a device kept offline (#287), sent with <c>syncChanges</c> and applied anyway with <c>resolveSyncChange</c>, where the
/// queue meets what someone else did meanwhile: nothing written over unseen, a retry's leniency only for a retry, the trash-once rule.
/// </summary>
[Collection(ApiCollection.Name)]
public class TyreSyncTests : IDisposable
{
    private const string Sync = """
        mutation($i: SyncChangesInput!) { syncChanges(input: $i) { applied parked results { id status entityId version reason { key args { name value } } } } }
        """;
    private const string Resolve = "mutation($i: ResolveSyncChangeInput!) { resolveSyncChange(input: $i) { id status } }";
    private const string SwapOnline = "mutation($i: SwapTyresInput!) { swapTyres(input: $i) { vehicleId } }";
    private const string Tyres = """
        query($v: UUID!) { vehicleTyres(vehicleId: $v) { sets { set { id } installations { installation { id from until pairOneFront } } } entries { id kind setId installationId day odometer note version } } }
        """;

    private readonly TestApp _app = TestApp.Standalone();

    public void Dispose() => _app.Dispose();

    private static async Task<JsonElement> Send(HttpClient c, params object[] changes) =>
        (await c.Gql(Sync, new { i = new { changes } })).Data().GetProperty("syncChanges");

    private static (string? Status, string? Key) Result(JsonElement sent, int index = 0)
    {
        var r = sent.GetProperty("results")[index];
        return (r.GetProperty("status").GetString(), r.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : r.GetProperty("reason").GetProperty("key").GetString());
    }

    private static string? Key(JsonElement response) =>
        response.TryGetProperty("errors", out var errors) ? errors[0].GetProperty("extensions").GetProperty("key").GetString() : null;

    private static async Task<JsonElement> TyresOf(HttpClient c, string car) => (await c.Gql(Tyres, new { v = car })).Data().GetProperty("vehicleTyres");

    private static JsonElement[] InstallationsOf(JsonElement tyres, string set) =>
        [.. tyres.GetProperty("sets").EnumerateArray().Single(s => s.GetProperty("set").GetProperty("id").GetString() == set).GetProperty("installations").EnumerateArray()
            .Select(x => x.GetProperty("installation"))];

    /// <summary>Alice's car: the summer set on since 28 Mar 2026 (pair 1 at the front), the winter set in storage; Bob an editor of its logs.</summary>
    private static async Task<(TwoUsers People, string Car, string Summer, string Winter, string SummerInstallation)> Car(TestApp app)
    {
        var people = await app.Users();
        var (car, summer, winter, installation) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var made = await Send(people.Alice,
            new { id = Guid.NewGuid(), addVehicle = new { id = car, name = "Octavia", fuelType = "PETROL" } },
            new { id = Guid.NewGuid(), addTyreSet = new { id = summer, installationId = installation, vehicleId = car, name = "Michelin", season = "SUMMER", since = "2023-03-25", fitted = true, day = "2026-03-28", pairOneFront = true } },
            new { id = Guid.NewGuid(), addTyreSet = new { id = winter, vehicleId = car, name = "Continental", season = "WINTER", since = "2022-11-19", fitted = false, day = "2026-03-28" } });
        Assert.Equal(3, made.GetProperty("applied").GetInt32());
        await people.Alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = car, userId = people.BobId, level = "EDIT" } });
        return (people, car, summer, winter, installation);
    }

    private static JsonElement LineOf(JsonElement tyres, string id) => tyres.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);

    [Fact]
    public async Task ALineAdded_FromTheQueue_IsAppliedOnce_ItsEditFromAnOlderVersionIsParked_AndItsTrashSentAgainIsDone()
    {
        var (people, car, summer, _, _) = await Car(_app);
        var (line, add) = (Guid.NewGuid().ToString(), Guid.NewGuid());
        var input = new { vehicleId = car, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", odometer = 148_200, note = "Rear left", id = line };

        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new { id = add, addTyreEntry = input })));
        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new { id = Guid.NewGuid(), addTyreEntry = input }))); // the same add, sent as a new change
        var version = LineOf(await TyresOf(people.Alice, car), line).GetProperty("version").GetInt32();

        var edit = await Send(people.Bob, new { id = Guid.NewGuid(), expectedVersion = version, updateTyreEntry = new { id = line, kind = "VALVE_REPLACED", setId = summer, day = "2026-09-20", odometer = 148_300 } });
        var stale = await Send(people.Alice, new { id = Guid.NewGuid(), expectedVersion = version, updateTyreEntry = new { id = line, kind = "OTHER", day = "2026-09-20" } });
        Assert.Equal(("APPLIED", null), Result(edit));
        Assert.Equal(("PARKED", "sync.versionMismatch"), Result(stale));
        var now = LineOf(await TyresOf(people.Alice, car), line);
        Assert.Equal(("VALVE_REPLACED", 148_300L, JsonValueKind.Null), (now.GetProperty("kind").GetString(), now.GetProperty("odometer").GetInt64(), now.GetProperty("note").ValueKind));
        var parked = (await people.Alice.Gql("{ parkedChanges { kind current { state tyreEntry { kind } } } }")).Data().GetProperty("parkedChanges")[0];
        Assert.Equal(("UPDATE_TYRE_ENTRY", "VALVE_REPLACED"), (parked.GetProperty("kind").GetString(), parked.GetProperty("current").GetProperty("tyreEntry").GetProperty("kind").GetString()));

        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new { id = Guid.NewGuid(), deleteTyreEntry = new { id = line } })));
        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new { id = Guid.NewGuid(), deleteTyreEntry = new { id = line } }))); // in the trash already
        Assert.DoesNotContain((await TyresOf(people.Alice, car)).GetProperty("entries").EnumerateArray(), e => e.GetProperty("id").GetString() == line);
        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new { id = Guid.NewGuid(), restoreTyreEntry = line })));
        Assert.Equal(148_300, LineOf(await TyresOf(people.Alice, car), line).GetProperty("odometer").GetInt64());
    }

    [Fact]
    public async Task ASwapsLineOnAnotherDay_FromTheQueue_MovesTheSwap_AndAMoveTheHistoryRefusesIsParkedWithItsReason()
    {
        var (people, car, summer, winter, summerInstallation) = await Car(_app);
        var installation = Guid.NewGuid().ToString();
        await people.Alice.Gql(SwapOnline, new { i = new { vehicleId = car, setId = winter, day = "2026-09-20", pairOneFront = true, odometer = 148_200, installationId = installation } });
        var line = LineOf(await TyresOf(people.Alice, car), installation);
        Assert.Equal(("SWAP", installation), (line.GetProperty("kind").GetString(), line.GetProperty("installationId").GetString()));

        var moved = await Send(people.Alice, new { id = Guid.NewGuid(), expectedVersion = line.GetProperty("version").GetInt32(),
            updateTyreEntry = new { id = installation, kind = "SWAP", setId = winter, day = "2026-09-22", odometer = 148_200 } });
        var before = await Send(people.Alice, new { id = Guid.NewGuid(), updateTyreEntry = new { id = installation, kind = "SWAP", setId = winter, day = "2026-03-28" } });

        Assert.Equal(("APPLIED", null), Result(moved));
        Assert.Equal(("PARKED", "tyres.swapBeforePrevious"), Result(before));
        var tyres = await TyresOf(people.Alice, car);
        Assert.Equal("2026-09-22", Assert.Single(InstallationsOf(tyres, winter)).GetProperty("from").GetString());
        Assert.Equal("2026-09-22", InstallationsOf(tyres, summer).Single(x => x.GetProperty("id").GetString() == summerInstallation).GetProperty("until").GetString());
        Assert.Equal(("2026-09-22", 148_200L), (LineOf(tyres, installation).GetProperty("day").GetString(), LineOf(tyres, installation).GetProperty("odometer").GetInt64()));
    }

    [Fact]
    public async Task AJoiningSwapWithAReading_SentAgainAsANewChange_WritesItsNoteLineOnce()
    {
        var (people, car, summer, winter, _) = await Car(_app);
        await people.Alice.Gql(SwapOnline, new { i = new { vehicleId = car, setId = winter, day = "2026-06-01", pairOneFront = true, installationId = Guid.NewGuid() } });
        await people.Alice.Gql(SwapOnline, new { i = new { vehicleId = car, setId = summer, day = "2026-08-01", pairOneFront = true, installationId = Guid.NewGuid() } });
        var installation = Guid.NewGuid().ToString();
        var joining = new { vehicleId = car, setId = summer, day = "2026-06-01", pairOneFront = true, replace = true, odometer = 143_000, note = "Joined", installationId = installation };

        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new { id = Guid.NewGuid(), swapTyres = joining })));
        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new { id = Guid.NewGuid(), swapTyres = joining }))); // the answer never arrived: sent again

        var lines = (await TyresOf(people.Alice, car)).GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("day").GetString() == "2026-06-01").ToList();
        var note = Assert.Single(lines);
        Assert.Equal(("NOTE", installation, 143_000L), (note.GetProperty("kind").GetString(), note.GetProperty("id").GetString(), note.GetProperty("odometer").GetInt64()));
    }

    [Fact]
    public async Task AnAmountSentFromADevice_NamesTheExpenseItMakes()
    {
        var (people, car, summer, _, _) = await Car(_app);
        var sent = await people.Alice.Gql(Sync, new { i = new { changes = new object[] { new { id = Guid.NewGuid(), addTyreEntry = new {
            vehicleId = car, kind = "OTHER", setId = summer, day = "2026-09-20", id = Guid.NewGuid(), fee = new { amount = 10m, currency = "EUR", title = "Other" } } } } } });
        Assert.Equal("sync.entityIdRequired", Key(sent));
    }

    [Fact]
    public async Task ALineWithItsLongestAccentedNoteTitleAndCategory_FitsOneChangeOfTheQueue()
    {
        var (people, car, summer, _, _) = await Car(_app);
        var (line, expense) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var fee = new { amount = 123456.78m, currency = "HUF", category = new string('ő', 60), title = new string('ű', 120), expenseId = expense };

        var sent = await Send(people.Alice, new { id = Guid.NewGuid(), addTyreEntry = new {
            vehicleId = car, kind = "PUNCTURE_REPAIRED", setId = summer, day = "2026-09-20", odometer = 148_200, note = new string('é', 500), id = line, fee } });

        Assert.Equal(("APPLIED", null), Result(sent));
        Assert.Equal(new string('ű', 120), LineOf(await TyresOf(people.Alice, car), line).GetProperty("note").GetString() is not null
            ? (await people.Alice.Gql("query($id: UUID!) { expense(id: $id) { title } }", new { id = expense })).Data().GetProperty("expense").GetProperty("title").GetString()
            : null);
    }

    [Fact]
    public async Task ASwapOntoASetOnTheCarSinceEarlier_IsParked_UnlessItsPairsAreAsAsked_WhichARetryOfAJoinedSwapIs()
    {
        var (people, car, _, winter, _) = await Car(_app);
        await people.Alice.Gql(SwapOnline, new { i = new { vehicleId = car, setId = winter, day = "2026-10-28", pairOneFront = true, installationId = Guid.NewGuid() } });
        var late = Guid.NewGuid().ToString();

        var otherPairs = await Send(people.Alice, new { id = late, swapTyres = new { vehicleId = car, setId = winter, day = "2026-11-02", pairOneFront = false, odometer = 149_000, installationId = Guid.NewGuid() } });
        var asAsked = await Send(people.Alice, new { id = Guid.NewGuid(), swapTyres = new { vehicleId = car, setId = winter, day = "2026-11-02", pairOneFront = true, installationId = Guid.NewGuid() } });
        var fromPapers = await Send(people.Alice, new { id = Guid.NewGuid(), addPastTyreSwaps = new { vehicleId = car, swaps = new object[] { new { day = "2026-11-03", setId = winter, installationId = Guid.NewGuid() } } } });

        Assert.Equal(("PARKED", "tyres.alreadyOnCar"), Result(otherPairs)); // a swap of its own that came too late: its pairs are not dropped
        Assert.Equal(("APPLIED", null), Result(asAsked));
        Assert.Equal(("PARKED", "tyres.alreadyOnCar"), Result(fromPapers));
        var installation = Assert.Single(InstallationsOf(await TyresOf(people.Alice, car), winter));
        Assert.Equal(("2026-10-28", true), (installation.GetProperty("from").GetString(), installation.GetProperty("pairOneFront").GetBoolean()));
        Assert.DoesNotContain((await TyresOf(people.Alice, car)).GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("odometer").ValueKind != JsonValueKind.Null); // the parked swap's reading was not written either

        // Applied anyway it is no retry: refused as online.
        Assert.Equal("tyres.alreadyOnCar", Key(await people.Alice.Gql(Resolve, new { i = new { id = late, action = "APPLY" } })));
    }

    [Fact]
    public async Task ATrashOfAInstallation_WhoseSetWasTrashedMeanwhile_IsApplied_ThereIsNothingLeftToDo()
    {
        var (people, car, summer, _, installation) = await Car(_app);
        await people.Alice.Gql("mutation($id: UUID!) { deleteTyreSet(id: $id) { vehicleId } }", new { id = summer });

        var sent = await Send(people.Alice, new { id = Guid.NewGuid(), deleteTyreInstallation = installation });

        Assert.Equal(("APPLIED", null), Result(sent));
        Assert.Equal(car, (await people.Alice.Gql("{ tyreTrash(skip: 0, take: 10) { vehicle { id } } }")).Data().GetProperty("tyreTrash")[0].GetProperty("vehicle").GetProperty("id").GetString());
    }

    [Fact]
    public async Task PositionsAlreadyAsAsked_AreRefusedOnline_AndARetrysAnswerOnlyFromTheQueue()
    {
        var (people, car, summer, _, _) = await Car(_app);
        object Same() => new { vehicleId = car, setId = summer, day = "2026-03-28", pairOneFront = true, installationId = Guid.NewGuid() };

        var online = await people.Alice.Gql("mutation($i: SwapTyresInput!) { changeTyrePositions(input: $i) { vehicleId } }", new { i = Same() });
        var queued = await Send(people.Alice, new { id = Guid.NewGuid(), changeTyrePositions = Same() });

        Assert.Equal("tyres.positionsUnchanged", Key(online));
        Assert.Equal(("APPLIED", null), Result(queued));
    }

    [Fact]
    public async Task AParkedChangeOfAInstallationOfATrashedSet_IsInTheTrash_ForAnEditorAndTheOwner_AndUnseenOnceTheSendersAccessEnds()
    {
        var (people, car, summer, _, installation) = await Car(_app);
        var parked = await Send(people.Bob, new { id = Guid.NewGuid(), expectedVersion = 99, updateTyreInstallation = new { id = installation, from = "2026-03-28", pairOneFront = false } });
        Assert.Equal(("PARKED", "sync.versionMismatch"), Result(parked));
        await people.Alice.Gql("mutation($id: UUID!) { deleteTyreSet(id: $id) { vehicleId } }", new { id = summer });
        const string current = "{ parkedChanges { kind current { state tyreInstallation { from } } } }";

        var editor = (await people.Bob.Gql(current)).Data().GetProperty("parkedChanges")[0].GetProperty("current");
        await people.Alice.Gql("mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }", new { i = new { vehicleId = car, userId = people.BobId, level = "NONE" } });
        var ended = (await people.Bob.Gql(current)).Data().GetProperty("parkedChanges")[0].GetProperty("current");
        var owner = (await people.Alice.Gql(current)).Data().GetProperty("parkedChanges")[0].GetProperty("current");

        Assert.Equal(("TRASHED", "2026-03-28"), (editor.GetProperty("state").GetString(), editor.GetProperty("tyreInstallation").GetProperty("from").GetString()));
        Assert.Equal(JsonValueKind.Null, ended.ValueKind); // his access ended: his change is still his, nothing of what is there now
        Assert.Equal("TRASHED", owner.GetProperty("state").GetString());
    }

    [Fact]
    public async Task PastSwaps_AreAtMost25_AndTwoRowsWithOneId_AreRefusedWithAKey_SoTheChangeIsParked()
    {
        var (people, car, _, winter, _) = await Car(_app);
        var many = Enumerable.Range(0, 26).Select(i => new { day = new DateOnly(2024, 1, 1).AddDays(i * 7).ToString("yyyy-MM-dd"), setId = winter, installationId = Guid.NewGuid() }).ToArray();
        var twice = Guid.NewGuid();

        var tooMany = await people.Alice.Gql("mutation($i: AddPastTyreSwapsInput!) { addPastTyreSwaps(input: $i) { vehicleId } }", new { i = new { vehicleId = car, swaps = many } });
        var sameId = await Send(people.Alice, new
        {
            id = Guid.NewGuid(),
            addPastTyreSwaps = new { vehicleId = car, swaps = new object[] { new { day = "2025-03-29", setId = winter, installationId = twice }, new { day = "2025-11-15", setId = winter, installationId = twice } } },
        });

        Assert.Equal("tyres.pastSwapsTooMany", Key(tooMany));
        Assert.Equal(("PARKED", "tyres.installationIdTwice"), Result(sameId));
    }

    [Fact]
    public async Task TheTyreTrash_IsCountedForAnEditor_ButEmptiedOnlyByWhoeverMayDelete()
    {
        var (people, _, _, _, installation) = await Car(_app);
        await people.Alice.Gql("mutation($id: UUID!) { deleteTyreInstallation(id: $id) { vehicleId } }", new { id = installation });
        const string counts = "{ tyreTrashCount tyreTrashDeletableCount }";

        var alice = (await people.Alice.Gql(counts)).Data();
        var bob = (await people.Bob.Gql(counts)).Data();

        Assert.Equal((1, 1), (alice.GetProperty("tyreTrashCount").GetInt32(), alice.GetProperty("tyreTrashDeletableCount").GetInt32()));
        Assert.Equal((1, 0), (bob.GetProperty("tyreTrashCount").GetInt32(), bob.GetProperty("tyreTrashDeletableCount").GetInt32()));
    }

    [Fact]
    public async Task AParkedInstallationApplied_WithASetOfAnotherVehicle_IsRefused_AsAnotherChange()
    {
        var (people, _, summer, _, _) = await Car(_app);
        var (other, otherSet) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await Send(people.Alice,
            new { id = Guid.NewGuid(), addVehicle = new { id = other, name = "Fabia", fuelType = "PETROL" } },
            new { id = Guid.NewGuid(), addTyreSet = new { id = otherSet, vehicleId = other, name = "Nokian", season = "WINTER", since = "2022-11-19", fitted = false, day = "2026-03-28" } });
        var (change, installation) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var parked = await Send(people.Alice, new { id = change, addTyreInstallation = new { id = installation, setId = summer, from = "2026-04-01", until = "2026-05-01" } });
        Assert.Equal(("PARKED", "tyres.setOnTwice"), Result(parked)); // the summer set is on the car then already

        var moved = await people.Alice.Gql(Resolve, new { i = new { id = change, action = "APPLY", change = new { id = change, addTyreInstallation = new { id = installation, setId = otherSet, from = "2026-04-01", until = "2026-05-01" } } } });

        Assert.Equal("sync.kindMismatch", Key(moved));
    }

    [Fact]
    public async Task TwentyFivePastSwaps_WithTheirInstallationIds_FitOneChangeOfTheQueue()
    {
        var (people, car, summer, winter, _) = await Car(_app);
        // The most a change carries: 25 rows, alternating sets, a reading on every one (#288: installationId is longer than stretchId was).
        var rows = Enumerable.Range(0, 25).Select(i => new
        {
            day = new DateOnly(2020, 1, 1).AddDays(i * 30).ToString("yyyy-MM-dd"), setId = i % 2 == 0 ? winter : summer, odometer = 100_000L + i * 1000, installationId = Guid.NewGuid(),
        }).ToArray();

        var sent = await Send(people.Alice, new { id = Guid.NewGuid(), addPastTyreSwaps = new { vehicleId = car, replace = false, swaps = rows } });

        Assert.Equal(("APPLIED", null), Result(sent));
    }

    [Fact]
    public async Task ATyresRelabelFromAnOlderVersion_IsParkedWithTheTyreAsItIsNow_AndARestoreOntoATakenCornerWithItsReason()
    {
        var (people, car, summer, _, installation) = await Car(_app);
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        string[] corners = ["FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT"];
        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new
        {
            id = Guid.NewGuid(), breakUpTyreSet = new { setId = summer, day = "2026-09-01", tyres = tyres.Select((id, n) => new { id, label = $"S{n + 1}", corner = corners[n] }) },
        })));

        Assert.Equal(("APPLIED", null), Result(await Send(people.Bob, new { id = Guid.NewGuid(), expectedVersion = 1, updateTyre = new { id = tyres[0], label = "S1b" } })));
        Assert.Equal(("PARKED", "sync.versionMismatch"), Result(await Send(people.Alice, new { id = Guid.NewGuid(), expectedVersion = 1, updateTyre = new { id = tyres[0], label = "S1a" } })));
        var parked = (await people.Alice.Gql("{ parkedChanges { kind current { state tyre { label } } } }")).Data().GetProperty("parkedChanges")[0];
        Assert.Equal(("UPDATE_TYRE", "S1b"), (parked.GetProperty("kind").GetString(), parked.GetProperty("current").GetProperty("tyre").GetProperty("label").GetString()));

        // S2's time on the car goes to the trash; S1 takes its corner; S2's restore, sent from a device, is parked with the reason.
        var s2Place = Tankstat.Domain.Tyres.TyreIds.PlaceOf(tyres[1], Guid.Parse(installation));
        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new { id = Guid.NewGuid(), deleteTyrePlace = s2Place })));
        var s1Place = Tankstat.Domain.Tyres.TyreIds.PlaceOf(tyres[0], Guid.Parse(installation));
        Assert.Equal(("APPLIED", null), Result(await Send(people.Alice, new { id = Guid.NewGuid(), updateTyrePlace = new { id = s1Place, from = "2026-03-28", corner = "FRONT_RIGHT" } })));
        Assert.Equal(("PARKED", "tyres.tyreRestoreConflict"), Result(await Send(people.Alice, new { id = Guid.NewGuid(), restoreTyrePlace = s2Place })));
    }
}
