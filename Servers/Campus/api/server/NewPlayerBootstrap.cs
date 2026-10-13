using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// What a brand-new IN-GAME account needs so the client runs its own account-creation flow
    /// (reference: auth placeNewPlayerInOrientation + the client's OrientationIdentityCreationFlow).
    ///
    /// The client walks "Recroom.AccountCreation.*" player settings inside the Orientation room:
    /// HasStarted -> AcceptedCodeOfConduct -> HasChosenUsername (the in-world name picker, which
    /// PUTs /Accounts/account/me/username) -> HasCreatedPassword (POST /Auth/account/me/
    /// changepassword) -> HasImportedFriends -> FinishedAvatarCustomization -> HasFinished. The
    /// default settings mark all of those done (website accounts already chose a name and
    /// password there), which is what left an in-game account stuck with its random name. A new
    /// in-game account gets them cleared, its tutorial mask zeroed so Orientation loads, and its
    /// presence seeded to the Orientation instance the client tags itself with (-2) - without that
    /// seed the heartbeat answered "dorm", the client read presence as out-of-sync and bounced
    /// the player out of the flow (the old "SocialOrientation loops -> logout").
    /// </summary>
    public static class NewPlayerBootstrap
    {
        public const long OrientationRoomId = 13;
        public const long OrientationInstanceId = -2;

        // Accounts just made by create_account, and when. The client answers a new account by PUTting five
        // AccountCreation flags True itself a second or two after login (before it shows a single page), which
        // ends the creation flow. An account that is "started, not finished" instead gets the client's own
        // "CONTINUE CREATING ACCOUNT" flow (customize, username, password, ...), so those first PUTs are dropped.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateTime> _fresh = new();
        // short on purpose: the burst lands within a few seconds of login, while the real steps (name, password) come later
        private static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(15);
        private static readonly string[] BurstKeys = { "HasStarted", "HasFinished", "HasChosenUsername", "HasCreatedPassword", "AcceptedCodeOfConduct" };

        /// <summary>True when this PUT is the client's own auto-completion of a brand-new account and should be dropped.</summary>
        public static bool ShouldIgnoreCreationPut(ulong accountId, string key, string value)
        {
            if (!ConfigCampus.HoldNewAccountCreation || !_fresh.TryGetValue(accountId, out var at)) return false;
            if (DateTime.UtcNow - at > FreshWindow) { _fresh.TryRemove(accountId, out _); return false; }
            if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return false;
            const string pre = "Recroom.AccountCreation.";
            return key.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && Array.Exists(BurstKeys, k => string.Equals(k, key.Substring(pre.Length), StringComparison.OrdinalIgnoreCase));
        }

        public static void Start(ulong accountId, Platforms platform, bool needsPassword)
        {
            if (ConfigCampus.SkipOrientation)
            {
                // Straight into the game: nothing to walk, nothing to seed. Marking the steps
                // done (rather than leaving them unset) matters - the client re-runs the flow on
                // any step that reads false, and the defaults it would otherwise inherit are only
                // applied to accounts that have no settings row at all.
                try
                {
                    SettingsDB.MarkOrientationComplete(accountId);
                    Console.WriteLine($"[newplayer] {accountId}: Orientation skipped (ConfigCampus.SkipOrientation)");
                }
                catch (Exception ex) { Console.WriteLine($"[newplayer] {accountId}: orientation skip failed - {ex.Message}"); }
                return;
            }

            try
            {
                // started + not finished is the state the client answers with "CONTINUE CREATING ACCOUNT" (verified locally on account 33)
                // HasStarted MUST be true: with false the client completes the account itself (PUTs every flag True a second after login, shows PLAY with a random
                // name and never shows a page - verified 2026-10-13). "Started, not finished" is what makes it offer "CONTINUE CREATING ACCOUNT" and run the pages.
                SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasStarted", "true", accountId);
                // (the early-PUT guard is no longer armed: with HasStarted=true seeded the client sends no early burst, and the guard swallowed real steps)
                SettingsDB.SetPlayerSetting("Recroom.AccountCreation.AcceptedCodeOfConduct", "false", accountId);
                SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasChosenUsername", "false", accountId);
                SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasCreatedPassword", needsPassword ? "false" : "true", accountId);
                // Friend import needs the platform's friends API (Steam/Meta), which this server
                // doesn't proxy - skip that step.
                SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasImportedFriends", "true", accountId);
                SettingsDB.SetPlayerSetting("Recroom.AccountCreation.FinishedAvatarCustomization", "false", accountId);
                SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasFinished", "false", accountId);
                SettingsDB.SetPlayerSetting("TUTORIAL_COMPLETE_MASK", "0", accountId);
                // Recroom.OOBE is the client's "out-of-box experience done" marker. Every account's default settings
                // carry OOBE=1 (= done), and a new account that still reads done made the client mark every
                // AccountCreation step complete ITSELF a few seconds after login (PUT HasStarted/HasFinished/
                // HasChosenUsername/HasCreatedPassword/AcceptedCodeOfConduct = True) and never show the avatar /
                // name / password steps. 0 = not done, so the client walks the flow.
                SettingsDB.SetPlayerSetting("Recroom.OOBE", "0", accountId);
            }
            catch (Exception ex) { Console.WriteLine($"[newplayer] {accountId}: settings failed - {ex.Message}"); }

            try
            {
                var inst = OrientationInstance();
                if (inst != null)
                {
                    HeartbeatDB.UpdatePlayerHeartbeat(accountId, inst, online: true, platform: platform);
                    Console.WriteLine($"[newplayer] {accountId}: seeded into Orientation (room {OrientationRoomId}, instance {OrientationInstanceId})");
                }
                else Console.WriteLine($"[newplayer] {accountId}: no Orientation room ({OrientationRoomId}) in RoomDB - not seeded");
            }
            catch (Exception ex) { Console.WriteLine($"[newplayer] {accountId}: presence seed failed - {ex.Message}"); }
        }

        /// <summary>The Orientation presence the client expects to be echoed while it runs the new-user flow.</summary>
        public static HeartbeatDB.RoomInstance? OrientationInstance()
        {
            var room = RoomDB.GetRoom((ulong)OrientationRoomId);
            if (room == null) return null;
            var sub = room.SubRooms?.FirstOrDefault();
            return new HeartbeatDB.RoomInstance
            {
                roomInstanceId = OrientationInstanceId,
                roomId = OrientationRoomId,
                subRoomId = (long)(sub?.SubRoomId ?? 1),
                location = sub?.UnitySceneId ?? "",
                roomInstanceType = 0,
                name = "^" + (string.IsNullOrEmpty(room.Name) ? "Orientation" : room.Name),
                maxCapacity = sub?.MaxPlayers > 0 ? sub.MaxPlayers : 4,
                isFull = false,
                isPrivate = false,
                isInProgress = false,
                photonRoomIdYeee = $"rec.{OrientationRoomId}",
                matchmakingPolicy = 0
            };
        }

        public static bool IsInOrientation(ulong accountId)
        {
            try { return HeartbeatDB.GetSelfPresence(accountId).room?.roomInstanceId == OrientationInstanceId; }
            catch { return false; }
        }
    }
}
