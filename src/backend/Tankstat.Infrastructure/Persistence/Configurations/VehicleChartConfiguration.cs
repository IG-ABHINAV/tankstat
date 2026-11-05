using System.Text.Json;
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
        b.Property(c => c.SecondMetric).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.SecondKind).HasConversion<string>().HasMaxLength(32);
        b.Ignore(c => c.Config);

        // A saved key figure (#273): every row from before is a chart; a figure's columns are null on a chart and the chart's on a figure.
        b.Property(c => c.Widget).HasConversion<string>().HasMaxLength(16).HasDefaultValue(WidgetKind.Chart);
        b.Property(c => c.Measure).HasConversion<string>().HasMaxLength(48);
        b.Property(c => c.Period).HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<TrendPeriod>(v, (JsonSerializerOptions?)null))
            .HasMaxLength(400);
        b.Property(c => c.Template).HasMaxLength(DashboardLayoutValues.MaxIdLength);
        b.Ignore(c => c.Figure);
        b.HasOne<Vehicle>().WithMany().HasForeignKey(c => c.VehicleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(c => new { c.VehicleId, c.CreatedById });

        // Stored as a UTC date-time so ordering translates on every provider (same as the other timestamps).
        b.Property(c => c.CreatedAt).HasConversion(v => v.UtcDateTime, v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));
    }
}
