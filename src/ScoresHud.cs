using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's scores in the corner: a column of players, each a character portrait with their points beside it in their
    /// player colour (1 white, 2 blue, 3 yellow, 4 green); your own row at the bottom, its portrait bigger. Solo it's
    /// just you; with Fika the others stack above. Portraits are loose PNGs in hud\ beside the DLL (score_char1..4).
    /// Where it sits, how big, and which character you are are F12 settings; each teammate row's picture and points can
    /// be nudged on their own (teammate 1 is the row right above yours).
    /// </summary>
    internal static class ScoresHud
    {
        public static readonly Color[] PlayerColours =
        {
            // By player slot = character (the user, 2026-11-07): 1 Richtofen green, 2 Dempsey white, 3 Nikolai blue,
            // 4 Takeo yellow
            new Color(0.45f, 0.95f, 0.35f),
            new Color(0.95f, 0.95f, 0.95f),
            new Color(0.35f, 0.75f, 1f),
            new Color(1f, 0.85f, 0.25f),
        };

        private static ConfigEntry<float> _x, _y, _size;
        private static ConfigEntry<int> _character, _colour;
        private static ConfigEntry<bool> _photos, _dummies;
        // Per teammate row (1 = right above yours): picture across/up, points across/up, in pixels at 1080p
        private static readonly ConfigEntry<float>[,] _nudge = new ConfigEntry<float>[3, 4];

        private static readonly List<Row> DummyRows = new List<Row>
        {
            new Row { Character = 2, Points = 3460, Colour = 2 },
            new Row { Character = 3, Points = 1820, Colour = 3 },
            new Row { Character = 4, Points = 5110, Colour = 4 },
        };

        /// <summary>Three made-up teammates (F12 "Show dummy players"), else none.</summary>
        public static IList<Row> Dummies => _dummies != null && _dummies.Value ? DummyRows : null;

        public static void Bind(ConfigFile config, string section)
        {
            _x = config.Bind(section, "Scores across", 0.006f, new ConfigDescription(
                "Where the scores sit, from the left edge of the screen (0) to the right (1).", new AcceptableValueRange<float>(0f, 1f)));
            _y = config.Bind(section, "Scores up", 0.23f, new ConfigDescription(
                "Where the bottom of the scores sits, from the bottom of the screen (0) to the top (1).", new AcceptableValueRange<float>(0f, 1f)));
            _size = config.Bind(section, "Scores size", 0.59f, new ConfigDescription("How big the scores are.", new AcceptableValueRange<float>(0.5f, 2.5f)));
            _character = config.Bind(section, "Your character", 1, new ConfigDescription(
                "Your portrait beside your points (four characters).", new AcceptableValueRange<int>(1, 4)));
            _photos = config.Bind(section, "Player photos", true,
                "On: a photo of each player's own PMC (teammates photographed on your game). Off: their character portrait (players 1-4).");
            _dummies = config.Bind(section, "Show dummy players", false,
                "Three made-up teammates in the scores and the Tab scoreboard, to lay them out before playing with a team.");
            _colour = config.Bind(section, "Your player colour", 1, new ConfigDescription(
                "Your points' colour: 1 white, 2 blue, 3 yellow, 4 green (players 1-4).", new AcceptableValueRange<int>(1, 4)));
            string[] what = { "picture across", "picture up", "points across", "points up" };
            for (int row = 0; row < 3; row++)
                for (int w = 0; w < 4; w++)
                    _nudge[row, w] = config.Bind(section, $"Teammate {row + 1} {what[w]}", 0f, new ConfigDescription(
                        $"Moves teammate {row + 1}'s {what[w].Split(' ')[0]} (teammate 1 is the row right above yours), in pixels at 1080p. Turn on \"Show dummy players\" to see them.",
                        new AcceptableValueRange<float>(-300f, 300f)));
        }

        private static Vector2 Nudge(int row, int first, float k) =>
            row < 3 && _nudge[row, first] != null ? new Vector2(_nudge[row, first].Value, -_nudge[row, first + 1].Value) * k : Vector2.zero;

        /// <summary>The real teammates (Fika): their colour, their points as they last told us (ZombiesNet), their face.</summary>
        public static IList<Row> Teammates()
        {
            var rows = new List<Row>();
            foreach (EFT.Player p in ZombiesTeam.List())
            {
                if (p == null || p.IsYourPlayer)
                    continue;
                int colour = ZombiesTeam.Colour(p);
                rows.Add(new Row
                {
                    Id = p.ProfileId,
                    Character = ZombiesVox.PortraitForSlot(colour),
                    Colour = colour,
                    Points = ZombiesNet.TeamPoints.TryGetValue(p.ProfileId, out int pts) ? pts : 500,
                    Face = PlayerPortrait.GetOrTake(p),
                });
            }
            return rows.Count > 0 ? rows : null;
        }

        /// <summary>One row: whose portrait, their points, their colour.</summary>
        public struct Row
        {
            public int Character, Points, Colour;
            public Texture2D Face; // the player's own character (PlayerPortrait), else the the original portrait
            public string Id;      // a teammate's profile id (their points roll and pop like yours)
        }

        // The original's points pop-ups: each gain or spend floats off the score and fades
        private struct Pop
        {
            public int Amount;
            public float Start, Drift, Lift;
        }

        // The score shown rolls to the real one (the original's tally), with a little bump as it changes
        private static float _shown = -1f, _bump;

        private static readonly List<Pop> Pops = new List<Pop>();
        private const float PopTime = 1.1f;
        private static readonly Color Gain = new Color(1f, 0.85f, 0.3f), Loss = new Color(1f, 0.25f, 0.2f);

        // Each teammate's row: its rolling tally, bump and pop-ups, from the points their game sends (user, 2026-11-09:
        // The original shows everyone's points moving, not just yours)
        private class TeamAnim
        {
            public float Shown = -1f, Bump;
            public int Last = int.MinValue;
            public readonly List<Pop> Pops = new List<Pop>();
        }

        private static readonly Dictionary<string, TeamAnim> Team = new Dictionary<string, TeamAnim>();

        /// <summary>Points gained (+) or spent (-) by you: shown floating off your score.</summary>
        public static void PopPoints(int amount)
        {
            AddPop(Pops, amount);
            if (amount != 0)
                _bump = 1f;
        }

        private static void AddPop(List<Pop> pops, int amount)
        {
            if (amount == 0)
                return;
            // Gains fly out on their own arc (up or down a little); spends drop away
            pops.Add(new Pop
            {
                Amount = amount, Start = Time.time,
                Drift = amount > 0 ? UnityEngine.Random.Range(-1f, 1f) : UnityEngine.Random.Range(0.6f, 1f),
                Lift = amount > 0 ? UnityEngine.Random.Range(0.4f, 1f) : 0f,
            });
        }

        private static readonly Texture2D[] Portraits = new Texture2D[4];
        private static readonly bool[] Tried = new bool[4];
        private static GUIStyle _style;

        /// <summary>Solo: just you (others go in first, above you, once Fika shares the match).</summary>
        public static void Draw(int points, IList<Row> others = null, Texture2D myFace = null)
        {
            float k = Screen.height / 1080f * _size.Value;
            float x = Screen.width * _x.Value;
            float bottom = Screen.height * (1f - _y.Value);
            float small = 64f * k, big = 98f * k, gap = 7f * k; // a little bigger (user, 2026-11-11)
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Overflow };
                // EFT's own font when the game has it for IMGUI (user, 2026-11-11)
                if (ZombiesUi.DisplayImguiFont() != null)
                {
                    _style.font = ZombiesUi.DisplayImguiFont();
                    _style.fontStyle = FontStyle.Bold;
                }

            // You, at the bottom, bigger
            float y = bottom - big;
            // In a match your colour is your place in the team (ZombiesTeam), the same on every Fika client
            int colour = ZombiesMatch.Active ? ZombiesTeam.Colour(Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer) : _colour.Value;
            // The tally: rolls towards the real score, quicker the further it has to go
            if (_shown < 0f || Mathf.Abs(_shown - points) > 100000f)
                _shown = points;
            float speed = Mathf.Max(Mathf.Abs(points - _shown) * 6f, 150f);
            _shown = Mathf.MoveTowards(_shown, points, speed * Time.deltaTime);
            _bump = Mathf.MoveTowards(_bump, 0f, Time.deltaTime * 4f);
            Rect mine = DrawRow(new Row { Character = ZombiesMatch.Active ? ZombiesVox.PortraitForSlot(colour) : _character.Value, Points = Mathf.RoundToInt(_shown), Colour = colour, Face = myFace }, x, y, big,
                50f * k * (1f + 0.12f * _bump));
            DrawPops(mine, k, Pops);
            if (others == null)
                return;
            for (int i = others.Count - 1, row = 0; i >= 0; i--, row++)
            {
                y -= small + gap;
                Row other = others[i];
                // The dummy players wear your own picture, to size the rows with a real face
                if (other.Face == null && ReferenceEquals(others, DummyRows))
                    other.Face = myFace;
                TeamAnim anim = null;
                if (!string.IsNullOrEmpty(other.Id))
                {
                    if (!Team.TryGetValue(other.Id, out anim))
                        Team[other.Id] = anim = new TeamAnim();
                    if (anim.Last != int.MinValue && anim.Last != other.Points)
                    {
                        AddPop(anim.Pops, other.Points - anim.Last);
                        anim.Bump = 1f;
                    }
                    anim.Last = other.Points;
                    if (anim.Shown < 0f || Mathf.Abs(anim.Shown - other.Points) > 100000f)
                        anim.Shown = other.Points;
                    anim.Shown = Mathf.MoveTowards(anim.Shown, other.Points, Mathf.Max(Mathf.Abs(other.Points - anim.Shown) * 6f, 150f) * Time.deltaTime);
                    anim.Bump = Mathf.MoveTowards(anim.Bump, 0f, Time.deltaTime * 4f);
                    other.Points = Mathf.RoundToInt(anim.Shown);
                }
                Rect theirs = DrawRow(other, x + (big - small) * 0.5f, y, small, 36f * k * (1f + 0.12f * (anim?.Bump ?? 0f)), Nudge(row, 0, k), Nudge(row, 2, k));
                if (anim != null)
                    DrawPops(theirs, k * 0.75f, anim.Pops);
            }
        }

        private static void DrawPops(Rect score, float k, List<Pop> pops)
        {
            pops.RemoveAll(p => Time.time - p.Start > PopTime);
            int size = Mathf.Max(8, Mathf.RoundToInt(30f * k));
            foreach (Pop p in pops)
            {
                float t = (Time.time - p.Start) / PopTime;
                // Gains: out to the right on an arc (a rise, then its drift); spends: dropping away under the score
                float ease = 1f - (1f - t) * (1f - t);
                float px = score.xMax + 12f * k + ease * (p.Amount > 0 ? 110f : 30f) * k;
                float arc = p.Amount > 0 ? (-Mathf.Sin(t * Mathf.PI) * 28f * p.Lift + p.Drift * t * 45f) : t * 60f * p.Drift;
                float py = score.center.y - size * 0.6f + arc * k;
                string text = (p.Amount > 0 ? "+" : "") + p.Amount;
                // A little bigger as it appears, settling smaller as it goes
                _style.fontSize = Mathf.Max(8, Mathf.RoundToInt(size * Mathf.Lerp(1.25f, 0.8f, ease)));
                float a = 1f - t * t;
                _style.normal.textColor = new Color(0f, 0f, 0f, 0.7f * a);
                GUI.Label(new Rect(px + 2f, py + 2f, 200f, size * 1.4f), text, _style);
                Color c = p.Amount > 0 ? Gain : Loss;
                c.a = a;
                _style.normal.textColor = c;
                GUI.Label(new Rect(px, py, 200f, size * 1.4f), text, _style);
            }
        }

        private static Rect DrawRow(Row row, float x, float y, float picture, float fontSize, Vector2 pictureNudge = default, Vector2 pointsNudge = default)
        {
            Texture2D portrait = row.Face != null && _photos?.Value == true ? row.Face : Portrait(row.Character);
            if (portrait != null)
                GUI.DrawTexture(new Rect(x + pictureNudge.x, y + pictureNudge.y, picture, picture), portrait, ScaleMode.ScaleToFit, true);
            _style.fontSize = Mathf.Max(8, Mathf.RoundToInt(fontSize));
            var text = new Rect(x + picture + picture * 0.15f + pointsNudge.x, y + pointsNudge.y, 400f, picture);
            string points = row.Points.ToString();
            // A soft dark edge so it reads on any background
            _style.normal.textColor = new Color(0f, 0f, 0f, 0.75f);
            GUI.Label(new Rect(text.x + 2f, text.y + 2f, text.width, text.height), points, _style);
            _style.normal.textColor = PlayerColours[Mathf.Clamp(row.Colour, 1, 4) - 1];
            GUI.Label(text, points, _style);
            Vector2 used = _style.CalcSize(new GUIContent(points));
            return new Rect(text.x, text.y, used.x, picture);
        }

        private static Texture2D Portrait(int character)
        {
            int i = Mathf.Clamp(character, 1, 4) - 1;
            if (Portraits[i] != null || Tried[i])
                return Portraits[i];
            Tried[i] = true;
            string file = Path.Combine(ZombiesPlugin.Dir, "hud", $"score_char{i + 1}.png");
            if (!File.Exists(file))
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            if (tex.LoadImage(File.ReadAllBytes(file)))
                Portraits[i] = tex;
            return Portraits[i];
        }
    }
}
