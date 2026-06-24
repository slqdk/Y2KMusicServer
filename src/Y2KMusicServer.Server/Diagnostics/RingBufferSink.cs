using System.IO;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Y2KMusicServer.Server.Diagnostics;

/// <summary>
/// Serilog sink that mirrors every emitted event into the in-memory
/// <see cref="LogRingBuffer"/> (which then fans out to the admin page over the
/// hub and backs the logs endpoint). The sink sees only events that pass the
/// pipeline's minimum level, so it automatically follows the live
/// <see cref="LogVerbositySwitch"/>.
///
/// The message is rendered with the <c>{Message:l}</c> ("literal") format so
/// embedded string properties are NOT wrapped in quotes — matching the
/// console/file output. (Plain <c>RenderMessage()</c> quotes strings, which
/// surfaced in the admin log as <c>by "unknown artist" [source: "Operator"]</c>
/// and empty-string artifacts like <c>(Smart Mix 3.0s"")</c>.) The exception is
/// captured into its own field, so it is intentionally left out of the template.
/// </summary>
public sealed class RingBufferSink : ILogEventSink
{
    private readonly LogRingBuffer _buffer;
    private readonly MessageTemplateTextFormatter _formatter;

    public RingBufferSink(LogRingBuffer buffer, IFormatProvider? fmt = null)
    {
        _buffer = buffer;
        _formatter = new MessageTemplateTextFormatter("{Message:l}", fmt);
    }

    public void Emit(LogEvent logEvent)
    {
        string source = "";
        if (logEvent.Properties.TryGetValue("SourceContext", out var sc)
            && sc is ScalarValue { Value: string ctx })
        {
            int dot = ctx.LastIndexOf('.');
            source = dot >= 0 && dot < ctx.Length - 1 ? ctx[(dot + 1)..] : ctx;
        }

        string message;
        using (var sw = new StringWriter())
        {
            _formatter.Format(logEvent, sw);
            message = sw.ToString();
        }

        _buffer.Add(
            logEvent.Timestamp.UtcDateTime,
            logEvent.Level.ToString(),
            source,
            message,
            logEvent.Exception?.ToString());
    }
}
