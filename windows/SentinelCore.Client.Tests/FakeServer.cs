using System.Net;
using System.Text;

namespace SentinelCore.Client.Tests;

/// <summary>
/// A real HTTP server on loopback, so <see cref="SentinelCore.Client.Networking.SentinelClient"/>
/// is exercised over an actual socket - status codes, headers, cookies, JSON bodies and all.
/// </summary>
/// <remarks>
/// Deliberately not a mocked HttpMessageHandler. Half of what this client does is about the parts a
/// mock replaces: whether a 401 becomes the right exception, whether a session cookie is carried
/// onto the next request, whether an error body is read out of a non-2xx response. Those only
/// prove anything against something that really speaks HTTP.
/// </remarks>
public sealed class FakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Func<HttpListenerContext, bool>> _handlers = new();

    public FakeServer()
    {
        // Port 0 is not available to HttpListener, so take the first free port the OS will lend.
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    /// <summary>Every request the server saw, in order, as "METHOD path?query".</summary>
    public List<string> Requests { get; } = new();

    /// <summary>The raw request bodies, in the same order as <see cref="Requests"/>.</summary>
    public List<string> Bodies { get; } = new();

    /// <summary>The Content-Type of each request, in the same order as <see cref="Requests"/>.</summary>
    public List<string> ContentTypes { get; } = new();

    /// <summary>The Cookie header each request carried, in the same order as <see cref="Requests"/>.</summary>
    public List<string> CookieHeaders { get; } = new();

    public FakeServer OnGet(string path, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On("GET", path, json, status);

    public FakeServer On(string method, string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _handlers.Add(context =>
        {
            if (!string.Equals(context.Request.HttpMethod, method, StringComparison.OrdinalIgnoreCase)) return false;
            if (context.Request.Url?.AbsolutePath != path) return false;
            Respond(context, status, body);
            return true;
        });
        return this;
    }

    /// <summary>Answers with a Set-Cookie, so cookie persistence across requests can be checked.</summary>
    public FakeServer OnSetsCookie(string method, string path, string cookie, string body = "{}")
    {
        _handlers.Add(context =>
        {
            if (!string.Equals(context.Request.HttpMethod, method, StringComparison.OrdinalIgnoreCase)) return false;
            if (context.Request.Url?.AbsolutePath != path) return false;
            context.Response.Headers.Add("Set-Cookie", cookie);
            Respond(context, HttpStatusCode.OK, body);
            return true;
        });
        return this;
    }

    /// <summary>Echoes back whatever cookie header the request carried, as JSON.</summary>
    public FakeServer OnEchoesCookie(string path)
    {
        _handlers.Add(context =>
        {
            if (context.Request.Url?.AbsolutePath != path) return false;
            var cookie = context.Request.Headers["Cookie"] ?? "";
            Respond(context, HttpStatusCode.OK, $"{{\"cookie\":\"{cookie}\"}}");
            return true;
        });
        return this;
    }

    /// <summary>Echoes the request's Content-Type back, so a body's framing can be asserted on.</summary>
    public FakeServer OnEchoesContentType(string method, string path)
    {
        _handlers.Add(context =>
        {
            if (!string.Equals(context.Request.HttpMethod, method, StringComparison.OrdinalIgnoreCase)) return false;
            if (context.Request.Url?.AbsolutePath != path) return false;
            var contentType = context.Request.ContentType ?? "";
            Respond(context, HttpStatusCode.OK, $"{{\"contentType\":\"{contentType}\"}}");
            return true;
        });
        return this;
    }

    public FakeServer OnBytes(string path, byte[] payload, string contentType = "image/jpeg")
    {
        _handlers.Add(context =>
        {
            if (context.Request.Url?.AbsolutePath != path) return false;
            context.Response.StatusCode = 200;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = payload.Length;
            context.Response.OutputStream.Write(payload);
            context.Response.OutputStream.Close();
            return true;
        });
        return this;
    }

    private async Task ServeAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                var url = context.Request.Url;
                lock (Requests)
                {
                    Requests.Add($"{context.Request.HttpMethod} {url?.PathAndQuery}");
                    ContentTypes.Add(context.Request.ContentType ?? "");
                    CookieHeaders.Add(context.Request.Headers["Cookie"] ?? "");
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    Bodies.Add(reader.ReadToEnd());
                }

                if (_handlers.All(handler => !handler(context)))
                {
                    Respond(context, HttpStatusCode.NotFound, "{\"error\":\"no handler\"}");
                }
            }
            catch (Exception)
            {
                // A listener thread that throws takes the whole fake server down and every test
                // with it, which reads as a client bug rather than a harness one.
            }
        }
    }

    private static void Respond(HttpListenerContext context, HttpStatusCode status, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = payload.Length;
        context.Response.OutputStream.Write(payload);
        context.Response.OutputStream.Close();
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // Already torn down.
        }
        _cancellation.Dispose();
    }
}
