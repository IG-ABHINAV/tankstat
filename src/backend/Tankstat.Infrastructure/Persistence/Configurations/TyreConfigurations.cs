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
        // Unlike a log, a trashed set is read by its own queries (its stretches hide through it), so there is no query filter.
    }
}

internal sealed class TyreStretchConfiguration : IEntityTypeConfiguration<TyreStretch>
{
    public void Configure(EntityTypeBuilder<TyreStretch> b)
    {
        b.ToTable("TyreStretches");
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

internal sealed class TyreLogDayConfiguration : IEntityTypeConfiguration<TyreLogDay>
{
    public void Configure(EntityTypeBuilder<TyreLogDay> b)
    {
        b.ToTable("TyreLogDays");
        SyncMapping.MapUpdatedAt(b);
        b.HasIndex(d => new { d.VehicleId, d.UpdatedAt });
        b.HasKey(d => d.Id);
        b.Property(d => d.Id).ValueGeneratedNever();
        b.Property(d => d.Note).HasMaxLength(TyreLogDay.MaxNoteLength);
        b.HasOne<Vehicle>().WithMany().HasForeignKey(d => d.VehicleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(d => new { d.VehicleId, d.Day }).IsUnique(); // one line per vehicle and day
        b.HasIndex(d => d.OwnerId);
        // The reading is owned by the day; clearing it, or purging the vehicle's tyres, removes it in code (as an expense's).
        b.HasOne(d => d.OdometerReading).WithOne().HasForeignKey<TyreLogDay>(d => d.OdometerReadingId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(d => d.IsEmpty);
    }
}
