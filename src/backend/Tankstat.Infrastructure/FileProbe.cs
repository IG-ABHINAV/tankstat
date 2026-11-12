using Tankstat.Application;

namespace Tankstat.Infrastructure;

/// <summary>The file system's answer for <see cref="IFileProbe"/>.</summary>
internal sealed class FileProbe : IFileProbe
{
    public bool Exists(string path) => File.Exists(path);
}
