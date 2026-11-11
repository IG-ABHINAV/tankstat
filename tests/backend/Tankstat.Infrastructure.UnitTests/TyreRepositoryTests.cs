using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tankstat.Application.Access;
using Tankstat.Application.Odometers;
using Tankstat.Application.Tyres;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Sync;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;
using Tankstat.Infrastructure.Persistence;
using Tankstat.TestSupport;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>The tyre tables on a real database: one save per change, the day's reading among the car's readings, the trash across two kinds.</summary>
public class TyreRepositoryTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private TestDatabase Database() => new(services: s => s.AddSingleton<TimeProvider>(_clock));

    private static async Task<Vehicle> Car(TestDatabase db, Guid? owner = null)
    {
        var car = TestData.Vehicle(owner ?? Owner);
        await db.Get<IVehicleRepository>().AddAsync(car, default);
        return car;
    }

    private static TyreSet Set(Vehicle car, string name = "Michelin", Guid? by = null) =>
        TyreSet.Create(car.OwnerId, by ?? car.OwnerId, car.Id, name, TyreSeason.Summer, null, D(2023, 3, 1), null, null, 0, null);

    private static async Task<(TyreSet Set, TyreInstallation Installation, TyreLogDay Day)> SetOnTheCar(TestDatabase db, Vehicle car, long? odometer = 148_200)
    {
        var set = Set(car);
        var installation = TyreInstallation.Create(car.OwnerId, car.OwnerId, car.Id, set.Id, D(2026, 3, 28), null, true);
        var day = TyreLogDay.Create(car.OwnerId, car.OwnerId, car.Id, D(2026, 3, 28), odometer, "At the garage");
        var save = new TyreSave();
        save.AddedSets.Add(set);
        save.AddedInstallations.Add(installation);
        save.AddedDays.Add(day);
        if (day.OdometerReading is { } reading) save.AddedReadings.Add(reading);
        await db.Get<ITyreRepository>().SaveAsync(save, default);
        return (set, installation, day);
    }

    [Fact]
    public async Task ASave_RoundTrips_AndTheDaysReading_IsOneOfTheCarsReadings()
    {
        await using var db = Database();
        var car = await Car(db);
        await SetOnTheCar(db, car);

        var book = await db.Get<ITyreRepository>().LoadAsync(car.Id, default);

        Assert.Equal("Michelin", Assert.Single(book.Sets).Name);
        Assert.Equal((D(2026, 3, 28), true), (Assert.Single(book.Installations).From, book.Installations[0].PairOneFront));
        Assert.Equal((148_200L, "At the garage"), (Assert.Single(book.Days).Odometer!.Value, book.Days[0].Note));
        Assert.Equal([new ReadingPoint(D(2026, 3, 28), 148_200)], book.Readings);
        Assert.Equal(148_200, (await db.Get<OdometerService>().LatestAsync(car.Id, default))!.Value);
        Assert.Equal(_clock.GetUtcNow(), book.Sets[0].UpdatedAt);
    }

    [Fact]
    public async Task ClearingADay_DeletesItsReading_ForGood()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        await SetOnTheCar(db, car);
        var day = (await repo.LoadAsync(car.Id, default)).Days[0];

        var (_, removed) = day.Update(null, null);
        var save = new TyreSave();
        save.RemovedDays.Add(day);
        save.RemovedReadings.Add(removed!);
        await repo.SaveAsync(save, default);

        var book = await repo.LoadAsync(car.Id, default);
        Assert.Empty(book.Days);
        Assert.Empty(book.Readings);
    }

    [Fact]
    public async Task TheSameAdd_SavedTwice_IsAnsweredFalse_AndSavesNothingOfTheSecond()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        var (set, installation, _) = await SetOnTheCar(db, car, odometer: null);
        // The second request built the same installation (same id) and a day line of its own.
        var again = TyreInstallation.Create(car.OwnerId, car.OwnerId, car.Id, set.Id, installation.From, null, true, installation.Id);
        var day = TyreLogDay.Create(car.OwnerId, car.OwnerId, car.Id, D(2026, 9, 1), 150_000, null);
        var save = new TyreSave();
        save.AddedInstallations.Add(again);
        save.AddedDays.Add(day);
        save.AddedReadings.Add(day.OdometerReading!);

        Assert.False(await repo.SaveAsync(save, default));

        var book = await repo.LoadAsync(car.Id, default);
        Assert.Single(book.Installations);
        Assert.DoesNotContain(book.Days, d => d.Day == D(2026, 9, 1)); // rolled back as a whole
        Assert.Empty(book.Readings);
    }

    [Fact]
    public async Task AStaleInstallation_IsNotSaved()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        await SetOnTheCar(db, car);
        var mine = (await repo.LoadAsync(car.Id, default)).Installations[0];
        var theirs = (await repo.LoadAsync(car.Id, default)).Installations[0];

        theirs.Update(theirs.From, D(2026, 9, 1), theirs.PairOneFront);
        var first = new TyreSave();
        first.ChangedInstallations.Add(theirs);
        await repo.SaveAsync(first, default);
        mine.Update(mine.From, D(2026, 9, 5), mine.PairOneFront);
        var second = new TyreSave();
        second.ChangedInstallations.Add(mine);

        Assert.Equal("sync.versionMismatch", (await Assert.ThrowsAsync<DomainException>(() => repo.SaveAsync(second, default))).Key);
    }

    [Fact]
    public async Task TheTrash_ListsTrashedSets_AndTrashedInstallationsOfLiveSets_NewestFirst_AndPurgingLeavesTombstones()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        var (kept, keptInstallation, _) = await SetOnTheCar(db, car, odometer: null);
        var gone = Set(car, "Pirelli");
        var goneInstallation = TyreInstallation.Create(Owner, Owner, car.Id, gone.Id, D(2025, 11, 15), D(2026, 3, 28), null);
        var add = new TyreSave();
        add.AddedSets.Add(gone);
        add.AddedInstallations.Add(goneInstallation);
        await repo.SaveAsync(add, default);

        keptInstallation = (await repo.FindInstallationAsync(keptInstallation.Id, default))!;
        keptInstallation.MarkDeleted(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
        gone = (await repo.FindSetAsync(gone.Id, default))!;
        gone.MarkDeleted(_clock.GetUtcNow());
        var trash = new TyreSave();
        trash.ChangedInstallations.Add(keptInstallation);
        trash.ChangedSets.Add(gone);
        await repo.SaveAsync(trash, default);

        var rows = await repo.ListTrashAsync(OwnerScope.All, 0, 10, default);
        Assert.Equal([(TyreTrashKind.Set, "Pirelli", 1), (TyreTrashKind.Installation, "Michelin", 0)], rows.Select(r => (r.Kind, r.SetName, r.InstallationCount)));
        Assert.Equal((D(2026, 3, 28), (DateOnly?)null), (rows[1].From, rows[1].Until));
        Assert.Equal([TyreTrashKind.Installation, TyreTrashKind.Set], (await repo.ListTrashAsync(OwnerScope.All, 0, 10, default, descending: false)).Select(r => r.Kind));
        Assert.Equal(2, await repo.CountTrashAsync(OwnerScope.All, default));
        Assert.Single(await repo.ListTrashAsync(OwnerScope.All, 1, 10, default));
        Assert.Equal(0, await repo.CountTrashAsync(OwnerScope.Of([Guid.NewGuid()]), default));

        Assert.Equal(2, await repo.PurgeAsync(OwnerScope.All, default));
        await using var ctx = await db.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Equal([kept.Id], ctx.TyreSets.Select(s => s.Id));
        Assert.Empty(ctx.TyreInstallations);
        (OfflineEntityType, Guid)[] expected = [(OfflineEntityType.TyreSet, gone.Id), (OfflineEntityType.TyreInstallation, goneInstallation.Id), (OfflineEntityType.TyreInstallation, keptInstallation.Id)];
        Assert.Equal(expected.Order(), ctx.Tombstones.Select(t => new { t.EntityType, t.EntityId }).AsEnumerable().Select(t => (t.EntityType, t.EntityId)).Order());
    }

    [Fact]
    public async Task PurgingTheVehicle_TakesItsTyres_AndAMovedUsersRowsShowTheirNewOwner()
    {
        await using var db = Database();
        var users = db.Get<IUserRepository>();
        var alice = User.CreateLocal("alice@x.co", null, false);
        var carol = User.CreateLocal("carol@x.co", null, false);
        await users.AddAsync(alice, default);
        await users.AddAsync(carol, default);
        var car = await Car(db, alice.Id);
        await SetOnTheCar(db, car);

        await db.Get<IUserDataRepository>().DeleteUserAsync(alice.Id, carol.Id, default);
        var moved = await db.Get<ITyreRepository>().LoadAsync(car.Id, default);
        Assert.Equal((carol.Id, carol.Id, carol.Id, carol.Id),
            (moved.Sets[0].OwnerId, moved.Sets[0].CreatedById, moved.Installations[0].OwnerId, moved.Days[0].OwnerId));

        var vehicles = db.Get<IVehicleRepository>();
        var loaded = (await vehicles.FindAsync(car.Id, default))!;
        loaded.MarkDeleted(_clock.GetUtcNow());
        await vehicles.UpdateAsync(loaded, default);
        await vehicles.PurgeAsync(OwnerScope.All, default);

        var after = await db.Get<ITyreRepository>().LoadAsync(car.Id, default);
        Assert.Empty(after.Sets);
        Assert.Empty(after.Days);
        Assert.Empty(after.Readings);
    }
}
