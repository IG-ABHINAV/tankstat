using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Infrastructure.Persistence.Configurations;

internal sealed class VehicleChartConfiguration : IEntityTypeConfiguration<VehicleChart>
{
    public void Configure(EntityTypeBuilder<VehicleChart> b)
    {
        b.ToTable("VehicleCharts");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.Title).HasMaxLength(VehicleChart.MaxTitleLength).IsRequired();
        b.Property(c => c.Metric).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.Grouping).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.Range).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.Trend).HasConversion<string>().HasMaxLength(32).HasDefaultValue(ChartTrend.None); // charts from before trends have none
        b.Ignore(c => c.Config);
        b.HasOne<Vehicle>().WithMany().HasForeignKey(c => c.VehicleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(c => new { c.VehicleId, c.CreatedById });

        // Stored as a UTC date-time so ordering translates on every provider (same as the other timestamps).
        b.Property(c => c.CreatedAt).HasConversion(v => v.UtcDateTime, v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));
    }
}
