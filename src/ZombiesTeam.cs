using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The players in a zombies match: their the original colours (1 white, 2 blue, 3 yellow, 4 green) and player numbers, given
    /// out in the order they joined (Fika's player numbers, the same on every client; solo it's just you); and the other players' tags: a small
    /// name over their head when close, a small dot when far, in their colour.
    /// </summary>
    internal static class ZombiesTeam
    {
        private const float NameRange = 25f;
        private static GUIStyle _style;
        private static Texture2D _dot;
        private static readonly List<Player> Players = new List<Player>();
        private static float _nextList;

        /// <summary>The match's human players, in colour order (refreshed now and then).</summary>
        public static IReadOnlyList<Player> List()
        {
            if (Time.time >= _nextList)
            {
                _nextList = Time.time + 1f;
                Players.Clear();
                if (Singleton<GameWorld>.Instantiated)
                    Players.AddRange(Singleton<GameWorld>.Instance.AllAlivePlayersList.Where(p => IsHuman(p) && !Gone(p))
                        .OrderBy(JoinOrder).ThenBy(p => p.ProfileId, System.StringComparer.Ordinal).Take(4));
                // Everyone who's been in this match, kept even once they're dead or out (the board at the end lists
                // them all: the one who got out saw only themselves, user 2026-11-11)
                if (ZombiesMatch.Active)
                    foreach (Player p in Players)
                        if (p != null && !Roster.Any(r => r.Id == p.ProfileId))
                            Roster.Add((p.ProfileId, p.Profile?.Nickname ?? "", p));
            }
            return Players;
        }

        /// <summary>Everyone who's played in this match (id, name, the player while they're about), in join order.</summary>
        public static readonly List<(string Id, string Name, Player Player)> Roster = new List<(string, string, Player)>();

        /// <summary>A new match: nobody's played yet.</summary>
        public static void ResetRoster()
        {
            Roster.Clear();
            Colours.Clear();
        }

        /// <summary>A player's colour by their profile id (the one kept for the match), 1 if unknown.</summary>
        public static int ColourOf(string id, Player p)
        {
            if (id != null && Colours.TryGetValue(id, out int c))
                return c;
            return p != null ? Colour(p) : 1;
        }

        /// <summary>
        /// A real player, not a zombie. On a Fika client the zombies are observed players whose IsAI is false (Fika's own
        /// IsObservedAI says it): the member's scores once showed three zombies as teammates. Zombies are scav-side too.
        /// </summary>
        // ---------- Players who dropped out (Fika leaves their body standing where it was) ----------

        private const float GoneAfter = 8f;
        private static float _nextBeat, _nextGone;
        private static readonly Dictionary<string, (List<Renderer> r, List<Collider> c)> Ghosts = new Dictionary<string, (List<Renderer>, List<Collider>)>();

        /// <summary>A teammate whose game stopped talking (disconnected): their body is hidden and walk-through.</summary>
        public static bool Gone(Player p) => p != null && (Ghosts.ContainsKey(p.ProfileId) || Out.Contains(p.ProfileId));

        // Players who got out at the exfil: hidden and walk-through on every game, never hunted, not in the team list
        private static readonly HashSet<string> Out = new HashSet<string>();

        public static void MarkExtracted(Player p)
        {
            if (p == null || !Out.Add(p.ProfileId))
                return;
            var renderers = p.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToList();
            var colliders = p.GetComponentsInChildren<Collider>(true).Where(c => c.enabled).ToList();
            GameObject hands = p.HandsController?.ControllerGameObject;
            if (hands != null)
            {
                renderers.AddRange(hands.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && !renderers.Contains(r)));
                colliders.AddRange(hands.GetComponentsInChildren<Collider>(true).Where(c => c.enabled && !colliders.Contains(c)));
            }
            foreach (Renderer r in renderers) r.enabled = false;
            foreach (Collider c in colliders) c.enabled = false;
            _nextList = 0f;
            ZombiesPlugin.Log.LogInfo($"Zombies: {p.Profile?.Nickname} got out at the exfil: hidden, out of the fight");
        }

        public static void ClearExtracted() => Out.Clear();

        /// <summary>
        /// Each frame of a match: a heartbeat to the others every 2 s, and every second the teammates not heard from
        /// for 8 s are taken out of the match (hidden, their colliders off so they block nothing, out of the team list:
        /// scores, zombies' targets); heard again (back), they're put back.
        /// </summary>
        public static void TickPresence()
        {
            if (!ZombiesNet.Online || !ZombiesMatch.LoadedIn)
                return;
            float now = Time.realtimeSinceStartup;
            if (now >= _nextBeat)
            {
                _nextBeat = now + 2f;
                // The headless isn't a player: it never shows as one to the others
                if (!ZombiesNet.Headless)
                {
                    ZombiesNet.Lobby(Singleton<GameWorld>.Instance?.MainPlayer?.Profile?.Nickname ?? "Player", true, ZombiesNet.IsHost);
                    ZombiesNet.ImIn();
                }
            }
            if (now < _nextGone || !Singleton<GameWorld>.Instantiated)
                return;
            _nextGone = now + 1f;
            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (p == null || p.IsYourPlayer || !IsHuman(p))
                    continue;
                // Never heard yet: counted from now (they may still be loading in)
                if (!ZombiesNet.LastHeard.TryGetValue(p.ProfileId, out float heard))
                    ZombiesNet.LastHeard[p.ProfileId] = heard = now;
                bool gone = now - heard > GoneAfter;
                if (gone && !Ghosts.ContainsKey(p.ProfileId))
                {
                    var renderers = p.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToList();
                    var colliders = p.GetComponentsInChildren<Collider>(true).Where(c => c.enabled).ToList();
                    // What's in their hands lives outside the body (a grenade was left floating in the air)
                    GameObject hands = p.HandsController?.ControllerGameObject;
                    if (hands != null)
                    {
                        renderers.AddRange(hands.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && !renderers.Contains(r)));
                        colliders.AddRange(hands.GetComponentsInChildren<Collider>(true).Where(c => c.enabled && !colliders.Contains(c)));
                    }
                    foreach (Renderer r in renderers) r.enabled = false;
                    foreach (Collider c in colliders) c.enabled = false;
                    Ghosts[p.ProfileId] = (renderers, colliders);
                    _nextList = 0f;
                    ZombiesPlugin.Log.LogInfo($"Zombies: {p.Profile?.Nickname} dropped out ({now - heard:0} s silent): hidden, walk-through");
                    ZombiesPlugin.Notify($"{p.Profile?.Nickname} disconnected.", false);
                }
                else if (!gone && Ghosts.TryGetValue(p.ProfileId, out var ghost))
                {
                    foreach (Renderer r in ghost.r) if (r != null) r.enabled = true;
                    foreach (Collider c in ghost.c) if (c != null) c.enabled = true;
                    Ghosts.Remove(p.ProfileId);
                    _nextList = 0f;
                    ZombiesPlugin.Log.LogInfo($"Zombies: {p.Profile?.Nickname} is back");
                }
            }
        }

        public static void ClearPresence()
        {
            Ghosts.Clear();
            ZombiesNet.LastHeard.Clear();
        }

        private static System.Type _fikaPlate;
        private static System.Reflection.FieldInfo _plateField;
        private static bool _plateLooked;
        private static float _nextPlates;

        /// <summary>
        /// Fika's own name plates (FikaHealthBar on every other player and bot: a name and health bar, "???" over the
        /// zombies) are switched off in a match, at runtime only (Fika's settings untouched): ours are the only tags.
        /// Each second, for players Fika gave a plate since.
        /// </summary>
        public static void HideFikaPlates()
        {
            if (Time.time < _nextPlates)
                return;
            _nextPlates = Time.time + 1f;
            if (!_plateLooked)
            {
                _plateLooked = true;
                _fikaPlate = HarmonyLib.AccessTools.TypeByName("Fika.Core.Main.Components.FikaHealthBar");
                _plateField = _fikaPlate != null ? HarmonyLib.AccessTools.Field(_fikaPlate, "_playerPlate") : null;
            }
            if (_fikaPlate == null || !Singleton<GameWorld>.Instantiated)
                return;
            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (p == null || !(p.GetComponent(_fikaPlate) is Behaviour bar) || !bar.enabled)
                    continue;
                bar.enabled = false;
                if (_plateField?.GetValue(bar) is Component plate && plate != null)
                    plate.gameObject.SetActive(false);
            }
        }

        /// <summary>A zombie (any bot), on the host or a Fika client alike.</summary>
        public static bool IsZombie(Player p) => p != null && !IsHuman(p) && !ZombiesNet.IsHeadlessPlayer(p);

        /// <summary>The same for a damage source (IPlayer).</summary>
        public static bool IsZombieSource(IPlayer p) => p != null && (p is Player pl ? !IsHuman(pl) && !ZombiesNet.IsHeadlessPlayer(pl) : p.IsAI);

        public static bool IsHuman(Player p)
        {
            if (p == null || p.IsAI || p.Profile?.Info?.Side == EPlayerSide.Savage)
                return false;
            // Fika's headless host has a player of its own that nobody plays: never a teammate
            if (ZombiesNet.IsHeadlessPlayer(p))
                return false;
            System.Type t = p.GetType();
            if (!ObservedAi.TryGetValue(t, out System.Reflection.FieldInfo field))
                ObservedAi[t] = field = t.GetField("IsObservedAI", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return !(field?.GetValue(p) is bool observedAi && observedAi);
        }

        private static readonly Dictionary<System.Type, System.Reflection.FieldInfo> ObservedAi = new Dictionary<System.Type, System.Reflection.FieldInfo>();

        // Fika's player number: the host gives each player the next one as they join (the host 1, then 2, 3, 4), the
        // same on every client. Without Fika: all the same, so the profile order decides
        private static readonly Dictionary<System.Type, System.Reflection.MemberInfo> NetIds = new Dictionary<System.Type, System.Reflection.MemberInfo>();

        /// <summary>
        /// This player's place in the team, 1..4, already right at the map build when the others aren't in yet: Fika's
        /// player number (the host 1, then each as they joined, the same on every game), else the team list's order.
        /// </summary>
        public static int Place(Player p)
        {
            if (p == null)
                return 1;
            int net = JoinOrder(p);
            // A Fika headless joined first (its own player is number 1): the lobby's leader is player 1 after it
            if (ZombiesNet.HeadlessMatch && net > 0)
                net--;
            return net > 0 ? Mathf.Clamp(net, 1, 4) : Colour(p);
        }

        /// <summary>How many are playing this match, as far as is known at the map build (the party, Fika's count, the list).</summary>
        public static int ExpectedPlayers() => Mathf.Max(ZombiesParty.Expected, Mathf.Max(ZombiesNet.Online ? (ZombiesNet.FikaPlayers?.Invoke() ?? 1) - (ZombiesNet.HeadlessMatch ? 1 : 0) : 1, List().Count));

        private static int JoinOrder(Player p)
        {
            System.Type type = p.GetType();
            if (!NetIds.TryGetValue(type, out System.Reflection.MemberInfo member))
                NetIds[type] = member = (System.Reflection.MemberInfo)HarmonyLib.AccessTools.Field(type, "NetId") ?? HarmonyLib.AccessTools.Property(type, "NetId");
            try
            {
                object id = member is System.Reflection.PropertyInfo prop ? prop.GetValue(p) : (member as System.Reflection.FieldInfo)?.GetValue(p);
                return id == null ? 0 : System.Convert.ToInt32(id);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>A player's colour number (1-4).</summary>
        // Each player's colour, kept for the whole match (user, 2026-11-11): it was their place in the list of living,
        // still-in players, so everyone after someone who died or got out changed colour (spectating showed it)
        private static readonly Dictionary<string, int> Colours = new Dictionary<string, int>();

        public static int Colour(Player player)
        {
            // Live (join order) until round 1 is under way, so every game has all the players before they're kept
            if (!ZombiesMatch.Active || ZombiesRounds.Round <= 0)
            {
                if (!ZombiesMatch.Active)
                    Colours.Clear();
                IReadOnlyList<Player> list0 = List();
                for (int i = 0; i < list0.Count; i++)
                    if (list0[i] == player)
                        return i + 1;
                return 1;
            }
            string id = player?.ProfileId;
            if (id == null)
                return 1;
            if (Colours.TryGetValue(id, out int had))
                return had;
            // New this match: everyone in the list gets theirs in join order, the first free colour each
            foreach (Player p in List())
                if (p != null && !Colours.ContainsKey(p.ProfileId))
                {
                    int free = 1;
                    while (Colours.ContainsValue(free) && free < 4)
                        free++;
                    Colours[p.ProfileId] = free;
                }
            return Colours.TryGetValue(id, out int now) ? now : 1;
        }

        public static void DrawTags()
        {
            Camera cam = Camera.main;
            if (cam == null)
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
                _dot = MakeDot();
            }
            float k = Screen.height / 1080f;
            _style.fontSize = Mathf.Max(8, Mathf.RoundToInt(15f * k));
            foreach (Player p in List())
            {
                if (p.IsYourPlayer)
                    continue;
                Vector3 over = (p.PlayerBones?.Head?.Original != null ? p.PlayerBones.Head.Original.position : p.Position + Vector3.up * 1.7f) + Vector3.up * 0.35f;
                Vector3 s = cam.WorldToScreenPoint(over);
                if (s.z <= 0f)
                    continue;
                float x = s.x, y = Screen.height - s.y;
                Color c = ScoresHud.PlayerColours[Colour(p) - 1];
                if (s.z <= NameRange)
                {
                    string name = p.Profile?.Nickname ?? "";
                    _style.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
                    GUI.Label(new Rect(x - 150f + 1f, y - 12f * k + 1f, 300f, 24f * k), name, _style);
                    _style.normal.textColor = c;
                    GUI.Label(new Rect(x - 150f, y - 12f * k, 300f, 24f * k), name, _style);
                }
                else
                {
                    float d = 7f * k;
                    GUI.color = c;
                    GUI.DrawTexture(new Rect(x - d * 0.5f, y - d * 0.5f, d, d), _dot);
                    GUI.color = Color.white;
                }
            }
        }

        private static Texture2D MakeDot()
        {
            const int n = 16;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01((1f - d) * 3f)));
                }
            t.Apply();
            return t;
        }
    }
}
