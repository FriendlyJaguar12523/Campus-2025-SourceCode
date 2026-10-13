using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The daily challenges (the watch's Challenges > Daily tab): which three objectives are served
    /// in GET /api/config/v2 "DailyObjectives", when the day rolls over, and what finishing all of
    /// them pays - a game request (the three-card reward picker), once per day.
    ///
    /// The set used to be types 20/21/22, which are the ONBOARDING objectives
    /// (OOBE_GoToLockerRoom / OOBE_GoToActivity / OOBE_FinishActivity - the 2025 client's enum has
    /// the same order as the reference table, read from its metadata). The client only fires those
    /// during the tutorial, so no one's challenges ever progressed: the live log has 3,000+
    /// myprogress reads and not one updateobjective. The defaults below are the everyday versions.
    ///
    /// Edited from the admin panel (Challenges tab), stored in Data/APIS/daily-objectives.json:
    ///   { "rewardEnabled": true, "xpPerObjective": 25, "sets": [[{"type":30,"score":1}, ...], ...] }
    /// A bare array (one set, or one set per day) is still read. Type ids are the ObjectiveType enum
    /// (reference .agents/skills/daily-objectives-config/SKILL.md). Players pick a change up on
    /// their next login (config/v2 is read then).
    ///
    /// Weekly challenges: NOT in this client build. It has no /api/challenge route at all (its API
    /// path table has objectives/v1/* and nothing for challenges; the live log has never seen one),
    /// only leftover enum names. api/server/WeeklyChallenge.cs serves a route nothing calls.
    /// </summary>
    public static class DailyChallenges
    {
        // 30 GoToRecCenter, 32 VisitACustomRoom, 31 FinishActivity.
        private static readonly int[][] DefaultSet = { new[] { 30, 1 }, new[] { 32, 1 }, new[] { 31, 1 } };

        private static readonly string File1 =
            System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "daily-objectives.json");

        private static readonly object _lock = new();
        private static List<List<Classes.DailyObjective>>? _sets;

        /// <summary>The config/v2 "DailyObjectives" value: seven day-sets (the client indexes by day).</summary>
        public static List<List<Classes.DailyObjective>> Sets { get { lock (_lock) return _sets ??= Load(); } }

        /// <summary>"file" when Data/APIS/daily-objectives.json supplied the sets, else "default".</summary>
        public static string Source { get; private set; } = "default";

        /// <summary>Finishing every challenge sends a game request. Off = XP per objective only.</summary>
        public static bool RewardEnabled { get; private set; } = true;

        /// <summary>How many objectives a group has to finish before its reward is paid.</summary>
        public static int RequiredFor(long group)
        {
            var s = Sets;
            if (s.Count == 0) return int.MaxValue;
            var set = group >= 0 && group < s.Count ? s[(int)group] : s[0];
            return set.Count == 0 ? int.MaxValue : set.Count;
        }

        /// <summary>The start of the current challenge day (UTC midnight). Progress from before it is stale.</summary>
        public static DateTime PeriodStartUtc => DateTime.UtcNow.Date;

        /// <summary>True when a group last reset (ClearedAt) before today - its progress belongs to an earlier day.</summary>
        public static bool IsStale(string? clearedAt) =>
            !DateTime.TryParse(clearedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            || t < PeriodStartUtc;

        /// <summary>
        /// Pay the daily reward: a game request (three token cards in the tokens box) pushed to the
        /// player's watch. The caller has already latched the group complete for today, so this runs
        /// once per player per day.
        /// </summary>
        public static void GrantReward(ulong accountId, long group)
        {
            if (!RewardEnabled)
            {
                Console.WriteLine($"[objectives] {accountId} finished every daily challenge (group {group}) - game request is switched off (admin panel)");
                return;
            }
            try
            {
                var sel = QuestRewards.Loaded
                    ? RewardSelections.Offer(accountId, QuestRewards.GiftContextGameRewardTokens, null, "Daily Challenges complete!")
                    : null;
                Console.WriteLine(sel != null
                    ? $"[objectives] {accountId} finished every daily challenge (group {group}) -> game request {sel.RewardSelectionId}"
                    : $"[objectives] {accountId} finished every daily challenge (group {group}) but no game request could be made (reward table not loaded)");
            }
            catch (Exception ex) { Console.WriteLine($"[objectives] {accountId} daily reward failed: {ex.Message}"); }
        }

        /// <summary>Re-read the file (after the admin panel saved it or reset it).</summary>
        public static void Reload() { lock (_lock) _sets = Load(); }

        /// <summary>Store new settings and apply them. Sets are validated by the caller.</summary>
        public static void Save(List<List<Classes.DailyObjective>> sets, bool rewardEnabled, int xpPerObjective)
        {
            var o = new JObject
            {
                ["rewardEnabled"] = rewardEnabled,
                ["xpPerObjective"] = xpPerObjective,
                ["sets"] = new JArray(sets.Select(set => new JArray(set.Select(d => new JObject { ["type"] = d.Type, ["score"] = d.Score }))))
            };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File1)!);
            System.IO.File.WriteAllText(File1, o.ToString());
            Reload();
        }

        /// <summary>Back to the built-in defaults (deletes the file).</summary>
        public static void ResetToDefaults()
        {
            try { if (System.IO.File.Exists(File1)) System.IO.File.Delete(File1); } catch { }
            ConfigCampus.ObjectiveCompletionXp = DefaultXp;
            Reload();
        }

        private static readonly int DefaultXp = ConfigCampus.ObjectiveCompletionXp;

        private static List<List<Classes.DailyObjective>> Load()
        {
            List<List<Classes.DailyObjective>>? days = null;
            Source = "default";
            RewardEnabled = true;
            try
            {
                if (System.IO.File.Exists(File1))
                {
                    var tok = JToken.Parse(System.IO.File.ReadAllText(File1));
                    JArray? arr = tok as JArray;
                    if (tok is JObject obj)
                    {
                        if (obj.TryGetValue("rewardEnabled", StringComparison.OrdinalIgnoreCase, out var re) && re.Type == JTokenType.Boolean)
                            RewardEnabled = re.Value<bool>();
                        if (obj.TryGetValue("xpPerObjective", StringComparison.OrdinalIgnoreCase, out var xp) && xp.Type == JTokenType.Integer)
                            ConfigCampus.ObjectiveCompletionXp = Math.Clamp(xp.Value<int>(), 0, 100000);
                        arr = obj.GetValue("sets", StringComparison.OrdinalIgnoreCase) as JArray;
                    }
                    if (arr != null && arr.Count > 0)
                    {
                        days = arr[0] is JArray
                            ? arr.OfType<JArray>().Select(ParseSet).Where(x => x.Count > 0).ToList()
                            : new List<List<Classes.DailyObjective>> { ParseSet(arr) };
                        if (days.Count == 0) days = null;
                    }
                    if (days != null) Source = "file";
                    Console.WriteLine(days != null
                        ? $"[objectives] daily challenges from Data/APIS/daily-objectives.json ({days.Count} set(s), reward {(RewardEnabled ? "on" : "off")}, {ConfigCampus.ObjectiveCompletionXp} XP each)"
                        : "[objectives] Data/APIS/daily-objectives.json has no usable sets - using the defaults");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[objectives] could not read daily-objectives.json ({ex.Message}) - using the defaults"); days = null; }

            days ??= new List<List<Classes.DailyObjective>>
            {
                DefaultSet.Select(d => new Classes.DailyObjective { Type = d[0], Score = d[1] }).ToList()
            };
            foreach (var set in days) foreach (var d in set) d.Xp = ConfigCampus.ObjectiveCompletionXp;

            // Seven entries, one per weekday, like the reference config; a shorter list repeats.
            var week = new List<List<Classes.DailyObjective>>();
            for (int i = 0; i < 7; i++) week.Add(days[i % days.Count]);
            return week;
        }

        private static List<Classes.DailyObjective> ParseSet(JArray set) => set.OfType<JObject>()
            .Select(o => new Classes.DailyObjective
            {
                Type  = (int?)(o.GetValue("type", StringComparison.OrdinalIgnoreCase)) ?? 0,
                Score = Math.Clamp((int?)(o.GetValue("score", StringComparison.OrdinalIgnoreCase)) ?? 1, 1, 1000)
            })
            .Where(o => Types.ContainsKey(o.Type))
            .ToList();

        // ---- the ObjectiveType enum (2025 client: same members and order as the reference table) ----

        /// <summary>Tutorial-only objectives: the client fires them during the new-player tutorial, never after.</summary>
        public static readonly HashSet<int> TutorialOnly = new() { 20, 21, 22, 25, 26 };

        /// <summary>Every usable objective type, id -> name. The rollups (10-15) and Default (-1) are left out.</summary>
        public static readonly SortedDictionary<int, string> Types = new()
        {
            [1] = "FirstSessionOfDay", [2] = "AddAFriend", [3] = "PartyUp", [5] = "LevelUp", [6] = "CheerAPlayer",
            [7] = "PointedAtPlayer", [8] = "CheerARoom", [9] = "SubscribeToPlayer",
            [20] = "OOBE_GoToLockerRoom", [21] = "OOBE_GoToActivity", [22] = "OOBE_FinishActivity",
            [25] = "NUX_PunchcardObjective", [26] = "NUX_AllPunchcardObjectives",
            [30] = "GoToRecCenter", [31] = "FinishActivity", [32] = "VisitACustomRoom", [33] = "CreateACustomRoom",
            [35] = "ScoreBasketInRecCenter", [36] = "UploadPhotoToRecNet", [37] = "UpdatePlayerBio", [38] = "SaveOutfitSlot",
            [39] = "PurchaseClothingItem", [40] = "PurchaseNonClothingItem", [41] = "DrinkWater", [42] = "ColorOnWhiteboard",
            [43] = "SetBasketballSkin", [44] = "ThrowBasketball", [45] = "PlaceInventionInDorm", [46] = "ChangeDormRoomSkin",
            [47] = "ToggleOwnedClothes", [48] = "EquipHat", [49] = "LoadOutfit", [50] = "SaveNewOutfitSlot", [51] = "SpawnCamera",
            [52] = "TakeSelfie", [53] = "PrintSelfie", [54] = "TakePictureOfPlayer", [55] = "PrintPictureOfPlayer",
            [56] = "PublishSelfieWithPlayer", [57] = "SpawnFoodWithOtherPlayers", [58] = "EmoteInRecCenter",
            [59] = "SendRoomChatInRecCenter", [60] = "UseFrendotron", [61] = "GoToDormRoom", [62] = "VisitSpecificRoom",
            [63] = "VisitPublicRRO", [64] = "VisitPublicRoomBySource", [65] = "FavoriteARoom", [66] = "TakePhotoWithFilter",
            [67] = "OpenYourPlayerProfile", [68] = "OpenOnlineStatusModal", [69] = "ChangeProfilePicture",
            [70] = "ChangePlayerDisplayName", [71] = "ChangePlayerDescriptionText", [72] = "OpenPlayerPronounsModal",
            [73] = "OpenOtherPlayersProfile", [74] = "VisitPlayersPortfolio", [75] = "FavoriteAFriend",
            [100] = "CharadesGames", [101] = "CharadesWinsPerformer", [102] = "CharadesWinsGuesser",
            [200] = "DiscGolfWins", [201] = "DiscGolfGames", [202] = "DiscGolfHolesUnderPar",
            [300] = "DodgeballWins", [301] = "DodgeballGames", [302] = "DodgeballHits",
            [400] = "PaddleballGames", [401] = "PaddleballWins", [402] = "PaddleballScores",
            [500] = "PaintballAnyModeGames", [501] = "PaintballAnyModeWins", [502] = "PaintballAnyModeHits",
            [600] = "PaintballCTFWins", [601] = "PaintballCTFGames", [602] = "PaintballCTFHits", [603] = "PaintballFlagCaptures",
            [700] = "PaintballTeamBattleWins", [701] = "PaintballTeamBattleGames", [702] = "PaintballTeamBattleHits",
            [710] = "PaintballFreeForAllWins", [711] = "PaintballFreeForAllGames", [712] = "PaintballFreeForAllHits",
            [800] = "SoccerWins", [801] = "SoccerGames", [802] = "SoccerGoals",
            [900] = "BowlingGames", [901] = "BowlingWins", [902] = "BowlingStrike",
            [1000] = "QuestGames", [1001] = "QuestWins", [1002] = "QuestPlayerRevives", [1003] = "QuestEnemyKills",
            [1010] = "QuestGames_Goblin1", [1011] = "QuestWins_Goblin1", [1012] = "QuestPlayerRevives_Goblin1", [1013] = "QuestEnemyKills_Goblin1",
            [1020] = "QuestGames_Goblin2", [1021] = "QuestWins_Goblin2", [1022] = "QuestPlayerRevives_Goblin2", [1023] = "QuestEnemyKills_Goblin2",
            [1030] = "QuestGames_Scifi1", [1031] = "QuestWins_Scifi1", [1032] = "QuestPlayerRevives_Scifi1", [1033] = "QuestEnemyKills_Scifi1",
            [1040] = "QuestGames_Pirate1", [1041] = "QuestWins_Pirate1", [1042] = "QuestPlayerRevives_Pirate1", [1043] = "QuestEnemyKills_Pirate1",
            [1050] = "QuestGames_Dracula1", [1051] = "QuestWins_Dracula1", [1052] = "QuestPlayerRevives_Dracula1", [1053] = "QuestEnemyKills_Dracula1",
            [2000] = "ArenaGames", [2001] = "ArenaWins", [2002] = "ArenaPlayerRevives", [2003] = "ArenaHeroTags", [2004] = "ArenaBotTags",
            [3000] = "RecRoyaleGames", [3001] = "RecRoyaleWins", [3002] = "RecRoyaleTags",
            [4000] = "StuntRunnerGames", [4001] = "StuntRunnerWins", [5000] = "RecRallyGames", [5001] = "RecRallyWins",
        };
    }
}
