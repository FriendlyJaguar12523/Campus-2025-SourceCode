using LiteDB;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// The in-game UI gradient (the Campus patch recolours Rec Room's UI gradients and accents).
    /// Chosen in the launcher before playing and kept HERE per account (Data/DBs/UiThemes.db), so it
    /// follows the player to any PC. The patch learns who's signed in after login and reads
    /// /api/campus/theme/{accountId}, re-checking every 30 s so a change made in the launcher shows
    /// without restarting the game. The presets live here too - the patch has none built in.
    /// </summary>
    public class UiThemeController : ControllerBase
    {
        public class Theme
        {
            [BsonId] public ulong AccountId { get; set; }
            public bool Enabled { get; set; } = true;
            public string Preset { get; set; } = "campus";        // a preset id, or "custom"
            public float[] Stops { get; set; } = Array.Empty<float>();   // custom: 4 x RGB (0-1)
            public float[] Accent { get; set; } = Array.Empty<float>();  // custom: RGB (0-1)
            public DateTime UpdatedAt { get; set; }
        }

        /// <summary>A preset: 4 gradient stops, the accent that replaces Rec Room's orange/white, and a tone
        /// for everything else (0 leave it, 1 greyscale it, 2 darken it).</summary>
        public sealed record Preset(string Id, string Name, float[] Stops, float[] Accent, int Tone);

        private static float[] Hex(params string[] hex) => hex.SelectMany(h => new[]
        {
            Convert.ToInt32(h.Substring(0, 2), 16) / 255f, Convert.ToInt32(h.Substring(2, 2), 16) / 255f, Convert.ToInt32(h.Substring(4, 2), 16) / 255f,
        }).Select(v => (float)Math.Round(v, 3)).ToArray();

        // The first six are the patch's old built-in themes, colour for colour.
        public static readonly Preset[] Presets =
        {
            new("campus",     "Campus",     new[] { .475f, .106f, .553f, .749f, .161f, .400f, .976f, .365f, .141f, .980f, .600f, .071f }, new[] { 1f, .6f, .1f }, 0),
            new("cyberpunk",  "Cyberpunk",  new[] { 0f, 1f, 1f, 1f, 0f, 1f, 0f, 0f, 1f, .5f, 0f, 1f }, new[] { 0f, 1f, 1f }, 0),
            new("nature",     "Nature",     new[] { 0f, .8f, 0f, .6f, 1f, 0f, 0f, .8f, .6f, 0f, .4f, 1f }, new[] { 0f, .8f, 0f }, 0),
            new("sunset",     "Sunset",     new[] { 1f, 0f, 0f, 1f, .5f, 0f, 1f, 1f, 0f, 1f, 0f, 1f }, new[] { 1f, 0f, 0f }, 0),
            new("monochrome", "Monochrome", new[] { 1f, 1f, 1f, .5f, .5f, .5f, .2f, .2f, .2f, 0f, 0f, 0f }, new[] { .5f, .5f, .5f }, 1),
            new("dark",       "Dark Mode",  new[] { .18f, .18f, .20f, .10f, .10f, .20f, .08f, .08f, .16f, .04f, .04f, .08f }, new[] { .2f, .2f, .3f }, 2),
            new("ocean",      "Ocean",      Hex("0B2E59", "0E6BA8", "16A6C9", "7FE3E0"), Hex("16A6C9"), 0),
            new("candy",      "Candy",      Hex("FF6FB5", "C58BF2", "8FB8FF", "9DF2E5"), Hex("FF6FB5"), 0),
            new("halloween",  "Halloween",  Hex("2A0D45", "6A1FB0", "FF7A1A", "FFB347"), Hex("FF7A1A"), 0),
        };

        private static readonly LiteDatabase _db = new(Path.Combine(Environment.CurrentDirectory, "Data", "DBs", "UiThemes.db"));
        private static ILiteCollection<Theme> Col => _db.GetCollection<Theme>("themes");

        private static Theme Load(ulong id) => Col.FindById(id) ?? new Theme { AccountId = id };

        private static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

        /// <summary>What the game should draw: the preset's colours, or the custom ones.</summary>
        private static object Resolved(Theme t)
        {
            var p = Presets.FirstOrDefault(x => x.Id == t.Preset);
            bool custom = p == null && t.Stops.Length == 12;
            p ??= Presets[0];
            var stops = custom ? t.Stops : p.Stops;
            var accent = custom && t.Accent.Length == 3 ? t.Accent : custom ? stops.Skip(9).Take(3).ToArray() : p.Accent;
            return new
            {
                ok = true,
                enabled = t.Enabled,
                preset = custom ? "custom" : p.Id,
                stops = Enumerable.Range(0, 4).Select(i => stops.Skip(i * 3).Take(3).ToArray()),
                accent,
                tone = custom ? 0 : p.Tone,
            };
        }

        private (player_info? user, IActionResult? error) LauncherCaller()
        {
            string auth = Request.Headers["Authorization"].ToString();
            string token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth.Substring(7).Trim() : "";
            string data = token.Length == 0 ? "" : ClientSecurity.DecodeToken(token, requireSurface: "launcher");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_"))
                return (null, StatusCode(401, new { ok = false, error = "Sign in again." }));
            try { var u = JsonConvert.DeserializeObject<player_info>(data); if (u != null) return (u, null); } catch { }
            return (null, StatusCode(401, new { ok = false, error = "Sign in again." }));
        }

        /// <summary>The presets, for the launcher's picker.</summary>
        [HttpGet("/api/launcher/v1/theme/presets")]
        public IActionResult PresetList() => Ok(new
        {
            ok = true,
            presets = Presets.Select(p => new { id = p.Id, name = p.Name, stops = Enumerable.Range(0, 4).Select(i => p.Stops.Skip(i * 3).Take(3).ToArray()), accent = p.Accent }),
        });

        /// <summary>The signed-in player's gradient (launcher).</summary>
        [HttpGet("/api/launcher/v1/theme")]
        public IActionResult Mine()
        {
            var (u, err) = LauncherCaller();
            if (err != null) return err;
            return Ok(Resolved(Load(u!.Id)));
        }

        public class ThemeBody
        {
            public bool enabled { get; set; } = true;
            public string? preset { get; set; }
            public float[][]? stops { get; set; }
            public float[]? accent { get; set; }
        }

        /// <summary>Save the signed-in player's gradient (launcher): a preset id, or "custom" with 4 stops + accent.</summary>
        [HttpPost("/api/launcher/v1/theme")]
        [HttpPut("/api/launcher/v1/theme")]
        public IActionResult Save([FromBody] ThemeBody? body)
        {
            var (u, err) = LauncherCaller();
            if (err != null) return err;
            if (body == null) return BadRequest(new { ok = false, error = "Nothing to save." });
            var t = Load(u!.Id);
            t.Enabled = body.enabled;
            string preset = (body.preset ?? "").Trim().ToLowerInvariant();
            if (preset == "custom")
            {
                if (body.stops == null || body.stops.Length != 4 || body.stops.Any(s => s == null || s.Length < 3))
                    return BadRequest(new { ok = false, error = "A custom gradient needs 4 colours." });
                t.Preset = "custom";
                t.Stops = body.stops.SelectMany(s => s.Take(3)).Select(Clamp01).ToArray();
                t.Accent = body.accent is { Length: >= 3 } a ? a.Take(3).Select(Clamp01).ToArray() : t.Stops.Skip(9).Take(3).ToArray();
            }
            else if (Presets.Any(p => p.Id == preset)) { t.Preset = preset; }
            else if (preset.Length > 0) return BadRequest(new { ok = false, error = "Unknown preset." });
            t.UpdatedAt = DateTime.UtcNow;
            Col.Upsert(t);
            Console.WriteLine($"[ui-theme] {u.Id} @{u.Username}: {(t.Enabled ? t.Preset : "off")}");
            return Ok(Resolved(t));
        }

        /// <summary>
        /// The patch's read: a player's gradient by account id. Cosmetic and harmless to expose, so no
        /// sign-in (the patch's own requests carry no token); the patch gate still applies.
        /// </summary>
        [HttpGet("/api/campus/theme/{accountId}")]
        public IActionResult ForPatch(ulong accountId)
        {
            if (accountId == 0 || PlayerDB.GetAccountById(accountId) == null) return Ok(Resolved(new Theme()));
            return Ok(Resolved(Load(accountId)));
        }
    }
}
