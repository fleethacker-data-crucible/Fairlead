using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fairlead.Core.Secrets
{
    /// <summary>A pairing code from Lazarette (lzfl1.&lt;pairing id&gt;.&lt;secret&gt;): who Fairlead is to that vault.</summary>
    public sealed class LazarettePairing
    {
        private static readonly Regex CodeShape = new Regex("^lzfl1\\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\\.([A-Za-z0-9_-]{43})$");

        public string Id { get; private set; }
        public byte[] Secret { get; private set; }

        /// <summary>Null when the text is not a pairing code. Never echo the text back: it is a secret.</summary>
        public static LazarettePairing TryParse(string code)
        {
            if (code == null) return null;
            var m = CodeShape.Match(code.Trim());
            if (!m.Success) return null;
            var secret = LazaretteProtocol.FromBase64Url(m.Groups[2].Value);
            if (secret == null || secret.Length != 32) return null;
            return new LazarettePairing { Id = m.Groups[1].Value, Secret = secret };
        }

        public string ToCode() { return "lzfl1." + Id + "." + LazaretteProtocol.Base64Url(Secret); }

        /// <summary>For the UI: the last four characters of the pairing id, never the secret.</summary>
        public string Hint { get { return "\u2026" + Id.Substring(Id.Length - 4); } }
    }

    /// <summary>
    /// The Lazarette secrets hand-off, Fairlead's half (Lazarette docs/FAIRLEAD.md; reference: Lazarette
    /// dev/fairlead-peer.cjs). The request carries an HMAC proof, never the pairing secret; the answer is sealed under a
    /// key derived from the secret and Fairlead's nonce, so only the paired Lazarette can produce one that opens.
    /// </summary>
    public static class LazaretteProtocol
    {
        public const string Label = "lazarette-fairlead-v1";
        public const string PeerPath = "/peer/fairlead/v1/secrets";
        public const string PeerHeader = "X-Fleet-Peer";
        public const string PeerHeaderValue = "fairlead/1";
        public const string SealDomain = "LZFL-v1";
        public const string PayloadFormat = "lazarette-fairlead-secrets/1";
        public static readonly Regex Name = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,79}$");
        public static readonly Regex Revision = new Regex("^[0-9a-f]{32}$");
        private static readonly byte[] ZeroSalt = new byte[32];

        public static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] FromBase64Url(string text)
        {
            if (text == null) return null;
            var s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: return null;
            }
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { return null; }
        }

        public static string NewNonce()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        public static byte[] Hmac(byte[] key, byte[] data)
        {
            using (var h = new HMACSHA256(key)) return h.ComputeHash(data);
        }

        /// <summary>HKDF-SHA256 (RFC 5869) with a 32-byte zero salt and one 32-byte output block.</summary>
        public static byte[] Hkdf(byte[] ikm, string info)
        {
            var prk = Hmac(ZeroSalt, ikm);
            var infoBytes = Encoding.UTF8.GetBytes(info);
            var block = new byte[infoBytes.Length + 1];
            Buffer.BlockCopy(infoBytes, 0, block, 0, infoBytes.Length);
            block[infoBytes.Length] = 1;
            return Hmac(prk, block);
        }

        /// <summary>base64url(HMAC-SHA256(secret, label\nPOST\npath\npairing\ntime\nnonce\nnames\nifRevision)).</summary>
        public static string Proof(LazarettePairing pairing, long time, string nonce, IList<string> names, string ifRevision)
        {
            var lines = string.Join("\n", new[] { Label, "POST", PeerPath, pairing.Id, time.ToString(System.Globalization.CultureInfo.InvariantCulture), nonce, string.Join(",", names), ifRevision ?? string.Empty });
            return Base64Url(Hmac(pairing.Secret, Encoding.UTF8.GetBytes(lines)));
        }

        /// <summary>
        /// Opens a sealed answer: checks the tag in constant time before decrypting. Returns null when it does not open,
        /// which means the answer did not come from the paired Lazarette.
        /// </summary>
        public static byte[] Open(LazarettePairing pairing, string nonce, string state, string revision, byte[] blob)
        {
            if (blob == null || blob.Length < 64 || (blob.Length - 48) % 16 != 0) return null;
            var k = Hkdf(pairing.Secret, Label + " response|" + nonce);
            var encKey = Hkdf(k, SealDomain + "-enc");
            var macKey = Hkdf(k, SealDomain + "-mac");
            var aad = Encoding.UTF8.GetBytes(Label + "|" + pairing.Id + "|" + nonce + "|" + state + "|" + revision);
            var ctLength = blob.Length - 48;
            var expected = Tag(macKey, aad, blob, ctLength);
            var diff = 0;
            for (var i = 0; i < 32; i++) diff |= expected[i] ^ blob[16 + ctLength + i];
            if (diff != 0) return null;
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                var iv = new byte[16];
                Buffer.BlockCopy(blob, 0, iv, 0, 16);
                aes.IV = iv;
                try
                {
                    using (var d = aes.CreateDecryptor()) return d.TransformFinalBlock(blob, 16, ctLength);
                }
                catch (CryptographicException) { return null; }
            }
        }

        /// <summary>HMAC-SHA256(macKey, uint64-LE(len(aad)) || aad || IV || ciphertext); blob holds IV then ciphertext.</summary>
        internal static byte[] Tag(byte[] macKey, byte[] aad, byte[] blob, int ctLength)
        {
            var data = new byte[8 + aad.Length + 16 + ctLength];
            var len = BitConverter.GetBytes((ulong)aad.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(len);
            Buffer.BlockCopy(len, 0, data, 0, 8);
            Buffer.BlockCopy(aad, 0, data, 8, aad.Length);
            Buffer.BlockCopy(blob, 0, data, 8 + aad.Length, 16 + ctLength);
            return Hmac(macKey, data);
        }
    }
}
