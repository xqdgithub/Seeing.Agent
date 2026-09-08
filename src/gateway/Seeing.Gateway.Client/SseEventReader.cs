using System.Runtime.CompilerServices;
using System.Text.Json;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.Client;

/// <summary>
/// 解析 SSE（text/event-stream）响应为 <see cref="GatewayEvent"/>
/// </summary>
public static class SseEventReader
{
    public static async IAsyncEnumerable<GatewayEvent> ReadEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream);
        var dataBuilder = new System.Text.StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (dataBuilder.Length > 0)
                {
                    dataBuilder.Append('\n');
                }

                dataBuilder.Append(line.AsSpan("data:".Length).TrimStart());
                continue;
            }

            if (line.Length == 0 && dataBuilder.Length > 0)
            {
                if (TryParseEvent(dataBuilder.ToString(), out var gatewayEvent))
                {
                    yield return gatewayEvent;
                }

                dataBuilder.Clear();
            }
        }

        if (dataBuilder.Length > 0 && TryParseEvent(dataBuilder.ToString(), out var trailingEvent))
        {
            yield return trailingEvent;
        }
    }

    internal static bool TryParseEvent(string data, out GatewayEvent gatewayEvent)
    {
        gatewayEvent = null!;

        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
        {
            return false;
        }

        var parsed = JsonSerializer.Deserialize<GatewayEvent>(data, GatewayJsonOptions.Default);
        if (parsed is null)
        {
            return false;
        }

        gatewayEvent = parsed;
        return true;
    }
}
