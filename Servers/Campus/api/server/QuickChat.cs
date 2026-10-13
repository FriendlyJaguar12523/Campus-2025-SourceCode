using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Personal quick chat. The game's quick chat menu is built from four phrase sets that the server hands out as game
    /// configs (Lifecycle.PSF.CannedChatOptions.CustomA / CustomB / RecRoom / Room): five phrases each, joined with a comma and a
    /// line break. A player edits their own sets on the website; <see cref="ApplyToConfigs"/> swaps just THEIR copy of those
    /// config values when the game asks for its configs, so nobody else sees them. The game reads configs at login, so a change
    /// shows from the next start.
    ///
    /// Safety: a quick chat phrase is sent as chat, so it is held to the same standard as typed chat - printable ASCII only, no
    /// commas or line breaks (they are the list separators and would split one phrase into two), a short length cap, and the
    /// chat swear filter (Sanitize.ContainsSwears). A phrase that fails is refused with a reason, never silently altered.
    /// </summary>
    public static class QuickChat
    {
        public const int PhrasesPerSet = 5;   // the standard set size; a set may have more (SetDef.Defaults.Length is the real count)
        public const int MaxPhraseLength = 24;
        private const string ConfigPrefix = "Lifecycle.PSF.CannedChatOptions.";

        public sealed class SetDef
        {
            public string Key = "";        // CustomA / CustomB / RecRoom / Room (also the config key suffix)
            public string Label = "";
            public string[] Defaults = Array.Empty<string>();
            public bool TrailingBreak = true;   // the stock value ends with a line break, except the RecRoom set
        }

        // The stock sets, exactly as Gameconfigs.json has them, in the order the in-game quick chat panel shows its four tabs:
        // the "Hi!" tab, the location-pin tab, thumbs up, thumbs down (confirmed from the panel: the first tab lists Hi!/Bye!/Ready?/BRB/Let's go!).
        public static readonly SetDef[] Sets =
        {
            new SetDef { Key = "RecRoom", Label = "Greetings (the Hi! tab)", Defaults = new[] { "Hi!", "Bye!", "Ready?", "BRB", "Let’s go!" }, TrailingBreak = false },
            new SetDef { Key = "Room",    Label = "Room (the location pin tab)", Defaults = new[] { "Come with me", "Help!", "Let's party up!", "Let's be friends!", "May I pick the room?" }, TrailingBreak = true },
            new SetDef { Key = "CustomA", Label = "Positive (the thumbs up tab)", Defaults = new[] { "Yes", "OK", "Thanks", "LOL", "You look great!", "Nice!", "Good game!", "Wait for me!" }, TrailingBreak = true },
            new SetDef { Key = "CustomB", Label = "Negative (the thumbs down tab)", Defaults = new[] { "No", "Boo", "Sorry", "I don't like this", "What the coach?!" }, TrailingBreak = true },
        };

        public static SetDef? Find(string? key) =>
            Sets.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>One phrase, cleaned for storage, or null with the reason it was refused. An empty phrase is valid (= keep the stock one).</summary>
        public static string? Clean(string? raw, out string? problem)
        {
            problem = null;
            string s = (raw ?? "").Replace("\r", " ").Replace("\n", " ").Replace(",", " ").Replace("\t", " ");
            s = string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (s.Length == 0) return "";
            foreach (char c in s)
                if (c < 32 || c > 126) { problem = "use plain letters, numbers and basic punctuation only"; return null; }
            if (s.Length > MaxPhraseLength) { problem = $"keep it to {MaxPhraseLength} characters or fewer"; return null; }
            if (Sanitize.ContainsSwears(s)) { problem = "that isn't allowed in chat"; return null; }
            return s;
        }

        /// <summary>The config VALUE for a set: the player's phrases with the stock phrase wherever a slot is empty.</summary>
        public static string ConfigValue(SetDef set, IReadOnlyList<string> custom)
        {
            var items = new string[set.Defaults.Length];
            for (int i = 0; i < items.Length; i++)
            {
                string c = i < custom.Count ? custom[i] : "";
                items[i] = string.IsNullOrWhiteSpace(c) ? set.Defaults[i] : c;
            }
            // "a,\r\nb,\r\n...e" - every phrase but the last is followed by a comma, as in the stock values
            string joined = string.Join(",\r\n", items);
            return set.TrailingBreak ? joined + "\r\n" : joined;
        }

        /// <summary>
        /// A copy of the game configs with this viewer's own quick chat sets swapped in, or null when they have none (the common case:
        /// the cached shared copy is served untouched). The parsed shared copy is kept so the per-player cost is one clone.
        /// </summary>
        public static string? ApplyToConfigs(string sharedConfigJson, ulong viewerId)
        {
            if (viewerId == 0) return null;
            var mine = SitePrefsDB.Get(viewerId).QuickChat;
            if (mine == null || mine.Count == 0) return null;

            JArray parsed;
            lock (_lock)
            {
                if (!ReferenceEquals(_parsedFor, sharedConfigJson))
                {
                    _parsed = JArray.Parse(sharedConfigJson);
                    _parsedFor = sharedConfigJson;
                }
                parsed = (JArray)_parsed!.DeepClone();
            }

            int swapped = 0;
            foreach (var entry in parsed)
            {
                string key = (string?)entry["Key"] ?? "";
                if (!key.StartsWith(ConfigPrefix, StringComparison.Ordinal)) continue;
                var set = Find(key.Substring(ConfigPrefix.Length));
                if (set == null || !mine.TryGetValue(set.Key, out var phrases) || phrases == null || phrases.All(string.IsNullOrWhiteSpace)) continue;
                entry["Value"] = ConfigValue(set, phrases);
                swapped++;
            }
            if (swapped == 0) return null;
            return parsed.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static readonly object _lock = new();
        private static string? _parsedFor;
        private static JArray? _parsed;
    }
}
