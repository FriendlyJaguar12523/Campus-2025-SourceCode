using Campus_live_rewrite.api.server.Classes.db_class;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.dbs;
using System.Security.Principal;
using static Campus_live_rewrite.api.WebsocketEvents;
using Campus_live_rewrite.api.server;
using Microsoft.IdentityModel.Tokens.Experimental;

namespace Campus_live_rewrite.api
{
    public class WebsocketEvents
    {
        public static Reponse create_player_updated_account2_Response(string id, account_data_me account)
        {
            return new Reponse
            {
                Id = id,
                Msg = account
            };
        }

        public static Reponse create_sub(string id, ulong playerid, int membershipType)
        {
            return new Reponse
            {
                Id = id,
                Msg = new Sub
                {
                    creatorAccountId = playerid,
                    clubId = 1,
                    membershipType = membershipType,
                }
            };
        }

        /// <summary>
        /// The live level frame. Every caller passes CUMULATIVE XP (what the progression GETs
        /// report - see Progress.CumulativeXpForLevel), but on THIS frame the client expects XP as
        /// progress INTO the current level (reference notification-payloads.ts, "confirmed live
        /// against the client's formatter"). Sending the lifetime total (e.g. 5025 at level 40,
        /// where a level holds at most 1080) left the frame unapplied: the watch caught up because
        /// it re-reads progression when opened, the nametag - which only listens - stayed stale.
        /// </summary>
        public static Reponse createResponse_levelupdate(ulong playerid, int level, int xp)
        {
            int intoLevel = Math.Max(0, xp - Campus_live_rewrite.api.server.Classes.Progress.CumulativeXpForLevel(level, 0));
            return new Reponse
            {
                Id = "PlayerProgressionLevelUpdate",
                Msg = new
                {
                    PlayerId = playerid,
                    Level = level,
                    XP = intoLevel,
                }
            };
        }

        public static Reponse CreateResponseReputationUpdate(ulong playerId)
        {
            var acc = PlayerDB.GetFullAccount(playerId);
            return new Reponse
            {
                Id = "ReputationUpdate",
                Msg = new
                {
                    AccountId = playerId,
                    IsCheerful = true,   // always true (see reputation endpoints) - a false here restricts club/room creation client-side

                    Noteriety = acc.player.PlayerReputation.Noteriety,
                    SelectedCheer = Campus_live_rewrite.api.dbs.PlayerDB.EffectiveSelectedCheer(acc.player.PlayerReputation.SelectedCheer, playerId),
                    CheerCredit = acc.player.PlayerReputation.CheerCredit,
                    CheerGeneral = acc.player.PlayerReputation.CheerGeneral,
                    CheerHelpful = acc.player.PlayerReputation.CheerHelpful,
                    CheerCreative = acc.player.PlayerReputation.CheerCreative,
                    CheerGreatHost = acc.player.PlayerReputation.CheerGreatHost,
                    CheerSportsman = acc.player.PlayerReputation.CheerSportsman,
                    SubscriberCount = acc.player_Extra.sub_count,
                    SubscribedCount = 0,
                    RecRoomDeveloper = PlayerDB.CheckDevFlag(acc.player.Id)
                }
            };
        }

        // Pushes a currency balance to the owner so an admin grant shows without reopening the
        // store. Id is the ResponseResults number as a string (the same convention CreateLogout
        // "6" / CreateBan "22" use), and the Msg mirrors the EXACT array shape the client already
        // parses from GET /api/storefronts/v4/balance/{type}, so a client that routes this event
        // through the same reader applies it, and one that doesn't simply ignores an unknown id.
        public static Reponse createResponse_balanceupdate(int currencyType, long balance)
        {
            return new Reponse
            {
                Id = ((int)ResponseResults.StorefrontBalanceUpdate).ToString(),
                // ONE object, not an array: the client decodes StorefrontBalanceUpdate (61) as a
                // single BalanceResponse {Balance, CurrencyType, Platform} (reference:
                // pushBalanceUpdate). The array form the GET endpoint uses failed to decode here,
                // so the number in the corner never moved until a relog. Platform is the
                // account-wide bucket (-2, ALL_PLATFORMS) - the client keeps a balance PER
                // (CurrencyType, Platform) and SUMS them, so this must name the same bucket the
                // GET /storefronts/v4/balance and the purchase responses report or it creates a
                // phantom second bucket that is added to the real one forever.
                Msg = new
                {
                    Balance = balance,
                    CurrencyType = currencyType,
                    Platform = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket
                }
            };
        }

        public static Reponse CreateMessageResponse(MessageData messageData)
        {
            return new Reponse
            {
                Id = "2",
                Msg = messageData
            };
        }

        /// <summary>MessageDeleted (3): the client drops that notification from its list (reference MessageDeletedPayload {Id}).</summary>
        public static Reponse CreateMessageDeleted(ulong messageId) => new Reponse { Id = "3", Msg = new { Id = messageId } };

        public static Reponse CreateLogout(ulong id)
        {
            return new Reponse
            {
                Id = "6", // 6 = Logout
                Msg = new
                {

                }
            };
        }

        public static Reponse CreateBan(Moderation_Detail.ModerationBlockDetails block)
        {
            return new Reponse
            {
                Id = "22",
                Msg = block
            };
        }

        public class Reponse
        {
            public string? Id { get; set; }

            public object? Msg { get; set; }
        }

        public enum ResponseResults
        {
            RelationshipChanged = 1,
            MessageReceived,
            MessageDeleted,
            PresenceHeartbeatResponse,
            RefreshLogin,
            Logout,
            SubscriptionUpdateProfile = 11,
            SubscriptionUpdatePresence,
            SubscriptionUpdateGameSession,
            SubscriptionUpdateRoom = 15,
            SubscriptionUpdateRoomPlaylist,
            ModerationQuitGame = 20,
            ModerationUpdateRequired,
            ModerationKick,
            ModerationKickAttemptFailed,
            ModerationRoomBan,
            ServerMaintenance,
            GiftPackageReceived = 30,
            GiftPackageReceivedImmediate,
            GiftPackageRewardSelectionReceived,
            ProfileJuniorStatusUpdate = 40,
            RelationshipsInvalid = 50,
            StorefrontBalanceAdd = 60,
            StorefrontBalanceUpdate,
            StorefrontBalancePurchase,
            ConsumableMappingAdded = 70,
            ConsumableMappingRemoved,
            PlayerEventCreated = 80,
            PlayerEventUpdated,
            PlayerEventDeleted,
            PlayerEventResponseChanged,
            PlayerEventResponseDeleted,
            PlayerEventStateChanged,
            ChatMessageReceived = 90,
            CommunityBoardUpdate = 95,
            CommunityBoardAnnouncementUpdate,
            InventionModerationStateChanged = 100,
            FreeGiftButtonItemsAdded = 110,
            LocalRoomKeyCreated = 120,
            LocalRoomKeyDeleted
        }

        public enum MessageType
        {
            GameInvite,
            GameInviteDeclined,
            GameJoinFailed,
            PartyActivitySwitch,
            FriendInvite,
            VoteToKick,
            GameInviteV2,
            PartyActivitySwitchV2,
            RequestGameInvite = 10,
            RequestGameInviteDeclined,
            FriendStatusOnline = 20,
            TextMessage = 30,
            FriendRequestAccepted = 40,
            PlayerCheer = 50,
            PlayerCheerAnonymous,
            RoomCoOwnerAdded = 60,
            RoomCoOwnerRemoved,
            RoomCoOwnerInvited,
            CreatorPublishedNewRoom = 70,
            PlayerAttendingEvent = 80,
            PlayerEventInvitation,
            GroupInvitation = 90,
            PlayerJoinedGroup,
            CoachMessage = 100
        }


        public static Reponse createResponse_msg(string message, ulong playerid)
        {
            return new Reponse
            {
                Id = "2",
                Msg = new
                {
                    FromPlayerId = (long)playerid,
                    Id = rng.Next(1, 0x7ffffff),
                    SentTime = DateTime.UtcNow,
                    Type = 100,
                    Data = message,
                    RoomId = 1
                }
            };
        }

        private static readonly Random rng = new Random();
    }
}
