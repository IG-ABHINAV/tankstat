using Tankstat.Application;
using Tankstat.Application.Auth;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

public class RefuelingServiceTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    private static RefuelingInput Input(
        DateOnly? date = null, decimal volume = 40, decimal cost = 60, string currency = "EUR", long odometer = 1000, bool full = true, string? note = null, decimal? discount = null) =>
        new(date ?? Day, volume, cost, currency, odometer, full, note, Discount: discount);

    /// <summary>Alice owns a car; Bob and Carol exist; Alice is signed in.</summary>
    private sealed record Scene(World W, User Alice, User Bob, User Carol, Vehicle Car);

    private static async Task<Scene> Setup()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        var carol = w.AddUser("carol@x.co");
        w.Current.SignInAs(alice);
        var car = await w.VehicleService.AddAsync("Alice car", null, FuelType.Petrol, default);
        return new Scene(w, alice, bob, carol, car);
    }

    private static Task<Refueling> Log(Scene s, RefuelingInput? input = null) => s.W.RefuelingService.LogAsync(s.Car.Id, input ?? Input(), default);

    // ---- basics --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Log_RecordsWhoLoggedIt_AndCreatesTheReadingAndCost()
    {
        var s = await Setup();

        var log = await Log(s, Input(volume: 41.5m, cost: 79.9m, currency: "huf", odometer: 12345, note: " Shell "));

        Assert.Equal((s.Alice.Id, s.Alice.Id, s.Car.Id), (log.OwnerId, log.CreatedById, log.VehicleId));
        Assert.Equal((41.5m, 79.9m, "HUF", 12345L, "Shell"), (log.Volume, log.TotalCost, log.Currency, log.Odometer, log.Note));
        Assert.Same(log, Assert.Single(s.W.Refuelings.Items));
    }

    [Fact]
    public async Task Log_StoresTheDiscount_AnUpdateWithoutOneKeepsIt_AndZeroClearsIt()
    {
        var s = await Setup();

        var log = await Log(s, Input(volume: 43, cost: 34744, currency: "HUF", discount: 430));
        Assert.Equal((34744m, 430m, 808m, 818m), (log.TotalCost, log.Discount, log.PricePerUnit, log.PricePerUnitBeforeDiscount));

        var kept = await s.W.RefuelingService.UpdateAsync(log.Id, Input(volume: 43, cost: 34744, currency: "HUF"), default); // a build from before discounts
        Assert.Equal(430m, kept.Discount);

        var cleared = await s.W.RefuelingService.UpdateAsync(log.Id, Input(volume: 43, cost: 34744, currency: "HUF", discount: 0), default);
        Assert.Null(cleared.Discount);
        Assert.Equal(808m, cleared.PricePerUnitBeforeDiscount);
    }

    [Fact]
    public async Task Log_RefusesADiscountWithoutATotal()
    {
        var s = await Setup();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Log(s, new RefuelingInput(Day, 40, null, "EUR", 1000, true, null, Discount: 5)));

        Assert.Equal("cost.discountWithoutAmount", ex.Key);
        Assert.Empty(s.W.Refuelings.Items);
    }

    [Fact]
    public async Task List_IsNewestFirst_AndCounts()
    {
        var s = await Setup();
        await Log(s, Input(new DateOnly(2026, 8, 1), odometer: 100));
        await Log(s, Input(new DateOnly(2026, 9, 1), odometer: 200));

        var list = await s.W.RefuelingService.ListAsync(s.Car.Id, new RefuelingQuery(), default);

        Assert.Equal([200L, 100L], list.Select(r => r.Odometer));
        Assert.Equal(2, await s.W.RefuelingService.CountAsync(s.Car.Id, default));
    }

    [Fact]
    public async Task Update_ChangesTheLog_AndItsReading()
    {
        var s = await Setup();
        var log = await Log(s);

        await s.W.RefuelingService.UpdateAsync(log.Id, Input(new DateOnly(2026, 9, 2), 30, 45, "USD", 2500, false, "x"), default);

        Assert.Equal((30m, 45m, "USD", 2500L, false), (log.Volume, log.TotalCost, log.Currency, log.Odometer, log.IsFullTank));
        Assert.Equal((new DateOnly(2026, 9, 2), 2500L), (log.OdometerReading!.Date, log.OdometerReading.Value));
    }

    [Fact]
    public async Task InvalidInput_IsRejected_AndNothingIsSaved()
    {
        var s = await Setup();

        await Assert.ThrowsAsync<DomainException>(() => Log(s, Input(volume: 0)));
        await Assert.ThrowsAsync<DomainException>(() => Log(s, Input(cost: -1)));
        await Assert.ThrowsAsync<DomainException>(() => Log(s, Input(currency: "EURO")));
        await Assert.ThrowsAsync<DomainException>(() => Log(s, Input(odometer: -1)));
        Assert.Empty(s.W.Refuelings.Items);
    }

    [Fact]
    public async Task DatesFurtherThanTomorrowAreRejected()
    {
        var s = await Setup();

        await Log(s, Input(new DateOnly(2026, 10, 2))); // tomorrow: fine for any time zone
        var e = await Assert.ThrowsAsync<DomainException>(() => Log(s, Input(new DateOnly(2026, 10, 3), odometer: 2000)));

        Assert.Equal("refueling.dateInFuture", e.Key);
    }

    // ---- the odometer ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Odometer_CannotGoBackwardsInTime()
    {
        var s = await Setup();
        await Log(s, Input(new DateOnly(2026, 9, 1), odometer: 1000));

        var e = await Assert.ThrowsAsync<DomainException>(() => Log(s, Input(new DateOnly(2026, 9, 10), odometer: 999)));

        Assert.Equal("odometer.belowPrevious", e.Key);
        Assert.Equal((1000L, "2026-09-01"), ((long)e.Args["previous"]!, e.Args["date"]));
    }

    [Fact]
    public async Task Odometer_CannotExceedALaterReading()
    {
        var s = await Setup();
        await Log(s, Input(new DateOnly(2026, 9, 10), odometer: 1500));

        var e = await Assert.ThrowsAsync<DomainException>(() => Log(s, Input(new DateOnly(2026, 9, 1), odometer: 1501)));

        Assert.Equal("odometer.aboveNext", e.Key);
    }

    [Fact]
    public async Task Odometer_CanEqualItsNeighbours_AndFillTheGapBetweenThem()
    {
        var s = await Setup();
        await Log(s, Input(new DateOnly(2026, 8, 1), odometer: 1000));
        await Log(s, Input(new DateOnly(2026, 9, 1), odometer: 2000));

        await Log(s, Input(new DateOnly(2026, 8, 15), odometer: 1000)); // equal to the earlier one
        await Log(s, Input(new DateOnly(2026, 8, 20), odometer: 1500)); // in between
        await Log(s, Input(new DateOnly(2026, 8, 20), odometer: 1000)); // same day readings do not constrain each other

        Assert.Equal(5, s.W.Refuelings.Items.Count);
    }

    [Fact]
    public async Task EditingALog_IsNotCheckedAgainstItself()
    {
        var s = await Setup();
        await Log(s, Input(new DateOnly(2026, 8, 1), odometer: 1000));
        var middle = await Log(s, Input(new DateOnly(2026, 8, 15), odometer: 1500));
        await Log(s, Input(new DateOnly(2026, 9, 1), odometer: 2000));

        await s.W.RefuelingService.UpdateAsync(middle.Id, Input(new DateOnly(2026, 8, 15), odometer: 1999), default);
        var e = await Assert.ThrowsAsync<DomainException>(() => s.W.RefuelingService.UpdateAsync(middle.Id, Input(new DateOnly(2026, 8, 15), odometer: 2001), default));

        Assert.Equal("odometer.aboveNext", e.Key);
    }

    [Fact]
    public async Task ReadingsOfOtherVehicles_DoNotMatter()
    {
        var s = await Setup();
        await Log(s, Input(odometer: 50_000));
        var bike = await s.W.VehicleService.AddAsync("Bike", null, FuelType.Petrol, default);

        await s.W.RefuelingService.LogAsync(bike.Id, Input(odometer: 10), default);

        Assert.Equal(2, s.W.Refuelings.Items.Count);
    }

    [Fact]
    public async Task TheOdometerHasNoUpperLimit()
    {
        var s = await Setup();

        var log = await Log(s, Input(odometer: 4_000_000_000_000));

        Assert.Equal(4_000_000_000_000, log.Odometer);
    }

    [Fact]
    public async Task ATrashedLogsReading_NoLongerConstrainsOthers_UntilRestored()
    {
        var s = await Setup();
        var high = await Log(s, Input(new DateOnly(2026, 9, 1), odometer: 5000));
        await s.W.RefuelingService.DeleteAsync(high.Id, default);

        await Log(s, Input(new DateOnly(2026, 9, 10), odometer: 100)); // would have been "below previous"

        var e = await Assert.ThrowsAsync<DomainException>(() => s.W.RefuelingService.RestoreAsync(high.Id, default)); // 5000 is now above the later 100
        Assert.Equal("odometer.aboveNext", e.Key);
    }

    [Fact]
    public async Task Defaults_SuggestTheLatestOdometerAndTheLastCurrency()
    {
        var s = await Setup();
        Assert.Equal(new RefuelingDefaults(null, null, null), await s.W.RefuelingService.DefaultsAsync(s.Car.Id, default));

        await Log(s, Input(new DateOnly(2026, 8, 1), odometer: 1000, currency: "EUR"));
        await Log(s, Input(new DateOnly(2026, 9, 1), odometer: 2000, currency: "HUF"));

        Assert.Equal(new RefuelingDefaults(2000, new DateOnly(2026, 9, 1), "HUF"), await s.W.RefuelingService.DefaultsAsync(s.Car.Id, default));
    }

    // ---- units ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Units_CanChangeWhileTheVehicleHasNoLogs_ThenAreLocked()
    {
        var s = await Setup();
        var us = MeasurementUnits.Create(DistanceUnit.Miles, VolumeUnit.UsGallons);

        var changed = await s.W.VehicleService.UpdateAsync(s.Car.Id, "Alice car", null, FuelType.Petrol, us, default);
        Assert.Equal(us, changed.Units);

        await Log(s);
        var e = await Assert.ThrowsAsync<DomainException>(() => s.W.VehicleService.UpdateAsync(s.Car.Id, "Alice car", null, FuelType.Petrol, MeasurementUnits.Metric, default));
        Assert.Equal("vehicle.unitsLocked", e.Key);

        // renaming (units omitted or unchanged) is still fine
        await s.W.VehicleService.UpdateAsync(s.Car.Id, "Renamed", null, FuelType.Petrol, null, default);
        await s.W.VehicleService.UpdateAsync(s.Car.Id, "Renamed again", null, FuelType.Petrol, us, default);
    }

    [Fact]
    public async Task Units_StayLocked_EvenWhenAllLogsAreTrashed()
    {
        var s = await Setup();
        var log = await Log(s);
        await s.W.RefuelingService.DeleteAsync(log.Id, default);

        await Assert.ThrowsAsync<DomainException>(() =>
            s.W.VehicleService.UpdateAsync(s.Car.Id, "Alice car", null, FuelType.Petrol, MeasurementUnits.Create(DistanceUnit.Miles, VolumeUnit.Liters), default)); // restoring would reinterpret the numbers
    }

    // ---- trash and permanent deletion ------------------------------------------------------------------------

    [Fact]
    public async Task Delete_MovesToTheTrash_Restore_BringsItBack()
    {
        var s = await Setup();
        var log = await Log(s);

        await s.W.RefuelingService.DeleteAsync(log.Id, default);
        Assert.Empty(await s.W.RefuelingService.ListAsync(s.Car.Id, new RefuelingQuery(), default));
        Assert.Same(log, Assert.Single(await s.W.RefuelingService.ListTrashAsync(new RefuelingQuery(), default)));
        Assert.Equal(1, await s.W.RefuelingService.CountTrashAsync(default));
        Assert.Null(await s.W.RefuelingService.FindAsync(log.Id, default));

        await s.W.RefuelingService.RestoreAsync(log.Id, default);
        Assert.Single(await s.W.RefuelingService.ListAsync(s.Car.Id, new RefuelingQuery(), default));
        Assert.Empty(await s.W.RefuelingService.ListTrashAsync(new RefuelingQuery(), default));
    }

    [Fact]
    public async Task EmptyTrash_RemovesTrashedLogsOnly_ForOwners()
    {
        var s = await Setup();
        var trashed = await Log(s, Input(odometer: 100));
        var kept = await Log(s, Input(new DateOnly(2026, 9, 2), odometer: 200));
        await s.W.RefuelingService.DeleteAsync(trashed.Id, default);

        Assert.Equal(1, await s.W.RefuelingService.CountDeletableTrashAsync(default));
        Assert.Equal(1, await s.W.RefuelingService.EmptyTrashAsync(default));

        Assert.Same(kept, Assert.Single(s.W.Refuelings.Items));
    }

    [Fact]
    public async Task EditLevel_MayTrashAndRestore_ButNotDeleteForGood()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.Edit));
        var log = await Log(s);
        s.W.Current.SignInAs(s.Bob);

        await s.W.RefuelingService.DeleteAsync(log.Id, default);
        Assert.Equal(1, await s.W.RefuelingService.CountTrashAsync(default));
        await s.W.RefuelingService.RestoreAsync(log.Id, default);
        await s.W.RefuelingService.DeleteAsync(log.Id, default);

        Assert.Equal(0, await s.W.RefuelingService.CountDeletableTrashAsync(default));
        Assert.Equal(0, await s.W.RefuelingService.EmptyTrashAsync(default));
        Assert.Single(s.W.Refuelings.Items); // still there
    }

    [Fact]
    public async Task DeleteLevel_MayEmptyTheTrash()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.Delete));
        var log = await Log(s);
        await s.W.RefuelingService.DeleteAsync(log.Id, default);
        s.W.Current.SignInAs(s.Bob);

        Assert.Equal(1, await s.W.RefuelingService.CountDeletableTrashAsync(default));
        Assert.Equal(1, await s.W.RefuelingService.EmptyTrashAsync(default));
        Assert.Empty(s.W.Refuelings.Items);
    }

    [Fact]
    public async Task Administrators_MayDeleteAnything()
    {
        var s = await Setup();
        var log = await Log(s);
        await s.W.RefuelingService.DeleteAsync(log.Id, default);
        s.W.Current.SignInAs(s.W.AddUser("root@x.co", admin: true));

        Assert.Equal(1, await s.W.RefuelingService.EmptyTrashAsync(default));
    }

    [Fact]
    public async Task VehiclesToo_NeedDeleteLevelToBeEmptiedFromTheTrash()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.Edit));
        await s.W.VehicleService.DeleteAsync(s.Car.Id, default);
        s.W.Current.SignInAs(s.Bob);

        Assert.Equal(1, await s.W.VehicleService.CountTrashAsync(default)); // may restore
        Assert.Equal(0, await s.W.VehicleService.CountDeletableTrashAsync(default));
        Assert.Equal(0, await s.W.VehicleService.EmptyTrashAsync(default));
        Assert.Single(s.W.Vehicles.Items);

        s.W.Grants.Items.Clear();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.Delete));
        Assert.Equal(1, await s.W.VehicleService.EmptyTrashAsync(default));
    }

    // ---- access to a vehicle's logs --------------------------------------------------------------------------

    [Fact]
    public async Task Strangers_SeeNothing_AndCannotLog()
    {
        var s = await Setup();
        var log = await Log(s);
        s.W.Current.SignInAs(s.Carol);

        Assert.Empty(await s.W.RefuelingService.ListAsync(s.Car.Id, new RefuelingQuery(), default));
        Assert.Equal(0, await s.W.RefuelingService.CountAsync(s.Car.Id, default));
        Assert.Null(await s.W.RefuelingService.FindAsync(log.Id, default));
        Assert.Null(await s.W.RefuelingService.DefaultsAsync(s.Car.Id, default));
        Assert.Equal(AccessLevel.None, await s.W.RefuelingService.LevelForVehicleAsync(s.Car.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Log(s));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.RefuelingService.UpdateAsync(log.Id, Input(), default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.RefuelingService.DeleteAsync(log.Id, default));
    }

    [Fact]
    public async Task ViewOnly_ReadsLogs_ButCannotChangeThem()
    {
        var s = await Setup();
        var log = await Log(s);
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));
        s.W.Current.SignInAs(s.Bob);

        Assert.Single(await s.W.RefuelingService.ListAsync(s.Car.Id, new RefuelingQuery(), default));
        await Assert.ThrowsAsync<ForbiddenException>(() => Log(s, Input(odometer: 5000)));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.W.RefuelingService.UpdateAsync(log.Id, Input(), default));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.W.RefuelingService.DeleteAsync(log.Id, default));
    }

    [Fact]
    public async Task EditOnTheVehicle_MeansEditOnItsLogs()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.Edit));
        s.W.Current.SignInAs(s.Bob);

        var log = await Log(s);

        Assert.Equal((s.Bob.Id, s.Alice.Id), (log.CreatedById, log.OwnerId)); // Bob logged it, it belongs to Alice's vehicle
        Assert.Equal(AccessLevel.Edit, await s.W.RefuelingService.LevelForVehicleAsync(s.Car.Id, default));
    }

    // ---- the per-vehicle log grant ---------------------------------------------------------------------------

    [Fact]
    public async Task ALogGrant_LetsAUserWorkWithTheLogs_WithoutEditingTheVehicle()
    {
        var s = await Setup();
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Edit, default);
        s.W.Current.SignInAs(s.Bob);

        // sees the vehicle (it shows up in their list) ...
        Assert.Same(s.Car, Assert.Single(await s.W.VehicleService.ListMineAsync(default)));
        Assert.Single(await s.W.VehicleService.ListMineAsync(default));
        Assert.NotNull(await s.W.VehicleService.FindAsync(s.Car.Id, default));
        // ... and its logs
        var log = await Log(s, Input(odometer: 3000));
        await s.W.RefuelingService.UpdateAsync(log.Id, Input(odometer: 3100), default);
        await s.W.RefuelingService.DeleteAsync(log.Id, default);
        Assert.Equal(AccessLevel.Edit, await s.W.RefuelingService.LevelForVehicleAsync(s.Car.Id, default));
        // ... but cannot touch the vehicle itself
        await Assert.ThrowsAsync<ForbiddenException>(() => s.W.VehicleService.UpdateAsync(s.Car.Id, "Hacked", null, FuelType.Lpg, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.W.VehicleService.DeleteAsync(s.Car.Id, default));
        Assert.Equal(0, await s.W.VehicleService.EmptyTrashAsync(default));
    }

    [Fact]
    public async Task ALogGrant_AtEditLevel_CannotDeleteForGood_ButDeleteLevelCan()
    {
        var s = await Setup();
        var log = await Log(s);
        await s.W.RefuelingService.DeleteAsync(log.Id, default);
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Edit, default);
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Carol.Id, AccessLevel.Delete, default);

        s.W.Current.SignInAs(s.Bob);
        Assert.Equal(1, await s.W.RefuelingService.CountTrashAsync(default)); // may restore
        Assert.Equal(0, await s.W.RefuelingService.EmptyTrashAsync(default));

        s.W.Current.SignInAs(s.Carol);
        Assert.Equal(1, await s.W.RefuelingService.EmptyTrashAsync(default));
    }

    [Fact]
    public async Task ALogGrant_AppliesToThatVehicleOnly()
    {
        var s = await Setup();
        var other = await s.W.VehicleService.AddAsync("Other car", null, FuelType.Diesel, default);
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Edit, default);
        s.W.Current.SignInAs(s.Bob);

        Assert.Null(await s.W.VehicleService.FindAsync(other.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.RefuelingService.LogAsync(other.Id, Input(), default));
        Assert.Single(await s.W.VehicleService.ListMineAsync(default));
    }

    [Fact]
    public async Task ALogGrant_AddsToWhatTheOwnerWideAccessAlreadyGives()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.Edit));
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Delete, default);
        s.W.Current.SignInAs(s.Bob);

        Assert.Equal(AccessLevel.Delete, await s.W.RefuelingService.LevelForVehicleAsync(s.Car.Id, default)); // the higher of the two
        Assert.Single(await s.W.VehicleService.ListMineAsync(default)); // not listed twice
    }

    [Fact]
    public async Task TheTrashOfLogs_OnlyShowsWhatTheUserMayRestore()
    {
        var s = await Setup();
        var log = await Log(s);
        await s.W.RefuelingService.DeleteAsync(log.Id, default);
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Edit, default);

        s.W.Current.SignInAs(s.Bob);
        Assert.Equal(1, await s.W.RefuelingService.CountTrashAsync(default));
        s.W.Current.SignInAs(s.Carol);
        Assert.Equal(0, await s.W.RefuelingService.CountTrashAsync(default));
    }

    [Fact]
    public async Task NoAuth_EveryoneCanDoEverything()
    {
        var w = new World(AuthMode.None);
        var car = await w.VehicleService.AddAsync("Shared", null, FuelType.Petrol, default);

        var log = await w.RefuelingService.LogAsync(car.Id, Day, 40, 60, 1000, true, default);
        await w.RefuelingService.DeleteAsync(log.Id, default);

        Assert.Equal(1, await w.RefuelingService.EmptyTrashAsync(default));
    }

    [Fact]
    public async Task Unauthenticated_CannotUseLogs()
    {
        var s = await Setup();
        s.W.Current.Principal = null;

        await Assert.ThrowsAsync<UnauthenticatedException>(() => s.W.RefuelingService.CountTrashAsync(default));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => Log(s));
    }
}

public class ResourceSharingTests
{
    private static async Task<(World W, User Alice, User Bob, User Carol, Vehicle Car)> Setup()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        var carol = w.AddUser("carol@x.co");
        w.Current.SignInAs(alice);
        return (w, alice, bob, carol, await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, default));
    }

    [Fact]
    public async Task Owner_SharesAndRevokes()
    {
        var (w, _, bob, _, car) = await Setup();

        await w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.Edit, default);
        var entry = Assert.Single(await w.Sharing.ListLogAccessAsync(car.Id, default));
        Assert.Equal((bob.Id, AccessLevel.Edit), (entry.User.Id, entry.Level));

        await w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.Delete, default); // upgrade, not duplicate
        Assert.Equal(AccessLevel.Delete, Assert.Single(await w.Sharing.ListLogAccessAsync(car.Id, default)).Level);

        await w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.None, default);
        Assert.Empty(await w.Sharing.ListLogAccessAsync(car.Id, default));
    }

    [Fact]
    public async Task Candidates_ExcludeTheOwner_TheCurrentUser_DisabledUsers_AndThoseWhoAlreadyHaveAccess()
    {
        var (w, _, bob, carol, car) = await Setup();
        var dave = w.AddUser("dave@x.co");
        dave.SetDisabled(true);
        await w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.Edit, default);

        var candidates = await w.Sharing.CandidatesAsync(car.Id, default);

        Assert.Equal([carol.Id], candidates.Select(u => u.Id));
    }

    [Fact]
    public async Task AnEditor_MayShare_ButNotMoreThanTheyHold()
    {
        var (w, alice, bob, carol, car) = await Setup();
        w.Grants.Items.Add(AccessGrant.Create(alice.Id, bob.Id, AccessLevel.Edit));
        w.Current.SignInAs(bob);

        await w.Sharing.SetLogAccessAsync(car.Id, carol.Id, AccessLevel.Edit, default);
        var e = await Assert.ThrowsAsync<ForbiddenException>(() => w.Sharing.SetLogAccessAsync(car.Id, carol.Id, AccessLevel.Delete, default));

        Assert.Equal("share.cannotGrantMore", e.Key);
    }

    [Fact]
    public async Task ALogGranteeAtDeleteLevel_CannotManageSharing()
    {
        var (w, _, bob, carol, car) = await Setup();
        await w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.Delete, default);
        w.Current.SignInAs(bob);

        var e = await Assert.ThrowsAsync<ForbiddenException>(() => w.Sharing.SetLogAccessAsync(car.Id, carol.Id, AccessLevel.Edit, default));
        Assert.Equal("share.editRequired", e.Key); // log access is not edit access to the vehicle
        await Assert.ThrowsAsync<ForbiddenException>(() => w.Sharing.ListLogAccessAsync(car.Id, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.Sharing.CandidatesAsync(car.Id, default));
    }

    [Fact]
    public async Task Administrators_MayShareAnything()
    {
        var (w, _, bob, _, car) = await Setup();
        w.Current.SignInAs(w.AddUser("root@x.co", admin: true));

        await w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.Delete, default);

        Assert.Single(await w.Sharing.ListLogAccessAsync(car.Id, default));
    }

    [Fact]
    public async Task StrangersAndViewers_CannotShare_OrEvenSeeWho()
    {
        var (w, alice, bob, carol, car) = await Setup();
        w.Current.SignInAs(carol);
        await Assert.ThrowsAsync<NotFoundException>(() => w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.Edit, default));

        w.Grants.Items.Add(AccessGrant.Create(alice.Id, carol.Id, AccessLevel.View));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.Edit, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.Sharing.ListLogAccessAsync(car.Id, default));
    }

    [Fact]
    public async Task Rules_OwnerNeedsNoGrant_UnknownUsersAndBadLevels()
    {
        var (w, alice, _, _, car) = await Setup();

        Assert.Equal("share.ownerHasAccess", (await Assert.ThrowsAsync<DomainException>(() => w.Sharing.SetLogAccessAsync(car.Id, alice.Id, AccessLevel.Edit, default))).Key);
        Assert.Equal("user.notFound", (await Assert.ThrowsAsync<NotFoundException>(() => w.Sharing.SetLogAccessAsync(car.Id, Guid.NewGuid(), AccessLevel.Edit, default))).Key);
        var bob = w.AddUser("b2@x.co");
        Assert.Equal("share.levelRequired", (await Assert.ThrowsAsync<DomainException>(() => w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.View, default))).Key);
    }

    [Fact]
    public async Task NoAuth_HasNoSharing_ButNothingIsNeededEither()
    {
        var w = new World(AuthMode.None);
        var car = await w.VehicleService.AddAsync("Shared", null, FuelType.Petrol, default);

        Assert.Empty(await w.Sharing.ListLogAccessAsync(car.Id, default));
        Assert.Empty(await w.Sharing.CandidatesAsync(car.Id, default));
    }
}
