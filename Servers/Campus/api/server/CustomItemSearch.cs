using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The store search bar (GET /api/customAvatarItems/v1/search?searchQuery=...), ranked.
    ///
    /// The real RecNet answers this from Elasticsearch, so a player types "eyepatches" and gets the
    /// eyepatches. Here it was a case-insensitive substring test on Name-or-Description, which is
    /// wrong in both directions: "eyepatches" matched NOTHING (no item spells the plural), and "eye"
    /// matched a belt whose description mentions eyes - in date order, so the belt could come first.
    ///
    /// Now: the query and each item are tokenised on word boundaries and lightly stemmed
    /// (eyepatches -> eyepatch, hoodies -> hoodie, glasses -> glass), every query token must be
    /// found in the item - in its name (strong), in its name with the spaces removed (so
    /// "eyepatch" finds "Eye Patch" and "top hat" finds "TopHat"), or in its description (weak) -
    /// and the hits are scored: an exact name first, then a name that starts with the query, then
    /// the phrase inside the name, then word matches, then description-only matches. Ties go to
    /// the newest item, which is also the order of an empty query.
    ///
    /// `ordering` is the client's PBFFECJMOHO: SearchScoreDescending 0, HotScoreDescending 1,
    /// Newest 2, Oldest 3, PriceAscending 4, PriceDescending 5, FeaturedFirst 6. Nothing records a
    /// hot score, so 1 ranks like 0.
    ///
    /// Each catalog record is indexed once, lazily, in a ConditionalWeakTable keyed on the record
    /// itself - so a reload of the catalog drops the old index with the old records, and the 24k
    /// item scan per search is a handful of string compares per item rather than re-tokenising
    /// every description on every keystroke (the client searches on every keystroke).
    /// </summary>
    public static class CustomItemSearch
    {
        private sealed class Indexed
        {
            public string Name = "";        // lower-cased, whitespace collapsed
            public string Squashed = "";    // Name with everything but letters and digits removed
            public string[] NameTokens = Array.Empty<string>();
            public string[] DescTokens = Array.Empty<string>();
            public string Created = "";
            public int Price;
            public bool Featured;
        }

        private static readonly ConditionalWeakTable<CustomItemsCache.Rec, Indexed> _index = new();

        private static Indexed Index(CustomItemsCache.Rec item) => _index.GetValue(item, Build);

        private static Indexed Build(CustomItemsCache.Rec item)
        {
            return new Indexed
            {
                Name = Collapse(item.Name),
                Squashed = Squash(item.Name),
                NameTokens = Tokens(item.Name),
                DescTokens = Tokens(item.Description),
                Created = item.Created,
                Price = item.Price,
                Featured = item.Featured
            };
        }

        // ---- text helpers ----------------------------------------------------------------------

        private static string Collapse(string s) =>
            string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private static string Squash(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string[] Tokens(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
                if (sb.Length > 0) { list.Add(Stem(sb.ToString())); sb.Clear(); }
            }
            if (sb.Length > 0) list.Add(Stem(sb.ToString()));
            return list.ToArray();
        }

        // A light English plural stem, enough for what a store is searched for: eyepatches ->
        // eyepatch, glasses -> glass, shoes -> shoe, hoodies -> hoodie, bunnies -> bunny. A word
        // ending in "ss" (dress, glass) is left alone, and short words are never touched.
        private static string Stem(string w)
        {
            if (w.Length <= 3) return w;
            if (w.EndsWith("ies") && w.Length > 4) return w[..^3] + "y";
            if (w.EndsWith("ss")) return w;
            if (w.EndsWith("es") && w.Length > 4)
            {
                string stemless = w[..^2];
                if (stemless.EndsWith("s") || stemless.EndsWith("x") || stemless.EndsWith("z") || stemless.EndsWith("ch") || stemless.EndsWith("sh"))
                    return stemless;
                return w[..^1];      // shoes -> shoe, hoodies handled above
            }
            if (w.EndsWith("s")) return w[..^1];
            return w;
        }

        // ---- scoring ---------------------------------------------------------------------------

        /// <summary>0 = no match. Higher is better.</summary>
        private static double Score(Indexed ix, string q, string qSquashed, string[] qTokens)
        {
            double score = 0;

            // Every query word has to be found somewhere - in the name (strongly), in the squashed
            // name (a word boundary the player didn't type), or in the description (weakly).
            foreach (var t in qTokens)
            {
                double best = 0;
                foreach (var n in ix.NameTokens)
                {
                    if (n == t) { best = Math.Max(best, 3); break; }
                    if (t.Length >= 2 && n.StartsWith(t)) best = Math.Max(best, 2);
                }
                // The word boundary the player didn't type: "eyepatch" for "Eye Patch", "top hat"
                // for "TopHat". Judged on the WHOLE query with its spaces removed, not the token,
                // so a short token can't hit the middle of an unrelated word ("die" in "hoodie").
                if (best < 1.5 && (qTokens.Length > 1 || qSquashed.Length >= 4) && ix.Squashed.Contains(qSquashed)) best = Math.Max(best, 1.5);
                if (best == 0)
                {
                    foreach (var d in ix.DescTokens)
                    {
                        if (d == t) { best = 1; break; }
                        if (t.Length >= 3 && d.StartsWith(t)) best = Math.Max(best, 0.75);
                    }
                }
                if (best == 0) return 0;
                score += best;
            }

            // Whole-query bonuses: the exact name, the name starting with what was typed, the
            // phrase inside the name, the phrase inside the name ignoring spaces.
            if (ix.Name == q) score += 100;
            else if (ix.Name.StartsWith(q)) score += 50;
            else if (ix.Name.Contains(q)) score += 25;
            else if (qSquashed.Length >= 3 && ix.Squashed.Contains(qSquashed)) score += 20;

            return score;
        }

        /// <summary>
        /// Filter <paramref name="items"/> to those matching <paramref name="query"/> (everything,
        /// when it is blank) and order them by <paramref name="ordering"/>.
        /// </summary>
        public static IEnumerable<CustomItemsCache.Rec> Search(IEnumerable<CustomItemsCache.Rec> items, string? query, int ordering)
        {
            string q = Collapse(query ?? "");
            string[] qTokens = Tokens(q);
            string qSquashed = Squash(q);
            bool searching = qTokens.Length > 0;

            IEnumerable<(CustomItemsCache.Rec item, Indexed ix, double score)> hits;
            if (searching)
            {
                hits = items.Select(it => { var ix = Index(it); return (it, ix, Score(ix, q, qSquashed, qTokens)); })
                            .Where(h => h.Item3 > 0);
            }
            else
            {
                hits = items.Select(it => (it, Index(it), 0.0));
            }

            IOrderedEnumerable<(CustomItemsCache.Rec item, Indexed ix, double score)> ordered = ordering switch
            {
                2 => hits.OrderByDescending(h => h.ix.Created),
                3 => hits.OrderBy(h => h.ix.Created),
                4 => hits.OrderBy(h => h.ix.Price).ThenByDescending(h => h.score),
                5 => hits.OrderByDescending(h => h.ix.Price).ThenByDescending(h => h.score),
                6 => hits.OrderByDescending(h => h.ix.Featured).ThenByDescending(h => h.score).ThenByDescending(h => h.ix.Created),
                _ => hits.OrderByDescending(h => h.score).ThenByDescending(h => h.ix.Created),
            };
            return ordered.Select(h => h.item);
        }
    }
}
