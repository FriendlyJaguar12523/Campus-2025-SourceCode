using System;
using Campus_live_rewrite.api.dbs;

namespace Campus_live_rewrite.api.server.Classes
{
    public class Progress
    {
        public int Level { get; set; }
        public ulong PlayerId { get; set; }
        public int XP { get; set; }

        // ---- The real Rec Room progression curve, ported from RecFlare -------------------------
        // LEVEL_REQUIRED_XP[level] is the XP needed to LEAVE that level (indexed BY level). A
        // player's stored XP is the progress INTO their current level - NOT a lifetime total -
        // and every LEVEL_REQUIRED_XP[level] XP earned lifts them one level, carrying the
        // remainder forward. This replaces the old made-up "1 + xp/1000" rule; the previous author
        // left a note that the real curve should replace exactly that one method when it turned up.
        //
        // This is also what the client renders its progress bar against (see ConfigV2's
        // LevelProgressionMaps, which now sends this same table), so client and server agree on
        // what level an XP total is - the disagreement is what tripped the Level/XP anti-cheat
        // ("obscured") check and dropped players to their dorm.
        public static readonly int[] LEVEL_REQUIRED_XP = new int[]
        {
            0, 10, 10, 10, 20, 20, 20, 20, 20, 20, 20, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45,
            115, 115, 115, 115, 115, 115, 115, 115, 115, 115, 360, 360, 360, 360, 360, 360, 360,
            360, 360, 360, 1080, 1080, 1080, 1080, 1080, 1080, 1080, 1080, 1080, 1080,
            // 51..98: unreachable by play (20,000,000 XP each) - they exist only so the client's
            // table (ConfigV2 LevelProgressionMaps sends this) can render level 99 for the one
            // account that holds it (ConfigCampus.Level99Username). Everyone else is capped at
            // PLAYER_MAX_LEVEL below.
            20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000,
            20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000,
            20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000,
            20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000,
            20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000, 20000000
        };

        public static readonly int MAX_LEVEL = LEVEL_REQUIRED_XP.Length - 1;   // 99 (100 entries, 0..99)
        /// <summary>The level any ordinary account can reach. Only the Level99Username account goes past it.</summary>
        public const int PLAYER_MAX_LEVEL = 50;

        public static bool MayExceedPlayerCap(ulong playerId)
        {
            var acc = PlayerDB.GetAccountById(playerId);
            return acc != null && string.Equals(acc.Username, ConfigCampus.Level99Username, StringComparison.OrdinalIgnoreCase);
        }

        // Synthetic GiftDropId for level N's reward tile = BASE + N; Gameconfigs.json's
        // Lifecycle.PSF.Progression.RewardMap maps each to its image.
        public const int LEVEL_REWARD_GIFTDROP_BASE = 2_000_000;

        // XP a single game reward pays out. Tunable - see ConfigCampus.GameRewardXp (was a
        // constant 5, the reference's value, which made the top levels take ~216 games each).
        public static int GAME_REWARD_XP => ConfigCampus.GameRewardXp;

        /// <summary>
        /// Spend XP on any levels it now pays for. Takes a (level, xp) pair where xp is progress
        /// into the current level, and returns the pair after applying every level-up the xp
        /// covers - each level costs LEVEL_REQUIRED_XP[level], and the remainder carries forward.
        /// Mirrors RecFlare's applyLevelUps.
        /// </summary>
        public static (int level, int xp) ApplyLevelUps(int level, int xp)
        {
            if (level < 1) level = 1;
            if (xp < 0) xp = 0;
            // Play stops at the player cap; 51+ is not earned.
            while (level < PLAYER_MAX_LEVEL)
            {
                int cost = LEVEL_REQUIRED_XP[level];
                if (cost <= 0 || xp < cost) break;
                xp -= cost;
                level += 1;
            }
            // At the cap there is nothing left to buy; park any overflow at 0 so the pair stays
            // consistent. A level above the cap (the Coach's 99) is left exactly as stored.
            if (level == PLAYER_MAX_LEVEL) xp = 0;
            if (level > MAX_LEVEL) { level = MAX_LEVEL; xp = 0; }
            return (level, xp);
        }

        // The CUMULATIVE (lifetime) XP for a given (level, xp-into-level) pair. The client derives
        // the crown/nameplate LEVEL NUMBER from the XP value against LEVEL_REQUIRED_XP (proven: a
        // 160-XP account shows "level 10", and 150 = the cost to reach level 10). Our stored XP is
        // progress INTO the current level, so reporting it raw made the client recompute a low
        // level (e.g. Kyle at Level 50 / into-level XP 0 -> the client read XP 0 -> level 1). So we
        // REPORT cumulative XP; internal storage stays per-level.
        public static int CumulativeXpForLevel(int level, int xpIntoLevel)
        {
            if (level < 1) level = 1;
            if (level > MAX_LEVEL) level = MAX_LEVEL;
            long total = 0;
            for (int l = 1; l < level && l < LEVEL_REQUIRED_XP.Length; l++) total += LEVEL_REQUIRED_XP[l];
            if (xpIntoLevel > 0) total += xpIntoLevel;
            return (int)total;
        }

        public static Progress GetPlayerLevel(ulong player_id)
        {
            var account = PlayerDB.GetFullAccount(player_id);
            if (account == null || account.player == null)
            {
                return new Progress { Level = 1, PlayerId = player_id, XP = 0 };
            }

            int level = account.player.Level;
            int xp = account.player.XP;

            // Normalise to a consistent (level, xp) pair before reporting it. Every legacy account
            // has XP 0 (the 2023 server only incremented Level, never tracked XP), and XP 0 is
            // consistent at any level in this per-level model - so nothing jumps. A stored pair
            // that somehow carries leftover XP is settled through the ladder here.
            if (ConfigCampus.EnforceLevelXpConsistency)
            {
                var (cLevel, cXp) = ApplyLevelUps(level < 1 ? 1 : level, xp);
                if (cLevel != level || cXp != xp)
                {
                    Console.WriteLine($"[progression] {account.player.Id}: stored Level {level}/XP {xp} " +
                                      $"normalised to Level {cLevel}/XP {cXp}");
                    level = cLevel; xp = cXp;
                }
            }
            else if (level < 1)
            {
                level = 1;
            }

            // Level 99 is the Coach's alone; anyone else past the player cap is shown at the cap.
            if (level > PLAYER_MAX_LEVEL && !MayExceedPlayerCap(account.player.Id)) { level = PLAYER_MAX_LEVEL; xp = 0; }
            if (level > PLAYER_MAX_LEVEL) { level = MAX_LEVEL; xp = 0; }

            // Report CUMULATIVE XP (see CumulativeXpForLevel) so the client computes the SAME level
            // for the crown/nameplate that we stored - not a low level recomputed from into-level XP.
            return new Progress { Level = level, PlayerId = account.player.Id, XP = CumulativeXpForLevel(level, xp) };
        }
    }
}
