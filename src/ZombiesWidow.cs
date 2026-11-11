using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Widow's Wine : slapped while you have a grenade, one of them bursts into a web round you that slows every
    /// zombie within 8 m to a crawl for 10 s (and the slap still counts). Your grenades turn to Semtex: they stick to the
    /// first thing they hit (a zombie, a wall, the floor), blink red, and go off into a web that slows the zombies round
    /// them, on top of the blast's damage.
    /// </summary>
    internal static class ZombiesWidow
    {
        private const float Reach = 8f, Hold = 10f, SlapGap = 1.5f;
        /// <summary>A webbed zombie's pace (of its own).</summary>
        public const float Slow = 0.3f;
        private static readonly Dictionary<BotOwner, float> Until = new Dictionary<BotOwner, float>();
        private static float _lastBurst = -100f;
        private static bool _patched;

        public static bool Webbed(BotOwner z) => z != null && Until.TryGetValue(z, out float t) && Time.time < t;

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            harmony.Patch(AccessTools.Method(typeof(Grenade), nameof(Grenade.InvokeBlowUpEvent)),
                prefix: new HarmonyMethod(typeof(ZombiesWidow), nameof(GrenadeWeb)));
            harmony.Patch(AccessTools.Method(typeof(Grenade), nameof(Grenade.OnCollisionEnter)),
                postfix: new HarmonyMethod(typeof(ZombiesWidow), nameof(Semtex)));
        }

        public static void Clear()
        {
            Until.Clear();
            foreach (GameObject c in Cocoons.Values)
                if (c != null)
                    Object.Destroy(c);
            Cocoons.Clear();
        }

        /// <summary>A zombie's slap on the local player: with Widow's Wine and a grenade, the web.</summary>
        public static void Slapped(Player player)
        {
            if (!ZombiesMatch.Active || !ZombiesPerks.Has("widow") || Time.time - _lastBurst < SlapGap)
                return;
            InventoryController inventory = player?.InventoryController;
            EFT.InventoryLogic.Item nade = inventory?.Inventory.Equipment.GetAllItems().FirstOrDefault(i => i.TemplateId.ToString() == ZombiesGrenades.M67);
            if (nade == null)
                return;
            var removed = EFT.InventoryLogic.ItemManipulator.Remove(nade, inventory, false, true);
            if (!removed.Succeeded)
                return;
            ZombiesItems.Removed(inventory, removed, nade);
            _lastBurst = Time.time;
            Web(((Component)player).transform.position, "slapped");
        }

        private static void GrenadeWeb(Grenade __instance)
        {
            try
            {
                if (!ZombiesMatch.Active || !ZombiesPerks.Has("widow") || __instance == null)
                    return;
                if (__instance.ProfileId != Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId)
                    return;
                Web(__instance.transform.position, "grenade");
            }
            catch
            {
                // Never in the way of the explosion
            }
        }

        // Semtex: your grenade sticks where it first lands, blinking red till it goes
        private static void Semtex(Grenade __instance, Collision collision)
        {
            try
            {
                if (!ZombiesMatch.Active || !ZombiesPerks.Has("widow") || __instance == null || collision?.collider == null)
                    return;
                if (__instance.ProfileId != Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId || __instance.GetComponent<Stuck>() != null)
                    return;
                __instance.SetKinematic(true);
                __instance.gameObject.AddComponent<Stuck>().Init(collision.collider.transform, collision.GetContact(0).point);
            }
            catch
            {
            }
        }

        private class Stuck : MonoBehaviour
        {
            private Transform _on;
            private Vector3 _local;
            private Quaternion _turn;
            private Light _blink;

            public void Init(Transform on, Vector3 at)
            {
                _on = on;
                transform.position = at;
                _local = on.InverseTransformPoint(at);
                _turn = Quaternion.Inverse(on.rotation) * transform.rotation;
                var go = new GameObject("Zombies_SemtexBlink");
                go.transform.SetParent(transform, false);
                _blink = go.AddComponent<Light>();
                _blink.type = LightType.Point;
                _blink.color = new Color(1f, 0.1f, 0.05f);
                _blink.range = 1.5f;
                ZombiesSounds.At("zm_ww_web_#", at, 0.5f, 15f);
            }

            private void LateUpdate()
            {
                if (_on != null)
                {
                    transform.position = _on.TransformPoint(_local);
                    transform.rotation = _on.rotation * _turn;
                }
                if (_blink != null)
                    _blink.intensity = Mathf.Repeat(Time.time * 4f, 1f) < 0.5f ? 3f : 0f;
            }

            // The pool takes the grenade back: no longer stuck
            private void OnDisable() => Destroy(this);

            private void OnDestroy()
            {
                if (_blink != null)
                    Destroy(_blink.gameObject);
            }
        }

        /// <summary>A teammate's web (ZombiesNet): the host holds the zombies, everyone sees the webs.</summary>
        public static void WebRemote(Vector3 at) => Web(at, "a teammate");

        private static void Web(Vector3 at, string why)
        {
            int held = 0;
            ZombiesSounds.At("zm_ww_cocoon", at, 1f, 30f);
            // Only the host's game moves the zombies: it holds them
            if (ZombiesNet.IsHost)
                foreach (BotOwner z in ZombiesRounds.Zombies.ToList())
                    if (z != null && Vector3.Distance(z.Position, at) <= Reach)
                    {
                        Until[z] = Time.time + Hold;
                        held++;
                    }
            // The webs on every zombie in reach, on everyone's game
            foreach (Player body in Singleton<GameWorld>.Instance.AllAlivePlayersList.Where(p => ZombiesTeam.IsZombie(p)).ToList())
                if (Vector3.Distance(body.Transform.position, at) <= Reach)
                {
                    Cocoon(body);
                    ZombiesSounds.At("zm_ww_web_#", body.Transform.position + Vector3.up, 0.9f, 25f);
                }
            ZombiesNet.Web(at);
            ZombiesPlugin.Log.LogInfo($"Zombies: Widow's Wine web ({why}){(ZombiesNet.IsHost ? $", {held} zombie(s) held" : "")}");
        }

        // ---------- The look: the original's web wrapped round the zombie ----------

        private static readonly Dictionary<Player, GameObject> Cocoons = new Dictionary<Player, GameObject>();
        private static Texture2D _webTexture;

        // Three crossed sheets of the original's web patch (one of its 4 tiles each) round the body, gone when the web is
        private static void Cocoon(Player z)
        {
            Transform body = z != null ? ((Component)z).transform : null;
            if (body == null)
                return;
            if (Cocoons.TryGetValue(z, out GameObject old) && old != null)
                Object.Destroy(old);
            var root = new GameObject("Zombies_Web");
            root.transform.SetParent(body, false);
            root.transform.localPosition = Vector3.up * 0.95f;
            for (int i = 0; i < 3; i++)
            {
                GameObject q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.Destroy(q.GetComponent<Collider>());
                q.transform.SetParent(root.transform, false);
                q.transform.localRotation = Quaternion.Euler(Random.Range(-8f, 8f), i * 60f + Random.Range(-10f, 10f), Random.Range(-6f, 6f));
                q.transform.localScale = new Vector3(0.95f, 2f, 1f);
                var m = new Material(Shader.Find("Sprites/Default")) { mainTexture = WebTexture, color = new Color(1f, 1f, 1f, 0.9f) };
                m.mainTextureScale = new Vector2(0.25f, 1f);
                m.mainTextureOffset = new Vector2(Random.Range(0, 4) * 0.25f, 0f);
                q.GetComponent<Renderer>().sharedMaterial = m;
            }
            Cocoons[z] = root;
            Object.Destroy(root, Hold);
        }

        private static Texture2D WebTexture
        {
            get
            {
                if (_webTexture != null)
                    return _webTexture;
                _webTexture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
                string file = System.IO.Path.Combine(ZombiesPlugin.Dir, "hud", "ww_web.png");
                if (System.IO.File.Exists(file))
                    _webTexture.LoadImage(System.IO.File.ReadAllBytes(file));
                return _webTexture;
            }
        }
    }
}
