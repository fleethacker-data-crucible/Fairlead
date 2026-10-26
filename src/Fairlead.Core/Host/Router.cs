using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Fairlead.Core.Host
{
    public delegate Task<HttpResponse> RouteHandler(HttpRequest request);

    /// <summary>Method + path-template routing (<c>/api/jobs/{id}</c>). Exact segments win over parameters.</summary>
    public sealed class Router
    {
        private sealed class Route
        {
            public Route(string method, string template, RouteHandler handler)
            {
                Method = method;
                Segments = template.Trim('/').Split('/');
                Handler = handler;
            }

            public string Method;
            public string[] Segments;
            public RouteHandler Handler;
        }

        private readonly List<Route> _routes = new List<Route>();

        public Router Map(string method, string template, RouteHandler handler)
        {
            _routes.Add(new Route(method.ToUpperInvariant(), template, handler));
            return this;
        }

        public Router Get(string template, RouteHandler handler) { return Map("GET", template, handler); }
        public Router Post(string template, RouteHandler handler) { return Map("POST", template, handler); }
        public Router Delete(string template, RouteHandler handler) { return Map("DELETE", template, handler); }
        public Router Put(string template, RouteHandler handler) { return Map("PUT", template, handler); }

        /// <summary>Returns null when no route matches; sets <paramref name="methodMismatch"/> when the path exists for another method.</summary>
        public RouteHandler? Resolve(HttpRequest request, out bool methodMismatch)
        {
            methodMismatch = false;
            var segments = request.Path.Trim('/').Split('/');
            RouteHandler? best = null;
            var bestScore = -1;
            Dictionary<string, string>? bestValues = null;
            foreach (var route in _routes)
            {
                if (route.Segments.Length != segments.Length) continue;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var score = 0;
                var ok = true;
                for (var i = 0; i < segments.Length; i++)
                {
                    var t = route.Segments[i];
                    if (t.StartsWith("{") && t.EndsWith("}")) { values[t.Substring(1, t.Length - 2)] = segments[i]; continue; }
                    if (!string.Equals(t, segments[i], StringComparison.OrdinalIgnoreCase)) { ok = false; break; }
                    score++;
                }
                if (!ok) continue;
                if (route.Method != request.Method) { methodMismatch = true; continue; }
                if (score > bestScore) { bestScore = score; best = route.Handler; bestValues = values; }
            }
            if (best != null && bestValues != null)
            {
                methodMismatch = false;
                foreach (var kv in bestValues) request.RouteValues[kv.Key] = kv.Value;
            }
            return best;
        }
    }

    /// <summary>JSON helpers with the one serializer configuration the API uses everywhere.</summary>
    public static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = true } },
            NullValueHandling = NullValueHandling.Include,
            Converters = { new StringEnumConverter { NamingStrategy = new CamelCaseNamingStrategy() } },
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            Formatting = Formatting.None,
        };

        public static string Serialize(object? value)
        {
            return JsonConvert.SerializeObject(value, Settings);
        }

        public static HttpResponse Ok(object? value)
        {
            return HttpResponse.Json(200, Serialize(value));
        }

        public static HttpResponse Accepted(object? value)
        {
            return HttpResponse.Json(202, Serialize(value));
        }

        public static HttpResponse Error(int status, string code, string message, string? requestId = null)
        {
            return HttpResponse.Json(status, Serialize(new { error = message, code = code, requestId = requestId }));
        }

        public static JObject Body(HttpRequest request)
        {
            var text = request.BodyText;
            if (string.IsNullOrWhiteSpace(text)) return new JObject();
            try
            {
                return JObject.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new BadRequestException("Body is not valid JSON: " + ex.Message);
            }
        }

        public static T? Read<T>(JObject body, string name, T? fallback = default(T))
        {
            var tok = body[name];
            if (tok == null || tok.Type == JTokenType.Null) return fallback;
            try { return tok.ToObject<T>(); }
            catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is ArgumentException)
            {
                throw new BadRequestException("Field '" + name + "' has the wrong type.");
            }
        }
    }

    public sealed class BadRequestException : Exception
    {
        public BadRequestException(string message) : base(message) { }
    }

    public sealed class NotFoundException : Exception
    {
        public NotFoundException(string message) : base(message) { }
    }

    /// <summary>Serves the built UI from a directory with SPA fallback and no directory listing.</summary>
    public sealed class StaticFiles
    {
        private static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" }, { ".mjs", "text/javascript; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" }, { ".json", "application/json; charset=utf-8" }, { ".map", "application/json; charset=utf-8" },
            { ".svg", "image/svg+xml" }, { ".png", "image/png" }, { ".ico", "image/x-icon" }, { ".woff2", "font/woff2" }, { ".woff", "font/woff" },
            { ".txt", "text/plain; charset=utf-8" }, { ".webmanifest", "application/manifest+json" },
        };

        private readonly string _root;

        public StaticFiles(string root)
        {
            _root = Path.GetFullPath(root);
        }

        public bool Exists { get { return Directory.Exists(_root) && File.Exists(Path.Combine(_root, "index.html")); } }
        public string Root { get { return _root; } }

        public HttpResponse? Serve(HttpRequest request)
        {
            if (request.Method != "GET" && request.Method != "HEAD") return null;
            var rel = request.Path.Trim('/');
            var candidate = rel.Length == 0 ? "index.html" : rel;
            var full = Path.GetFullPath(Path.Combine(_root, candidate.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return HttpResponse.Text(403, "Forbidden");
            if (!File.Exists(full))
            {
                // SPA fallback for client-side routes (no extension) only.
                if (Path.HasExtension(candidate)) return null;
                full = Path.Combine(_root, "index.html");
                if (!File.Exists(full)) return null;
            }
            var ext = Path.GetExtension(full);
            string? type;
            if (!Types.TryGetValue(ext, out type) || type == null) type = "application/octet-stream";
            var response = HttpResponse.Bytes(200, File.ReadAllBytes(full), type);
            response.Headers["Cache-Control"] = ext.Equals(".html", StringComparison.OrdinalIgnoreCase) ? "no-store" : "public, max-age=3600";
            if (request.Method == "HEAD") response.Body = new byte[0];
            return response;
        }
    }
}
