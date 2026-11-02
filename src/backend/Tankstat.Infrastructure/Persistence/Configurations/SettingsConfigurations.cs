using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tankstat.Domain.Settings;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Infrastructure.Persistence.Configurations;

/// <summary>A list stored as JSON text, so the same mapping works on all four databases (grid columns, dashboard entries and tiles).</summary>
internal static class JsonListMapping
{
    /// <param name="maxLength">The column's length; null for no limit (the domain caps the count).</param>
    public static PropertyBuilder<IReadOnlyList<T>> AsJsonList<T>(this PropertyBuilder<IReadOnlyList<T>> property, int? maxLength = 4000)
    {
        property
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<T>>(v, (JsonSerializerOptions?)null) ?? new List<T>(),
                new ValueComparer<IReadOnlyList<T>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s)),
                    v => v.ToList()))
            .IsRequired();
        // 4000: 50 grid ids of 40 characters, or 60 `chart:<guid>` entries, quoted and separated.
        if (maxLength is { } max) property.HasMaxLength(max);
        return property;
    }
}

// The settings tables are keyed by the user id without a foreign key to Users: the anonymous user (authentication off) has no row there,
// and UserDataRepository deletes them with the user.

internal sealed class UiSettingsConfiguration : IEntityTypeConfiguration<UiSettings>
{
    public void Configure(EntityTypeBuilder<UiSettings> b)
    {
        b.ToTable("UiSettings");
        b.HasKey(s => s.UserId);
        b.Property(s => s.UserId).ValueGeneratedNever();
        b.Property(s => s.Language).HasMaxLength(UiSettings.MaxLanguageLength);
        b.Property(s => s.ColorMode).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.Surface).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.TimeFormat).HasConversion<string>().HasMaxLength(24);
        b.Property(s => s.DateFormat).HasConversion<string>().HasMaxLength(24);
        // Stored as a UTC date-time so comparisons translate on every provider (same as the other timestamps).
        b.Property(s => s.UpdatedAt).HasConversion(v => v.UtcDateTime, v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));
    }
}

internal sealed class GridSettingsConfiguration : IEntityTypeConfiguration<GridSettings>
{
    public void Configure(EntityTypeBuilder<GridSettings> b)
    {
        b.ToTable("GridSettings");
        b.HasKey(g => new { g.UserId, g.GridId });
        b.Property(g => g.GridId).HasMaxLength(GridSettings.MaxGridIdLength).IsRequired();
        // The column lists as JSON text, so the same mapping works on all four databases (like Notification.Args).
        b.Property(g => g.Order).AsJsonList();
        b.Property(g => g.Hidden).AsJsonList();
        b.Property(g => g.SortColumn).HasMaxLength(GridSettings.MaxColumnIdLength).IsRequired();
        b.Property(g => g.SortDirection).HasConversion<string>().HasMaxLength(32);
        b.Property(g => g.UpdatedAt).HasConversion(v => v.UtcDateTime, v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));
    }
}

internal sealed class VehicleOrderConfiguration : IEntityTypeConfiguration<VehicleOrder>
{
    public void Configure(EntityTypeBuilder<VehicleOrder> b)
    {
        b.ToTable("VehicleOrders");
        b.HasKey(o => new { o.UserId, o.VehicleId });
        b.HasOne<Vehicle>().WithMany().HasForeignKey(o => o.VehicleId).OnDelete(DeleteBehavior.Cascade); // a purged vehicle takes its positions along
        // No further index: the home list looks positions up by the key (UserId, VehicleId), nothing reads them by position.
    }
}
