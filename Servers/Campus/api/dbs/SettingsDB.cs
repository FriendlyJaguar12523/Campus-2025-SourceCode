using LiteDB;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.dbs
{
    public class SettingsDB
    {
        private static readonly string _dbPath = Path.Combine(Environment.CurrentDirectory, "Data", "DBs", "Settings.db");
        private static readonly LiteDatabase _db = new LiteDatabase(_dbPath);

        // Merge this store's write-ahead log back into the data file.
        // See DbMaintenance for why this must be called explicitly.
        public static void Checkpoint() => _db.Checkpoint();

        private static ILiteCollection<PlayerSetting> PlayerSettings => _db.GetCollection<PlayerSetting>("players_settings");

        public static List<Setting> LoadSettings(ulong playerId)
        {
            return PlayerSettings.FindById(playerId)?.Settings ?? new List<Setting>();
        }

        public static void SaveSettings(List<Setting> settings, ulong playerId)
        {
            if (playerId == 0) return;

            var player = new PlayerSetting
            {
                PlayerId = playerId,
                Settings = settings
            };

            PlayerSettings.Upsert(player); // ✅ SAFE
        }


        public static void SetPlayerSetting(string key, string value, ulong playerId)
        {
            if (string.IsNullOrWhiteSpace(key) || playerId == 0)
                return;

            if (key is "SplitTestAssignedSegments" or "Growth.LastEmailPromptTime")
                return;

            var col = PlayerSettings;

            var player = col.FindById(playerId) ?? new PlayerSetting
            {
                PlayerId = playerId,
                Settings = CreateDefaultSettings()
            };

            var setting = player.Settings.FirstOrDefault(s => s.Key == key);
            if (setting != null)
                setting.Value = value;
            else
                player.Settings.Add(new Setting { Key = key, Value = value });

            col.Upsert(player); // 🔥 KEY LINE
        }

        /// <summary>Every step of the new-user / Orientation flow, marked done.</summary>
        private static readonly (string Key, string Value)[] OrientationDone =
        {
            ("Recroom.AccountCreation.HasStarted",                "true"),
            ("Recroom.AccountCreation.AcceptedCodeOfConduct",     "true"),
            ("Recroom.AccountCreation.HasChosenUsername",         "true"),
            ("Recroom.AccountCreation.HasCreatedPassword",        "true"),
            ("Recroom.AccountCreation.HasImportedFriends",        "true"),
            ("Recroom.AccountCreation.FinishedAvatarCustomization", "true"),
            ("Recroom.AccountCreation.HasFinished",               "true"),
            // Every bit set = every tutorial/orientation step complete.
            ("TUTORIAL_COMPLETE_MASK",                            "4294967295"),
            ("Recroom.OOBE",                                      "1"),
        };

        /// <summary>
        /// Mark this player as having finished Orientation. One upsert, not nine - SetPlayerSetting
        /// reads and writes the whole settings document per call, which on a full backfill is a
        /// read-modify-write per key per account.
        /// </summary>
        public static bool MarkOrientationComplete(ulong playerId)
        {
            if (playerId == 0) return false;

            var col = PlayerSettings;
            var player = col.FindById(playerId) ?? new PlayerSetting
            {
                PlayerId = playerId,
                Settings = CreateDefaultSettings()
            };
            player.Settings ??= CreateDefaultSettings();

            bool changed = false;
            foreach (var (key, value) in OrientationDone)
            {
                var setting = player.Settings.FirstOrDefault(s => s.Key == key);
                if (setting == null) { player.Settings.Add(new Setting { Key = key, Value = value }); changed = true; }
                else if (setting.Value != value) { setting.Value = value; changed = true; }
            }

            if (changed) col.Upsert(player);
            return changed;
        }

        /// <summary>
        /// Backfill: mark Orientation complete for every account that already has a settings row.
        /// Accounts with no row inherit CreateDefaultSettings(), which already says complete.
        /// Returns how many rows were actually changed.
        /// </summary>
        public static int SkipOrientationForAllPlayers()
        {
            int n = 0;
            foreach (var id in PlayerSettings.FindAll().Select(p => p.PlayerId).ToList())
            {
                try { if (MarkOrientationComplete(id)) n++; }
                catch (Exception ex) { Console.WriteLine($"[orientation] {id}: skip failed - {ex.Message}"); }
            }
            return n;
        }

        public static void DeletePlayerKey(string key, ulong playerId)
        {
            if (string.IsNullOrWhiteSpace(key) || playerId == 0)
                return;

            var col = PlayerSettings;

            var player = col.FindById(playerId);
            if (player == null || player.Settings == null)
                return;

            player.Settings.RemoveAll(s => s.Key == key);

            col.Upsert(player);
        }

        public static string? GetPlayerSettingValue(string key, ulong playerId)
        {
            if (string.IsNullOrWhiteSpace(key) || playerId == 0)
                return null;

            // ignored keys
            if (key == "SplitTestAssignedSegments")
                return null;

            if (key == "Growth.LastEmailPromptTime")
                return null;

            var settings = LoadSettings(playerId);
            var setting = settings.Find(s => s.Key == key);

            return setting?.Value;
        }

        public static void SetPlayerSettingFromJson(string jsonData, ulong playerId)
        {
            if (string.IsNullOrWhiteSpace(jsonData) || playerId == 0)
                return;

            Setting? setting;
            try
            {
                setting = JsonConvert.DeserializeObject<Setting>(jsonData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse setting JSON: {ex.Message}");
                return;
            }

            if (setting == null || string.IsNullOrEmpty(setting.Key) || setting.Key == "SplitTestAssignedSegments")
                return;

            SetPlayerSetting(setting.Key, setting.Value ?? "", playerId);
        }

        public static void DeleteSettings(ulong playerId)
        {
            if (playerId == 0) return;

            var col = PlayerSettings; 
            col.DeleteMany(x => x.PlayerId == playerId);
        }

        public static List<Setting> CreateDefaultSettings() => new List<Setting>
        {
            new Setting { Key = "MOD_BLOCKED_TIME", Value = "0" },
            new Setting { Key = "MOD_BLOCKED_DURATION", Value = "0" },
            new Setting { Key = "PlayerSessionCount", Value = "0" },
            new Setting { Key = "ShowRoomCenter", Value = "1" },
            new Setting { Key = "QualitySettings", Value = "1" },
            new Setting { Key = "Recroom.OOBE", Value = "1" },
            new Setting { Key = "VoiceFilter", Value = "0" },
            new Setting { Key = "VIGNETTED_TELEPORT_ENABLED", Value = "0" },
            new Setting { Key = "CONTINUOUS_ROTATION_MODE", Value = "0" },
            new Setting { Key = "ROTATION_INCREMENT", Value = "0" },
            new Setting { Key = "ROTATE_IN_PLACE_ENABLED", Value = "0" },
            new Setting { Key = "TeleportBuffer", Value = "0" },
            new Setting { Key = "VoiceChat", Value = "1" },
            new Setting { Key = "PersonalBubble", Value = "0" },
            new Setting { Key = "ShowNames", Value = "1" },
            new Setting { Key = "H.264 plugin", Value = "1" },
            // Mark account creation as fully done so the client doesn't get stuck in first-boot
            // flows that expect Steam / Meta OAuth pages we don't serve.
            new Setting { Key = "Recroom.AccountCreation.HasStarted", Value = "true" },
            new Setting { Key = "Recroom.AccountCreation.HasFinished", Value = "true" },
            new Setting { Key = "Recroom.AccountCreation.HasChosenUsername", Value = "true" },
            new Setting { Key = "Recroom.AccountCreation.HasCreatedPassword", Value = "true" },
            // The client re-enters the flow on ANY step that reads false, so the three that were
            // missing here have to be stated too - not left to whatever the client defaults to.
            new Setting { Key = "Recroom.AccountCreation.AcceptedCodeOfConduct", Value = "true" },
            new Setting { Key = "Recroom.AccountCreation.HasImportedFriends", Value = "true" },
            new Setting { Key = "Recroom.AccountCreation.FinishedAvatarCustomization", Value = "true" },
            // Every bit set = every tutorial/orientation step marked complete. Prevents the
            // client from trying to load SocialOrientation, which currently loops → logout.
            new Setting { Key = "TUTORIAL_COMPLETE_MASK", Value = "4294967295" },
            new Setting { Key = "BACKPACK_FAVORITE_TOOL", Value = "-1" }
        };
    }
}
