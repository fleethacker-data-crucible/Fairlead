using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fairlead.Core.Host
{
    public sealed class HttpRequest
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "/";
        public string Query { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = new byte[0];
        public IPEndPoint? RemoteEndPoint { get; set; }
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public Dictionary<string, string> RouteValues { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Cancelled when the server stops. A client that disconnects mid-stream is seen as a failed write.</summary>
        public CancellationToken Aborted { get; set; }
        /// <summary>Set only by Fairlead itself (the playground); never derived from anything on the wire.</summary>
        public bool Internal { get; set; }

        public bool IsLoopback { get { return RemoteEndPoint == null || IPAddress.IsLoopback(RemoteEndPoint.Address); } }

        public string? Header(string name)
        {
            string? v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        public string BodyText { get { return Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Body); } }

        public Dictionary<string, string> QueryValues
        {
            get
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(Query)) return dict;
                foreach (var pair in Query.TrimStart('?').Split('&'))
                {
                    if (pair.Length == 0) continue;
                    var eq = pair.IndexOf('=');
                    var k = Uri.UnescapeDataString(eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' ');
                    var v = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                    dict[k] = v;
                }
                return dict;
            }
        }
    }

    /// <summary>Writes a chunked response body as it is produced (server-sent events from a model).</summary>
    public interface IResponseStream
    {
        Task WriteAsync(byte[] data, CancellationToken token);
        Task WriteAsync(string text, CancellationToken token);
    }

    public sealed class HttpResponse
    {
        public int Status { get; set; } = 200;
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = new byte[0];
        /// <summary>When set, the body is streamed with chunked transfer encoding instead of <see cref="Body"/>.</summary>
        public Func<IResponseStream, CancellationToken, Task>? Stream { get; set; }

        public static HttpResponse Text(int status, string text, string contentType = "text/plain; charset=utf-8")
        {
            var r = new HttpResponse { Status = status, Body = Encoding.UTF8.GetBytes(text) };
            r.Headers["Content-Type"] = contentType;
            return r;
        }

        public static HttpResponse Json(int status, string json)
        {
            return Text(status, json, "application/json; charset=utf-8");
        }

        public static HttpResponse Bytes(int status, byte[] body, string contentType)
        {
            var r = new HttpResponse { Status = status, Body = body };
            r.Headers["Content-Type"] = contentType;
            return r;
        }

        public static HttpResponse EventStream(Func<IResponseStream, CancellationToken, Task> writer)
        {
            var r = new HttpResponse { Status = 200, Stream = writer };
            r.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
            r.Headers["Cache-Control"] = "no-cache";
            r.Headers["X-Accel-Buffering"] = "no";
            return r;
        }
    }

    public sealed class HttpParseException : Exception
    {
        public HttpParseException(string message) : base(message) { }
    }

    /// <summary>
    /// A small, strict HTTP/1.1 server on a TcpListener. HttpListener is avoided on purpose: on a locked-down
    /// Windows workstation it needs HTTP.sys URL reservations that a standard user does not have.
    /// One task per connection, no keep-alive, no chunked requests, bounded header and body sizes; responses may
    /// stream (chunked) so model tokens reach the client as they are generated.
    /// </summary>
    public sealed class HttpServer : IDisposable
    {
        private const int MaxHeaderBytes = 64 * 1024;
        /// <summary>Requests can carry base64 images; 32 MB covers several.</summary>
        private const int MaxBodyBytes = 32 * 1024 * 1024;
        private readonly Func<HttpRequest, Task<HttpResponse>> _handler;
        private readonly Diagnostics _log;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private int _active;

        public HttpServer(Func<HttpRequest, Task<HttpResponse>> handler, Diagnostics log)
        {
            _handler = handler ?? throw new ArgumentNullException("handler");
            _log = log ?? throw new ArgumentNullException("log");
        }

        public int Port { get; private set; }
        public bool IsRunning { get { return _listener != null; } }
        public int ActiveConnections { get { return _active; } }
        /// <summary>True when bound to every interface (FAIRLEAD_BIND=lan); otherwise loopback only.</summary>
        public bool BoundToLan { get; private set; }
        /// <summary>
        /// Decides, after the request is read, whether a non-loopback peer may be served. Null (the default) refuses
        /// every non-loopback peer. The host allows only gateway routes, and only with a client key.
        /// </summary>
        public Func<HttpRequest, bool>? AllowRemote { get; set; }

        /// <summary>Binds <paramref name="preferredPort"/>; falls back to an OS-assigned port when it is taken.</summary>
        public int Start(int preferredPort, bool lan = false)
        {
            if (_listener != null) throw new InvalidOperationException("Server already started.");
            var address = lan ? IPAddress.Any : IPAddress.Loopback;
            TcpListener listener;
            try
            {
                listener = new TcpListener(address, preferredPort);
                listener.Start(128);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied)
            {
                _log.Warn("PORT_FALLBACK", "Preferred port " + preferredPort + " is in use; selecting a free port. Tools configured for " + preferredPort + " will not reach Fairlead until it is free.");
                listener = new TcpListener(address, 0);
                listener.Start(128);
            }
            _listener = listener;
            BoundToLan = lan;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
            _log.Info("SERVER_START", "Listening on " + (lan ? "0.0.0.0" : "127.0.0.1") + ":" + Port);
            return Port;
        }

        public void Stop()
        {
            var listener = _listener;
            if (listener == null) return;
            _listener = null;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            try { listener.Stop(); } catch (SocketException) { }
            try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _log.Info("SERVER_STOP", "Listener closed.");
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            var listener = _listener;
            while (!token.IsCancellationRequested && listener != null)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (token.IsCancellationRequested) break; continue; }
                catch (InvalidOperationException) { break; }
                var _ = Task.Run(() => HandleConnection(client, token), token);
            }
        }

        private async Task HandleConnection(TcpClient client, CancellationToken token)
        {
            Interlocked.Increment(ref _active);
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 60000;
                    client.NoDelay = true;
                    var remote = client.Client.RemoteEndPoint as IPEndPoint;
                    using (var stream = client.GetStream())
                    {
                        HttpRequest request;
                        try
                        {
                            request = await ReadRequest(stream, token).ConfigureAwait(false);
                        }
                        catch (HttpParseException ex)
                        {
                            _log.Warn("BAD_REQUEST", ex.Message);
                            await WriteResponse(stream, HttpResponse.Text(400, "Bad request: " + ex.Message), token).ConfigureAwait(false);
                            return;
                        }
                        catch (IOException) { return; }
                        catch (ObjectDisposedException) { return; }
                        request.RemoteEndPoint = remote;
                        request.Aborted = token;
                        if (!request.IsLoopback && (AllowRemote == null || !AllowRemote(request)))
                        {
                            await WriteResponse(stream, HttpResponse.Text(403, "Loopback access only"), token).ConfigureAwait(false);
                            return;
                        }
                        HttpResponse response;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            response = await _handler(request).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _log.Error("REQUEST_FAILED", ex.GetType().Name + ": " + ex.Message, request.Path, request.RequestId);
                            response = HttpResponse.Json(500, "{\"error\":{\"message\":\"Internal error\",\"type\":\"server_error\"},\"requestId\":\"" + request.RequestId + "\"}");
                        }
                        if (request.Path.StartsWith("/api/", StringComparison.Ordinal) && request.Path != "/api/heartbeat")
                            _log.Info("REQUEST", request.Method + " " + request.Path + " -> " + response.Status + " " + sw.ElapsedMilliseconds + "ms", request.Path, request.RequestId);
                        try { await WriteResponse(stream, response, token).ConfigureAwait(false); }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static async Task<HttpRequest> ReadRequest(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[8192];
            var bodyStart = -1;
            var total = new List<byte>();
            while (bodyStart < 0)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (read <= 0) throw new HttpParseException("Client disconnected before headers were complete.");
                for (var i = 0; i < read; i++) total.Add(buffer[i]);
                if (total.Count > MaxHeaderBytes + MaxBodyBytes) throw new HttpParseException("Request too large.");
                bodyStart = FindHeaderEnd(total);
                if (bodyStart < 0 && total.Count > MaxHeaderBytes) throw new HttpParseException("Headers exceed " + MaxHeaderBytes + " bytes.");
            }
            var headerText = Encoding.ASCII.GetString(total.ToArray(), 0, bodyStart);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.")) throw new HttpParseException("Unsupported request line.");
            var request = new HttpRequest { Method = requestLine[0].ToUpperInvariant() };
            var target = requestLine[1];
            var q = target.IndexOf('?');
            request.Path = NormalizePath(q < 0 ? target : target.Substring(0, q));
            request.Query = q < 0 ? string.Empty : target.Substring(q);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                var colon = line.IndexOf(':');
                if (colon <= 0) throw new HttpParseException("Malformed header.");
                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            if (request.Headers.ContainsKey("Transfer-Encoding")) throw new HttpParseException("Chunked requests are not supported.");
            var length = 0;
            var lengthHeader = request.Header("Content-Length");
            if (lengthHeader != null && (!int.TryParse(lengthHeader, out length) || length < 0)) throw new HttpParseException("Invalid Content-Length.");
            if (length > MaxBodyBytes) throw new HttpParseException("Body exceeds " + MaxBodyBytes + " bytes.");
            var have = total.Count - (bodyStart + 4);
            // curl and .NET Framework clients wait for "100 Continue" before sending a larger body.
            if (length > 0 && have < length && string.Equals(request.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
            {
                var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await stream.WriteAsync(cont, 0, cont.Length, token).ConfigureAwait(false);
            }
            var body = new byte[length];
            if (have > 0) total.CopyTo(bodyStart + 4, body, 0, Math.Min(have, length));
            var offset = Math.Max(0, Math.Min(have, length));
            while (offset < length)
            {
                var read = await stream.ReadAsync(body, offset, length - offset, token).ConfigureAwait(false);
                if (read <= 0) throw new HttpParseException("Client disconnected before the body was complete.");
                offset += read;
            }
            request.Body = body;
            return request;
        }

        private static int FindHeaderEnd(List<byte> bytes)
        {
            for (var i = 3; i < bytes.Count; i++)
            {
                if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10) return i - 3;
            }
            return -1;
        }

        /// <summary>Collapses dot segments so static file handlers never see a traversal.</summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            var decoded = Uri.UnescapeDataString(path);
            var parts = new List<string>();
            foreach (var seg in decoded.Replace('\\', '/').Split('/'))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(seg);
            }
            return "/" + string.Join("/", parts);
        }

        private static async Task WriteResponse(NetworkStream stream, HttpResponse response, CancellationToken token)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(ReasonPhrase(response.Status)).Append("\r\n");
            if (!response.Headers.ContainsKey("Content-Type")) response.Headers["Content-Type"] = "application/octet-stream";
            if (response.Stream != null) response.Headers["Transfer-Encoding"] = "chunked";
            else response.Headers["Content-Length"] = response.Body.Length.ToString();
            response.Headers["Connection"] = "close";
            response.Headers["Date"] = DateTimeOffset.UtcNow.ToString("r");
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = response.Headers.ContainsKey("Cache-Control") ? response.Headers["Cache-Control"] : "no-store";
            foreach (var h in response.Headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head, 0, head.Length, token).ConfigureAwait(false);
            if (response.Stream != null)
            {
                await stream.FlushAsync(token).ConfigureAwait(false);
                var chunked = new ChunkedStream(stream);
                await response.Stream(chunked, token).ConfigureAwait(false);
                await chunked.CompleteAsync(token).ConfigureAwait(false);
                return;
            }
            if (response.Body.Length > 0) await stream.WriteAsync(response.Body, 0, response.Body.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private sealed class ChunkedStream : IResponseStream
        {
            private static readonly byte[] Crlf = { 13, 10 };
            private readonly Stream _inner;

            public ChunkedStream(Stream inner) { _inner = inner; }

            public Task WriteAsync(string text, CancellationToken token)
            {
                return WriteAsync(Encoding.UTF8.GetBytes(text), token);
            }

            public async Task WriteAsync(byte[] data, CancellationToken token)
            {
                if (data.Length == 0) return;
                var size = Encoding.ASCII.GetBytes(data.Length.ToString("X") + "\r\n");
                await _inner.WriteAsync(size, 0, size.Length, token).ConfigureAwait(false);
                await _inner.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
                await _inner.WriteAsync(Crlf, 0, 2, token).ConfigureAwait(false);
                await _inner.FlushAsync(token).ConfigureAwait(false);
            }

            public async Task CompleteAsync(CancellationToken token)
            {
                var end = Encoding.ASCII.GetBytes("0\r\n\r\n");
                await _inner.WriteAsync(end, 0, end.Length, token).ConfigureAwait(false);
                await _inner.FlushAsync(token).ConfigureAwait(false);
            }
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 304: return "Not Modified";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 413: return "Payload Too Large";
                case 422: return "Unprocessable Entity";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "Status";
            }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
