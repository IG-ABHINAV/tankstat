using Tankstat.Application.Telemetry;

namespace Tankstat.Application.UnitTests;

public class TelemetryGateTests
{
    private const string Endpoint = "http://collector:4317";

    private static TelemetryPlan Decide(params (string Key, string? Value)[] settings)
    {
        var values = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);
        return TelemetryGate.Decide(key => values.GetValueOrDefault(key));
    }

    [Fact]
    public void NothingSet_IsOff_AndNothingIsSaid()
    {
        var plan = Decide();

        Assert.Equal(TelemetryState.Off, plan.State);
        Assert.False(plan.On);
        Assert.Empty(plan.Problems);
        Assert.False(plan.TraceDatabase || plan.TraceDatabaseIgnored || plan.TraceDatabaseInvalid);
    }

    [Fact]
    public void TheEndpoint_TurnsItOn_WithTheSdksDefaultsAndTheAppsServiceName()
    {
        var plan = Decide((OtelSettings.Endpoint, Endpoint));

        Assert.Equal((TelemetryState.On, "grpc", "tankstat"), (plan.State, plan.Protocol, plan.ServiceName));
    }

    [Fact]
    public void TheirOwnValues_AreTaken_Trimmed()
    {
        var plan = Decide((OtelSettings.Endpoint, " https://otlp.example.com "), (OtelSettings.Protocol, " http/protobuf "), (OtelSettings.ServiceName, " cars "),
            (OtelSettings.Headers, "x-api-key=zzzz"));

        Assert.Equal((TelemetryState.On, "http/protobuf", "cars"), (plan.State, plan.Protocol, plan.ServiceName));
    }

    [Theory]
    [InlineData("   ")] // blank is not set
    [InlineData("")]
    public void ABlankEndpoint_IsNone(string blank) => Assert.Equal(TelemetryState.Off, Decide((OtelSettings.Endpoint, blank)).State);

    [Theory]
    [InlineData(OtelSettings.Endpoint, "collector:4317")]
    [InlineData("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "zzzz")]
    [InlineData(OtelSettings.Protocol, "GRPC")]
    [InlineData("OTEL_EXPORTER_OTLP_LOGS_PROTOCOL", "http/json")]
    [InlineData(OtelSettings.Headers, "zzzz")]
    [InlineData("OTEL_EXPORTER_OTLP_METRICS_HEADERS", "a=1,")]
    public void ASettingTheSdkCannotUse_LeavesItOff_NamingTheSettingNeverTheValue(string key, string value)
    {
        var plan = key == OtelSettings.Endpoint ? Decide((key, value)) : Decide((OtelSettings.Endpoint, Endpoint), (key, value));

        Assert.Equal(TelemetryState.Misconfigured, plan.State);
        var problem = Assert.Single(plan.Problems);
        Assert.StartsWith(key, problem);
        Assert.DoesNotContain(value, problem);
    }

    [Fact]
    public void ASignalsOwnEndpointAlone_LeavesItOff_SinceTheOtherSignalsWouldGoToTheSdksDefault()
    {
        var plan = Decide(("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://tempo:4318/v1/traces"));

        Assert.Equal(TelemetryState.SignalEndpointWithoutBase, plan.State);
        Assert.Equal(["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"], plan.SignalEndpoints);
    }

    [Fact]
    public void ASignalsOwnEndpoint_BesideTheBaseOne_OnlyRedirectsItsSignal() =>
        Assert.Equal(TelemetryState.On, Decide((OtelSettings.Endpoint, Endpoint), ("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://tempo:4318/v1/traces")).State);

    [Theory]
    [InlineData("true", TelemetryState.Disabled)]
    [InlineData("TRUE", TelemetryState.Disabled)]
    [InlineData("false", TelemetryState.On)]
    [InlineData("zzzz", TelemetryState.On)] // not a switch the SDK reads as true
    public void TheSdksOwnOffSwitch_Wins(string disabled, TelemetryState state) =>
        Assert.Equal(state, Decide((OtelSettings.Endpoint, Endpoint), (OtelSettings.SdkDisabled, disabled)).State);

    [Fact]
    public void TheDatabaseSwitch_CountsOnlyWhileTelemetryIsExported()
    {
        var on = Decide((OtelSettings.Endpoint, Endpoint), (TelemetryOptions.TraceDatabaseKey, "true"));
        var withoutEndpoint = Decide((TelemetryOptions.TraceDatabaseKey, "True"));
        var disabled = Decide((OtelSettings.Endpoint, Endpoint), (OtelSettings.SdkDisabled, "true"), (TelemetryOptions.TraceDatabaseKey, "true"));
        var misconfigured = Decide((OtelSettings.Endpoint, "zzzz"), (TelemetryOptions.TraceDatabaseKey, "true"));
        var off = Decide((OtelSettings.Endpoint, Endpoint));

        Assert.Equal((true, false), (on.TraceDatabase, on.TraceDatabaseIgnored));
        Assert.All([withoutEndpoint, disabled, misconfigured], plan => Assert.Equal((false, true), (plan.TraceDatabase, plan.TraceDatabaseIgnored)));
        Assert.Equal((false, false), (off.TraceDatabase, off.TraceDatabaseIgnored));
    }

    [Fact]
    public void ADatabaseSwitchThatIsNoSwitch_IsOff_AndSaid()
    {
        var plan = Decide((OtelSettings.Endpoint, Endpoint), (TelemetryOptions.TraceDatabaseKey, "yes"));

        Assert.Equal((TelemetryState.On, false, true), (plan.State, plan.TraceDatabase, plan.TraceDatabaseInvalid));
    }
}
