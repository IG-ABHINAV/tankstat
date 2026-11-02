using System.Diagnostics;
using Tankstat.Application.Photos;
using Tankstat.Application.Recognition;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Sync;
using Tankstat.Application.Telemetry;
using Tankstat.Domain;
using Tankstat.Domain.Recognition;
using Tankstat.Domain.Sync;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;
using Tankstat.TestSupport;

namespace Tankstat.Application.UnitTests;

/// <summary>
/// The app's own spans and metrics (#219) where the work happens: a photo read, a sync batch and its changes. Spans are found by the ids this
/// test made (a listener hears every test's), metrics on this world's own meter.
/// </summary>
public class AppTelemetryTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    private sealed record Scene(World W, User Alice, Vehicle Car);

    private static async Task<Scene> Setup()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        w.Current.SignInAs(alice);
        var car = await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, default);
        return new Scene(w, alice, car);
    }

    private static async Task<Guid> Queued(Scene s)
    {
        var id = await s.W.Drafts.UploadAsync(s.Car.Id, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }, default);
        await s.W.Recognition.QueueForDraftAsync(id, ReadingPurpose.Refueling, "hu", default);
        return id;
    }

    // ---- photo reading ------------------------------------------------------------------------------------

    [Fact]
    public async Task APhotoRead_IsATraceOfItsOwn_WithTheOutcomeAndCounts_AndItsDurationIsMeasured()
    {
        var s = await Setup();
        using var spans = new CapturedSpans(AppTraces.SourceName);
        using var measured = new CapturedMeasurements(s.W.Metrics.Meter);
        var id = await Queued(s);
        s.W.Recognizer.Answer = _ => new RecognitionResult("fake-1", DocumentKind.FuelReceipt,
            [new(ReadingFieldName.Total, "24669", 0.93, ValueSource.Read), new(ReadingFieldName.Volume, "38.52", 0.4, ValueSource.Read)]);

        await s.W.Processor.ProcessDueAsync(default);

        var read = Assert.Single(spans.Named("tankstat.photo_reading.read", "tankstat.image.id", id));
        Assert.Equal(default, read.ParentSpanId); // no request carries the worker
        Assert.Equal(("read", "FuelReceipt", 2, 1, "fake"), ((string?)read.GetTagItem("tankstat.photo_reading.outcome"), (string?)read.GetTagItem("tankstat.photo_reading.kind"),
            (int?)read.GetTagItem("tankstat.photo_reading.values"), (int?)read.GetTagItem("tankstat.photo_reading.sure"), (string?)read.GetTagItem("tankstat.photo_reading.provider")));
        Assert.Equal(ActivityStatusCode.Unset, read.Status);
        var duration = Assert.Single(measured.Of("tankstat.photo_reading.duration"));
        Assert.Equal(("read", "FuelReceipt"), (duration.Tags["tankstat.photo_reading.outcome"], duration.Tags["tankstat.photo_reading.kind"]));
        Assert.DoesNotContain(duration.Tags.Values, v => Equals(v, id)); // never an id as a metric attribute
    }

    [Fact]
    public async Task ARefusedPhoto_FailsItsSpan_ByTheErrorsType_NeverItsMessage()
    {
        var s = await Setup();
        using var spans = new CapturedSpans(AppTraces.SourceName);
        var id = await Queued(s);
        s.W.Recognizer.Answer = _ => throw new RecognitionRejectedException("zzzz what the model said");

        await s.W.Processor.ProcessDueAsync(default);

        var read = Assert.Single(spans.Named("tankstat.photo_reading.read", "tankstat.image.id", id));
        Assert.Equal((ActivityStatusCode.Error, "failed", typeof(RecognitionRejectedException).FullName),
            (read.Status, (string?)read.GetTagItem("tankstat.photo_reading.outcome"), (string?)read.GetTagItem("error.type")));
        Assert.Null(read.StatusDescription);
        Assert.DoesNotContain(read.TagObjects, t => t.Value?.ToString()?.Contains("zzzz") == true);
        Assert.Empty(read.Events); // no exception event: its message is the model's text
    }

    [Fact]
    public async Task AProviderThatIsAway_LeavesTheReadingQueued_AndTheSpanSaysSo()
    {
        var s = await Setup();
        using var spans = new CapturedSpans(AppTraces.SourceName);
        var id = await Queued(s);
        s.W.Recognizer.Answer = _ => throw new HttpRequestException("zzzz connection refused");

        await s.W.Processor.ProcessDueAsync(default);

        var read = Assert.Single(spans.Named("tankstat.photo_reading.read", "tankstat.image.id", id));
        Assert.Equal(("retrying", typeof(HttpRequestException).FullName), ((string?)read.GetTagItem("tankstat.photo_reading.outcome"), (string?)read.GetTagItem("error.type")));
    }

    // ---- sync ---------------------------------------------------------------------------------------------

    private static SyncChangeRequest Log(Scene s, Guid id, long odometer, DateOnly date) =>
        new(id, SyncChangeKind.LogRefueling, id, s.Car.Id, null, "{\"note\":\"zzzz typed on the device\"}", async ct =>
        {
            var r = await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(date, 40, 60, "EUR", odometer, true, null), ct, id: id);
            return new AppliedChange(r.Id, r.Version, r.VehicleId);
        });

    [Fact]
    public async Task ASyncBatch_HasASpanPerChange_ParkedOnesFailedByTheirKey_AndTheChangesAreCounted()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, 40, 60, "EUR", 1000, true, null), default);
        using var spans = new CapturedSpans(AppTraces.SourceName);
        using var measured = new CapturedMeasurements(s.W.Metrics.Meter);
        var (below, fine) = (Guid.NewGuid(), Guid.NewGuid());

        await s.W.Sync.SyncAsync([Log(s, below, 900, Day.AddDays(3)), Log(s, fine, 2000, Day.AddDays(4))], default);
        await s.W.Sync.SyncAsync([Log(s, fine, 2000, Day.AddDays(4))], default); // sent again: answered from the ledger, no span of its own

        var parked = Assert.Single(spans.Named("tankstat.sync.change", "tankstat.sync.change.id", below));
        var applied = Assert.Single(spans.Named("tankstat.sync.change", "tankstat.sync.change.id", fine));
        Assert.Equal(("parked", ActivityStatusCode.Error, "LogRefueling"), ((string?)parked.GetTagItem("tankstat.sync.change.result"), parked.Status, (string?)parked.GetTagItem("tankstat.sync.change.kind")));
        Assert.StartsWith("odometer.", (string?)parked.GetTagItem("error.type")); // the reason's key
        Assert.Equal(("applied", ActivityStatusCode.Unset, (object?)s.Car.Id), ((string?)applied.GetTagItem("tankstat.sync.change.result"), applied.Status, applied.GetTagItem("tankstat.vehicle.id")));
        var batch = Assert.Single(spans.Ended, a => a.SpanId == parked.ParentSpanId);
        Assert.Equal(("tankstat.sync.batch", 2, 1, 1), (batch.OperationName, (int?)batch.GetTagItem("tankstat.sync.changes"), (int?)batch.GetTagItem("tankstat.sync.applied"), (int?)batch.GetTagItem("tankstat.sync.parked")));
        Assert.Equal(batch.SpanId, applied.ParentSpanId);
        Assert.DoesNotContain(spans.Ended.SelectMany(a => a.TagObjects), t => t.Value?.ToString()?.Contains("zzzz") == true); // never what a change holds

        Assert.Equal(["applied", "parked", "repeated"], measured.Of("tankstat.sync.changes").Select(m => (string)m.Tags["tankstat.sync.change.result"]!).Order());
    }

    [Fact]
    public async Task ABatchRefusedAsAWhole_FailsItsSpan_ByTheRefusalsKey()
    {
        var s = await Setup();
        var id = Guid.NewGuid();
        await s.W.Sync.SyncAsync([Log(s, id, 1000, Day)], default);
        var bob = s.W.AddUser("bob@x.co");
        s.W.Current.SignInAs(bob);
        using var spans = new CapturedSpans(AppTraces.SourceName);
        using var request = new Activity("request").Start(); // the test's own parent, which finds the batch among every test's spans

        await Assert.ThrowsAsync<DomainException>(() => s.W.Sync.SyncAsync([Log(s, id, 1000, Day)], default));

        var batch = Assert.Single(spans.Ended, a => a.ParentSpanId == request.SpanId);
        Assert.Equal(("tankstat.sync.batch", ActivityStatusCode.Error, "sync.idTaken"), (batch.OperationName, batch.Status, (string?)batch.GetTagItem("error.type")));
        Assert.Null(batch.StatusDescription); // the key, never the message with its id
    }

    // ---- the helpers ----------------------------------------------------------------------------------------

    [Fact]
    public void FailedBy_MarksTheKeyOrTheType_NeverAStop()
    {
        using var spans = new CapturedSpans(AppTraces.SourceName);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var keyed = Marked(new DomainException("some.key", "zzzz the message"));
        var typed = Marked(new InvalidOperationException("zzzz the message"));
        var stopped = Marked(new OperationCanceledException());
        var cutShort = Marked(new InvalidOperationException("zzzz a read cut short"), cancelled.Token); // thrown once the stop was asked for

        Assert.Equal((ActivityStatusCode.Error, "some.key"), (keyed.Status, (string?)keyed.GetTagItem("error.type")));
        Assert.Equal((ActivityStatusCode.Error, typeof(InvalidOperationException).FullName), (typed.Status, (string?)typed.GetTagItem("error.type")));
        Assert.Equal((ActivityStatusCode.Unset, null), (stopped.Status, (string?)stopped.GetTagItem("error.type")));
        Assert.Equal((ActivityStatusCode.Unset, null), (cutShort.Status, (string?)cutShort.GetTagItem("error.type")));
        var ended = spans.Named("tankstat.test.helpers", "tankstat.test.id", Id);
        Assert.Equal(4, ended.Count);
        Assert.DoesNotContain(ended.SelectMany(a => a.TagObjects), t => t.Value?.ToString()?.Contains("zzzz") == true);
        Assert.All(ended, a => Assert.Null(a.StatusDescription));
    }

    private static readonly Guid Id = Guid.NewGuid();

    /// <summary>A span of this test's, marked by the filter the way a <c>catch when</c> would, which catches nothing (false), then ended.</summary>
    private static Activity Marked(Exception e, CancellationToken ct = default)
    {
        using var a = AppTraces.Source.StartActivity("tankstat.test.helpers")!;
        a.SetTag("tankstat.test.id", Id);
        Assert.False(a.FailedBy(e, ct));
        return a;
    }
}
