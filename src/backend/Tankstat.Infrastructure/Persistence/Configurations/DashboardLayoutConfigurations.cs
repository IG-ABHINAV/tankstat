using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Infrastructure.Persistence.Configurations;

// A vehicle's dashboard and a person's own arrangement of it: two tables, since a missing user id cannot be part of a key and the anonymous
// user's id is all zeros. Both go with the vehicle; the personal ones are keyed by the user without a foreign key, like the settings, and
// UserDataRepository deletes them with the user.

internal sealed class VehicleDashboardLayoutConfiguration : IEntityTypeConfiguration<VehicleDashboardLayout>
{
    public void Configure(EntityTypeBuilder<VehicleDashboardLayout> b)
    {
        b.ToTable("VehicleDashboardLayouts");
        b.HasKey(l => l.VehicleId);
        b.Property(l => l.VehicleId).ValueGeneratedNever();
        b.HasOne<Vehicle>().WithMany().HasForeignKey(l => l.VehicleId).OnDelete(DeleteBehavior.Cascade);
        Lists(b);
    }

    internal static void Lists<T>(EntityTypeBuilder<T> b) where T : DashboardLayout
    {
        b.Property(l => l.Figures).AsJsonList();
        b.Property(l => l.Charts).AsJsonList();
        b.Property(l => l.Hidden).AsJsonList();
        b.Ignore(l => l.Values);
        // Stored as a UTC date-time so comparisons translate on every provider (same as the other timestamps).
        b.Property(l => l.UpdatedAt).HasConversion(v => v.UtcDateTime, v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));
    }
}

internal sealed class UserDashboardLayoutConfiguration : IEntityTypeConfiguration<UserDashboardLayout>
{
    public void Configure(EntityTypeBuilder<UserDashboardLayout> b)
    {
        b.ToTable("UserDashboardLayouts");
        b.HasKey(l => new { l.UserId, l.VehicleId });
        b.HasOne<Vehicle>().WithMany().HasForeignKey(l => l.VehicleId).OnDelete(DeleteBehavior.Cascade);
        VehicleDashboardLayoutConfiguration.Lists(b);
    }
}
