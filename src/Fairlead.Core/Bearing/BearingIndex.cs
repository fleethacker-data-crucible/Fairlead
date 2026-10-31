using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Fairlead.Core.Bearing
{
    /// <summary>A stretch of one document: what the model is shown and what a citation points back to.</summary>
    public sealed class BearingPassage
    {
        /// <summary>Path relative to the folder, with forward slashes.</summary>
        public string File { get; set; }
        public string FullPath { get; set; }
        public int FromLine { get; set; }
        public int ToLine { get; set; }
        /// <summary>Nearest Markdown heading above the passage, if any.</summary>
        public string Heading { get; set; }
        public string Text { get; set; }

        public BearingPassage()
        {
            File = string.Empty;
            FullPath = string.Empty;
            Heading = string.Empty;
            Text = string.Empty;
        }

        public string Location
        {
            get { return FromLine == ToLine ? "line " + FromLine : "lines " + FromLine + "-" + ToLine; }
        }

        /// <summary>A file link that opens the document, with a line fragment (#L12-L18) that editors and viewers understand.</summary>
        public string Link
        {
            get
            {
                string uri;
                try { uri = new Uri(FullPath).AbsoluteUri; }
                catch (UriFormatException) { uri = FullPath; }
                return uri + "#L" + FromLine + (ToLine > FromLine ? "-L" + ToLine : string.Empty);
            }
        }
    }

    public sealed class BearingHit
    {
        public BearingPassage Passage { get; set; }
        public double Score { get; set; }
    }

    /// <summary>
    /// A small in-memory full-text index (BM25) over a local folder of text documents. Nothing is sent anywhere: no
    /// embedding model, no retrieval service. It is rebuilt when files are added, removed or changed.
    /// </summary>
    public sealed class BearingIndex
    {
        public const int MaxFiles = 2000;
        public const long MaxFileBytes = 1024 * 1024;
        public const long MaxTotalBytes = 64L * 1024 * 1024;
        private const int TargetChars = 500;
        private const int MaxChars = 900;
        private const double K1 = 1.5;
        private const double B = 0.75;
        private static readonly string[] Extensions = { ".txt", ".md", ".markdown", ".text", ".csv", ".log", ".rst" };
        private static readonly HashSet<string> Stop = new HashSet<string>(
            ("a an and are as at be but by can do does for from had has have how i if in into is it its of on or our so such than that the their then there these they this to was we were what when where which who why will with would you your").Split(' '),
            StringComparer.Ordinal);

        private readonly object _lock = new object();
        private readonly string _folder;
        private List<BearingPassage> _passages = new List<BearingPassage>();
        private List<Dictionary<string, int>> _tf = new List<Dictionary<string, int>>();
        private List<int> _len = new List<int>();
        private Dictionary<string, int> _df = new Dictionary<string, int>(StringComparer.Ordinal);
        private double _avgLen = 1;
        private string _fingerprint = null;
        private DateTime _checkedAt = DateTime.MinValue;

        public BearingIndex(string folder)
        {
            _folder = folder;
            Files = 0;
            Skipped = 0;
        }

        public string Folder { get { return _folder; } }
        public int Files { get; private set; }
        public int Skipped { get; private set; }
        public int PassageCount { get { lock (_lock) return _passages.Count; } }
        public DateTimeOffset? IndexedAt { get; private set; }

        /// <summary>Re-reads the folder when something changed (checked at most every two seconds unless forced).</summary>
        public void Refresh(bool force)
        {
            lock (_lock)
            {
                if (!force && (DateTime.UtcNow - _checkedAt).TotalSeconds < 2) return;
                _checkedAt = DateTime.UtcNow;
                var files = Enumerate();
                var print = Fingerprint(files);
                if (print == _fingerprint) return;
                Build(files);
                _fingerprint = print;
            }
        }

        public List<BearingHit> Search(string query, int top)
        {
            Refresh(false);
            var terms = Tokens(query).Distinct().ToList();
            var hits = new List<BearingHit>();
            if (terms.Count == 0) return hits;
            lock (_lock)
            {
                var n = _passages.Count;
                var need = Math.Max(1, (int)Math.Ceiling(terms.Count * 0.5));
                for (var i = 0; i < n; i++)
                {
                    double score = 0;
                    var matched = 0;
                    foreach (var t in terms)
                    {
                        int tf;
                        if (!_tf[i].TryGetValue(t, out tf)) continue;
                        matched++;
                        var df = _df[t];
                        var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                        score += idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * _len[i] / _avgLen));
                    }
                    if (matched >= need) hits.Add(new BearingHit { Passage = _passages[i], Score = score });
                }
            }
            return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Passage.File, StringComparer.Ordinal).ThenBy(h => h.Passage.FromLine).Take(Math.Max(1, top)).ToList();
        }

        /// <summary>Lower-case words of two or more characters without common filler words, plurals trimmed.</summary>
        public static IEnumerable<string> Tokens(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            var sb = new StringBuilder();
            for (var i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && char.IsLetterOrDigit(text[i])) { sb.Append(char.ToLowerInvariant(text[i])); continue; }
                if (sb.Length == 0) continue;
                var w = sb.ToString();
                sb.Length = 0;
                if (w.Length < 2 || Stop.Contains(w)) continue;
                if (w.Length > 3 && w[w.Length - 1] == 's' && w[w.Length - 2] != 's') w = w.Substring(0, w.Length - 1);
                result.Add(w);
            }
            return result;
        }

        // ---------- building ----------

        private sealed class FileEntry
        {
            public string Full;
            public string Relative;
            public long Length;
            public long Ticks;
        }

        private List<FileEntry> Enumerate()
        {
            var list = new List<FileEntry>();
            if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder)) { Skipped = 0; return list; }
            var root = Path.GetFullPath(_folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var skipped = 0;
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && list.Count < MaxFiles)
            {
                var dir = pending.Pop();
                string[] subdirs;
                string[] files;
                try { subdirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var sub in subdirs.OrderBy(s => s, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    // A link out of the folder must not widen what can be read or cited.
                    if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(sub);
                }
                foreach (var f in files.OrderBy(s => s, StringComparer.Ordinal))
                {
                    if (!Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                    var info = new FileInfo(f);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    if (info.Length > MaxFileBytes || total + info.Length > MaxTotalBytes || list.Count >= MaxFiles) { skipped++; continue; }
                    total += info.Length;
                    list.Add(new FileEntry { Full = f, Relative = f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'), Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks });
                }
            }
            Skipped = skipped;
            return list;
        }

        private static string Fingerprint(List<FileEntry> files)
        {
            var sb = new StringBuilder();
            foreach (var f in files) sb.Append(f.Relative).Append('|').Append(f.Length.ToString(CultureInfo.InvariantCulture)).Append('|').Append(f.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        private void Build(List<FileEntry> files)
        {
            var passages = new List<BearingPassage>();
            var read = 0;
            foreach (var f in files)
            {
                string text;
                try { text = File.ReadAllText(f.Full, new UTF8Encoding(false)); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                read++;
                passages.AddRange(Split(f.Relative, f.Full, text));
            }
            var tf = new List<Dictionary<string, int>>();
            var len = new List<int>();
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in passages)
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                var total = 0;
                // The file name and heading count as words of the passage, so "the leave policy" finds leave-policy.md.
                foreach (var t in Tokens(p.File.Replace('/', ' ').Replace('-', ' ').Replace('_', ' ') + " " + p.Heading + " " + p.Text))
                {
                    int c;
                    counts.TryGetValue(t, out c);
                    counts[t] = c + 1;
                    total++;
                }
                foreach (var t in counts.Keys)
                {
                    int d;
                    df.TryGetValue(t, out d);
                    df[t] = d + 1;
                }
                tf.Add(counts);
                len.Add(Math.Max(1, total));
            }
            _passages = passages;
            _tf = tf;
            _len = len;
            _df = df;
            _avgLen = len.Count == 0 ? 1 : len.Average();
            Files = read;
            IndexedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Paragraph-sized passages with the line numbers they came from.</summary>
        public static List<BearingPassage> Split(string relative, string full, string text)
        {
            var result = new List<BearingPassage>();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var heading = string.Empty;
            var sb = new StringBuilder();
            var start = 0;
            var last = 0;
            Action flush = () =>
            {
                var body = sb.ToString().Trim();
                if (body.Length > 0) result.Add(new BearingPassage { File = relative, FullPath = full, FromLine = start, ToLine = last, Heading = heading, Text = body });
                sb.Length = 0;
                start = 0;
            };
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd();
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    if (sb.Length >= TargetChars) flush();
                    else if (sb.Length > 0) sb.Append('\n');
                    continue;
                }
                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    var h = trimmed.TrimStart('#').Trim();
                    if (h.Length > 0)
                    {
                        flush();
                        heading = h;
                    }
                }
                if (line.Length > MaxChars)
                {
                    flush();
                    for (var at = 0; at < line.Length;)
                    {
                        var take = Math.Min(MaxChars, line.Length - at);
                        if (at + take < line.Length)
                        {
                            var space = line.LastIndexOf(' ', at + take - 1, take);
                            if (space > at) take = space - at + 1;
                        }
                        result.Add(new BearingPassage { File = relative, FullPath = full, FromLine = i + 1, ToLine = i + 1, Heading = heading, Text = line.Substring(at, take).Trim() });
                        at += take;
                    }
                    continue;
                }
                if (sb.Length > 0 && sb.Length + line.Length > MaxChars) flush();
                if (start == 0) start = i + 1;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
                last = i + 1;
            }
            flush();
            return result;
        }
    }
}
