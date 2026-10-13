using System.Text;
using System.Text.RegularExpressions;

namespace Campus_live_rewrite.api.server.Classes
{
    public class Sanitize
    {
        public string Context { get; set; } = "RoomChat";
        public int Intent { get; set; } = 1;
        public bool PreRemoveBlockedCharacters { get; set; } = true;
        public string ReplacementChar { get; set; } = "*";
        public string Value { get; set; } = "";
        public int ruleset { get; set; } = 0;

        private const char MaskChar = '*';


        // Words that should never be censored
        private static readonly HashSet<string> Whitelist = new(StringComparer.OrdinalIgnoreCase)
        {
            "password",
            "pass",
            "class",
            "assistant",
            "account"
        };

        // Bad words in NORMALIZED form (see Normalize): lowercase letters only, leet
        // characters folded, runs of 3+ identical letters collapsed to 2. Both sides of
        // the comparison go through the same normaliser, so a plain set lookup catches
        // "F.U.C.K" and "b1tch" with no pattern matching at all.
        //
        // This replaced 1749 compiled regexes of the form ^f.{0,1}u.{0,1}c.{0,1}k$, run
        // against every token of every message. The .{0,1} gaps were meant to jump
        // separator characters - but Normalize already strips every non-letter before the
        // match, so all they did was let a 3-letter entry match a 4-or-5-letter innocent
        // word: "fag" matched FLAG, "gay" matched GRAY, "cum" matched CHUM, "hoe" matched
        // HOSE. Exact comparison on the normalised token is both correct and far cheaper.
        private static readonly HashSet<string> Swears = new(StringComparer.Ordinal);

        static Sanitize()
        {
            // Absolute path. This runs in a static constructor, so a miss here throws
            // TypeInitializationException on EVERY caller - room chat, DM threads, room
            // names, usernames, display names, bios. A missing word list must degrade to
            // "nothing is filtered", never take those endpoints down.
            string path = Path.Combine(Environment.CurrentDirectory, "Data", "badwords.txt");

            try
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[Sanitize] word list not found at {path} - nothing will be filtered");
                    return;
                }

                int masked = 0;
                foreach (var line in File.ReadAllLines(path))
                {
                    // A masked entry ("f***", "****..just", "h*ll") is somebody's already-censored
                    // OUTPUT, not a word: normalised it becomes "just", "hll", "f" - which is how
                    // "just" got censored in chat. Skipped, never learnt.
                    if (line.Contains('*')) { masked++; continue; }
                    var normalized = Normalize(line);
                    if (normalized.Length >= 3) Swears.Add(normalized);
                }
                if (masked > 0) Console.WriteLine($"[Sanitize] skipped {masked} masked (*) entries in the word list");

                Console.WriteLine($"[Sanitize] loaded {Swears.Count} words from {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sanitize] could not load word list ({ex.Message}) - nothing will be filtered");
            }
        }

        /* ----------------------------------------------------
         * PUBLIC API
         * --------------------------------------------------*/

        public static bool ContainsSwears(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            if (ConfigCampus.AllowSwears)
                return false;

            if (SplitKeepingWhitespace(message).Any(chunk => !IsWhitespace(chunk) && ChunkIsSwear(chunk))) return true;
            return FindConcealed(message).Count > 0;
        }

        public static string SanitizeMessage(string message, char mask = MaskChar)
        {
            if (string.IsNullOrWhiteSpace(message))
                return message;

            var sb = new StringBuilder(message.Length);

            // Two passes, because they catch different evasions.
            //
            //  1. The whole whitespace-delimited chunk, punctuation stripped. This is what
            //     catches "f.u.c.k", "f-u-c-k", "f*ck" and zero-width characters wedged
            //     between letters - splitting on \W+ first would turn those into
            //     single-letter tokens that are too short to test, which is exactly how
            //     they used to get through.
            //
            //  2. Failing that, the individual words inside the chunk, so "hey,bitch"
            //     (one chunk, normalising to the harmless "heybitch") still gets caught on
            //     the word "bitch".
            //  3. The CONCEALED forms neither of those see, run first over the whole message so
            //     the masked characters are already in place when the token passes look:
            //     letters spread over several tokens ("s h i t", "sh it") and a swear
            //     repeated or lightly suffixed inside one token ("shitshit", "shitty").
            //     See FindConcealed for the rules that keep "this hit" and "cockpit" alone.
            var concealed = FindConcealed(message);
            if (concealed.Count > 0)
            {
                var chars = message.ToCharArray();
                foreach (var (start, end) in concealed)
                    for (int i = start; i < end; i++)
                        if (char.IsLetterOrDigit(chars[i])) chars[i] = mask;
                Console.WriteLine($"[Sanitize] Censored concealed word(s) in message: '{message}'");
                message = new string(chars);
            }

            foreach (var chunk in SplitKeepingWhitespace(message))
            {
                if (IsWhitespace(chunk))
                {
                    sb.Append(chunk);
                    continue;
                }

                if (ChunkIsSwear(chunk))
                {
                    sb.Append(new string(mask, chunk.Length));
                    Console.WriteLine($"[Sanitize] Censored word in message: '{chunk}'");
                    continue;
                }

                foreach (var token in Regex.Split(chunk, @"(\W+)"))
                {
                    if (IsWord(token) && ChunkIsSwear(token))
                    {
                        sb.Append(new string(mask, token.Length));
                        Console.WriteLine($"[Sanitize] Censored word in message: '{token}'");
                    }
                    else
                    {
                        sb.Append(token);
                    }
                }
            }

            return sb.ToString();
        }

        /* ----------------------------------------------------
         * INTERNAL HELPERS
         * --------------------------------------------------*/

        private static string[] SplitKeepingWhitespace(string input) =>
            Regex.Split(input, @"(\s+)");

        // Swears of 4+ letters, the only ones worth hunting inside other text: the 3-letter
        // entries ("ass", "cum") are exactly the ones that live inside innocent words.
        private static readonly Lazy<string[]> LongSwears = new(() =>
            Swears.Where(w => w.Length >= 4).OrderByDescending(w => w.Length).ToArray());

        /// <summary>
        /// Character spans [start, end) of swears that the token passes cannot see:
        ///
        ///   spread   - the letters of one swear spread across several whitespace-separated
        ///              tokens ("s h i t", "sh it", "s.h.i t"). A match counts only when it
        ///              starts at the start of a token and ends at the end of one, covering
        ///              whole tokens - so "this hit" (thi|shit spans two partial tokens) is
        ///              not a match, "s h i t" is.
        ///   compound - one token that IS a swear repeated or lightly suffixed: "shitshit",
        ///              "shitty", "fucked". The rest of the token after the swear(s) must be
        ///              at most 2 letters, and the swear must start the token - so "cockpit"
        ///              (3 letters after) and "scunthorpe" (swear in the middle) are left alone.
        ///
        /// Whole-token exact matches are the caller's job (ChunkIsSwear); this only adds the
        /// forms it misses.
        /// </summary>
        private static List<(int start, int end)> FindConcealed(string message)
        {
            var spans = new List<(int, int)>();
            if (string.IsNullOrWhiteSpace(message) || ConfigCampus.AllowSwears || Swears.Count == 0) return spans;

            // Normalised letter stream with, for every letter, its index in the original.
            var letters = new StringBuilder(message.Length);
            var origIdx = new List<int>(message.Length);
            var tokenStarts = new HashSet<int>();   // stream positions that begin a token
            var tokenEnds = new HashSet<int>();     // stream positions just past a token's last letter
            bool inToken = false;
            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                if (char.IsWhiteSpace(c))
                {
                    if (inToken) { tokenEnds.Add(letters.Length); inToken = false; }
                    continue;
                }
                string n = Normalize(c.ToString());
                if (n.Length == 0) continue;   // punctuation / digit that folds to nothing
                if (!inToken) { tokenStarts.Add(letters.Length); inToken = true; }
                letters.Append(n[0]);
                origIdx.Add(i);
            }
            if (inToken) tokenEnds.Add(letters.Length);
            string stream = letters.ToString();
            if (stream.Length < 4) return spans;

            // Per-token bounds in stream coordinates, for the compound rule.
            var tokens = tokenStarts.OrderBy(x => x).Zip(tokenEnds.OrderBy(x => x), (a, b) => (a, b)).ToList();

            foreach (var swear in LongSwears.Value)
            {
                int from = 0;
                while ((from = stream.IndexOf(swear, from, StringComparison.Ordinal)) >= 0)
                {
                    int end = from + swear.Length;
                    bool spread = tokenStarts.Contains(from) && tokenEnds.Contains(end)
                                  && tokens.Count(t => t.a >= from && t.b <= end) >= 2;
                    bool compound = false;
                    if (!spread)
                    {
                        var tok = tokens.FirstOrDefault(t => t.a <= from && end <= t.b);
                        if (tok != default && tok.a == from)
                        {
                            // Strip every leading repeat of the swear, then allow a tiny tail.
                            int p = from;
                            while (p + swear.Length <= tok.b && string.CompareOrdinal(stream, p, swear, 0, swear.Length) == 0) p += swear.Length;
                            int tail = tok.b - p;
                            // The tail has to be an actual suffix ("s", "y", "ed", "er"...). Any
                            // two letters used to count, so "hello" was hell+o and censored.
                            string tailText = tail > 0 ? stream.Substring(p, tail) : "";
                            bool suffix = tail == 0 || CompoundSuffixes.Contains(tailText);
                            if (tail <= 2 && suffix && (p > from + swear.Length || tail > 0))   // repeated, or one swear + short suffix
                            {
                                compound = true; end = tok.b;
                                string whole = stream.Substring(tok.a, tok.b - tok.a);
                                if (Whitelist.Contains(whole) || Swears.Contains(whole)) compound = false;   // exact forms are the token pass's
                            }
                        }
                    }
                    if (spread || compound)
                    {
                        if (!spans.Any(x => x.Item1 <= origIdx[from] && origIdx[end - 1] < x.Item2))
                            spans.Add((origIdx[from], origIdx[end - 1] + 1));
                        from = end;
                    }
                    else from++;
                }
            }
            return spans;
        }

        private static readonly HashSet<string> CompoundSuffixes = new(StringComparer.Ordinal)
            { "s", "y", "z", "ed", "er", "es", "in", "ie", "ty", "rs", "ah" };

        private static bool IsWhitespace(string s) =>
            s.Length > 0 && char.IsWhiteSpace(s[0]);

        private static bool IsWord(string input) =>
            input.Any(char.IsLetterOrDigit);

        private static bool ChunkIsSwear(string raw)
        {
            var normalized = Normalize(raw);

            if (normalized.Length < 3) return false;
            if (Whitelist.Contains(normalized)) return false;
            if (Swears.Contains(normalized)) return true;

            // "fuuuuck". Normalize leaves runs at 2 ("fuuck") so that "ass" does not
            // become "as", but that means a stretched word no longer matches its entry.
            // Squeezing every run to a single letter gives "fuck", which does. Only the
            // TOKEN is squeezed - the word list keeps its 2-letter runs, so this cannot
            // make the innocent "as" match the entry "ass".
            var squeezed = Regex.Replace(normalized, @"(.)\1+", "$1");
            return squeezed.Length >= 3
                && !Whitelist.Contains(squeezed)
                && Swears.Contains(squeezed);
        }

        private static string Normalize(string input)
        {
            input = input.ToLowerInvariant();

            input = Regex.Replace(input, @"[0@]", "o");
            input = Regex.Replace(input, @"[1!|]", "i");
            input = Regex.Replace(input, @"[$5]", "s");
            input = Regex.Replace(input, @"3", "e");
            input = Regex.Replace(input, @"7", "t");

            // Remove non letters
            input = Regex.Replace(input, @"[^a-z]", "");

            // Collapse repeats (fuuuuuck → fuuck)
            input = Regex.Replace(input, @"(.)\1{2,}", "$1$1");

            return input;
        }
    }
}
