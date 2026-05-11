using System;
using System.Security.Cryptography;
using System.Text;

namespace AutoTabOrganiser.Util
{
    internal static class Hashing
    {
        public static string Sha256Hex(string text)
        {
            if (text == null) text = string.Empty;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
