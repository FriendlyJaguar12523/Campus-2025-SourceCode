namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Anti-farm throttle for the one currency-minting endpoint, POST /api/storefronts/v2/balance.
    ///
    /// It answers Allow or Deny per award attempt, keyed on the player's LIVE room instance
    /// (decided server-side from the heartbeat, never the request body):
    ///
    ///   * RATE LIMIT -> Deny. At most one award per player per BalanceRateLimitSeconds. A second
    ///     award that arrives sooner - e.g. the client glitching and firing twice within seconds -
    ///     simply doesn't go through. No currency, no ban: the player is not punished for a glitch.
    ///
    ///   * LONG SESSION -> Deny. A player parked in ONE instance past BalanceMaxInstanceMinutes, or
    ///     already awarded BalanceMaxAwardsPerInstance times in it, is refused further awards. The
    ///     counters reset the moment they move to a different room instance (legit players move;
    ///     farmers park).
    ///
    /// All in-memory and self-pruning. No bans are issued here.
    /// </summary>
    public static class BalanceGuard
    {
        public enum Decision { Allow, Deny }

        private sealed class State
        {
            public long InstanceId;
            public DateTime FirstSeenUtc;
            public DateTime LastAwardUtc;
            public int Awards;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<ulong, State> _state = new();

        /// <summary>
        /// Decide what to do with an award attempt BEFORE any currency is granted. On Allow the
        /// attempt is recorded (advances the rate-limit clock and the per-instance total).
        /// </summary>
        public static (Decision decision, string reason) Evaluate(ulong playerId, long roomInstanceId)
        {
            if (!ConfigCampus.BalanceGuardEnabled)
                return (Decision.Allow, "");

            var now = DateTime.UtcNow;
            lock (_lock)
            {
                // Fresh session whenever the player is in a different instance than last time
                // (or we have never seen them) - this is what resets the long-session counters.
                if (!_state.TryGetValue(playerId, out var st) || st.InstanceId != roomInstanceId)
                {
                    st = new State { InstanceId = roomInstanceId, FirstSeenUtc = now, LastAwardUtc = DateTime.MinValue };
                    _state[playerId] = st;
                }

                // RATE LIMIT -> deny (the glitch-guard). Not an award, so nothing is recorded.
                double sinceLast = (now - st.LastAwardUtc).TotalSeconds;
                if (sinceLast < Math.Max(1, ConfigCampus.BalanceRateLimitSeconds))
                {
                    return (Decision.Deny,
                        $"rate-limited: {sinceLast:F0}s since last award (< {ConfigCampus.BalanceRateLimitSeconds}s) in instance {roomInstanceId}");
                }

                // LONG SESSION -> deny.
                double mins = (now - st.FirstSeenUtc).TotalMinutes;
                if (mins >= ConfigCampus.BalanceMaxInstanceMinutes ||
                    st.Awards >= ConfigCampus.BalanceMaxAwardsPerInstance)
                {
                    return (Decision.Deny,
                        $"parked in instance {roomInstanceId} for {mins:F0} min / {st.Awards} awards - denying");
                }

                // Allow: record the award.
                st.LastAwardUtc = now;
                st.Awards++;
                return (Decision.Allow, "");
            }
        }

        /// <summary>Forget a player's live session (e.g. on disconnect).</summary>
        public static void ClearSession(ulong playerId)
        {
            lock (_lock) { _state.Remove(playerId); }
        }
    }
}
