using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    // When set (format "user:password"), requests must present matching Basic auth.
    static string? RequiredBasicAuth;

    // Cache last POST body per normalized endpoint
    record CachedEntry(byte[] Body, string ContentType, DateTime ReceivedAt);
    static readonly ConcurrentDictionary<string, CachedEntry> Cache = new();

    // Subscribers per normalized endpoint (for long-lived GETs)
    class Subscriber
    {
        public NetworkStream Stream;
        public SemaphoreSlim WriteSem = new(1, 1);
        public string ContentType;
    }
    static readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Subscriber>> Subscribers = new();

    static async Task Main(string[] args)
    {
        int port = 2323;

        // Simple arg parsing:
        // - integer arg sets port
        // - --auth=user:pass sets required basic auth (overrides env)
        // Environment fallback: BASIC_AUTH="user:pass"
        RequiredBasicAuth = Environment.GetEnvironmentVariable("BASIC_AUTH");

        foreach (var a in args)
        {
            if (a.StartsWith("--auth=", StringComparison.OrdinalIgnoreCase))
            {
                RequiredBasicAuth = a.Substring("--auth=".Length);
            }
            else if (int.TryParse(a, out var p))
            {
                port = p;
            }
        }

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"Listening on port {port}. Press Ctrl+C to exit.");
        if (!string.IsNullOrEmpty(RequiredBasicAuth))
            Console.WriteLine("Basic auth required.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Stopping listener...");
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _ = HandleClientAsync(client, cts.Token);
            }
        }
        finally
        {
            listener.Stop();
            Console.WriteLine("Listener stopped.");
        }
    }

    static async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var endpoint = client.Client.RemoteEndPoint;
        var endpointStr = endpoint?.ToString() ?? "unknown";
        Console.WriteLine($"Client connected: {endpoint}");
        using var ns = client.GetStream();

        try
        {
            // Support multiple requests on the same connection when client uses keep-alive.
            while (!token.IsCancellationRequested && client.Connected)
            {
                // Read headers first (scan for \r\n\r\n)
                var headerBuffer = new MemoryStream();
                var readBuffer = new byte[8192];
                int headerEndIndex = -1;
                while (!token.IsCancellationRequested)
                {
                    int n;
                    try
                    {
                        n = await ns.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), token);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (IOException) { return; }

                    if (n == 0) // client closed
                        return;

                    headerBuffer.Write(readBuffer, 0, n);

                    var bytes = headerBuffer.GetBuffer();
                    var len = (int)headerBuffer.Length;
                    headerEndIndex = IndexOfSequence(bytes, len, new byte[] { 13, 10, 13, 10 }); // \r\n\r\n
                    if (headerEndIndex >= 0)
                        break;

                    // safety: if headers grow absurdly large, abort
                    if (headerBuffer.Length > 64 * 1024)
                    {
                        Console.WriteLine($"[{endpointStr}] Headers too large, closing connection.");
                        return;
                    }
                }

                if (headerEndIndex < 0) return;

                // Extract header bytes and parse
                var headerBytes = headerBuffer.GetBuffer();
                var headersLen = headerEndIndex + 4;
                var headerString = Encoding.ASCII.GetString(headerBytes, 0, headersLen);
                var headerLines = headerString.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var requestLine = headerLines.Length > 0 ? headerLines[0] : "";
                var method = "";
                var path = "";
                var version = "";
                {
                    var parts = requestLine.Split(' ', 3);
                    if (parts.Length >= 1) method = parts[0];
                    if (parts.Length >= 2) path = parts[1];
                    if (parts.Length >= 3) version = parts[2];
                }

                // Build header dictionary
                var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < headerLines.Length; i++)
                {
                    var line = headerLines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var name = line.Substring(0, idx).Trim();
                    var value = line.Substring(idx + 1).Trim();
                    headers[name] = value;
                }

                // Basic auth check (if configured)
                if (!string.IsNullOrEmpty(RequiredBasicAuth))
                {
                    var authorized = false;
                    if (headers.TryGetValue("Authorization", out var authHeader) && !string.IsNullOrEmpty(authHeader))
                    {
                        // Expect form: "Basic base64(user:pass)"
                        var parts = authHeader.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2 && parts[0].Equals("Basic", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Trim()));
                                authorized = decoded == RequiredBasicAuth;
                            }
                            catch
                            {
                                authorized = false;
                            }
                        }
                    }

                    if (!authorized)
                    {
                        Console.WriteLine($"[{endpointStr}] Unauthorized request (missing or invalid Authorization header).");
                        // Respond 401 and close connection (include CORS)
                        var respBuilder = new StringBuilder();
                        respBuilder.AppendLine("HTTP/1.1 401 Unauthorized");
                        respBuilder.AppendLine("WWW-Authenticate: Basic realm=\"ProtocolDebugger\"");
                        respBuilder.AppendLine("Content-Length: 0");
                        respBuilder.AppendLine("Connection: close");
                        respBuilder.AppendLine("Access-Control-Allow-Origin: *");
                        respBuilder.AppendLine();
                        var respBytes = Encoding.ASCII.GetBytes(respBuilder.ToString());
                        try
                        {
                            await ns.WriteAsync(respBytes.AsMemory(0, respBytes.Length), token);
                            await ns.FlushAsync(token);
                        }
                        catch { }
                        return;
                    }
                }

                // Handle Expect: 100-continue
                if (headers.TryGetValue("Expect", out var expect) && expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    await ns.WriteAsync(cont.AsMemory(0, cont.Length), token);
                    await ns.FlushAsync(token);
                }

                // Determine Content-Length
                long contentLength = 0;
                if (headers.TryGetValue("Content-Length", out var clv) && long.TryParse(clv, out var cl))
                    contentLength = cl;
                if (headers.TryGetValue("Transfer-Encoding", out var te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // For simplicity, do not implement chunked parsing here.
                    Console.WriteLine($"[{endpointStr}] Chunked Transfer-Encoding not supported. Closing connection.");
                    return;
                }

                // Calculate how many body bytes we already have after headers
                var totalBuffer = headerBuffer.ToArray();
                var initialBodyBytes = (int)headerBuffer.Length - headersLen;
                var bodyStream = new MemoryStream();
                if (initialBodyBytes > 0)
                    bodyStream.Write(totalBuffer, headersLen, initialBodyBytes);

                // Read remaining body bytes if any
                while (bodyStream.Length < contentLength)
                {
                    var remaining = (int)(contentLength - bodyStream.Length);
                    var toRead = Math.Min(remaining, readBuffer.Length);
                    int n;
                    try
                    {
                        n = await ns.ReadAsync(readBuffer.AsMemory(0, toRead), token);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (IOException) { return; }

                    if (n == 0) break; // client closed unexpectedly
                    bodyStream.Write(readBuffer, 0, n);
                }

                var bodyBytes = bodyStream.ToArray();
                string bodyText = bodyBytes.Length > 0 ? Encoding.UTF8.GetString(bodyBytes) : "";

                // Print summary + headers
                Console.WriteLine($"[{endpointStr}] {method} {path} {version}");
                foreach (var kv in headers)
                    Console.WriteLine($"[{endpointStr}] {kv.Key}: {kv.Value}");

                // Normalize the path for caching and matching
                var normalized = NormalizePath(path);

                // Handle GET/POST behavior with cache
                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    // Cache the POST body for this endpoint (store content-type if present)
                    headers.TryGetValue("Content-Type", out var contentType);
                    contentType ??= "application/octet-stream";
                    var entry = new CachedEntry(bodyBytes, contentType, DateTime.UtcNow);
                    Cache[normalized] = entry;

                    // Broadcast to subscribers (fire-and-forget)
                    _ = BroadcastUpdateAsync(normalized, entry);

                    // Also pretty-print JSON if it is JSON
                    if (!string.IsNullOrEmpty(contentType) && contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(bodyBytes);
                            var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                            Console.WriteLine($"[{endpointStr}] JSON body (cached for {normalized}):\n{pretty}");
                        }
                        catch (JsonException)
                        {
                            Console.WriteLine($"[{endpointStr}] Invalid JSON body (printing raw, cached for {normalized}):\n{bodyText}");
                        }
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(bodyText))
                            Console.WriteLine($"[{endpointStr}] Body cached for {normalized} (raw):\n{bodyText}");
                    }

                    // Respond 200 OK (include CORS)
                    var responseBody = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    var keepAlive = DetermineKeepAlive(headers, version);
                    var responseBuilder = new StringBuilder();
                    responseBuilder.AppendLine("HTTP/1.1 200 OK");
                    responseBuilder.AppendLine("Content-Type: application/json; charset=utf-8");
                    responseBuilder.AppendLine($"Content-Length: {responseBody.Length}");
                    responseBuilder.AppendLine($"Connection: {(keepAlive ? "keep-alive" : "close")}");
                    responseBuilder.AppendLine("Access-Control-Allow-Origin: *");
                    responseBuilder.AppendLine();

                    var headerBytesToSend = Encoding.ASCII.GetBytes(responseBuilder.ToString());
                    await ns.WriteAsync(headerBytesToSend.AsMemory(0, headerBytesToSend.Length), token);
                    await ns.WriteAsync(responseBody.AsMemory(0, responseBody.Length), token);
                    await ns.FlushAsync(token);

                    if (!keepAlive) break;
                    continue;
                }
                else if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    // Register subscriber for long-lived updates
                    var subscriberId = Guid.NewGuid();
                    var subs = Subscribers.GetOrAdd(normalized, _ => new ConcurrentDictionary<Guid, Subscriber>());

                    var contentTypeForResponse = "application/json";
                    Cache.TryGetValue(normalized, out var currentEntry);
                    if (currentEntry != null) contentTypeForResponse = currentEntry.ContentType;

                    var subscriber = new Subscriber { Stream = ns, ContentType = contentTypeForResponse };
                    if (!subs.TryAdd(subscriberId, subscriber))
                    {
                        // unlikely, but if failed respond 500 (include CORS)
                        var body = Encoding.UTF8.GetBytes("Internal Server Error");
                        var respBuilder = new StringBuilder();
                        respBuilder.AppendLine("HTTP/1.1 500 Internal Server Error");
                        respBuilder.AppendLine("Content-Type: text/plain; charset=utf-8");
                        respBuilder.AppendLine($"Content-Length: {body.Length}");
                        respBuilder.AppendLine("Connection: close");
                        respBuilder.AppendLine("Access-Control-Allow-Origin: *");
                        respBuilder.AppendLine();
                        var head = Encoding.ASCII.GetBytes(respBuilder.ToString());
                        await ns.WriteAsync(head.AsMemory(0, head.Length), token);
                        await ns.WriteAsync(body.AsMemory(0, body.Length), token);
                        await ns.FlushAsync(token);
                        return;
                    }

                    // Send headers for chunked response (long-lived) and include CORS
                    var headerBuilder = new StringBuilder();
                    headerBuilder.AppendLine("HTTP/1.1 200 OK");
                    headerBuilder.AppendLine($"Content-Type: {subscriber.ContentType}");
                    headerBuilder.AppendLine("Transfer-Encoding: chunked");
                    headerBuilder.AppendLine("Connection: keep-alive");
                    headerBuilder.AppendLine("Access-Control-Allow-Origin: *");
                    headerBuilder.AppendLine();
                    var headerBytesToSend = Encoding.ASCII.GetBytes(headerBuilder.ToString());
                    try
                    {
                        await ns.WriteAsync(headerBytesToSend.AsMemory(0, headerBytesToSend.Length), token);
                        await ns.FlushAsync(token);
                    }
                    catch
                    {
                        // client went away; remove subscriber and continue to next connection
                        subs.TryRemove(subscriberId, out _);
                        break;
                    }

                    // If we already have cached content, send it immediately as a chunk
                    if (currentEntry != null && currentEntry.Body.Length > 0)
                    {
                        try
                        {
                            await WriteChunkAsync(subscriber, currentEntry.Body, token);
                        }
                        catch
                        {
                            subs.TryRemove(subscriberId, out _);
                            break;
                        }
                    }

                    // Start a background reader to detect client disconnects (reads 1 byte)
                    var readerTask = Task.Run(async () =>
                    {
                        var rbuf = new byte[1];
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                int r = 0;
                                try
                                {
                                    r = await ns.ReadAsync(rbuf.AsMemory(0, 1), token);
                                }
                                catch
                                {
                                    break;
                                }

                                if (r == 0) break; // client closed
                                // ignore any data client sends
                            }
                        }
                        finally
                        {
                            // remove the subscriber
                            subs.TryRemove(subscriberId, out _);
                        }
                    });

                    // Wait until client disconnects (readerTask completes) or server token cancelled
                    await readerTask;

                    // ensure subscriber removed
                    subs.TryRemove(subscriberId, out _);

                    // close connection (handler loop will finish)
                    break;
                }
                else
                {
                    // Method not allowed for this simple server (include CORS)
                    var body = Encoding.UTF8.GetBytes("Method Not Allowed");
                    var keepAlive = DetermineKeepAlive(headers, version);
                    var respBuilder = new StringBuilder();
                    respBuilder.AppendLine("HTTP/1.1 405 Method Not Allowed");
                    respBuilder.AppendLine("Content-Type: text/plain; charset=utf-8");
                    respBuilder.AppendLine($"Content-Length: {body.Length}");
                    respBuilder.AppendLine($"Connection: {(keepAlive ? "keep-alive" : "close")}");
                    respBuilder.AppendLine("Access-Control-Allow-Origin: *");
                    respBuilder.AppendLine();

                    var headBytes = Encoding.ASCII.GetBytes(respBuilder.ToString());
                    await ns.WriteAsync(headBytes.AsMemory(0, headBytes.Length), token);
                    await ns.WriteAsync(body.AsMemory(0, body.Length), token);
                    await ns.FlushAsync(token);

                    if (!keepAlive) break;
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{endpointStr}] Error: {ex.Message}");
        }
        finally
        {
            Console.WriteLine($"Client disconnected: {endpoint}");
            try { client.Close(); } catch { }
        }
    }

    static bool DetermineKeepAlive(System.Collections.Generic.IDictionary<string, string> headers, string version)
    {
        if (headers.TryGetValue("Connection", out var connVal))
        {
            return connVal.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        // HTTP/1.1 defaults to keep-alive unless Connection: close
        return version.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase);
    }

    // Broadcast update to all subscribers on a normalized endpoint.
    static async Task BroadcastUpdateAsync(string normalized, CachedEntry entry)
    {
        if (!Subscribers.TryGetValue(normalized, out var subs)) return;
        var list = subs.ToArray(); // snapshot
        foreach (var kv in list)
        {
            var id = kv.Key;
            var sub = kv.Value;
            try
            {
                await WriteChunkAsync(sub, entry.Body, CancellationToken.None);
            }
            catch
            {
                // remove failed subscriber
                subs.TryRemove(id, out _);
            }
        }
    }

    static async Task WriteChunkAsync(Subscriber sub, byte[] body, CancellationToken token)
    {
        // format: <hex len>\r\n<body>\r\n
        await sub.WriteSem.WaitAsync(token);
        try
        {
            var lenLine = Encoding.ASCII.GetBytes($"{body.Length:X}\r\n");
            var endLine = new byte[] { 13, 10 };
            await sub.Stream.WriteAsync(lenLine, 0, lenLine.Length, token);
            if (body.Length > 0)
                await sub.Stream.WriteAsync(body, 0, body.Length, token);
            await sub.Stream.WriteAsync(endLine, 0, endLine.Length, token);
            await sub.Stream.FlushAsync(token);
        }
        finally
        {
            sub.WriteSem.Release();
        }
    }

    // Normalize path:
    // - case-insensitive (lowercased)
    // - accept letters, digits, '-', '_', '/'
    // - stop at the first invalid character and ignore the rest
    static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        var sb = new StringBuilder();
        foreach (var ch in path.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '/')
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                // stop at first invalid char
                break;
            }
        }
        var result = sb.Length == 0 ? "/" : sb.ToString();
        return result;
    }

    static int IndexOfSequence(byte[] buffer, int bufferLength, byte[] sequence)
    {
        if (bufferLength == 0 || sequence.Length == 0 || bufferLength < sequence.Length) return -1;
        for (int i = 0; i <= bufferLength - sequence.Length; i++)
        {
            var found = true;
            for (int j = 0; j < sequence.Length; j++)
            {
                if (buffer[i + j] != sequence[j]) { found = false; break; }
            }
            if (found) return i;
        }
        return -1;
    }
}
