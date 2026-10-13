using Campus_live_rewrite.api.dbs;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Automatic enforcement for things only a tampered client does (a Maker Pen check the
    /// server refuses, text that skipped the chat filter): the player is thrown out with a
    /// timeout. A timeout is PlayerDB.Set_moderation_block with isBan=false - the moderation
    /// frame makes the client leave, matchmaking refuses them until it runs out, and the
    /// admin panel's Ban Logs show it (logKind "auto-timeout"). Their presence is cleared so
    /// the heartbeat resync does not drop them straight back into the room. Staff are never
    /// auto-actioned.
    /// </summary>
    public static class AutoEnforce
    {
        public static bool IsStaff(ulong id) => PlayerDB.CheckDevFlag(id) || PlayerDB.CheckModFlag(id);

        public static void DisconnectAndTimeout(ulong playerId, Moderation_Detail.ReportCategory category, int seconds, string message, string note)
        {
            if (IsStaff(playerId))
            {
                Console.WriteLine($"[auto-enforce] {playerId} is staff - not actioned ({note})");
                return;
            }
            try
            {
                PlayerDB.Set_moderation_block(playerId, category, Math.Max(1, seconds), 0, false, message, null, false, 0,
                    addAppealMessage: false, logKind: "auto-timeout", logNote: note);
                HeartbeatDB.UpdatePlayerHeartbeat(playerId, null, online: false);
                Console.WriteLine($"[auto-enforce] {playerId} disconnected + timed out {seconds}s: {note}");
            }
            catch (Exception ex) { Console.WriteLine($"[auto-enforce] could not action {playerId}: {ex.Message}"); }
        }
    }
}
