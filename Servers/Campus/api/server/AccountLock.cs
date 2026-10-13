using System.Collections.Generic;
using System.Linq;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The anti-alt account lock. Accounts are made either in-game (the client's first launch) or
    /// on the website. The FIRST time an account signs in from the game it is bound to that Steam
    /// (or other platform) id AND to that device. After that:
    ///   - the account can only be signed in from that same Steam id / device class,
    ///   - that Steam id and that device may host at most ConfigCampus.MaxAccountsPerDevice
    ///     accounts: one made in-game plus one made on the website (CanShareSlot), so a player
    ///     can play either way and switch between them from the client's account picker.
    ///
    /// A short allowlist (ConfigCampus.LockExemptUsernames - the owner's own test accounts) is
    /// exempt so they can share one Steam/device. The exemption is pinned to account IDS at
    /// startup, so renaming some other account to an exempt username later grants it nothing.
    /// </summary>
    public static class AccountLock
    {
        private static HashSet<ulong> _exempt = new HashSet<ulong>();

        /// <summary>
        /// Resolve ConfigCampus.LockExemptUsernames to the account ids they currently name, once
        /// at startup. Call after the admin-seed (Program.cs) so the seeded accounts exist.
        /// </summary>
        public static void ResolveExemptions()
        {
            var set = new HashSet<ulong>();
            foreach (var name in ConfigCampus.LockExemptUsernames ?? System.Array.Empty<string>())
            {
                ulong id = PlayerDB.GetAccountIdByUsernameCI(name);
                if (id != 0)
                {
                    set.Add(id);
                    System.Console.WriteLine($"[account-lock] exemption: '{name}' -> account {id}");
                }
                else
                {
                    System.Console.WriteLine($"[account-lock] exemption: '{name}' not found (skipped)");
                }
            }
            _exempt = set;
            System.Console.WriteLine($"[account-lock] lock is {(ConfigCampus.EnforceAccountLock ? "ENABLED" : "disabled")}, "
                                   + $"{_exempt.Count} exempt account(s)");
        }

        public static bool IsExempt(ulong accountId) => _exempt.Contains(accountId);

        /// <summary>
        /// A device's class for the lock. The account cap is "one PC and one Quest headset", so
        /// every login collapses to one of two buckets: Oculus (Meta Quest) is the VR/headset
        /// class, everything else (Steam, Standalone, ...) is the PC class. platform_id is NOT
        /// consulted - it is client-supplied and spoofable.
        /// </summary>
        private static string DeviceClass(Platforms p) =>
            p == Platforms.Oculus ? "Quest" : "PC";

        /// <summary>
        /// Check the account-device lock for a login and, if it passes, bind the device (with its
        /// platform) to the account. Returns false with a player-facing message when the login must
        /// be refused. The rule: an account may sign in from at most ConfigCampus.MaxDevicesPerAccount
        /// devices AND at most one per class (one PC, one Quest) - and a device may host only one
        /// (non-exempt) account. The spoofable platform_id no longer gates anything; it is recorded
        /// for reference only. Exempt accounts (Coach/Kyle) bypass enforcement.
        /// </summary>
        public static bool TryEnforceAndBind(ulong accountId, Platforms platform, ulong platformId,
                                             string? deviceId, out string error)
        {
            error = "";

            // The Quest no-Meta build sends the sentinel id 1 for EVERY headset, so it identifies
            // nobody (see PlatformIdentity). Treat it as "no platform id": recording it would make
            // the cached-login picker offer this account to every other Quest player, and
            // clash-checking it would refuse every headset after the first as "already linked to a
            // different Campus account". The per-device half of the lock below is unaffected - it
            // keys on the device id - so one-PC-one-Quest still holds for headset players.
            if (PlatformIdentity.IsSentinel(platform, platformId))
                platformId = 0;

            // One account per Steam id. If this account isn't already bound to this Steam id but a
            // DIFFERENT non-exempt account is, refuse - otherwise a second website account logging in
            // on the same Steam records a duplicate binding, and the cached-login picker can then land
            // on a random account (the "logs into a random account" bug). Existing bindings pass, so
            // this only blocks a NEW account claiming an already-taken Steam. Exempt owner/test
            // accounts (they deliberately share one Steam) skip this entirely.
            if (platformId != 0 && ConfigCampus.EnforceAccountLock && !IsExempt(accountId))
            {
                bool alreadyMine = PlayerDB.GetAccountPlatformId(accountId, platform) == platformId;
                if (!alreadyMine)
                {
                    PlayerDB.GetLogins(platform, platformId, out var steamAccounts);
                    var steamOthers = (steamAccounts ?? new List<AccountLogin>())
                        .Select(a => a.accountId)
                        .Where(a => a != 0 && a != accountId && !IsExempt(a))
                        .Distinct().ToList();
                    if (!CanShareSlot(accountId, steamOthers, out string slotWhy))
                    {
                        error = $"This Steam account already has {slotWhy}. Log in with that one - each Steam "
                              + "account may have one Campus account made in-game and one made on the website.";
                        System.Console.WriteLine($"[account-lock] refused: Steam {platformId} already has {slotWhy} "
                                               + $"({string.Join(",", steamOthers)}), login tried for {accountId}");
                        return false;
                    }
                }
            }

            // Record the platform id for reference (profile/support) and so the cached-login lookup
            // can find this account by its Steam id next launch.
            if (platformId != 0)
                PlayerDB.AddPlatformId(accountId, platform, platformId);

            // Owner/test accounts, or the lock switched off: bind and allow, no enforcement.
            if (!ConfigCampus.EnforceAccountLock || IsExempt(accountId))
            {
                PlayerDB.BindDevice(deviceId, platform, accountId);
                return true;
            }

            // No usable device id: nothing to key the per-device lock on. Allow the login rather
            // than lock out a legitimate client that didn't send one - this is the known soft spot
            // (a modified client can also spoof the id), documented as such.
            if (string.IsNullOrWhiteSpace(deviceId))
                return true;

            deviceId = deviceId.Trim();

            var bindings = PlayerDB.GetDeviceBindings(accountId);
            bool knownDevice = bindings.Any(b => b.DeviceId == deviceId);

            if (!knownDevice)
            {
                string cls = DeviceClass(platform);

                // (1) Already a DIFFERENT device of this same class on the account? (a second PC,
                //     or a second headset). If that device has not signed in for
                //     ConfigCampus.DeviceReplaceAfterMinutes (a reinstall, a new PC, a re-imaged
                //     headset, an id that changed) the new one TAKES ITS PLACE; only a device that
                //     is still in use holds the slot. Before this a player who changed PCs was
                //     locked out of their own account for good ("requires a VPN" reports - they
                //     were actually hitting this).
                var sameClass = bindings.Where(b => b.DeviceId != deviceId && DeviceClass(b.Platform) == cls).ToList();
                if (sameClass.Count > 0)
                {
                    var idle = TimeSpan.FromMinutes(ConfigCampus.DeviceReplaceAfterMinutes);
                    var stale = sameClass.Where(b => b.LastSeen == null || DateTime.UtcNow - b.LastSeen.Value >= idle).ToList();
                    if (stale.Count == sameClass.Count)
                    {
                        PlayerDB.UnbindDevices(accountId, stale.Select(b => b.DeviceId!).Where(d => d != null));
                        bindings = PlayerDB.GetDeviceBindings(accountId);
                        System.Console.WriteLine($"[account-lock] account {accountId}: {cls} device(s) {string.Join(",", stale.Select(b => b.DeviceId))} " +
                                                 $"idle for {ConfigCampus.DeviceReplaceAfterMinutes}+ min, replaced by '{deviceId}'");
                        sameClass.Clear();
                    }
                }
                if (sameClass.Count > 0)
                {
                    error = cls == "Quest"
                        ? "This account is already tied to a different Quest headset. Each account "
                          + "is limited to one PC and one Quest headset."
                        : "This account is already tied to a different PC. Each account is limited "
                          + "to one PC and one Quest headset.";
                    System.Console.WriteLine($"[account-lock] refused: account {accountId} already has a "
                                           + $"{cls}-class device, login tried from a new {cls} device '{deviceId}'");
                    return false;
                }

                // (2) Overall device cap.
                int distinct = bindings.Select(b => b.DeviceId).Distinct().Count();
                if (distinct >= ConfigCampus.MaxDevicesPerAccount)
                {
                    error = "This account already uses its allowed devices (one PC and one Quest headset).";
                    System.Console.WriteLine($"[account-lock] refused: account {accountId} at device cap "
                                           + $"({distinct}/{ConfigCampus.MaxDevicesPerAccount}), new device '{deviceId}'");
                    return false;
                }
            }

            // (3) Anti-alt: the other (non-exempt) accounts already on this device. A device may
            //     host one game-made account and one website account (CanShareSlot); anything
            //     beyond that is refused. Gates NEW bindings only: an account this device is
            //     already bound to was admitted when that binding was made (or predates the
            //     rule), and re-checking it on every launch would lock a player out of an account
            //     the picker itself just offered.
            if (!knownDevice)
            {
                var devOthers = PlayerDB.AccountsOnDevice(deviceId)
                    .Where(o => o != accountId && !IsExempt(o)).Distinct().ToList();
                if (!CanShareSlot(accountId, devOthers, out string devWhy))
                {
                    error = $"This device already has {devWhy}. Log in with that one - each device may have "
                          + "one Campus account made in-game and one made on the website.";
                    System.Console.WriteLine($"[account-lock] refused: device '{deviceId}' already has {devWhy} "
                                           + $"({string.Join(",", devOthers)}), login tried for {accountId}");
                    return false;
                }
            }

            // Passed - bind the device (with platform) so the cap holds from now on.
            PlayerDB.BindDevice(deviceId, platform, accountId);
            return true;
        }

        /// <summary>
        /// May <paramref name="accountId"/> join the accounts already on a device / Steam id
        /// (<paramref name="others"/>, non-exempt, excluding itself)? The rule (see
        /// ConfigCampus.MaxAccountsPerDevice): at most that many accounts, and among them at most
        /// one made in-game and at most one made on the website. An account of Unknown origin
        /// (older than the flag) only counts toward the cap. <paramref name="why"/> names what is
        /// already there, for the player-facing message.
        /// </summary>
        public static bool CanShareSlot(ulong accountId, List<ulong> others, out string why)
        {
            why = "";
            if (others.Count == 0) return true;
            int cap = System.Math.Max(1, ConfigCampus.MaxAccountsPerDevice);
            if (others.Count >= cap)
            {
                why = others.Count == 1 ? "a Campus account" : "its Campus accounts";
                return false;
            }
            if (!ConfigCampus.DeviceSlotsByOrigin) return true;
            var mine = PlayerDB.OriginOf(accountId);
            if (mine == PlayerDB.AccountOrigin.Unknown) return true;
            if (others.Any(o => PlayerDB.OriginOf(o) == mine))
            {
                why = mine == PlayerDB.AccountOrigin.InGame ? "an account made in-game" : "an account made on the website";
                return false;
            }
            return true;
        }
    }
}
