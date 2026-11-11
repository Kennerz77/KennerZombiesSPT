using System.Collections;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's frag grenades, as M67s in the rig (G throws one, EFT's own throw). Everyone starts with 2. Each new round
    /// adds 2, up to 4 (user, 2026-11-11: was back up to 2), the wall buy and Max Ammo fill to 4. In a zombies
    /// match the M67 goes off 2 s after it leaves the hand (EFT's own fuse is 5 s from the pin). Never Pack-a-Punched.
    /// Their icons sit left of the ammo counter, one per grenade.
    /// </summary>
    internal static class ZombiesGrenades
    {
        public const string M67 = "58d3db5386f77426186285a0";
        /// <summary>The wall buy's What for grenades (in the editor's wall buy list).</summary>
        public const string WallId = "m67_grenades";
        public const int Start = 2, PerRound = 2, Most = 4;
        /// <summary>the original's fuse: 5 s from the pin coming out (held in the hand it cooks; ZombiesQuickGrenade).</summary>
        public const float FuseSeconds = 5f;

        private static bool _patched;
        private static bool _iconLoading;

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            harmony.Patch(AccessTools.Method(typeof(Grenade), nameof(Grenade.StartTimer)),
                prefix: new HarmonyMethod(typeof(ZombiesGrenades), nameof(ShortFuse)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(ThrowWeap), nameof(ThrowWeap.FragmentsCount)),
                postfix: new HarmonyMethod(typeof(ZombiesGrenades), nameof(NoShrapnel)));
            harmony.Patch(AccessTools.Method(typeof(Grenade), nameof(Grenade.InvokeBlowUpEvent)),
                prefix: new HarmonyMethod(typeof(ZombiesGrenades), nameof(Blast)));
        }

        // No shrapnel in a zombies match: EFT's explosion reads the fragment count off the grenade (ThrowWeap) for its
        // fragments, so none fly. The bang, the flash, the smoke and the shake stay.
        private static void NoShrapnel(ref int __result)
        {
            if (ZombiesMatch.Active)
                __result = 0;
        }

        // The original's frag instead (an M67's damage is all shrapnel, it has no blast of its own): on the thrower's own game,
        // every zombie within the radius with nothing solid in between takes the original damage, full close in, less to the edge
        // (ZombiesHealth's "Grenade damage" settings). Only zombies: no friendly fire. The hits and kills are the
        // thrower's (EFT damage from them, through ZombiesRules/ZombiesHealth like a shot: 10 points a zombie hit).
        private static void Blast(Grenade __instance)
        {
            try
            {
                if (!ZombiesMatch.Active || __instance?.WeaponSource == null)
                    return;
                // Frag grenades only (by their template's own count: the item's reads 0 here); flashes and smokes do nothing
                if (!(__instance.WeaponSource.Template is ThrowWeapTemplate template) || template.FragmentsCount <= 0)
                    return;
                GameWorld world = Singleton<GameWorld>.Instance;
                Player me = world?.MainPlayer;
                if (me == null || __instance.ProfileId != me.ProfileId)
                    return;
                IObserverToPlayerBridge thrower = world.GetAlivePlayerBridgeByProfileID(me.ProfileId);
                Vector3 at = __instance.transform.position + Vector3.up * 0.3f;
                // The monkey's blast reaches half as far again (it's meant to take the crowd round it)
                float radius = ZombiesHealth.GrenadeRadius * (ZombiesMonkey.Is(__instance.WeaponSource) ? 1.5f : 1f);
                int hit = 0;
                foreach (Player zombie in world.AllAlivePlayersList.Where(p => ZombiesTeam.IsZombie(p)).ToList())
                {
                    if (zombie.HealthController == null || !zombie.HealthController.IsAlive)
                        continue;
                    Vector3 chest = zombie.Transform.position + Vector3.up * 1.1f;
                    float d = Vector3.Distance(at, chest);
                    if (d > radius || Physics.Linecast(at, chest, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
                        continue;
                    float bo = Mathf.Lerp(ZombiesHealth.GrenadeNear, ZombiesHealth.GrenadeFar, Mathf.InverseLerp(1f, radius, d));
                    var info = new DamageInfo
                    {
                        DamageType = EDamageType.Explosion,
                        Damage = ZombiesHealth.ExplosionEft(bo),
                        Direction = (chest - at).normalized,
                        HitPoint = chest,
                        HitNormal = (at - chest).normalized,
                        MasterOrigin = at,
                        Player = thrower,
                        Weapon = __instance.WeaponSource,
                        BodyPartColliderType = EBodyPartColliderType.RibcageUp,
                    };
                    ZombiesRules.HitZombie(zombie, info);
                    hit++;
                }
                ZombiesDebug.Log($"grenade blast at {at}: {hit} zombie(s) hit");
                // Too close to your own grenade: a slap without PHD Flopper
                ZombiesPhd.SelfBlast(me, at, "grenade");
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies grenades: the blast failed ({e.Message})");
            }
        }

        private static readonly AccessTools.FieldRef<Grenade, float> TimeSpent = AccessTools.FieldRefAccess<Grenade, float>("_timeSpent");

        /// <summary>This thrown grenade goes off now (EFT's own blow-up: the explosion, then it's gone).</summary>
        public static void Detonate(Grenade g)
        {
            if (g == null)
                return;
            try
            {
                g.InvokeBlowUpEvent();
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: a grenade wouldn't go off ({e.Message})");
            }
            // Gone after the bang, whatever EFT does with it
            if (g != null)
                Object.Destroy(g.gameObject, 0.1f);
        }

        // The thrown grenade's timer runs what's left of the fuse after the time held: set so 2 s are left
        private static void ShortFuse(Grenade __instance)
        {
            if (!ZombiesMatch.Active || __instance?.WeaponSource == null)
                return;
            // The Cymbal Monkey: its own look, sounds and lure, and a fuse as long as its song (it may run past the
            // M67's own delay, so the time spent starts below zero)
            string thrower = __instance.ProfileId;
            bool mine = thrower == null || thrower == Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
            if (ZombiesMonkey.Is(__instance.WeaponSource))
            {
                // No fuse of its own: it goes off when its song ends (ZombiesMonkey.Detonate); held in the hand it never cooks
                if (mine)
                    ZombiesQuickGrenade.TakeCooked();
                ZombiesMonkey.Thrown(__instance);
                TimeSpent(__instance) = -100000f;
                return;
            }
            if (__instance.WeaponSource.TemplateId.ToString() != M67)
                return;
            ZombiesGrenadeIndicator.Thrown(__instance);
            // What's left of the 5 s: all of it from a tap, less what it cooked in the hand (thrown on 4: it goes off a
            // second after leaving the hand)
            // Fika: every game runs its own copy of a throw, the host's included; the thrower sends how long it was
            // held (a teammate's cooked grenade ran the whole fuse on the host: user, 2026-11-09)
            float cooked = 0f;
            // Held until the key was let go, plus the throw's own swing since then (it burns fuse too: without it a
            // cooked grenade went off a little late, user 2026-11-09)
            if (thrower != null && Cooks.TryGetValue(thrower, out var c) && Time.time - c.at < 2.5f)
                cooked = c.cooked + (Time.time - c.at);
            if (mine)
            {
                float here = ZombiesQuickGrenade.TakeCooked();
                if (cooked <= 0f)
                    cooked = here;
            }
            float left = Mathf.Max(0.05f, FuseSeconds - cooked);
            TimeSpent(__instance) = Mathf.Max(0f, __instance.WeaponSource.GetExplDelay - left);
        }

        // How long each player's last grenade was held before it left the hand (theirs from ZombiesNet)
        private static readonly System.Collections.Generic.Dictionary<string, (float cooked, float at)> Cooks =
            new System.Collections.Generic.Dictionary<string, (float, float)>();

        /// <summary>This player let a cooked grenade go: kept for its throw here, and sent to the others' copies.</summary>
        public static void Released(float cooked)
        {
            string me = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
            if (me == null || cooked <= 0f)
                return;
            Cooks[me] = (cooked, Time.time);
            ZombiesNet.Cook(cooked);
        }

        public static void CookRemote(string from, float cooked) => Cooks[from] = (cooked, Time.time);

        public static int Count(Player player) =>
            player?.InventoryController?.Inventory.Equipment.GetAllItems().Count(i => i.TemplateId.ToString() == M67) ?? 0;

        /// <summary>M67s taken away until the player has at most <paramref name="most"/>, then filled up to it.</summary>
        public static void TrimTo(Player player, int most)
        {
            if (player?.InventoryController == null)
                return;
            foreach (Item g in player.InventoryController.Inventory.Equipment.GetAllItems().Where(i => i.TemplateId.ToString() == M67).Skip(most).ToList())
            {
                try
                {
                    var removed = ItemManipulator.Remove(g, player.InventoryController, false, true);
                    if (removed.Succeeded)
                        ZombiesItems.Removed(player.InventoryController, removed, g);
                }
                catch (System.Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies: a grenade wasn't taken back ({e.Message})");
                }
            }
            FillTo(player, most);
        }

        /// <summary>Grenades given until the player has <paramref name="upTo"/> (none taken away).</summary>
        public static void FillTo(Player player, int upTo)
        {
            if (player?.InventoryController != null)
                ZombiesPlugin.Instance.StartCoroutine(Fill(player, upTo));
        }

        private static IEnumerator Fill(Player player, int upTo)
        {
            int need = upTo - Count(player);
            if (need <= 0)
                yield break;
            Item[] nades = Enumerable.Range(0, need).Select(_ => Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), M67, null)).Where(i => i != null).ToArray();
            // The model loaded first, or the throw has nothing to show
            yield return ZombiesItems.LoadModels(nades);
            InventoryController inventory = player.InventoryController;
            int given = 0;
            foreach (Item nade in nades)
            {
                ItemAddress place = ZombiesItems.FindGrid(inventory, nade);
                if (place == null || !ZombiesItems.Add(inventory, nade, place, out _))
                    break;
                given++;
            }
            ZombiesDebug.Log($"grenades: +{given} = {Count(player)}");
        }

        /// <summary>A new round: 2 more, up to 4.</summary>
        public static void NewRound()
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me != null && ZombiesDowned.Mine == ZombiesDowned.State.Up)
                FillTo(me, Mathf.Min(Most, Count(me) + PerRound));
        }

        // ---------- Their icons ----------

        /// <summary>
        /// The original's grenade slots left of the ammo count: 4 outlines, and in each slot filled, the M67 itself (its model,
        /// photographed once).
        /// </summary>
        public static void Draw(Player player, float right, float bottom, float s)
        {
            if (_picture == null || _outline == null)
            {
                if (!_iconLoading)
                    ZombiesPlugin.Instance.StartCoroutine(MakeIcons());
                return;
            }
            int n = Count(player);
            float h = 46f * s, w = h * _picture.width / Mathf.Max(1f, _picture.height), gap = 3f * s;
            Color before = GUI.color;
            for (int i = 0; i < Most; i++)
            {
                // Filled from the count outwards
                var r = new Rect(right - (i + 1) * (w + gap), bottom - h, w, h);
                bool full = i < n;
                GUI.color = new Color(0f, 0f, 0f, full ? 0.6f : 0.35f);
                GUI.DrawTexture(new Rect(r.x + 2f * s, r.y + 2f * s, r.width, r.height), full ? _picture : _outline);
                GUI.color = full ? Color.white : new Color(1f, 1f, 1f, 0.55f);
                GUI.DrawTexture(r, full ? _picture : _outline);
            }
            // The tactical (Cymbal Monkeys) left of the grenades: its three slots, each the monkey's picture or outline
            // Kept once you've had them this life, an outline for each one thrown 
            int monkeys = ZombiesMonkey.Count(player);
            if (monkeys > 0 || ZombiesMonkey.HadThisLife)
            {
                if (_monkeyPicture == null && !_monkeyLoading)
                    MakeMonkeyIcons();
                if (_monkeyPicture != null)
                {
                    float mh = h * 1.05f, mw = mh * _monkeyPicture.width / Mathf.Max(1f, _monkeyPicture.height);
                    float left = right - Most * (w + gap) - 10f * s;
                    for (int i = 0; i < ZombiesMonkey.Carry; i++)
                    {
                        var r = new Rect(left - (i + 1) * (mw + gap), bottom - mh, mw, mh);
                        bool full = i < monkeys;
                        GUI.color = new Color(0f, 0f, 0f, full ? 0.6f : 0.35f);
                        GUI.DrawTexture(new Rect(r.x + 2f * s, r.y + 2f * s, r.width, r.height), full ? _monkeyPicture : _monkeyOutline);
                        GUI.color = full ? Color.white : new Color(1f, 1f, 1f, 0.55f);
                        GUI.DrawTexture(r, full ? _monkeyPicture : _monkeyOutline);
                    }
                }
            }
            GUI.color = before;
        }

        private static Texture2D _monkeyPicture, _monkeyOutline;
        private static bool _monkeyLoading;

        // The original's own HUD icon for the monkey (hud/zm_monkey_icon.png) and an outline cut
        // from its shape; without the file, the monkey's model (zombies.bundle) photographed front on, as the M67's
        private static void MakeMonkeyIcons()
        {
            _monkeyLoading = true;
            try
            {
                string file = System.IO.Path.Combine(ZombiesPlugin.Dir, "hud", "zm_monkey_icon.png");
                if (System.IO.File.Exists(file))
                {
                    var icon = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(icon, System.IO.File.ReadAllBytes(file)))
                    {
                        icon.filterMode = FilterMode.Bilinear;
                        // White, not the original's cyan (user 2026-11-09): each pixel's brightest channel as grey, lifted, so
                        // the monkey's shading stays and it sits with the white grenade pictures
                        Color32[] px = icon.GetPixels32();
                        for (int i = 0; i < px.Length; i++)
                        {
                            byte v = (byte)Mathf.Min(255, Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b)) * 1.15f + 20f);
                            px[i] = new Color32(v, v, v, px[i].a);
                        }
                        icon.SetPixels32(px);
                        icon.Apply();
                        _monkeyPicture = icon;
                        var cut = PictureAndOutline(icon);
                        Object.Destroy(cut.picture);
                        _monkeyOutline = cut.outline;
                        return;
                    }
                    Object.Destroy(icon);
                }
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: monkey HUD icon unread ({e.Message})");
            }
            GameObject prefab = ZombiesAssets.Model("zm_monkey");
            if (prefab == null)
                return;
            GameObject model = Object.Instantiate(prefab);
            model.transform.SetPositionAndRotation(new Vector3(0f, -500f, 0f), Quaternion.identity);
            model.GetComponentInChildren<Animator>()?.Play("rest", 0, 0f);
            Texture2D shot = ZombiesChalk.Silhouette(model, Vector3.forward, Vector3.up, 96);
            Object.Destroy(model);
            if (shot == null)
                return;
            (_monkeyPicture, _monkeyOutline) = PictureAndOutline(shot);
            Object.Destroy(shot);
        }

        private static (Texture2D picture, Texture2D outline) PictureAndOutline(Texture2D shot)
        {
            int w = shot.width, h = shot.height;
            Color32[] src = shot.GetPixels32();
            var solid = new bool[src.Length];
            var picture = new Color32[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                solid[i] = src[i].a > 5;
                float k = solid[i] ? 1.25f / Mathf.Sqrt(src[i].a / 255f) : 0f;
                picture[i] = new Color32((byte)Mathf.Min(255f, src[i].r * k), (byte)Mathf.Min(255f, src[i].g * k), (byte)Mathf.Min(255f, src[i].b * k), (byte)(solid[i] ? 255 : 0));
            }
            var outline = new Color32[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool any = false, all = true;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            bool on = xx >= 0 && yy >= 0 && xx < w && yy < h && solid[yy * w + xx];
                            any |= on;
                            all &= on;
                        }
                    outline[y * w + x] = new Color32(255, 255, 255, (byte)(any && !all ? 255 : 0));
                }
            return (Make(w, h, picture), Make(w, h, outline));
        }

        private static Texture2D _picture, _outline;

        /// <summary>The M67's picture (the HUD's filled grenade), once it's made; null until then.</summary>
        public static Texture2D Picture => _picture;

        // The M67's own model, side on: its picture (textured, unlit) and its outline
        private static IEnumerator MakeIcons()
        {
            _iconLoading = true;
            Item nade = Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), M67, null);
            if (nade == null)
                yield break;
            GameObject model = null;
            yield return BoxGuns.MakeItemModel(nade, go => model = go);
            if (model == null)
                yield break;
            model.transform.SetPositionAndRotation(new Vector3(0f, -500f, 0f), Quaternion.identity);
            Texture2D shot = ZombiesChalk.Silhouette(model, Vector3.right, Vector3.up, 96);
            Object.Destroy(model);
            if (shot == null)
                yield break;
            int w = shot.width, h = shot.height;
            Color32[] src = shot.GetPixels32();
            var solid = new bool[src.Length];
            var picture = new Color32[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                solid[i] = src[i].a > 5;
                // Blended onto clear, the colour came out times its alpha: undone, a touch brighter for the HUD
                float k = solid[i] ? 1.25f / Mathf.Sqrt(src[i].a / 255f) : 0f;
                picture[i] = new Color32((byte)Mathf.Min(255f, src[i].r * k), (byte)Mathf.Min(255f, src[i].g * k), (byte)Mathf.Min(255f, src[i].b * k), (byte)(solid[i] ? 255 : 0));
            }
            var outline = new Color32[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool any = false, all = true;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            bool on = xx >= 0 && yy >= 0 && xx < w && yy < h && solid[yy * w + xx];
                            any |= on;
                            all &= on;
                        }
                    outline[y * w + x] = new Color32(255, 255, 255, (byte)(any && !all ? 255 : 0));
                }
            _picture = Make(w, h, picture);
            _outline = Make(w, h, outline);
            Object.Destroy(shot);
        }

        private static Texture2D Make(int w, int h, Color32[] px)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
