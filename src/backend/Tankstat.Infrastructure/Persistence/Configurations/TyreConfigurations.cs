using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Infrastructure.Persistence.Configurations;

/// <summary>Stored as a UTC date-time so comparisons translate on every provider (as on vehicles and logs).</summary>
internal static class TrashColumn
{
    public static void Map<T>(EntityTypeBuilder<T> b, System.Linq.Expressions.Expression<Func<T, DateTimeOffset?>> deletedAt) where T : class =>
        b.Property(deletedAt).HasConversion(
            v => v == null ? (DateTime?)null : v.Value.UtcDateTime,
            v => v == null ? null : new DateTimeOffset(DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)));
}

internal sealed class TyreSetConfiguration : IEntityTypeConfiguration<TyreSet>
{
    public void Configure(EntityTypeBuilder<TyreSet> b)
    {
        b.ToTable("TyreSets");
        SyncMapping.MapUpdatedAt(b);
        b.HasIndex(s => new { s.VehicleId, s.UpdatedAt }); // the offline feed reads a vehicle's rows changed since a moment
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.Name).HasMaxLength(TyreSet.MaxNameLength).IsRequired();
        b.Property(s => s.Size).HasMaxLength(TyreSet.MaxSizeLength);
        b.Property(s => s.Season).HasConversion<string>().HasMaxLength(16);
        b.HasOne<Vehicle>().WithMany().HasForeignKey(s => s.VehicleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(s => s.OwnerId);
        TrashColumn.Map(b, s => s.DeletedAt);
        b.HasIndex(s => s.DeletedAt);
        // Unlike a log, a trashed set is read by its own queries (its installations hide through it), so there is no query filter.
    }
}

internal sealed class TyreInstallationConfiguration : IEntityTypeConfiguration<TyreInstallation>
{
    public void Configure(EntityTypeBuilder<TyreInstallation> b)
    {
        b.ToTable("TyreInstallations");
        SyncMapping.MapUpdatedAt(b);
        b.HasIndex(s => new { s.VehicleId, s.UpdatedAt });
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        // The set is the only cascading key: a key to the vehicle as well would give one vehicle delete two paths to these rows, which SQL
        // Server refuses. VehicleId is kept for access filtering, the feed and the user move.
        b.HasOne<TyreSet>().WithMany().HasForeignKey(s => s.SetId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(s => new { s.SetId, s.From });
        b.HasIndex(s => s.VehicleId);
        b.HasIndex(s => s.OwnerId);
        TrashColumn.Map(b, s => s.DeletedAt);
        b.HasIndex(s => s.DeletedAt);
    }
}

internal sealed class TyreEntryConfiguration : IEntityTypeConfiguration<TyreEntry>
{
    public void Configure(EntityTypeBuilder<TyreEntry> b)
    {
        b.ToTable("TyreEntries");
        SyncMapping.MapUpdatedAt(b);
        b.HasIndex(e => new { e.VehicleId, e.UpdatedAt });
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(e => e.Note).HasMaxLength(TyreEntry.MaxNoteLength);
        // The vehicle's is the only cascading key (#288): the set and the installation are plain columns, cleaned up in code, so SQL Server
        // never sees two cascade paths to these rows. A swap's line has its installation's id, which keeps one line per installation.
        b.HasOne<Vehicle>().WithMany().HasForeignKey(e => e.VehicleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => new { e.VehicleId, e.Day });
        b.HasIndex(e => e.SetId);
        b.HasIndex(e => e.OwnerId);
        b.HasIndex(e => e.ExpenseId); // an expense's line (its chip), and the link cleared when the expense is purged; no key, as for RecurringCompletion
        b.Ignore(e => e.TakesAmount);
        // The reading is owned by the line; clearing it, or purging the line, removes it in code (as an expense's).
        b.HasOne(e => e.OdometerReading).WithOne().HasForeignKey<TyreEntry>(e => e.OdometerReadingId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        TrashColumn.Map(b, e => e.DeletedAt);
        b.HasIndex(e => e.DeletedAt);
        b.Ignore(e => e.IsSwapLine);
    }
}
