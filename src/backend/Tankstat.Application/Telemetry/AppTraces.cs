using System.Diagnostics;
using Tankstat.Domain;

namespace Tankstat.Application.Telemetry;

/// <summary>
/// The app's own spans (#219), beside what the libraries record (a request, GraphQL's phases, HTTP calls, database commands): the work no
/// request carries (a backup run, the restore and the migrations at start, a photo read) and the steps inside a request worth seeing (a sync
/// batch and its changes). <c>System.Diagnostics</c> only, so any layer may start one; exported while telemetry is on
/// (<c>TelemetryExtensions</c>), and free while it is off: <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> answers null when
/// nothing listens, hence <c>activity?.</c> everywhere.
/// <para>
/// Names are <c>tankstat.&lt;area&gt;.&lt;verb&gt;</c>, tags <c>tankstat.&lt;area&gt;.&lt;what&gt;</c>, and both keep the log's promise:
/// ids, counts and reasons, never what someone typed, never an exception's message (an app's message quotes values): a failure is
/// <see cref="Failed"/>, the error's key or type.
/// </para>
/// </summary>
public static class AppTraces
{
    public const string SourceName = "Tankstat";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Starts a span of work that goes on by itself (a job): the root of a trace of its own, never the child of a request it may outlive
    /// (<see cref="Task.Run(Func{Task})"/> carries <see cref="Activity.Current"/> along), linked to that request's span instead.
    /// </summary>
    public static Activity? StartJob(string name)
    {
        var caller = Activity.Current;
        Activity.Current = null; // a default parent context means the current activity: none, for a root
        return Source.StartActivity(name, ActivityKind.Internal, parentContext: default, links: caller is null ? null : [new ActivityLink(caller.Context)]);
    }

    /// <summary>Marks the span failed with <paramref name="errorType"/>: a key (<c>backup.wrongKey</c>) or an exception's type, never its message.</summary>
    public static void Failed(this Activity? activity, string errorType)
    {
        if (activity is null) return;
        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag("error.type", errorType);
    }

    /// <summary>
    /// For an exception filter, <c>catch (Exception e) when (activity.FailedBy(e)) { }</c>: marks the span failed by <paramref name="exception"/>
    /// and catches nothing (false), so the exception goes on as it was, stack and all.
    /// </summary>
    public static bool FailedBy(this Activity? activity, Exception exception)
    {
        activity.Failed(ErrorTypeOf(exception));
        return false;
    }

    /// <summary>The error type of <paramref name="exception"/>: its key when it has one, else the type's name.</summary>
    public static string ErrorTypeOf(Exception exception) => exception is KeyedException keyed ? keyed.Key : exception.GetType().FullName ?? exception.GetType().Name;
}
