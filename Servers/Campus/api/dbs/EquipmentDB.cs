using LiteDB;

namespace Campus_live_rewrite.api.dbs
{
    // What each player has equipped, per equipment prefab.
    //
    // The client POSTs /api/equipment/v1/update every time a skin is equipped, sending
    // the full entry for that prefab:
    //
    //   [{"PrefabName":"[ShareCamera]","ModificationGuid":"48c97ace-...",
    //     "FriendlyName":"Camping Share Camera","Tooltip":"","Rarity":0,
    //     "ThumbnailImageName":null,"Favorited":true}]
    //
    // That route did not exist, so the POST 404'd and the choice lived only in the
    // running client - at the next login everyone was back on the default skin.
    //
    // Stored as a collection inside Players.db rather than its own file, so it rides the
    // existing checkpoint (DbMaintenance.CheckpointAll) instead of adding another
    // write-ahead log that never gets merged.
    public static class EquipmentDB
    {
        public class EquippedItem
        {
            public string PrefabName { get; set; } = "";
            public string ModificationGuid { get; set; } = "";
            public string? FriendlyName { get; set; }
            public string? Tooltip { get; set; }
            public int Rarity { get; set; }
            public string? ThumbnailImageName { get; set; }
            public bool Favorited { get; set; }
        }

        public class PlayerEquipment
        {
            [BsonId]
            public ulong PlayerId { get; set; }
            public List<EquippedItem> Equipped { get; set; } = new();
        }

        private static readonly ILiteCollection<PlayerEquipment> _col =
            PlayerDB.PlayerDBFile.GetCollection<PlayerEquipment>("player_equipment");

        /// <summary>
        /// The saved loadout, every skin spelled as the locker list spells it and one entry per
        /// skin. Entries saved under a store row's 22-char short guid (34 of them on 19 accounts,
        /// 2026-09-27) were replayed by getUnlocked as a SECOND copy of a skin already in the list.
        /// </summary>
        public static List<EquippedItem> Get(ulong playerId)
        {
            var list = _col.FindById(playerId)?.Equipped ?? new List<EquippedItem>();
            var byKey = new Dictionary<string, EquippedItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in list)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.PrefabName)) continue;
                e.ModificationGuid = Campus_live_rewrite.api.server.SkinOwnership.CanonicalGuid(e.PrefabName, e.ModificationGuid) ?? "";
                byKey[PlayerDB.EquipmentSkinKey(e.PrefabName, e.ModificationGuid)] = e;   // the later entry wins, as in Update
            }
            return byKey.Values.ToList();
        }

        /// <summary>
        /// Records the state the client just sent, keyed by prefab AND modification guid.
        /// Returns the player's full loadout afterwards.
        ///
        /// Keying by prefab alone loses data. One request carries SEVERAL entries for the
        /// same prefab - the newly equipped skin, plus the one it replaced so its flag can
        /// be cleared:
        ///
        ///   [MakerPen] Professor Maker Pen   (the new one)
        ///   [MakerPen] Host 2 Maker Pen      (the old one, flag being cleared)
        ///
        /// Replacing "all entries for [MakerPen]" therefore kept only whichever happened to
        /// come LAST in the array - frequently the one being switched OFF - so the skin came
        /// back unequipped. The client decides which entry is current; our job is to store
        /// exactly what it told us, per item.
        /// </summary>
        public static List<EquippedItem> Update(ulong playerId, IEnumerable<EquippedItem>? items)
        {
            var row = _col.FindById(playerId) ?? new PlayerEquipment { PlayerId = playerId };
            if (items == null) return row.Equipped;

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.PrefabName)) continue;

                // Stored under the locker list's spelling, and matched on the normalized key, so a
                // skin equipped under a short guid replaces its entry instead of adding a twin.
                item.ModificationGuid = Campus_live_rewrite.api.server.SkinOwnership.CanonicalGuid(item.PrefabName, item.ModificationGuid) ?? "";
                string key = PlayerDB.EquipmentSkinKey(item.PrefabName, item.ModificationGuid);
                row.Equipped.RemoveAll(e => string.Equals(PlayerDB.EquipmentSkinKey(e.PrefabName, e.ModificationGuid), key, StringComparison.OrdinalIgnoreCase));

                row.Equipped.Add(item);
            }

            _col.Upsert(row);
            return row.Equipped;
        }
    }
}
