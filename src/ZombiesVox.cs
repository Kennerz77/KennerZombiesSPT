using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's crew as the players' voices: each player in a zombies match is, without being told, one of Dempsey (0),
    /// Nikolai (1), Richtofen (2) and Takeo (3) - solo a random one each match; with a team each a different one, by
    /// their profile ids in order, the same on every game - and says that character's the original lines (Origins and Kino, built
    /// by 99_Tools/zm_vox_build.py into plugins\Kennerz_ZM_Core\vox\c&lt;n&gt;\&lt;category&gt;_&lt;i&gt;.wav) when things happen:
    /// box guns by kind, the teddy, wall buys, too poor, Pack-a-Punch, perks, power-ups, going down, back up, reviving,
    /// kills, ammo, getting swarmed, boards, the start. Each category has its own chance and wait, one line at a time.
    /// </summary>
    internal static class ZombiesVox
    {
        // The voice folders c0..c3 are Kino / Shi No Numa's numbering, the original's (the user heard Takeo as c2): 0 Dempsey,
        // 1 Nikolai, 2 Takeo, 3 Richtofen
        public static readonly string[] Names = { "Dempsey", "Nikolai", "Takeo", "Richtofen" };

        private static ConfigEntry<float> _volume;
        private static int _me = -1;
        private static string _matchFor;
        private static float _quietUntil;
        private static readonly Dictionary<string, float> Next = new Dictionary<string, float>();
        private static readonly Dictionary<string, List<string>> Files = new Dictionary<string, List<string>>();
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
        private static AudioSource _source;
        private const bool PlayTeammates = false;

        public static void Bind(ConfigFile config, string section)
        {
            _volume = config.Bind("2. Zombies sounds", "Character voice", 0.462f, new ConfigDescription(
                "Your character's lines.", new AcceptableValueRange<float>(0f, 1f)));
        }

        /// <summary>Your character this match (0-3).</summary>
        public static int Me
        {
            get
            {
                string match = ZombiesMatch.LocationHere ?? "";
                if (_me < 0 || _matchFor != match)
                {
                    _matchFor = match;
                    _me = Pick();
                    ZombiesPlugin.Log.LogInfo($"Zombies: you're {Names[_me]} this match");
                }
                return _me;
            }
        }

        /// <summary>
        /// Each player slot's character (the user, 2026-11-07): player 1 Richtofen, player 2 Dempsey, player 3 Nikolai,
        /// player 4 Takeo. Values are the original's character numbers (data/vox/c0 Dempsey, c1 Nikolai, c2 Richtofen, c3 Takeo).
        /// </summary>
        private static readonly int[] BySlot = { 3, 0, 1, 2 };

        /// <summary>The the original portrait (score_char1..4: Dempsey, Nikolai, Richtofen, Takeo) for player slot 1..4.</summary>
        public static int PortraitForSlot(int slot) => PortraitBySlot[Mathf.Clamp(slot, 1, 4) - 1];

        // score_char1..4 are the original's order (Dempsey, Nikolai, Richtofen, Takeo): player 1 Richtofen 3, 2 Dempsey 1, 3 Nikolai 2, 4 Takeo 4
        private static readonly int[] PortraitBySlot = { 3, 1, 2, 4 };

        // Solo: player 1. A team: each player's slot by profile id order (the same on every game)
        private static int Pick()
        {
            // The same player number as the spawns and colours: Fika's (host 1, then join order), solo 1
            Player mine = Singleton<GameWorld>.Instance?.MainPlayer;
            int slot = ZombiesTeam.ExpectedPlayers() > 1 && mine != null ? ZombiesTeam.Place(mine) - 1 : 0;
            return BySlot[Mathf.Clamp(slot, 0, 3)];
        }

        public static void NewMatch() => _me = -1;

        private const float ChatterGap = 8f;
        private static float _lastLineAt = -100f;

        /// <summary>
        /// The same, after a moment (the original: the character's reaction to a power-up comes about 2 s after the pickup, once
        /// the announcer has said its name).
        /// </summary>
        public static void SayAfter(float seconds, string category, float chance = 1f, float wait = 4f) =>
            ZombiesPlugin.Instance.StartCoroutine(SayLater(seconds, category, chance, wait));

        private static System.Collections.IEnumerator SayLater(float seconds, string category, float chance, float wait)
        {
            yield return new WaitForSeconds(seconds);
            if (ZombiesDowned.Mine == ZombiesDowned.State.Up)
                Say(category, chance, wait);
        }

        /// <summary>Your character says a line of this kind, maybe: chance 0-1, then not again for <paramref name="wait"/> s.</summary>
        private static readonly Dictionary<string, string> Fallback = new Dictionary<string, string> { ["wpck_rifle"] = "wpck_smg", ["wpck_raygun"] = "wpck_ray" };

        private const float RaygunLineChance = 0.6f;

        public static void Say(string category, float chance = 1f, float wait = 4f)
        {
            if (!ZombiesMatch.Active || Time.time < _quietUntil)
                return;
            if (Next.TryGetValue(category, out float next) && Time.time < next)
                return;
            // The original's pace (the user's gameplay video, 2026-11-07: a line every 15-20 s, nearly all reactions to the box,
            // power-ups, perks and the nuke): the chatter (kills, ammo, boards) waits 8 s after any line
            if ((category.StartsWith("kill_") || category == "ammo_low" || category == "rebuild_boards") && Time.time < _lastLineAt + ChatterGap)
                return;
            if (Random.value > chance)
                return;
            int c = Me;
            string key = $"c{c}/{category}";
            if (!Files.TryGetValue(key, out List<string> list))
            {
                string dir = Path.Combine(ZombiesPlugin.Dir, "vox", "c" + c);
                // Only "<category>_<n>" ("pap" never takes the "pap_wait" lines)
                list = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, category + "_*.wav").Select(Path.GetFileNameWithoutExtension)
                        .Where(n => int.TryParse(n.Substring(category.Length + 1), out _)).ToList()
                    : new List<string>();
                Files[key] = list;
            }
            if (list.Count == 0)
            {
                // A character with none of these lines says its nearest kind (rifles: only Dempsey has rifle lines;
                // the others' old "rifle" ones were sniper lines, 2026-11-08)
                if (Fallback.TryGetValue(category, out string instead))
                    Say(instead, 1f, wait);
                return;
            }
            string name = list[Random.Range(0, list.Count)];
            string clipKey = $"c{c}/{name}";
            if (!Clips.TryGetValue(clipKey, out AudioClip clip) || clip == null || clip.length <= 0f)
            {
                clip = ZombiesWav.Load(Path.Combine(ZombiesPlugin.Dir, "vox", "c" + c), name);
                Clips[clipKey] = clip;
            }
            clip = ZombiesWav.Fresh(clip);
            if (clip == null)
                return;
            if (_source == null)
            {
                var go = new GameObject("Zombies_CharacterVoice");
                Object.DontDestroyOnLoad(go);
                _source = go.AddComponent<AudioSource>();
                ZombiesHeadAudio.Setup(_source, 16);
                _source.playOnAwake = false;
            }
            _source.Stop();
            _source.clip = clip;
            _source.volume = (_volume?.Value ?? 0.9f) * ZombiesSounds.Volume(ZombiesSounds.Group.Zombies);
            _source.Play();
            _quietUntil = Time.time + clip.length + 0.4f;
            _lastLineAt = Time.time + clip.length;
            Next[category] = Time.time + wait;
            ZombiesDebug.Log($"vox: {Names[c]} says {name}");
            // Only you hear your own character (user, 2026-11-09): never sent to teammates
        }

        /// <summary>
        /// A teammate's character line (ZombiesNet): not played any more (user, 2026-11-09: your character's lines are
        /// only for you). Lines from an older build that still sends them are ignored here.
        /// </summary>
        public static void Remote(string from, int c, string name)
        {
            if (!PlayTeammates || !ZombiesMatch.Active || c < 0 || c > 3 || string.IsNullOrEmpty(name) || name.Contains("/") || name.Contains("\\"))
                return;
            Player who = Singleton<GameWorld>.Instance?.AllAlivePlayersList.FirstOrDefault(p => p != null && p.ProfileId == from);
            if (who == null)
                return;
            string clipKey = $"c{c}/{name}";
            if (!Clips.TryGetValue(clipKey, out AudioClip clip) || clip == null || clip.length <= 0f)
            {
                clip = ZombiesWav.Load(Path.Combine(ZombiesPlugin.Dir, "vox", "c" + c), name);
                Clips[clipKey] = clip;
            }
            clip = ZombiesWav.Fresh(clip);
            if (clip == null)
                return;
            Transform head = who.PlayerBones?.Head?.Original ?? who.Transform.Original;
            var go = new GameObject("Zombies_TeammateVoice");
            go.transform.SetParent(head, false);
            AudioSource s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 3f;
            s.maxDistance = 40f;
            s.volume = (_volume?.Value ?? 0.9f) * ZombiesSounds.Volume(ZombiesSounds.Group.Zombies);
            s.Play();
            Object.Destroy(go, clip.length + 0.2f);
        }

        /// <summary>A gun out of the box: the line for its kind (the Ray Gun, the monkey, a pistol...).</summary>
        public static void BoxPickup(BoxGuns.Gun gun)
        {
            if (gun == null)
                return;
            if (gun.Id == BoxGuns.MonkeyId)
            {
                Say("wpck_monkey", 1f, 2f);
                return;
            }
            if (gun.Role == "box_wonder")
            {
                // The Ray Gun out of the box: now and then the "I got the Ray Gun!" line (Dempsey's, from the user,
                // 2026-11-09; the others fall back to their own lines). Only the box: the PaP'd one says "pap" lines
                bool raygun = gun.Id == "raygun" || gun.Id == ZombiesAmmo.RaygunId;
                Say(raygun && Random.value < RaygunLineChance ? "wpck_raygun" : "wpck_ray", 1f, 2f);
                return;
            }
            string cls = ZombiesGunTable.Class(gun) ?? "";
            string cat = cls switch
            {
                "pistol" => Random.value < 0.4f ? "wpck_crappy" : "wpck_pistol",
                "smg" => "wpck_smg",
                "shotgun" => "wpck_shotgun",
                "ar" => "wpck_rifle",
                "lmg" => "wpck_mg",
                "sniper" => "wpck_snipe",
                "launcher" => "wpck_explo",
                _ => "wpck_rifle",
            };
            Say(cat, 0.85f, 2f);
        }

        /// <summary>A perk drunk (its id as in ZombiesPerks): its line a moment later.</summary>
        public static void PerkAfter(string id, float seconds)
        {
            string cat = PerkCategory(id);
            if (cat != null)
                SayAfter(seconds, cat, 1f, 2f);
        }

        /// <summary>A perk drunk (its id as in ZombiesPerks).</summary>
        public static void Perk(string id)
        {
            string cat = PerkCategory(id);
            if (cat != null)
                Say(cat, 1f, 2f);
        }

        private static string PerkCategory(string id)
        {
            return id switch
            {
                "jugg" => "perk_jugg",
                "speed" => "perk_speed",
                "revive" => "perk_revive",
                "dtap" => "perk_dtap",
                "stamin" => "perk_stamin",
                "mule" => "perk_mule",
                _ => null,
            };
        }

        // ---------- What's watched every second: ammo, being swarmed ----------

        private static float _nextLook;
        private static string _lastHeld;

        public static void Tick(Player me)
        {
            if (!ZombiesMatch.Active || me == null || Time.time < _nextLook || ZombiesDowned.Mine != ZombiesDowned.State.Up)
                return;
            _nextLook = Time.time + 1f;
            // Swarmed: four or more zombies within 3 m
            int close = ZombiesRounds.Zombies.Count(z => z?.GetPlayer != null && Vector3.Distance(z.Position, me.Transform.position) < 3f);
            if (close >= 4)
                Say("oh_shit", 0.6f, 40f);
            // Ammo: the gun in the hands
            if (me.HandsController?.Item is EFT.InventoryLogic.Weapon w && !ZombiesAmmo.IsWonderWeapon(w))
            {
                ZombiesAmmo.Shown(w, out int mag, out int reserve);
                // Bone dry (nothing in it, nothing left): said often, not just now and then, and every time a dry gun
                // is swapped to (user, 2026-11-09). Not held back by the chatter gap
                bool swappedTo = w.Id.ToString() != _lastHeld;
                _lastHeld = w.Id.ToString();
                if (mag == 0 && reserve == 0)
                {
                    if (swappedTo)
                        Next.Remove("ammo_out");
                    Say("ammo_out", 1f, 15f);
                }
                // Low: the last of it, nothing left in reserve (user: the ammo lines are for when it's all nearly gone)
                else if (reserve == 0 && mag > 0 && mag <= Mathf.Max(2, (w.GetCurrentMagazine()?.MaxCount ?? 8) / 3))
                    Say("ammo_low", 0.5f, 60f);
            }
        }
    }
}
