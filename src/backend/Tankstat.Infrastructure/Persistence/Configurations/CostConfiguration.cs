using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Infrastructure.Persistence.Configurations;

internal sealed class CostConfiguration : IEntityTypeConfiguration<Cost>
{
    public void Configure(EntityTypeBuilder<Cost> b)
    {
        b.ToTable("Costs");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.Amount).HasPrecision(14, 2);
        b.Property(c => c.Discount).HasPrecision(14, 2);
        b.Property(c => c.Currency).HasMaxLength(3).IsRequired();
        b.HasOne<Vehicle>().WithMany().HasForeignKey(c => c.VehicleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(c => new { c.VehicleId, c.Date });
        b.HasIndex(c => c.OwnerId);

        // Stored as UTC date-time so comparisons translate on every provider (same as vehicles and logs).
        b.Property(c => c.DeletedAt).HasConversion(
            v => v == null ? (DateTime?)null : v.Value.UtcDateTime,
            v => v == null ? null : new DateTimeOffset(DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)));

        // A trashed log trashes its cost, so reports never count it.
        b.HasQueryFilter(c => c.DeletedAt == null);
    }
}
