using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Fairlead.Core.Protocol
{
    /// <summary>
    /// A multipart/form-data request body (an audio upload) that Fairlead forwards without interpreting the file: the parts are
    /// kept byte for byte, and only the text of one field (the model name) can be replaced for the target that is tried.
    /// </summary>
    public sealed class RawUpload
    {
        private sealed class Part
        {
            public string Headers;
            public string Name;
            public byte[] Data;
        }

        private static readonly Regex NameOf = new Regex("(?<![A-Za-z])name=(?:\"([^\"]*)\"|([^; \r\n]+))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex FileOf = new Regex("filename=", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private readonly List<Part> _parts = new List<Part>();

        public string ContentType { get; private set; }
        public string Boundary { get; private set; }

        /// <summary>Reads the parts of a multipart body. Returns null with an error when it is not one.</summary>
        public static RawUpload Parse(byte[] body, string contentType, out string error)
        {
            error = null;
            var m = contentType == null ? null : Regex.Match(contentType, "boundary=(?:\"([^\"]+)\"|([^;\\s]+))", RegexOptions.IgnoreCase);
            if (contentType == null || !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) || m == null || !m.Success)
            {
                error = "Send the audio as multipart/form-data (fields: file, model).";
                return null;
            }
            var boundary = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
            var starts = new List<int>();
            for (var at = IndexOf(body, delimiter, 0); at >= 0; at = IndexOf(body, delimiter, at + delimiter.Length)) starts.Add(at);
            if (starts.Count < 2) { error = "The multipart body has no parts."; return null; }
            var upload = new RawUpload { ContentType = contentType, Boundary = boundary };
            for (var i = 0; i + 1 < starts.Count; i++)
            {
                var from = starts[i] + delimiter.Length;
                var to = starts[i + 1];
                if (from + 1 < body.Length && body[from] == (byte)'\r' && body[from + 1] == (byte)'\n') from += 2;
                if (to >= 2 && body[to - 2] == (byte)'\r' && body[to - 1] == (byte)'\n') to -= 2;
                if (to < from) continue;
                var blank = IndexOf(body, new[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' }, from);
                if (blank < 0 || blank > to) { error = "A part of the multipart body has no header block."; return null; }
                var headers = Encoding.GetEncoding(28591).GetString(body, from, blank - from);
                var data = new byte[to - (blank + 4)];
                Buffer.BlockCopy(body, blank + 4, data, 0, data.Length);
                var n = NameOf.Match(headers);
                upload._parts.Add(new Part { Headers = headers, Name = !n.Success ? string.Empty : n.Groups[1].Success ? n.Groups[1].Value : n.Groups[2].Value, Data = data });
            }
            return upload;
        }

        /// <summary>The text of a non-file field, or null when there is none.</summary>
        public string Field(string name)
        {
            foreach (var p in _parts)
                if (string.Equals(p.Name, name, StringComparison.Ordinal) && !FileOf.IsMatch(p.Headers)) return Encoding.UTF8.GetString(p.Data);
            return null;
        }

        public bool HasFile
        {
            get
            {
                foreach (var p in _parts) if (FileOf.IsMatch(p.Headers)) return true;
                return false;
            }
        }

        /// <summary>The same upload with one text field set (added when it was missing). The file parts are untouched.</summary>
        public RawUpload WithField(string name, string value)
        {
            var copy = new RawUpload { ContentType = ContentType, Boundary = Boundary };
            var done = false;
            foreach (var p in _parts)
            {
                if (!done && string.Equals(p.Name, name, StringComparison.Ordinal) && !FileOf.IsMatch(p.Headers))
                {
                    copy._parts.Add(new Part { Headers = p.Headers, Name = p.Name, Data = Encoding.UTF8.GetBytes(value) });
                    done = true;
                }
                else copy._parts.Add(p);
            }
            if (!done) copy._parts.Add(new Part { Headers = "Content-Disposition: form-data; name=\"" + name + "\"", Name = name, Data = Encoding.UTF8.GetBytes(value) });
            return copy;
        }

        public byte[] ToBytes()
        {
            var latin1 = Encoding.GetEncoding(28591);
            using (var ms = new MemoryStream())
            {
                foreach (var p in _parts)
                {
                    var head = latin1.GetBytes("--" + Boundary + "\r\n" + p.Headers + "\r\n\r\n");
                    ms.Write(head, 0, head.Length);
                    ms.Write(p.Data, 0, p.Data.Length);
                    ms.WriteByte((byte)'\r');
                    ms.WriteByte((byte)'\n');
                }
                var end = latin1.GetBytes("--" + Boundary + "--\r\n");
                ms.Write(end, 0, end.Length);
                return ms.ToArray();
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            for (var i = start; i <= haystack.Length - needle.Length; i++)
            {
                var j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
