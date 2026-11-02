using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tankstat.Api.SmokeTests;

/// <summary>
/// A collector of the smallest kind: answers every OTLP/HTTP export (<c>http/protobuf</c>) with 200 and keeps its body. Protobuf writes a
/// string as its UTF-8 bytes, so a span's name is found in the body as it is, without decoding the rest.
/// </summary>
internal sealed class OtlpReceiver : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<(string Path, byte[] Body)> _received = new();

    public OtlpReceiver()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Endpoint = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Endpoint + "/");
        _listener.Start();
        _ = Task.Run(ListenAsync);
    }

    public string Endpoint { get; }

    private async Task ListenAsync()
    {
        while (_listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body);
                _received.Enqueue((context.Request.Url!.AbsolutePath, body.ToArray()));
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return;
            }
            catch (Exception)
            {
                // One request that could not be read or answered (a connection dropped): the next export is still received.
            }
        }
    }

    /// <summary>Whether an export to <paramref name="path"/> holding <paramref name="text"/> arrives within <paramref name="wait"/> (looked at once with none).</summary>
    public async Task<bool> ReceivesAsync(string path, string text, TimeSpan wait)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            if (_received.Any(r => r.Path == path && r.Body.AsSpan().IndexOf(needle) >= 0)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100);
        }
    }

    public void Dispose() => _listener.Close();
}
