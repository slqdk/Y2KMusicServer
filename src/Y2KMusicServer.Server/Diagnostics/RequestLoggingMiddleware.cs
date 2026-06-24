using System.Diagnostics;

namespace Y2KMusicServer.Server.Diagnostics;

/// <summary>
/// Logs one line per HTTP request under the "WebServer" category at Debug, so it
/// surfaces only when verbose logging is on: the category sits outside the
/// Microsoft / System overrides pinned to Warning in Program.cs, so it follows
/// the live verbosity switch exactly like the engine's own Debug logs. When
/// verbose is off the middleware does nothing but a single level check per
/// request (no timing, no allocation, no log).
///
/// Registered first in the pipeline so the elapsed time and status code reflect
/// the whole request, including static files and the SPA fallback.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger _log;
    private static long _seq;

    public RequestLoggingMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
    {
        _next = next;
        _log = loggerFactory.CreateLogger("WebServer");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_log.IsEnabled(LogLevel.Debug))
        {
            await _next(context);
            return;
        }

        long seq = Interlocked.Increment(ref _seq);
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            _log.LogDebug(
                "#{Seq} {Ip} {Method} {Path}{Query} -> {Status} ({Elapsed} ms)",
                seq,
                context.Connection.RemoteIpAddress?.ToString() ?? "?",
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                context.Request.QueryString.Value ?? "",
                context.Response.StatusCode,
                sw.ElapsedMilliseconds);
        }
    }
}
