namespace Tankstat.Application;

/// <summary>Whether a file the app can read is at a path (#312: Application reads no file itself; the telemetry certificate check asks here).</summary>
public interface IFileProbe
{
    bool Exists(string path);
}
