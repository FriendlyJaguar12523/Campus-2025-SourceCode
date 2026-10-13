using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Data/APIS/Gameconfigs.json as the client receives it. The file is the source; a handful of
    /// values are rewritten on the way out so that changing them is a config line rather than an
    /// edit to an 870-entry JSON file. Cached on the file's mtime.
    /// </summary>
    public static class GameConfigs
    {
        private static readonly object _lock = new();
        private static string? _json;
        private static string _stamp = "";

        private static string Stamp(string path) =>
            (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : "-") + "|" + ConfigCampus.LevelRewardImage + "|" + ConfigCampus.SkipOrientation + "|" + ConfigCampus.ClientDebugLogPaths;

        /// <summary>
        /// The configs with every Level Rewards picture pointed at ConfigCampus.LevelRewardImage:
        /// each entry of Lifecycle.PSF.Progression.RewardMap (one per level, keyed by the
        /// synthetic GiftDropId ConfigV2 hands out) and UnknownRewardImage.
        /// </summary>
        public static string WithRewardImage(string path)
        {
            string stamp = Stamp(path);
            if (_json != null && stamp == _stamp) return _json;

            lock (_lock)
            {
                if (_json != null && stamp == _stamp) return _json;
                string raw = File.ReadAllText(path);
                try
                {
                    var arr = JArray.Parse(raw);
                    int rewritten = 0;
                    foreach (var e in arr)
                    {
                        string key = (string?)e["Key"] ?? "";
                        if (key == "Lifecycle.InRoomChat.AccountCreationCutoff" && !string.IsNullOrWhiteSpace(ConfigCampus.InRoomChatAccountCreationCutoff))
                        {
                            e["Value"] = ConfigCampus.InRoomChatAccountCreationCutoff; rewritten++;
                        }
                        else if (key == "Debug.Logs" &&!string.IsNullOrWhiteSpace(ConfigCampus.ClientDebugLogPaths))
                        {
                            // the value is a comma / newline separated list of log paths (GLOBAL_ENABLE, Network.TachyonClient, ...)
                            string cur = ((string?)e["Value"] ?? "").TrimEnd();
                            var extra = ConfigCampus.ClientDebugLogPaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            e["Value"] = cur + (cur.EndsWith(",") ? "\n" : ",\n") + string.Join(",\n", extra);
                            rewritten++;
                        }
                        else if (key == "Lifecycle.PSF.Progression.UnknownRewardImage")
                        {
                            e["Value"] = ConfigCampus.LevelRewardImage; rewritten++;
                        }
                        else if (key == "Lifecycle.PSF.Progression.RewardMap")
                        {
                            // The value is a JSON object serialised as a string: {"2000001":"x.png",...}
                            try
                            {
                                var map = JObject.Parse((string?)e["Value"] ?? "{}");
                                foreach (var p in map.Properties()) p.Value = ConfigCampus.LevelRewardImage;
                                e["Value"] = map.ToString(Newtonsoft.Json.Formatting.None);
                                rewritten++;
                            }
                            catch (Exception ex) { Console.WriteLine($"[gameconfigs] RewardMap unreadable, left as-is: {ex.Message}"); }
                        }
                    }
                    _json = arr.ToString(Newtonsoft.Json.Formatting.None);
                    Console.WriteLine($"[gameconfigs] level reward image -> '{ConfigCampus.LevelRewardImage}' ({rewritten} config value(s) rewritten)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[gameconfigs] could not rewrite ({ex.Message}) - serving the file as-is");
                    _json = raw;
                }
                _stamp = stamp;
                return _json;
            }
        }
    }
}
