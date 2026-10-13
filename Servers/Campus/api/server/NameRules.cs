using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// One set of rules for every place a name can be set - the website signup, the client's
    /// first-run "choose a username" (acc/account/me/username), the in-game @name change
    /// (account/me/username) and the in-game display-name change (account/me/displayname).
    /// Before this each path had its own checks and the gaps between them were the spoofing
    /// holes: the first-run path took ANY string (spaces, control characters, zero-width joiners,
    /// unlimited length, no swear filter), @name uniqueness was case-sensitive (so "kyle" sat next
    /// to "Kyle"), and a display name could be blank, all punctuation, or an admin's name.
    ///
    ///   @username     3-20 chars, [A-Za-z0-9_], unique case-insensitively, not a reserved word,
    ///                 passes the swear filter.
    ///   display name  1-15 chars of printable ASCII, whitespace collapsed, at least one letter
    ///                 or digit, passes the swear filter, and may not impersonate an owner /
    ///                 staff account or a reserved word once look-alike noise is stripped
    ///                 ("K y l e", "Coach_", "c0ach" are all "the same name" for this check).
    /// </summary>
    public static class NameRules
    {
        public const int UsernameMin = 3, UsernameMax = 20, DisplayNameMax = 15;

        // Names nobody may hold, in any casing or with look-alike decoration. Owner/staff accounts
        // themselves are protected by AccountLock's exemption list (their real usernames).
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "admin", "administrator", "moderator", "mod", "campus", "system", "root", "staff",
            "support", "null", "undefined", "everyone", "here", "recroom", "official", "owner",
            "developer", "dev", "coach", "campusteam", "campusstaff", "campusmod"
        };

        private static readonly Regex UsernameShape = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

        /// <summary>Fold a name to what a reader perceives: lowercase, letters and digits only,
        /// common digit-for-letter swaps undone. Two names that fold alike are "the same name".</summary>
        public static string Fold(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s.ToLowerInvariant())
            {
                char c = ch switch { '0' => 'o', '1' => 'l', '3' => 'e', '4' => 'a', '5' => 's', '7' => 't', '@' => 'a', '$' => 's', '|' => 'l', _ => ch };
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Does this name fold to a reserved word or to an owner/staff account's name
        /// (other than <paramref name="self"/>'s own)?</summary>
        public static bool Impersonates(string name, ulong self)
        {
            string f = Fold(name);
            if (f.Length == 0) return false;
            foreach (var staff in ConfigCampus.LockExemptUsernames ?? Array.Empty<string>())
            {
                if (Fold(staff) != f) continue;
                ulong id = PlayerDB.GetAccountIdByUsernameCI(staff);
                if (id != 0 && id == self) return false;   // it IS that account - its own name is fine
                return true;
            }
            return Reserved.Any(r => Fold(r) == f);
        }

        /// <summary>Null = acceptable (and <paramref name="clean"/> is the value to store); otherwise the player-facing reason.</summary>
        public static string? CheckUsername(string? raw, ulong self, out string clean)
        {
            clean = (raw ?? "").Trim();
            if (clean.Length < UsernameMin) return $"Your @name must have at least {UsernameMin} characters.";
            if (clean.Length > UsernameMax) return $"Your @name must not exceed {UsernameMax} characters.";
            if (!UsernameShape.IsMatch(clean)) return "Your @name can only use letters, numbers and underscores.";
            if (Sanitize.ContainsSwears(clean)) return "Your @name contains a word that isn't allowed.";
            if (Impersonates(clean, self)) return "That @name isn't available.";
            ulong taken = PlayerDB.GetAccountIdByUsernameCI(clean);
            if (taken != 0 && taken != self) return "That @name is already taken.";
            return null;
        }

        public static string? CheckDisplayName(string? raw, ulong self, out string clean)
        {
            clean = Regex.Replace((raw ?? "").Trim(), @"\s+", " ");
            if (clean.Length == 0) return "Your display name can't be empty.";
            if (clean.Length > DisplayNameMax) return $"Your display name must not exceed {DisplayNameMax} characters.";
            if (clean.Any(c => c < 0x20 || c > 0x7E)) return "Your display name can only use letters, numbers and basic punctuation.";
            if (!clean.Any(char.IsLetterOrDigit)) return "Your display name needs at least one letter or number.";
            if (Sanitize.ContainsSwears(clean)) return "Your display name contains a word that isn't allowed.";
            if (Impersonates(clean, self)) return "That display name isn't available.";
            return null;
        }
    }
}
