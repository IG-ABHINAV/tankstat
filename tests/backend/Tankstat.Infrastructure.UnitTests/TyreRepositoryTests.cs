using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tankstat.Application.Access;
using Tankstat.Application.Odometers;
using Tankstat.Application.Tyres;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;
using Tankstat.Application.Expenses;
using Tankstat.Domain;
using Tankstat.Domain.Measurements;
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

    private static async Task<(TyreSet Set, TyreInstallation Installation, TyreEntry Line)> SetOnTheCar(TestDatabase db, Vehicle car, long? odometer = 148_200)
    {
        var set = Set(car);
        var installation = TyreInstallation.Create(car.OwnerId, car.OwnerId, car.Id, set.Id, D(2026, 3, 28), null, true);
        var line = TyreEntry.ForInstallation(installation, car.OwnerId);
        var (reading, _, _) = line.Update(line.Kind, line.SetId, line.Day, odometer, "At the garage");
        var save = new TyreSave();
        save.AddedSets.Add(set);
        save.AddedInstallations.Add(installation);
        save.AddedEntries.Add(line);
        if (reading is not null) save.AddedReadings.Add(reading);
        await db.Get<ITyreRepository>().SaveAsync(save, default);
        return (set, installation, line);
    }

    [Fact]
    public async Task ASave_RoundTrips_AndTheLinesReading_IsOneOfTheCarsReadings()
    {
        await using var db = Database();
        var car = await Car(db);
        await SetOnTheCar(db, car);

        var book = await db.Get<ITyreRepository>().LoadAsync(car.Id, default);

        Assert.Equal("Michelin", Assert.Single(book.Sets).Name);
        Assert.Equal((D(2026, 3, 28), true), (Assert.Single(book.Installations).From, book.Installations[0].PairOneFront));
        var line = Assert.Single(book.Entries);
        Assert.Equal((TyreEntryKind.Swap, book.Installations[0].Id, 148_200L, "At the garage"), (line.Kind, line.InstallationId!.Value, line.Odometer!.Value, line.Note));
        Assert.Equal([new ReadingPoint(D(2026, 3, 28), 148_200)], book.Readings);
        Assert.Equal(148_200, (await db.Get<OdometerService>().LatestAsync(car.Id, default))!.Value);
        Assert.Equal(_clock.GetUtcNow(), book.Sets[0].UpdatedAt);
    }

    [Fact]
    public async Task ClearingALinesReading_DeletesIt_ForGood()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        await SetOnTheCar(db, car);
        var line = (await repo.LoadAsync(car.Id, default)).Entries[0];

        var (_, removed, _) = line.Update(line.Kind, line.SetId, line.Day, null, line.Note);
        var save = new TyreSave();
        save.ChangedEntries.Add(line);
        save.RemovedReadings.Add(removed!);
        await repo.SaveAsync(save, default);

        var book = await repo.LoadAsync(car.Id, default);
        Assert.Null(Assert.Single(book.Entries).OdometerReading);
        Assert.Empty(book.Readings);
        await using var ctx = await db.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Empty(ctx.OdometerReadings.IgnoreQueryFilters());
    }

    [Fact]
    public async Task ATrashedLine_TakesItsReadingOutOfTheCarsReadings_AndBringsItBack()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        await SetOnTheCar(db, car);
        var line = (await repo.LoadAsync(car.Id, default)).Entries[0];

        line.MarkDeleted(_clock.GetUtcNow());
        var trash = new TyreSave();
        trash.ChangedEntries.Add(line);
        await repo.SaveAsync(trash, default);
        var trashed = await repo.LoadAsync(car.Id, default);
        Assert.Empty(trashed.Readings);
        Assert.Equal(148_200, Assert.Single(trashed.Entries).Odometer); // the reading is in the trash with it

        var back = trashed.Entries[0];
        back.Restore();
        var restore = new TyreSave();
        restore.ChangedEntries.Add(back);
        await repo.SaveAsync(restore, default);
        Assert.Equal([new ReadingPoint(D(2026, 3, 28), 148_200)], (await repo.LoadAsync(car.Id, default)).Readings);
    }

    [Fact]
    public async Task AReadingHandedToANoteLine_StaysOneRow()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        await SetOnTheCar(db, car);
        var line = (await repo.LoadAsync(car.Id, default)).Entries[0];

        var kept = TyreEntry.Keeping(line, car.OwnerId);
        var save = new TyreSave();
        save.AddedEntries.Add(kept);
        save.ChangedEntries.Add(line);
        await repo.SaveAsync(save, default);

        var book = await repo.LoadAsync(car.Id, default);
        var note = book.Entries.Single(e => e.Kind == TyreEntryKind.Note);
        Assert.Equal((148_200L, "At the garage"), (note.Odometer!.Value, note.Note));
        Assert.Null(book.Entries.Single(e => e.Kind == TyreEntryKind.Swap).OdometerReading);
        Assert.Single(book.Readings);
    }

    [Fact]
    public async Task TheSameAdd_SavedTwice_IsAnsweredFalse_AndSavesNothingOfTheSecond()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        var (set, installation, _) = await SetOnTheCar(db, car, odometer: null);
        // The second request built the same installation (same id) and a line of its own.
        var again = TyreInstallation.Create(car.OwnerId, car.OwnerId, car.Id, set.Id, installation.From, null, true, installation.Id);
        var line = TyreEntry.Create(car.OwnerId, car.OwnerId, car.Id, TyreEntryKind.Note, null, D(2026, 9, 1), 150_000, null);
        var save = new TyreSave();
        save.AddedInstallations.Add(again);
        save.AddedEntries.Add(line);
        save.AddedReadings.Add(line.OdometerReading!);

        Assert.False(await repo.SaveAsync(save, default));

        var book = await repo.LoadAsync(car.Id, default);
        Assert.Single(book.Installations);
        Assert.DoesNotContain(book.Entries, e => e.Day == D(2026, 9, 1)); // rolled back as a whole
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
    public async Task TheTrash_ListsTrashedSets_AndTrashedLinesOfLiveSets_NewestFirst_AndPurgingLeavesTombstones()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        var (kept, keptInstallation, keptLine) = await SetOnTheCar(db, car);
        var gone = Set(car, "Pirelli");
        var goneInstallation = TyreInstallation.Create(Owner, Owner, car.Id, gone.Id, D(2025, 11, 15), D(2026, 3, 28), null);
        var goneLine = TyreEntry.ForInstallation(goneInstallation, Owner);
        var loose = TyreEntry.Create(Owner, Owner, car.Id, TyreEntryKind.Other, null, D(2026, 5, 2), null, "Wheel bolts");
        var add = new TyreSave();
        add.AddedSets.Add(gone);
        add.AddedInstallations.Add(goneInstallation);
        add.AddedEntries.Add(goneLine);
        add.AddedEntries.Add(loose);
        await repo.SaveAsync(add, default);

        keptInstallation = (await repo.FindInstallationAsync(keptInstallation.Id, default))!;
        keptInstallation.MarkDeleted(_clock.GetUtcNow());
        keptLine = (await repo.FindEntryAsync(keptLine.Id, default))!;
        keptLine.MarkDeleted(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
        gone = (await repo.FindSetAsync(gone.Id, default))!;
        gone.MarkDeleted(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
        loose = (await repo.FindEntryAsync(loose.Id, default))!;
        loose.MarkDeleted(_clock.GetUtcNow());
        var trash = new TyreSave();
        trash.ChangedInstallations.Add(keptInstallation);
        trash.ChangedEntries.Add(keptLine);
        trash.ChangedEntries.Add(loose);
        trash.ChangedSets.Add(gone);
        await repo.SaveAsync(trash, default);

        var rows = await repo.ListTrashAsync(OwnerScope.All, 0, 10, default);
        Assert.Equal([(TyreTrashKind.Entry, null, TyreEntryKind.Other, 0), (TyreTrashKind.Set, "Pirelli", null, 1), (TyreTrashKind.Entry, "Michelin", TyreEntryKind.Swap, 0)],
            rows.Select(r => (r.Kind, r.SetName, r.EntryKind, r.InstallationCount)));
        Assert.Equal((D(2026, 3, 28), (DateOnly?)null), (rows[2].Day, rows[2].Until));
        Assert.Equal([TyreTrashKind.Entry, TyreTrashKind.Set, TyreTrashKind.Entry], (await repo.ListTrashAsync(OwnerScope.All, 0, 10, default, descending: false)).Select(r => r.Kind));
        Assert.Equal(3, await repo.CountTrashAsync(OwnerScope.All, default));
        Assert.Equal(2, (await repo.ListTrashAsync(OwnerScope.All, 1, 10, default)).Count);
        Assert.Equal(0, await repo.CountTrashAsync(OwnerScope.Of([Guid.NewGuid()]), default));

        Assert.Equal(3, await repo.PurgeAsync(OwnerScope.All, default));
        await using var ctx = await db.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Equal([kept.Id], ctx.TyreSets.Select(s => s.Id));
        Assert.Empty(ctx.TyreInstallations);
        Assert.Empty(ctx.TyreEntries);
        Assert.Empty(ctx.OdometerReadings.IgnoreQueryFilters()); // the trashed line's reading went with it
        (OfflineEntityType, Guid)[] expected = [(OfflineEntityType.TyreSet, gone.Id), (OfflineEntityType.TyreInstallation, goneInstallation.Id), (OfflineEntityType.TyreInstallation, keptInstallation.Id),
            (OfflineEntityType.TyreEntry, goneLine.Id), (OfflineEntityType.TyreEntry, keptLine.Id), (OfflineEntityType.TyreEntry, loose.Id)];
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
            (moved.Sets[0].OwnerId, moved.Sets[0].CreatedById, moved.Installations[0].OwnerId, moved.Entries[0].OwnerId));

        var vehicles = db.Get<IVehicleRepository>();
        var loaded = (await vehicles.FindAsync(car.Id, default))!;
        loaded.MarkDeleted(_clock.GetUtcNow());
        await vehicles.UpdateAsync(loaded, default);
        await vehicles.PurgeAsync(OwnerScope.All, default);

        var after = await db.Get<ITyreRepository>().LoadAsync(car.Id, default);
        Assert.Empty(after.Sets);
        Assert.Empty(after.Entries);
        Assert.Empty(after.Readings);
    }

    /// <summary>A line with an expense of its amount (#288): one reading row, held by both.</summary>
    private static async Task<(TyreEntry Line, Expense Expense)> LineWithExpense(TestDatabase db, Vehicle car)
    {
        var line = TyreEntry.Create(car.OwnerId, car.OwnerId, car.Id, TyreEntryKind.PunctureRepaired, null, D(2026, 9, 20), 148_200, "Rear left");
        var expense = Expense.Create(car.OwnerId, car.OwnerId, car.Id, line.Day, "Puncture repaired", "Tyres", Cost.Create(car.OwnerId, car.Id, line.Day, 25, "EUR"), line.OdometerReading);
        line.LinkExpense(expense.Id);
        var save = new TyreSave();
        save.AddedEntries.Add(line);
        save.AddedReadings.Add(line.OdometerReading!);
        save.AddedExpenses.Add(expense);
        Assert.True(await db.Get<ITyreRepository>().SaveAsync(save, default));
        return (line, expense);
    }

    [Fact]
    public async Task ALineAndItsExpense_AreSavedTogether_HoldingOneReadingRow_LoadedAsOneObject()
    {
        await using var db = Database();
        var car = await Car(db);
        var (line, expense) = await LineWithExpense(db, car);

        var book = await db.Get<ITyreRepository>().LoadAsync(car.Id, default);
        var loaded = book.Entries.Single(e => e.Id == line.Id);
        var linked = Assert.Single(book.LinkedExpenses);
        Assert.Equal(expense.Id, linked.Id);
        Assert.Same(loaded.OdometerReading, linked.OdometerReading); // one object: the two are saved as one row
        Assert.Equal([new ReadingPoint(D(2026, 9, 20), 148_200)], book.Readings);
        Assert.Equal(25m, (await db.Get<IExpenseRepository>().FindAsync(expense.Id, default))!.Amount);
        Assert.Equal(line.Id, Assert.Single(await db.Get<ITyreRepository>().ListEntriesByExpenseAsync([expense.Id], default)).Id);

        // A change of the two saved together: the line's day and reading, the expense following.
        loaded.Update(loaded.Kind, null, D(2026, 9, 21), 148_300, null);
        linked.FollowLine(loaded.Day, loaded.OdometerReading);
        var save = new TyreSave();
        save.ChangedEntries.Add(loaded);
        save.ChangedExpenses.Add(linked);
        await db.Get<ITyreRepository>().SaveAsync(save, default);
        var after = await db.Get<IExpenseRepository>().FindAsync(expense.Id, default);
        Assert.Equal((D(2026, 9, 21), (long?)148_300, D(2026, 9, 21)), (after!.Date, after.Odometer, after.Cost!.Date));
    }

    [Fact]
    public async Task AnExpenseLeftWithoutItsAmount_TakesTheOneItsLineGivesIt_AsANewCostRow()
    {
        await using var db = Database();
        var car = await Car(db);
        var (line, expense) = await LineWithExpense(db, car);
        var repo = db.Get<ITyreRepository>();

        // The Expenses tab left the amount to a photo being read: the cost row goes.
        var book = await repo.LoadAsync(car.Id, default);
        var (loaded, linked) = (book.Entries.Single(e => e.Id == line.Id), Assert.Single(book.LinkedExpenses));
        var emptied = new TyreSave();
        emptied.ExpenseChanges.Add(linked.UpdateWithLineReading(linked.Date, linked.Title, linked.Category, null, null, loaded.OdometerReading, null, car.OwnerId, null, readingPhotos: true));
        emptied.Change(linked);
        Assert.True(await repo.SaveAsync(emptied, default));
        Assert.Null((await db.Get<IExpenseRepository>().FindAsync(expense.Id, default))!.Amount);

        // The amount typed on the line: a cost the expense did not have, saved as a new row.
        book = await repo.LoadAsync(car.Id, default);
        (loaded, linked) = (book.Entries.Single(e => e.Id == line.Id), Assert.Single(book.LinkedExpenses));
        Assert.True(linked.FollowLine(loaded.Day, loaded.OdometerReading, 40, "EUR", "Tyres", setCategory: true));
        var typed = new TyreSave();
        typed.ExpenseChanges.Add(new LinkedChanges(null, null, linked.Cost, null));
        typed.Change(linked);
        Assert.True(await repo.SaveAsync(typed, default));

        var after = await db.Get<IExpenseRepository>().FindAsync(expense.Id, default);
        Assert.Equal((40m, "EUR", loaded.Day, (long?)148_200), (after!.Amount!.Value, after.Currency, after.Cost!.Date, after.Odometer));
    }

    [Fact]
    public async Task PurgingTheLine_LeavesTheExpenseWithTheReading_AndPurgingTheExpense_LeavesTheLineWithItButUnlinked()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        var (line, expense) = await LineWithExpense(db, car);
        var (other, otherExpense) = await LineWithExpense(db, car);

        // The first line purged: its expense, live, keeps the reading.
        var first = (await repo.LoadAsync(car.Id, default)).Entries.Single(e => e.Id == line.Id);
        first.MarkDeleted(_clock.GetUtcNow());
        first.OdometerReading!.Restore(); // still held by the live expense
        var trash = new TyreSave();
        trash.ChangedEntries.Add(first);
        await repo.SaveAsync(trash, default);
        await repo.PurgeAsync(OwnerScope.All, default);
        Assert.Equal(148_200, (await db.Get<IExpenseRepository>().FindAsync(expense.Id, default))!.Odometer);

        // The second line's expense purged: the line keeps its reading, without the link.
        var expenses = db.Get<IExpenseRepository>();
        var gone = (await expenses.FindAsync(otherExpense.Id, default))!;
        gone.MarkDeleted(_clock.GetUtcNow());
        gone.OdometerReading!.Restore(); // the live line holds it
        await expenses.UpdateAsync(gone, LinkedChanges.None, default);
        await expenses.PurgeAsync(OwnerScope.All, default);
        var kept = (await repo.LoadAsync(car.Id, default)).Entries.Single(e => e.Id == other.Id);
        Assert.Equal((148_200L, (Guid?)null), (kept.Odometer!.Value, kept.ExpenseId));
    }

    [Fact]
    public async Task WhatAnExpenseShowsOfItsLine_Changing_MarksTheExpenseForDownload_ThoughItIsNotSavedItself()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        var (set, _, _) = await SetOnTheCar(db, car, odometer: null);
        var line = TyreEntry.Create(car.OwnerId, car.OwnerId, car.Id, TyreEntryKind.PunctureRepaired, set.Id, D(2026, 9, 20), null, "Rear left");
        var expense = Expense.Create(car.OwnerId, car.OwnerId, car.Id, line.Day, "Puncture repaired", "Tyres", Cost.Create(car.OwnerId, car.Id, line.Day, 25, "EUR"), null);
        line.LinkExpense(expense.Id);
        var added = new TyreSave();
        added.AddedEntries.Add(line);
        added.AddedExpenses.Add(expense);
        Assert.True(await repo.SaveAsync(added, default));
        var (otherLine, other) = await LineWithExpense(db, car); // a line of no set, which nothing below touches until it is purged
        var created = _clock.GetUtcNow();
        async Task<DateTimeOffset> Saved(Expense e) => (await db.Get<IExpenseRepository>().FindAsync(e.Id, default))!.UpdatedAt;
        async Task<TyreEntry> Line(TyreEntry e) => (await repo.LoadAsync(car.Id, default)).Entries.Single(l => l.Id == e.Id);

        // The line goes to the trash alone: the expense stays, and now says its line is in the trash.
        _clock.Advance(TimeSpan.FromMinutes(5));
        var trashed = await Line(line);
        trashed.MarkDeleted(_clock.GetUtcNow());
        var trash = new TyreSave();
        trash.ChangedEntries.Add(trashed);
        Assert.True(await repo.SaveAsync(trash, default));
        Assert.Equal((_clock.GetUtcNow(), created), (await Saved(expense), await Saved(other)));

        // The set renamed: the expense names its line's set.
        _clock.Advance(TimeSpan.FromMinutes(5));
        var renamed = (await repo.LoadAsync(car.Id, default)).Sets.Single(s => s.Id == set.Id);
        renamed.Update("Michelin Primacy 4", renamed.Season, renamed.Size, renamed.Since, renamed.MadeWeek, renamed.MadeYear);
        var rename = new TyreSave();
        rename.ChangedSets.Add(renamed);
        Assert.True(await repo.SaveAsync(rename, default));
        Assert.Equal((_clock.GetUtcNow(), created), (await Saved(expense), await Saved(other)));

        // The line comes back without it: the expense no longer shows a line.
        _clock.Advance(TimeSpan.FromMinutes(5));
        var back = await Line(line);
        back.Restore();
        back.UnlinkExpense();
        var restore = new TyreSave();
        restore.ChangedEntries.Add(back);
        Assert.True(await repo.SaveAsync(restore, default));
        Assert.Equal((_clock.GetUtcNow(), created), (await Saved(expense), await Saved(other)));

        // A line purged: its expense stays, without a line.
        var gone = await Line(otherLine);
        gone.MarkDeleted(_clock.GetUtcNow());
        gone.OdometerReading!.Restore(); // still held by the live expense
        var purged = new TyreSave();
        purged.ChangedEntries.Add(gone);
        Assert.True(await repo.SaveAsync(purged, default));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await repo.PurgeAsync(OwnerScope.All, default);
        Assert.Equal(_clock.GetUtcNow(), await Saved(other));
        Assert.Empty(await repo.ListEntriesByExpenseAsync([other.Id], default));
    }

    /// <summary>A set on the car broken up into four tyres (#289), each with a place in its installation.</summary>
    private static async Task<(TyreSet Set, TyreInstallation Installation, IReadOnlyList<Tyre> Tyres, IReadOnlyList<TyrePlace> Places)> BrokenUp(TestDatabase db, Vehicle car)
    {
        var (set, installation, _) = await SetOnTheCar(db, car);
        var history = new TyreHistory(car.OwnerId, car.Id, [set], [installation]);
        var rules = new TyrePlaces(car.OwnerId, car.Id, history, [set], [], []);
        var made = rules.BreakUp(set, [new(null, "W1", TyreCorner.FrontLeft), new(null, "W2", TyreCorner.FrontRight), new(null, "W3", TyreCorner.RearLeft), new(null, "W4", null)],
            D(2026, 9, 1), car.OwnerId);
        var save = new TyreSave();
        save.ChangedSets.Add(set);
        save.Take(made);
        Assert.True(await db.Get<ITyreRepository>().SaveAsync(save, default));
        return (set, installation, made.CreatedTyres, made.Created);
    }

    [Fact]
    public async Task ABrokenUpSet_RoundTrips_WithItsTyresAndTheirPlaces()
    {
        await using var db = Database();
        var car = await Car(db);
        var (set, installation, tyres, _) = await BrokenUp(db, car);

        var book = await db.Get<ITyreRepository>().LoadAsync(car.Id, default);

        Assert.Equal(D(2026, 9, 1), Assert.Single(book.Sets).BrokenUpOn);
        Assert.Equal(["W1", "W2", "W3", "W4"], book.SingleTyres.OrderBy(t => t.Label).Select(t => t.Label));
        Assert.All(book.SingleTyres, t => Assert.Equal(set.Id, t.SetId));
        Assert.Equal([TyreCorner.FrontLeft, TyreCorner.FrontRight, TyreCorner.RearLeft, (TyreCorner?)null],
            book.SingleTyres.OrderBy(t => t.Label).Select(t => book.TyrePlaces.Single(p => p.TyreId == t.Id).Corner));
        Assert.All(book.TyrePlaces, p => Assert.Equal((installation.Id, D(2026, 3, 28), (DateOnly?)null), (p.InstallationId, p.From, p.Until)));
        Assert.Equal(tyres.Select(t => t.Id).Order(), (await db.Get<ITyreRepository>().ListTyresAsync([.. tyres.Select(t => t.Id)], default)).Select(t => t.Id).Order());
    }

    [Fact]
    public async Task TheTrash_ListsATrashedTyre_AndATrashedTimeOnTheCar_AndPurgingTakesThemWithTombstones()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        var (_, _, tyres, places) = await BrokenUp(db, car);
        var w1 = tyres.Single(t => t.Label == "W1");
        var w2Place = places.Single(p => p.TyreId == tyres.Single(t => t.Label == "W2").Id);
        w1.MarkDeleted(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
        w2Place.MarkDeleted(_clock.GetUtcNow());
        var trash = new TyreSave();
        trash.ChangedTyres.Add(w1);
        trash.ChangedPlaces.Add(w2Place);
        Assert.True(await repo.SaveAsync(trash, default));

        var rows = await repo.ListTrashAsync(OwnerScope.All, 0, 10, default);
        Assert.Equal([(TyreTrashKind.Place, "W2", (TyreCorner?)TyreCorner.FrontRight, "Michelin"), (TyreTrashKind.Tyre, "W1", null, "Michelin")],
            rows.Select(r => (r.Kind, r.Label, r.Corner, r.SetName)));
        Assert.Equal((D(2026, 3, 28), (DateOnly?)null), (rows[0].Day, rows[0].Until));

        Assert.Equal(2, await repo.PurgeAsync(OwnerScope.All, default));
        await using var ctx = await db.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Equal(["W2", "W3", "W4"], ctx.Tyres.Select(t => t.Label).AsEnumerable().Order());
        Assert.Equal(2, ctx.TyrePlaces.Count()); // W1's went with it, W2's alone
        (OfflineEntityType, Guid)[] expected = [(OfflineEntityType.Tyre, w1.Id), (OfflineEntityType.TyrePlace, places.Single(p => p.TyreId == w1.Id).Id), (OfflineEntityType.TyrePlace, w2Place.Id)];
        Assert.Equal(expected.Order(), ctx.Tombstones.Select(t => new { t.EntityType, t.EntityId }).AsEnumerable().Select(t => (t.EntityType, t.EntityId)).Order());
    }

    [Fact]
    public async Task PurgingATrashedSet_TakesItsTyresAndPlaces_AndAMovedUsersTyresShowTheirNewOwner()
    {
        await using var db = Database();
        var users = db.Get<IUserRepository>();
        var alice = User.CreateLocal("alice@x.co", null, false);
        var carol = User.CreateLocal("carol@x.co", null, false);
        await users.AddAsync(alice, default);
        await users.AddAsync(carol, default);
        var car = await Car(db, alice.Id);
        var (set, _, _, _) = await BrokenUp(db, car);
        await db.Get<IUserDataRepository>().DeleteUserAsync(alice.Id, carol.Id, default);
        var moved = await db.Get<ITyreRepository>().LoadAsync(car.Id, default);
        Assert.All(moved.SingleTyres, t => Assert.Equal((carol.Id, carol.Id), (t.OwnerId, t.CreatedById)));
        Assert.All(moved.TyrePlaces, p => Assert.Equal(carol.Id, p.OwnerId));

        set = moved.Sets.Single();
        set.MarkDeleted(_clock.GetUtcNow());
        var trash = new TyreSave();
        trash.ChangedSets.Add(set);
        await db.Get<ITyreRepository>().SaveAsync(trash, default);
        Assert.Equal(1, await db.Get<ITyreRepository>().PurgeAsync(OwnerScope.All, default));

        var after = await db.Get<ITyreRepository>().LoadAsync(car.Id, default);
        Assert.Empty(after.SingleTyres);
        Assert.Empty(after.TyrePlaces);
    }

    [Fact]
    public async Task LinesMadeTogether_SaveAndLoadOneReadingRow_WithTheirExpense_AndPurgingOneKeepsTheRowTheOtherHolds()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        var (set, _, tyres, _) = await BrokenUp(db, car);
        var group = Guid.NewGuid();
        var first = TyreEntry.Create(car.OwnerId, car.OwnerId, car.Id, TyreEntryKind.PunctureRepaired, set.Id, D(2026, 9, 20), 148_200, null, group, tyres[2].Id, group);
        var second = first.ForAnotherTyre(tyres[3].Id, car.OwnerId);
        var expense = Expense.Create(car.OwnerId, car.OwnerId, car.Id, first.Day, "Puncture repaired", "Tyres", Cost.Create(car.OwnerId, car.Id, first.Day, 13_000, "HUF"), first.OdometerReading);
        first.LinkExpense(expense.Id);
        second.LinkExpense(expense.Id);
        var save = new TyreSave();
        save.AddedEntries.Add(first);
        save.AddedEntries.Add(second);
        save.AddedReadings.Add(first.OdometerReading!);
        save.AddedExpenses.Add(expense);
        Assert.True(await repo.SaveAsync(save, default));

        var book = await repo.LoadAsync(car.Id, default);
        var (a, b) = (book.Entries.Single(e => e.Id == first.Id), book.Entries.Single(e => e.Id == second.Id));
        Assert.Same(a.OdometerReading, b.OdometerReading); // one row, one object
        Assert.Same(a.OdometerReading, book.ExpenseOf(a)!.OdometerReading);
        await using (var ctx = await db.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
            Assert.Equal(1, ctx.OdometerReadings.Count(r => r.Id == first.OdometerReadingId));

        // A save of both after a change of the shared reading writes it once.
        a.Update(a.Kind, a.SetId, a.Day, 148_300, a.Note);
        b.Update(b.Kind, b.SetId, b.Day, 148_300, b.Note);
        var change = new TyreSave();
        change.ChangedEntries.Add(a);
        change.ChangedEntries.Add(b);
        Assert.True(await repo.SaveAsync(change, default));

        // One line to the trash and purged: the row the other line and the expense hold stays.
        a = (await repo.LoadAsync(car.Id, default)).Entries.Single(e => e.Id == first.Id);
        a.MarkDeleted(_clock.GetUtcNow());
        var trash = new TyreSave();
        trash.ChangedEntries.Add(a);
        await repo.SaveAsync(trash, default);
        await repo.PurgeAsync(OwnerScope.All, default);
        await using var after = await db.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Equal(148_300, after.OdometerReadings.IgnoreQueryFilters().Single(r => r.Id == first.OdometerReadingId).Value);
        Assert.Equal(first.OdometerReadingId, after.TyreEntries.Single(e => e.Id == second.Id).OdometerReadingId);
    }

    [Fact]
    public async Task ATrashedTyre_ListsAloneInTheTrash_AndItsPurgeTakesItsLines_KeepingTheReadingAnotherTyresLineHolds()
    {
        await using var db = Database();
        var car = await Car(db);
        var repo = db.Get<ITyreRepository>();
        var (set, _, tyres, _) = await BrokenUp(db, car);
        var group = Guid.NewGuid();
        var first = TyreEntry.Create(car.OwnerId, car.OwnerId, car.Id, TyreEntryKind.PunctureRepaired, set.Id, D(2026, 9, 20), 148_200, null, group, tyres[2].Id, group);
        var second = first.ForAnotherTyre(tyres[3].Id, car.OwnerId);
        var add = new TyreSave();
        add.AddedEntries.Add(first);
        add.AddedEntries.Add(second);
        add.AddedReadings.Add(first.OdometerReading!);
        Assert.True(await repo.SaveAsync(add, default));

        var tyre = (await repo.LoadAsync(car.Id, default)).SingleTyres.Single(t => t.Id == tyres[2].Id);
        tyre.MarkDeleted(_clock.GetUtcNow());
        var trash = new TyreSave();
        trash.ChangedTyres.Add(tyre);
        await repo.SaveAsync(trash, default);
        Assert.Equal([TyreTrashKind.Tyre], (await repo.ListTrashAsync(OwnerScope.All, 0, 10, default)).Select(r => r.Kind));

        Assert.Equal(1, await repo.PurgeAsync(OwnerScope.All, default));
        await using var after = await db.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Equal([second.Id], after.TyreEntries.IgnoreQueryFilters().Where(e => e.GroupId == group).Select(e => e.Id).ToList());
        Assert.Equal(148_200, after.OdometerReadings.IgnoreQueryFilters().Single(r => r.Id == first.OdometerReadingId).Value);
    }
}
