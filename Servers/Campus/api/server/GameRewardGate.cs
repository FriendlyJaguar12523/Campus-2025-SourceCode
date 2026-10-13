using System;
using System.Collections.Generic;
using Campus_live_rewrite.api.server.Classes;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Rate limit on POST /api/gamerewards/v1/request - the whole request, not just the reward
    /// box it may offer.
    ///
    /// The client asks for a reward twice around the end of a game: rewardType=PostGameActivity
    /// (the game complete) and, once a day, rewardType=FirstActivityOfDay. Before this the
    /// currency and XP were paid on EVERY PostGameActivity - only the three-choice box sat behind
    /// a cooldown - so a client replaying the call farmed tokens and levels.
    ///
    /// The rule:
    ///   - FirstActivityOfDay is once per day (PlayerDB.TryClaimDailyReward, unchanged) and is
    ///     always let through when it is the day's first.
    ///   - PostGameActivity is at most ONE per ConfigCampus.GameRewardCompleteCooldownSeconds per
    ///     player ... EXCEPT when the player's daily FirstActivityOfDay was accepted within the
    ///     last GameRewardDailyGraceSeconds: the two arrive together at the end of the day's
    ///     first game, and that game complete must not be eaten by a cooldown the previous game
    ///     started. The grace is consumed by the one game complete it lets through.
    /// A refused request is answered null (nothing offered) and pays nothing.
    /// </summary>
    public static class GameRewardGate
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<ulong, DateTime> _lastComplete = new();
        private static readonly Dictionary<ulong, DateTime> _dailyGrace = new();

        /// <summary>Call when a FirstActivityOfDay claim was ACCEPTED (it was the day's first).</summary>
        public static void NoteDailyAccepted(ulong playerId)
        {
            lock (_lock) _dailyGrace[playerId] = DateTime.UtcNow;
        }

        /// <summary>
        /// Whether a PostGameActivity may be paid now. Marks the clock when it may.
        /// <paramref name="why"/> explains a refusal.
        /// </summary>
        public static bool TryGameComplete(ulong playerId, out string why)
        {
            why = "";
            int cd = ConfigCampus.GameRewardCompleteCooldownSeconds;
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                bool graced = _dailyGrace.TryGetValue(playerId, out var daily)
                              && (now - daily).TotalSeconds <= ConfigCampus.GameRewardDailyGraceSeconds;
                if (graced)
                {
                    _dailyGrace.Remove(playerId);   // one game complete rides on the daily bonus
                    _lastComplete[playerId] = now;
                    return true;
                }
                if (cd > 0 && _lastComplete.TryGetValue(playerId, out var last) && (now - last).TotalSeconds < cd)
                {
                    why = $"game complete {Math.Round((now - last).TotalSeconds)}s after the last one (limit one per {cd}s)";
                    return false;
                }
                _lastComplete[playerId] = now;
                return true;
            }
        }

        /// <summary>The gate for a request of <paramref name="type"/>; only game completes are limited here.</summary>
        public static bool Allow(ulong playerId, GameRewardType type, out string why)
        {
            why = "";
            return type != GameRewardType.PostGameActivity || TryGameComplete(playerId, out why);
        }
    }
}
