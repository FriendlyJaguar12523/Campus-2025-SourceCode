using Newtonsoft.Json;

namespace Campus_live_rewrite.api.server
{
    // A small, admin-curated, ORDERED list of featured room ids, persisted to a JSON file so the
    // GAME (Room_server/featuredrooms/current), the WEBSITE (/api/site/rooms/featured) and the
    // admin panel all read the exact same set - "featured" is one shared thing, not two lists.
    // Order matters (it's the display order), so this is a list, not a flag per room.
    public static class FeaturedRoomsStore
    {
        private static readonly string FilePath = Path.Combine(Environment.CurrentDirectory, "Data", "featured_rooms.json");
        private static readonly object Lock = new();

        public static List<ulong> GetIds()
        {
            lock (Lock)
            {
                try
                {
                    if (File.Exists(FilePath))
                        return JsonConvert.DeserializeObject<List<ulong>>(File.ReadAllText(FilePath)) ?? new List<ulong>();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[featured] read failed: {ex.Message}");
                }
                return new List<ulong>();
            }
        }

        // Replaces the whole list. De-duplicates while PRESERVING order (first wins).
        public static void SetIds(IEnumerable<ulong> ids)
        {
            var clean = new List<ulong>();
            foreach (var id in ids)
                if (id != 0 && !clean.Contains(id)) clean.Add(id);

            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonConvert.SerializeObject(clean));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[featured] write failed: {ex.Message}");
                }
            }
        }
    }
}
