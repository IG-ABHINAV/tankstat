using Tankstat.Domain.Tyres;

namespace Tankstat.Domain.UnitTests;

/// <summary>A line of the tyre log (#288): its kinds, a swap's locked line, its reading.</summary>
public class TyreEntryTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Car = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static TyreEntry Line(TyreEntryKind kind = TyreEntryKind.PunctureRepaired, long? odometer = 148_200) =>
        TyreEntry.Create(Owner, Owner, Car, kind, Guid.NewGuid(), D(2026, 5, 2), odometer, " Rear left ");

    [Theory]
    [InlineData(TyreEntryKind.Swap)]
    [InlineData(TyreEntryKind.PositionsChanged)]
    public void ASwapOrAChangeOfPositions_IsNeverALineOfItsOwn(TyreEntryKind kind)
    {
        Assert.Equal("tyres.entryKindNotAllowed", Assert.Throws<DomainException>(() => Line(kind)).Key);
        var line = Line();
        Assert.Equal("tyres.entryKindNotAllowed", Assert.Throws<DomainException>(() => line.Update(kind, line.SetId, line.Day, null, null)).Key);
    }

    [Fact]
    public void ALine_HoldsItsReading_OnItsDay_AndATrimmedNote()
    {
        var line = Line();

        Assert.Equal((148_200L, D(2026, 5, 2), "Rear left", 1), (line.Odometer!.Value, line.OdometerReading!.Date, line.Note, line.Version));
    }

    [Fact]
    public void ANewDay_MovesTheReading_AndClearingItLetsGoOfIt()
    {
        var line = Line();
        var reading = line.OdometerReading!;

        var (created, removed, changed) = line.Update(TyreEntryKind.ValveReplaced, null, D(2026, 5, 4), 148_300, "Valve");
        Assert.Equal((null, null, true), (created, removed, changed));
        Assert.Equal((D(2026, 5, 4), 148_300L, (Guid?)null, TyreEntryKind.ValveReplaced), (reading.Date, reading.Value, line.SetId, line.Kind));

        var (_, gone, _) = line.Update(line.Kind, null, line.Day, null, "Valve");
        Assert.Same(reading, gone);
        Assert.Null(line.OdometerReadingId);
        Assert.False(line.Update(line.Kind, null, line.Day, null, "Valve").Changed); // nothing changed: not saved again
    }

    [Fact]
    public void ASwapsLine_KeepsItsKindAndSet_ButTakesAReadingAndANote()
    {
        var installation = TyreInstallation.Create(Owner, Owner, Car, Guid.NewGuid(), D(2026, 3, 28), null, true);
        var line = TyreEntry.ForInstallation(installation, Owner);
        Assert.Equal((installation.Id, (Guid?)installation.Id, (Guid?)installation.SetId, D(2026, 3, 28), TyreEntryKind.Swap),
            (line.Id, line.InstallationId, line.SetId, line.Day, line.Kind));

        Assert.Equal("tyres.swapLineLocked", Assert.Throws<DomainException>(() => line.Update(TyreEntryKind.Other, line.SetId, line.Day, null, null)).Key);
        Assert.Equal("tyres.swapLineLocked", Assert.Throws<DomainException>(() => line.Update(line.Kind, Guid.NewGuid(), line.Day, null, null)).Key);
        Assert.True(line.Update(line.Kind, line.SetId, line.Day, 148_200, "At the garage").Changed);
    }

    [Fact]
    public void InTheTrash_ItsReadingIsInTheTrashToo_AndItCannotBeChanged()
    {
        var line = Line();

        line.MarkDeleted(Now);
        Assert.Equal(Now, line.OdometerReading!.DeletedAt);
        Assert.Equal("tyres.entryTrashedCannotEdit", Assert.Throws<DomainException>(() => line.Update(line.Kind, line.SetId, line.Day, null, null)).Key);
        Assert.Equal("tyres.entryAlreadyTrashed", Assert.Throws<DomainException>(() => line.MarkDeleted(Now)).Key);

        line.Restore();
        Assert.Null(line.OdometerReading.DeletedAt);
        Assert.Equal("tyres.entryNotTrashed", Assert.Throws<DomainException>(() => line.Restore()).Key);
    }

    [Fact]
    public void ALineKeptForItsDay_TakesTheReadingAndNote_AndTheLineKeepsNeither()
    {
        var installation = TyreInstallation.Create(Owner, Owner, Car, Guid.NewGuid(), D(2026, 3, 28), null, true);
        var line = TyreEntry.ForInstallation(installation, Owner);
        line.Update(line.Kind, line.SetId, line.Day, 148_200, "At the garage");
        var reading = line.OdometerReading;

        var kept = TyreEntry.Keeping(line, Owner);

        Assert.Equal((TyreEntryKind.Note, line.SetId, D(2026, 3, 28), "At the garage", (Guid?)null), (kept.Kind, kept.SetId, kept.Day, kept.Note, kept.InstallationId));
        Assert.Same(reading, kept.OdometerReading);
        Assert.Equal((null, null), (line.OdometerReading, line.Note));
    }
}
