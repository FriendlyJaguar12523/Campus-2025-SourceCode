using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Platform ids that are NOT identities.
    ///
    /// The Quest patch has two builds. The one headset players actually use (the "no-Meta-account"
    /// build, no Oculus App Id configured) cannot obtain a real Meta user id, so it deliberately
    /// sends the sentinel id 1 together with a fixed, unverifiable nonce. That is by design: the
    /// platform login is MEANT to fail, at a step the client handles, so it falls through to its own
    /// username/password screen.
    ///
    /// The consequence is that EVERY no-Meta headset sends the same id 1. So it must never be used
    /// to look an account up or to bind one:
    ///   - bound: the first Quest player to log in claims id 1, and the cached-login picker then
    ///     offers THAT player's account to every other headset - with requirePassword false, i.e.
    ///     a no-password login into someone else's account.
    ///   - clash-checked: every Quest player after the first is refused with "already linked to a
    ///     different Campus account".
    ///
    /// Quest players are identified by their account credentials and their device id instead, so
    /// the per-device half of the account lock still holds.
    /// </summary>
    public static class PlatformIdentity
    {
        /// <summary>The id the Quest no-Meta build sends in place of a Meta user id.</summary>
        public const ulong OculusNoAccountSentinel = 1;

        /// <summary>
        /// The account id carried by the placeholder cached-login entry (requirePassword=true) that
        /// sends a headset to the username/password screen. It is not an account: the client
        /// resolves it through /Accounts/account/bulk to label the picker, and PlayerDB answers with
        /// a neutral "sign in" identity instead of the "Player0" default that made it look like a
        /// broken login into a real account. Nothing else may ever use id 0.
        /// </summary>
        public const ulong LoginPlaceholderAccountId = 0;
        public const string LoginPlaceholderUsername = "campus";
        public const string LoginPlaceholderDisplayName = "Sign in to Campus";

        /// <summary>True when this platform id identifies nobody and must not be trusted as one.</summary>
        public static bool IsSentinel(Platforms platform, ulong platformId) =>
            platform == Platforms.Oculus && platformId == OculusNoAccountSentinel;
    }
}
