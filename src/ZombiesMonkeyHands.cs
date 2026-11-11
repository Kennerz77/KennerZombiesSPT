using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The Cymbal Monkey in your hands (first person) shows as the original's monkey, not the M67 it's built on: while a monkey
    /// is in the hands (held ready or EFT's quick throw), every renderer of the grenade's own model is hidden and
    /// zombies.bundle's zm_monkey rides the same hand bone in its place, sized and turned by the fit sliders (F12
    /// "Monkey in the hand"). While it's being wound and held it shivers on its key (the clap anim run slowly). EFT's
    /// own throw animation moves it for now; the original's own monkey arm anims (vm_monkey_bomb_*) are to come.
    /// The first time, the grenade model's bones are logged (to fit it).
    /// </summary>
    internal static class ZombiesMonkeyHands
    {
        private static ConfigEntry<float> _size, _x, _y, _z, _pitch, _yaw, _roll;
        private static ConfigEntry<float> _bx, _by, _bz, _bpitch, _byaw, _broll, _bsize;
        private class Held
        {
            public GameObject Shown;
            public Transform On;      // the M67's own grenade bone (EFT's arms)
            public Transform Weapon;  // the weapon bone the original's arm clips move (ZombiesMonkeyView)
            public Animator Anim;
        }

        // The original's tag_weapon under EFT's weapon bone, in the monkey prefab's axes (BoxImporter's (-x, z, -y)): the M67
        // rig's weapon rest turn and the bake's jgunLocal (MonkeyHandsImporter) give Rx(90) * Ry(90), position 0
        private static readonly Quaternion ClassicGrip = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);

        // Per player (by profile): every player holding a monkey shows it, teammates' too (third person, Fika)
        private static readonly Dictionary<string, Held> ByPlayer = new Dictionary<string, Held>();
        private static bool _logged;
        private static float _nextOthers;

        public static void Bind(ConfigFile config, string section)
        {
            const string s = "21. Monkey bomb";
            _size = config.Bind(s, "Size in the hand (m)", 0.32f, new ConfigDescription("The monkey's height in your hand (about 0.3 m).", new AcceptableValueRange<float>(0.05f, 0.8f)));
            _x = config.Bind(s, "Right (m)", 0f, new ConfigDescription("Along the hand bone's own axes.", new AcceptableValueRange<float>(-0.3f, 0.3f)));
            _y = config.Bind(s, "Up (m)", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-0.3f, 0.3f)));
            _z = config.Bind(s, "Forward (m)", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-0.3f, 0.3f)));
            _pitch = config.Bind(s, "Pitch", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-180f, 180f)));
            _yaw = config.Bind(s, "Yaw", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-180f, 180f)));
            _roll = config.Bind(s, "Roll", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-180f, 180f)));
            // With the original's arms the monkey rides the weapon bone their clips move (at the original's grip): its own fit
            _bsize = config.Bind(s, "Classic arms: monkey size (m)", 0.49f, new ConfigDescription(
                "The monkey's height (wires included) while the classic arms hold it: the classic view model is 0.49 m, held close to the eye.",
                new AcceptableValueRange<float>(0.1f, 1f)));
            _bx = config.Bind(s, "Classic arms: right (m)", 0f, new ConfigDescription("Monkey fit while the classic arms play (it sits at the classic grip).", new AcceptableValueRange<float>(-0.3f, 0.3f)));
            _by = config.Bind(s, "Classic arms: up (m)", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-0.3f, 0.3f)));
            _bz = config.Bind(s, "Classic arms: forward (m)", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-0.3f, 0.3f)));
            _bpitch = config.Bind(s, "Classic arms: pitch", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-180f, 180f)));
            _byaw = config.Bind(s, "Classic arms: yaw", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-180f, 180f)));
            _broll = config.Bind(s, "Classic arms: roll", 0f, new ConfigDescription("", new AcceptableValueRange<float>(-180f, 180f)));
        }

        /// <summary>Each frame of a match: our own hands, and every few frames the other players'.</summary>
        public static void Tick(Player me)
        {
            Show(me);
            if (Time.time < _nextOthers)
                return;
            _nextOthers = Time.time + 0.1f;
            var world = Comfort.Common.Singleton<GameWorld>.Instance;
            if (world?.AllAlivePlayersList == null)
                return;
            foreach (Player p in world.AllAlivePlayersList)
                if (p != null && p != me && ZombiesTeam.IsHuman(p))
                    Show(p);
            // Players gone: their monkeys too
            foreach (string id in ByPlayer.Keys.ToList())
                if (world.AllAlivePlayersList.All(p => p == null || p.ProfileId != id))
                    Drop(id);
        }

        private static void Show(Player p)
        {
            if (p == null)
                return;
            var hands = p.HandsController;
            bool monkey = ZombiesMatch.Active && hands != null && ZombiesMonkey.Is(hands.Item) && hands.ControllerGameObject != null;
            if (!monkey)
            {
                Drop(p.ProfileId);
                return;
            }
            GameObject root = hands.ControllerGameObject;
            ByPlayer.TryGetValue(p.ProfileId, out Held h);
            if (h == null || h.Shown == null || h.On == null || !h.On.IsChildOf(root.transform) && h.On != root.transform)
            {
                Drop(p.ProfileId);
                h = Make(root);
                if (h == null)
                    return;
                ByPlayer[p.ProfileId] = h;
                // Out of the pocket: the original's raise sounds (vm_monkey_bomb_pullpin's notes)
                ZombiesPlugin.Instance.StartCoroutine(RaiseSounds(h.On));
            }
            // EFT shows the grenade's renderers again as it animates: kept hidden
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                if (r.enabled && !r.transform.IsChildOf(h.Shown.transform) && !(r is SkinnedMeshRenderer && IsArm(r.transform)))
                    r.enabled = false;
            // The original's arms playing: on the weapon bone their clips move, else on the M67's own grenade bone
            Transform want = p.IsYourPlayer && ZombiesMonkeyView.Active && h.Weapon != null ? h.Weapon : h.On;
            if (h.Shown.transform.parent != want)
                h.Shown.transform.SetParent(want, false);
            Fit(h);
            if (h.Anim != null)
                h.Anim.speed = 0.35f; // a slow shiver on its key while held
        }

        // The original's raise (vm_monkey_bomb_pullpin) notes: zmb_vox_monkey_raise at frame 7, zmb_monkey_anim_cymb (monk_cymb_st)
        // at 20 and 30, zmb_monkey_anim_key (monkey_ratchet) at 43, at 30 fps, squeezed into our crank like the arms are
        private static readonly (int frame, string sound, float volume)[] RaiseNotes =
        {
            (7, "zm_monkey_vox_raise_#", 0.7f),
            (20, "zm_monkey_cymbal_start", 0.8f),
            (30, "zm_monkey_cymbal_start", 0.8f),
            (43, "zm_monkey_ratchet", 0.9f),
        };

        private static System.Collections.IEnumerator RaiseSounds(Transform on)
        {
            float scale = ZombiesMonkeyView.CrankSeconds / (45f / 30f), start = Time.time;
            Vector3 last = on != null ? on.position : Vector3.zero;
            foreach (var (frame, sound, volume) in RaiseNotes)
            {
                float at = start + frame / 30f * scale;
                while (Time.time < at)
                {
                    if (on != null)
                        last = on.position;
                    yield return null;
                }
                if (!ZombiesMatch.Active)
                    yield break;
                if (on != null)
                    ZombiesSounds.On(sound, on, volume, 20f);
                else
                    ZombiesSounds.At(sound, last, volume, 20f);
            }
        }

        private static bool IsArm(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                string n = p.name.ToLowerInvariant();
                if (n.Contains("arm") || n.Contains("hand") || n.Contains("glove") || n.Contains("sleeve"))
                    return true;
            }
            return false;
        }

        private static Held Make(GameObject root)
        {
            GameObject prefab = ZombiesAssets.Model("zm_monkey");
            if (prefab == null)
                return null;
            var h = new Held();
            // The grenade's own model: the transform holding its biggest non-arm mesh
            Renderer main = root.GetComponentsInChildren<Renderer>(true).Where(r => !(r is SkinnedMeshRenderer && IsArm(r.transform)))
                .OrderByDescending(r => r.bounds.size.sqrMagnitude).FirstOrDefault();
            h.On = main != null ? main.transform.parent ?? main.transform : root.transform;
            h.Weapon = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "weapon" && t.parent != null && t.parent.name == "Weapon_root_anim");
            if (!_logged)
            {
                _logged = true;
                var sb = new StringBuilder("Zombies monkey in the hand: grenade model under ").Append(root.name).Append(":\n");
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true).Take(80))
                    sb.Append("  ").Append(Path(t, root.transform)).Append(t.GetComponent<Renderer>() is Renderer r ? $" [{r.GetType().Name}]" : "").Append('\n');
                sb.Append("monkey on: ").Append(Path(h.On, root.transform));
                ZombiesPlugin.Log.LogInfo(sb.ToString());
            }
            h.Shown = Object.Instantiate(prefab, h.On, false);
            h.Shown.name = "zm_monkey_hands";
            foreach (Renderer r in h.Shown.GetComponentsInChildren<Renderer>(true))
                r.gameObject.layer = h.On.gameObject.layer;
            h.Anim = h.Shown.GetComponentInChildren<Animator>();
            h.Anim?.Play("clap", 0, 0f);
            Fit(h);
            return h;
        }

        private static void Fit(Held h)
        {
            if (h?.Shown == null)
                return;
            Transform parent = h.Shown.transform.parent;
            if (parent != null && parent == h.Weapon && parent != h.On)
            {
                h.Shown.transform.localPosition = new Vector3(_bx?.Value ?? 0f, _by?.Value ?? 0f, _bz?.Value ?? 0f);
                h.Shown.transform.localRotation = ClassicGrip * Quaternion.Euler(_bpitch?.Value ?? 0f, _byaw?.Value ?? 0f, _broll?.Value ?? 0f);
            }
            else
            {
                h.Shown.transform.localPosition = new Vector3(_x?.Value ?? 0f, _y?.Value ?? 0f, _z?.Value ?? 0f);
                h.Shown.transform.localRotation = Quaternion.Euler(_pitch?.Value ?? 0f, _yaw?.Value ?? 0f, _roll?.Value ?? 0f);
            }
            // Its prefab is about 1.24 m tall (export units): scaled to the slider's height (the original's arms: the original's view
            // model size, checked against it in MonkeyHandsPreview)
            float lossy = parent != null ? parent.lossyScale.y : 1f;
            float height = parent != null && parent == h.Weapon ? _bsize?.Value ?? 0.49f : _size?.Value ?? 0.32f;
            h.Shown.transform.localScale = Vector3.one * (height / 1.24f) / Mathf.Max(0.0001f, lossy);
        }

        private static void Drop(string id)
        {
            if (id == null || !ByPlayer.TryGetValue(id, out Held h))
                return;
            if (h.Shown != null)
                Object.Destroy(h.Shown);
            ByPlayer.Remove(id);
        }

        private static string Path(Transform t, Transform root)
        {
            string p = t.name;
            for (Transform x = t.parent; x != null && x != root; x = x.parent)
                p = x.name + "/" + p;
            return p;
        }
    }
}
