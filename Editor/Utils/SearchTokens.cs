using System;
using System.Collections.Generic;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Helpers for Unity's whitespace-separated search-filter tokens ("t:Prefab l:Enemy"):
    /// case-insensitive whole-token containment and toggling. Pure string functions.
    /// </summary>
    internal static class SearchTokens
    {
        /// <summary>Whitespace-token containment, case-insensitive ("t:Prefab" in "boss t:Prefab").</summary>
        internal static bool HasSearchToken(string text, string token)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var part in text.Split(' '))
                if (part.Equals(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Adds the token to the search text, or removes it when already present.</summary>
        internal static string ToggleSearchToken(string text, string token)
        {
            text ??= "";
            if (!HasSearchToken(text, token))
                return (text + " " + token).Trim();
            var parts = new List<string>();
            foreach (var part in text.Split(' '))
                if (part.Length > 0 && !part.Equals(token, StringComparison.OrdinalIgnoreCase))
                    parts.Add(part);
            return string.Join(" ", parts);
        }
    }
}
