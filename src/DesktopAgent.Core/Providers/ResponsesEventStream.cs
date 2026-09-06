using System.Text;
using System.Text.Json;

namespace DesktopAgent.Core.Providers;

/// <summary>Only the terminal response can become a proposal; deltas and tool events are never actions.</summary>
internal static class ResponsesEventStream
{
    internal sealed record Result(byte[] Bytes, bool SearchCall);
    internal static async Task<Result> ReadAsync(Stream stream, int idleMs, Action<int> bytesRead,
        Action<string> eventReceived, CancellationToken ct, bool stopAfterSearch)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        byte[] bytes = new byte[8192]; char[] chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var line = new StringBuilder(); var data = new StringBuilder();
        int total = 0; string eventName = "";
        Result? ConsumeLine()
        {
            string value = line.ToString().TrimEnd('\r'); line.Clear();
            if (value.StartsWith("event:", StringComparison.Ordinal)) eventName = value[6..].Trim();
            else if (value.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(value[5..].TrimStart(' ')); }
            else if (value.Length == 0 && data.Length > 0)
            {
                using var doc = JsonDocument.Parse(data.ToString(), new JsonDocumentOptions { MaxDepth = 32 });
                data.Clear();
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString() ?? "";
                if (eventName.Length > 0 && eventName != type) throw new ProviderCallException("STREAM_EVENT_MISMATCH");
                eventName = "";
                eventReceived(type);
                if (stopAfterSearch && type == "response.output_item.done" && root.TryGetProperty("item", out var item) &&
                    item.GetProperty("type").GetString() == "web_search_call" && item.GetProperty("status").GetString() == "completed")
                    return new(Encoding.UTF8.GetBytes(item.GetRawText()), true);
                if (type is "response.completed" or "response.incomplete" or "response.failed")
                {
                    var response = root.GetProperty("response");
                    if (response.GetProperty("status").GetString() != type[9..]) throw new ProviderCallException("STREAM_TERMINAL_MISMATCH");
                    return new(Encoding.UTF8.GetBytes(response.GetRawText()), false);
                }
                if (type == "error") throw new ProviderCallException("STREAM_SERVER_ERROR");
            }
            return null;
        }
        while (true)
        {
            idle.CancelAfter(idleMs);
            int count;
            try { count = await stream.ReadAsync(bytes, idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderCallException("RESPONSE_IDLE_TIMEOUT"); }
            if (count == 0) throw new ProviderCallException("INCOMPLETE_EVENT_STREAM");
            total += count; if (total > 1024 * 1024) throw new ProviderCallException("RESPONSE_TOO_LARGE");
            bytesRead(count);
            int length = decoder.GetChars(bytes, 0, count, chars, 0, false);
            for (int i = 0; i < length; i++)
            {
                if (chars[i] != '\n') { line.Append(chars[i]); continue; }
                if (ConsumeLine() is { } terminal) { ct.ThrowIfCancellationRequested(); return terminal; }
            }
        }
    }
}
