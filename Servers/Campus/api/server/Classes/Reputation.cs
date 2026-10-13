using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server.Classes
{
    public class Rep
    {
        public ulong AccountId { get; set; }
        public bool IsCheerful { get; set; }
        public int Noteriety { get; set; }
        public CheerCategoryEnum SelectedCheer { get; set; }
        public int CheerCredit { get; set; }
        public int CheerGeneral { get; set; }
        public int CheerHelpful { get; set; }
        public int CheerCreative { get; set; }
        public int CheerGreatHost { get; set; }
        public int CheerSportsman { get; set; }
        // RecFlare's ReputationDto carries these two; the profile reads them off the reputation
        // object, so they must be present (a missing field can make the client's parser throw).
        public int SubscriberCount { get; set; }
        public int SubscribedCount { get; set; }
    }
}
