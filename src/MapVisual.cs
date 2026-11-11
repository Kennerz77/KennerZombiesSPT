using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// How one placed thing looks, in the map editor and in a match: the real model where there is one (the mystery
    /// box, the Perk Beverages machines, Pack-a-Punch, a wall buy's gun, and the doors, debris and window boards once
    /// they're in zombies.bundle), else a coloured box its size. The model stands with its bottom middle on the spot.
    /// Play-area walls, blockers and spawns always show as boxes. Nothing on it collides.
    /// </summary>
    internal class MapVisual : MonoBehaviour
    {
        // The machines and the box are made big and set out at this size (as in the hideout)
        private const float MachineScale = 0.35f;
        // The map editor's default barricade depth: at it, the boards are exactly as thick as made
        private const float WindowDepth = 0.3f;
        private static readonly Vector3 WallGunSize = new Vector3(0.8f, 0.4f, 0.05f);

        public MapEditor.Entry Entry { get; private set; }

        private Transform _model;

        /// <summary>The thing's model (once it's made; a wall gun's takes a moment).</summary>
        public Transform ModelRoot => _model;
        private Renderer _box;
        private float _baseScale = 1f;
        // The model at its base scale, measured in this object's space: its size and its middle
        private Vector3 _natural, _centre;

        public bool HasModel => _model != null;

        /// <summary>Whether it's for a match: copied map props then keep their own colliders (solid as on the map).</summary>
        private bool _forMatch;

        /// <summary>True when a copied map prop kept its own colliders (a match needs no box round it then).</summary>
        public bool HasOwnColliders { get; private set; }

        public static MapVisual Make(MapEditor.Entry e, bool forMatch = false)
        {
            var go = new GameObject($"Zombies_{e.Type}{(string.IsNullOrEmpty(e.What) ? "" : "_" + e.What)} (KennerZombies)");
            MapVisual v = go.AddComponent<MapVisual>();
            v.Entry = e;
            v._forMatch = forMatch;
            v.Build();
            return v;
        }

        private void Build()
        {
            switch (Entry.Type)
            {
                case MapEditor.Kind.BoxSpot:
                    // Its hinge away from the player, who it faces when placed
                    SetModel(ZombiesAssets.Box(), MachineScale, Quaternion.Euler(0f, 180f, 0f));
                    break;
                case MapEditor.Kind.PerkMachine:
                    SetModel(OtherModPrefab("perk_beverages", "perk_" + Entry.What), MachineScale, Quaternion.identity);
                    break;
                case MapEditor.Kind.PackAPunch:
                    SetModel(OtherModPrefab("pap_hideout", "pap_machine"), MachineScale, Quaternion.identity);
                    break;
                case MapEditor.Kind.Door:
                    GameObject door = ZombiesAssets.Model("zm_door_" + Entry.What);
                    // Not one of our looks: a Tarkov door of the map, by its name (the Unity Placer keeps a template copy)
                    if (door == null && !string.IsNullOrEmpty(Entry.What))
                    {
                        SceneProp(AssetCatalog.SceneTemplate(Entry.What));
                        break;
                    }
                    SetModel(door, 1f, FaceForward(door));
                    break;
                case MapEditor.Kind.Debris:
                    GameObject debris = ZombiesAssets.Model("zm_debris_" + Entry.What);
                    if (debris == null && !string.IsNullOrEmpty(Entry.What))
                    {
                        SceneProp(AssetCatalog.SceneTemplate(Entry.What));
                        break;
                    }
                    SetModel(debris, 1f, Quaternion.identity);
                    break;
                // Kino der Toten's power switch (zombies.bundle), about 1 m tall
                case MapEditor.Kind.PowerSwitch:
                case MapEditor.Kind.ExfilActivator:
                    SetModel(ZombiesAssets.Model("zm_power_switch"), 0.17f, Quaternion.identity);
                    break;
                case MapEditor.Kind.Window:
                    SetModel(ZombiesAssets.Model("zm_window"), 1f, Quaternion.identity);
                    break;
                // The blue 115 rock (zombies.bundle), glowing and humming with PaP's own rock glow
                case MapEditor.Kind.Rock:
                    SetModel(ZombiesAssets.Model("zm_rock_115"), 1f, Quaternion.identity);
                    if (_model != null)
                        ZombiesRock.Glow(_model.gameObject, Entry.What != "lore");
                    break;
                // Lore lightning: live in the editor too, to see it; its marker box only in the editor
                case MapEditor.Kind.Lightning:
                    gameObject.AddComponent<ZombiesLightning>().Init(Entry);
                    break;
                case MapEditor.Kind.WallBuy:
                    StartCoroutine(WallGun());
                    break;
                case MapEditor.Kind.PowerLight:
                case MapEditor.Kind.Lamp:
                    // The map's fixture named in "What" when it has one (else Customs' fluorescent bar, else a plain bar), and its light
                    var fixtures = AssetCatalog.SceneAssets().Select(a => a.Name).ToList();
                    string fixture = !string.IsNullOrEmpty(Entry.What) && fixtures.Contains(Entry.What)
                        ? Entry.What
                        : fixtures.FirstOrDefault(n => n.StartsWith("Fluorescent_lamp_01"));
                    if (fixture != null)
                        SceneProp(AssetCatalog.SceneTemplate(fixture));
                    AddPowerLight(Entry.Type == MapEditor.Kind.Lamp);
                    break;
                case MapEditor.Kind.Decal:
                    MakeDecal();
                    return;
                case MapEditor.Kind.Prop:
                    if (Entry.What != null && Entry.What.StartsWith(AssetCatalog.ScenePrefix))
                    {
                        string propName = Entry.What.Substring(AssetCatalog.ScenePrefix.Length);
                        GameObject found = AssetCatalog.SceneTemplate(propName);
                        if (found != null)
                            SceneProp(found);
                        else
                            StartCoroutine(SceneLater(propName)); // its part of the map may still be loading
                    }
                    else if (Entry.What != null && Entry.What.StartsWith(AssetCatalog.ItemPrefix))
                        StartCoroutine(ItemProp(Entry.What.Substring(AssetCatalog.ItemPrefix.Length)));
                    break;
            }
            if (_model == null)
                MakeBox();
            Apply();
            if (_forMatch && Entry.Type == MapEditor.Kind.Lightning)
                ShowBox(false);
        }

        /// <summary>
        /// A power light's light, just under the fixture. In a match it's the power's: dark until the power is on
        /// (PowerLights); in the editor it stays lit, to see what it lights.
        /// </summary>
        private void AddPowerLight(bool always = false)
        {
            var go = new GameObject("PowerLight");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.down * 0.2f;
            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.93f, 0.82f);
            light.range = 9f;
            light.intensity = 1.5f;
            light.shadows = LightShadows.None;
            bool street = !string.IsNullOrEmpty(Entry.What) && (Entry.What.StartsWith("Streetlight") || Entry.What.StartsWith("Searchlight"));
            if (always || street)
            {
                // Outside lamps: sodium orange, a wider pool; others a warm white
                light.color = street ? new Color(1f, 0.82f, 0.55f) : new Color(1f, 0.9f, 0.7f);
                light.range = street ? 16f : 11f;
                light.intensity = street ? 1.7f : 1.3f;
                go.transform.localPosition = street ? Vector3.up * 0.2f : Vector3.down * 0.2f;
            }
            if (!string.IsNullOrEmpty(Entry.What) && Entry.What.StartsWith("Barrel_fire"))
            {
                // A burning barrel: its own warm, wide light, up out of the barrel
                light.color = new Color(1f, 0.6f, 0.28f);
                light.range = 10f;
                light.intensity = 1.7f;
                go.transform.localPosition = Vector3.up * 1.2f;
            }
            if (_forMatch && !always)
                PowerLights.Register(gameObject);
        }

        private static readonly Dictionary<string, Texture2D> DecalTextures = new Dictionary<string, Texture2D>();

        /// <summary>A wall decal (the original's blood hands, drips, scrawl, chalk): a cut-out quad on the wall, facing out of it.</summary>
        private void MakeDecal()
        {
            string name = string.IsNullOrEmpty(Entry.What) ? "blood_hand_01" : Entry.What;
            if (!DecalTextures.TryGetValue(name, out Texture2D tex) || tex == null)
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
                string file = System.IO.Path.Combine(ZombiesPlugin.Dir, "decals", name + ".png");
                if (System.IO.File.Exists(file))
                    tex.LoadImage(System.IO.File.ReadAllBytes(file));
                else
                    ZombiesPlugin.Log.LogWarning($"Decal {name}: no {file}");
                DecalTextures[name] = tex;
            }
            GameObject q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "decal";
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, false);
            Shader shader = new[] { "Sprites/Default", "Unlit/Transparent Cutout", "Unlit/Transparent" }.Select(Shader.Find).FirstOrDefault(sh => sh != null && sh.isSupported);
            var m = new Material(shader) { mainTexture = tex, color = Color.white, name = "zm_decal" };
            Renderer r = q.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // A quad faces back along its own +Z: turned round so it faces out of the wall (the entry's forward)
            q.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            float w = Entry.Size != null && Entry.Size.Length >= 2 ? Entry.Size[0] : (Entry.Scale > 0f ? Entry.Scale : 1f);
            float h = Entry.Size != null && Entry.Size.Length >= 2 ? Entry.Size[1] : w * (tex.height > 0 ? (float)tex.height / tex.width : 1f);
            q.transform.localScale = new Vector3(w, h, 1f);
            q.transform.localPosition = new Vector3(0f, h * 0.5f, 0.01f);
            transform.SetPositionAndRotation(MapEditor.Pos(Entry), MapEditor.Rotation(Entry));
        }

        /// <summary>Puts it where its entry says, at its size (call after changing the entry).</summary>
        public void Apply()
        {
            Vector3 size = Size;
            transform.SetPositionAndRotation(MapEditor.Pos(Entry), MapEditor.Rotation(Entry));
            if (_model != null)
            {
                Vector3 k = Entry.Size != null
                    ? new Vector3(size.x / _natural.x, size.y / _natural.y, size.z / _natural.z)
                    : Vector3.one * Scale;
                if (Entry.Size == null && Entry.Stretch != null && Entry.Stretch.Length == 3)
                    k = Vector3.Scale(k, new Vector3(Entry.Stretch[0], Entry.Stretch[1], Entry.Stretch[2]));
                // The barricade's boards keep their the original shape (wide thin planks): its depth follows its width and
                // height, the Depth setting only thickens it from there (the default depth is as made)
                if (Entry.Type == MapEditor.Kind.Window && Entry.Size != null)
                    k.z = (k.x + k.y) * 0.5f * (size.z / WindowDepth);
                _model.localScale = k * _baseScale;
                _model.localPosition = new Vector3(0f, size.y * 0.5f, 0f) - Vector3.Scale(_centre, k);
            }
            else if (_box != null)
            {
                _box.transform.localScale = size;
                _box.transform.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
            }
        }

        /// <summary>Its size in metres as it stands (width, height, depth before its turn).</summary>
        public Vector3 Size => Entry.Size != null ? new Vector3(Entry.Size[0], Entry.Size[1], Entry.Size[2])
            : _model != null ? Vector3.Scale(_natural * Scale, Entry.Stretch != null && Entry.Stretch.Length == 3 ? new Vector3(Entry.Stretch[0], Entry.Stretch[1], Entry.Stretch[2]) : Vector3.one)
            : (Entry.Type == MapEditor.Kind.WallBuy ? WallGunSize : MapEditor.MarkerSize(Entry.Type)) * Scale;

        private float Scale => Entry.Scale > 0f ? Entry.Scale : 1f;

        /// <summary>The space it takes in the world (for clicking on it and the selection box).</summary>
        public Bounds WorldBounds()
        {
            Vector3 size = Size;
            Quaternion turn = transform.rotation;
            var b = new Bounds(transform.position + turn * Vector3.up * (size.y * 0.5f), Vector3.zero);
            for (int c = 0; c < 8; c++)
                b.Encapsulate(b.center + turn * Vector3.Scale(size * 0.5f, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1)));
            return b;
        }

        /// <summary>
        /// Where a ray first meets what's drawn of it (each mesh's own box, so things can be stacked on it in the
        /// editor, where nothing placed collides): distance along the ray and the side's outward direction.
        /// </summary>
        public bool Raycast(Ray ray, out float distance, out Vector3 normal)
        {
            distance = float.MaxValue;
            normal = Vector3.up;
            foreach (Renderer r in SolidLooking())
            {
                if (r == null || !r.enabled)
                    continue;
                Bounds b = r.bounds;
                if (!b.IntersectRay(ray, out float d) || d >= distance)
                    continue;
                distance = d;
                // Which side it went in by: the one the entry point lies on
                Vector3 p = ray.GetPoint(d) - b.center, e = b.extents;
                Vector3 q = new Vector3(e.x > 0f ? p.x / e.x : 0f, e.y > 0f ? p.y / e.y : 0f, e.z > 0f ? p.z / e.z : 0f);
                normal = Mathf.Abs(q.y) >= Mathf.Abs(q.x) && Mathf.Abs(q.y) >= Mathf.Abs(q.z) ? Vector3.up * Mathf.Sign(q.y)
                    : Mathf.Abs(q.x) >= Mathf.Abs(q.z) ? Vector3.right * Mathf.Sign(q.x) : Vector3.forward * Mathf.Sign(q.z);
            }
            return distance < float.MaxValue;
        }

        private List<Renderer> _drawn;

        /// <summary>Its meshes as drawn up close (first LOD, no shadow stand-ins, no lines or effects), found once.</summary>
        private List<Renderer> SolidLooking()
        {
            if (_drawn != null)
                return _drawn;
            _drawn = new List<Renderer>();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || r.forceRenderingOff || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
                    || r is ParticleSystemRenderer || r is LineRenderer || r.name == "Arrow")
                    continue;
                LODGroup group = r.GetComponentInParent<LODGroup>();
                if (group != null && !InFirstLod(group, r))
                    continue;
                _drawn.Add(r);
            }
            return _drawn;
        }

        private static bool InFirstLod(LODGroup group, Renderer r)
        {
            LOD[] lods = group.GetLODs();
            if (lods.Length == 0)
                return true;
            foreach (Renderer first in lods[0].renderers)
                if (first == r)
                    return true;
            // Not in any LOD at all: drawn always
            for (int i = 1; i < lods.Length; i++)
                foreach (Renderer other in lods[i].renderers)
                    if (other == r)
                        return false;
            return true;
        }

        /// <summary>A solid box its size on this object (for a match: doors, debris, machines).</summary>
        public BoxCollider AddSolid(int layer)
        {
            var go = new GameObject("Solid");
            go.transform.SetParent(transform, false);
            if (layer >= 0)
                go.layer = layer;
            BoxCollider c = go.AddComponent<BoxCollider>();
            Vector3 size = Size;
            c.size = size;
            c.center = new Vector3(0f, size.y * 0.5f, 0f);
            return c;
        }

        /// <summary>
        /// The editor's direction arrow, flat on its foot: a window's points the way zombies come in (from its front,
        /// the outside, to the inside); a zombie spawn's and the player start's the way they face.
        /// </summary>
        public void AddArrow()
        {
            bool window = Entry.Type == MapEditor.Kind.Window;
            bool facing = Entry.Type == MapEditor.Kind.ZombieSpawn || Entry.Type == MapEditor.Kind.PlayerStart;
            // Things used from one side (the box, machines, Pack-a-Punch, wall buys, the power switch, doors, debris): a
            // yellow arrow in front of them, pointing out of the side the player uses
            bool front = Entry.Type == MapEditor.Kind.BoxSpot || Entry.Type == MapEditor.Kind.PerkMachine || Entry.Type == MapEditor.Kind.PackAPunch
                         || Entry.Type == MapEditor.Kind.WallBuy || Entry.Type == MapEditor.Kind.PowerSwitch || Entry.Type == MapEditor.Kind.Door
                         || Entry.Type == MapEditor.Kind.Debris;
            if (!window && !facing && !front)
                return;
            var go = new GameObject("Arrow");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * 0.03f + (front ? Vector3.forward * (Size.z * 0.5f + 0.7f) : Vector3.zero);
            go.transform.localRotation = window ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            go.AddComponent<MeshFilter>().sharedMesh = ArrowMesh();
            var r = go.AddComponent<MeshRenderer>();
            r.material = new Material(Shader.Find("Sprites/Default"))
            {
                color = window ? new Color(1f, 0.25f, 0.15f, 0.9f) : front ? new Color(1f, 0.85f, 0.2f, 0.9f) : new Color(0.3f, 1f, 0.4f, 0.9f),
            };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        private static Mesh _arrow;

        /// <summary>A flat arrow 1.2 m long pointing along +Z, centred on its middle.</summary>
        private static Mesh ArrowMesh()
        {
            if (_arrow != null)
                return _arrow;
            const float shaft = 0.07f, head = 0.25f, back = -0.6f, neck = 0.2f, tip = 0.6f;
            _arrow = new Mesh { name = "zm_editor_arrow" };
            _arrow.vertices = new[]
            {
                new Vector3(-shaft, 0f, back), new Vector3(shaft, 0f, back), new Vector3(shaft, 0f, neck), new Vector3(-shaft, 0f, neck),
                new Vector3(-head, 0f, neck), new Vector3(head, 0f, neck), new Vector3(0f, 0f, tip),
            };
            _arrow.triangles = new[] { 0, 3, 2, 0, 2, 1, 4, 6, 5 };
            _arrow.RecalculateNormals();
            _arrow.RecalculateBounds();
            return _arrow;
        }

        /// <summary>Tints the coloured box (the selected one shows lighter in the editor).</summary>
        public void Tint(Color c)
        {
            if (_box != null)
                _box.material.color = c;
        }

        public void ShowBox(bool on)
        {
            if (_box != null)
                _box.enabled = on;
        }

        // ---------- Models ----------

        private void MakeBox()
        {
            GameObject go = GameObject.CreatePrimitive(Entry.Type == MapEditor.Kind.PlayerStart ? PrimitiveType.Capsule : PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            _drawn = null;
            _box = go.GetComponent<Renderer>();
            _box.material = new Material(Shader.Find("Sprites/Default")) { color = MapEditor.Colour(Entry.Type) };
            _box.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // Some the original doors were modelled facing sideways (thin along X): turned so every door's face looks down +Z
        private static Quaternion FaceForward(GameObject prefab)
        {
            MeshFilter mf = prefab != null ? prefab.GetComponentInChildren<MeshFilter>() : null;
            Vector3 size = mf?.sharedMesh != null ? mf.sharedMesh.bounds.size : Vector3.one;
            return size.x < size.z ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
        }

        private void SetModel(GameObject prefab, float baseScale, Quaternion turn)
        {
            if (prefab == null)
                return;
            GameObject go = Instantiate(prefab);
            Strip(go);
            Adopt(go.transform, baseScale, turn);
        }

        /// <summary>Hangs a model under this object and measures it (with this object unturned at the origin).</summary>
        private void Adopt(Transform model, float baseScale, Quaternion turn)
        {
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            model.SetParent(transform, false);
            model.localPosition = Vector3.zero;
            model.localRotation = turn;
            model.localScale = Vector3.one * baseScale;
            Bounds b = BoxGuns.VisibleBounds(model.gameObject);
            transform.SetPositionAndRotation(position, rotation);
            _model = model;
            _drawn = null;
            _baseScale = baseScale;
            _natural = Vector3.Max(b.size, Vector3.one * 0.01f);
            _centre = b.center;
            if (_box != null)
            {
                Destroy(_box.gameObject);
                _box = null;
            }
        }

        /// <summary>The wall buy's gun, built as the wall gives it (takes a moment: its parts load first).</summary>
        private IEnumerator WallGun()
        {
            // The grenades' wall buy: an M67
            if (Entry.What == ZombiesGrenades.WallId)
            {
                yield return ItemProp(ZombiesGrenades.M67);
                yield break;
            }
            BoxGuns.Gun gun = BoxGuns.FindGun(Entry.What);
            if (gun == null)
                yield break;
            BoxGuns.Model made = null;
            yield return BoxGuns.MakeModel(gun, m => made = m);
            if (made?.Go == null)
                yield break;
            if (this == null)
            {
                Destroy(made.Go);
                yield break;
            }
            Adopt(made.Go.transform, 1f, made.Align);
            Apply();
        }

        // A map prop whose part of the map wasn't up yet when the layout was built (user, 2026-11-10: a placed fence
        // showed as a blue box in solo): looked for again every 2 s for a minute before giving up
        private System.Collections.IEnumerator SceneLater(string propName)
        {
            for (int i = 0; i < 30; i++)
            {
                yield return new WaitForSeconds(2f);
                if (this == null)
                    yield break;
                GameObject found = AssetCatalog.SceneTemplate(propName);
                if (found == null)
                    continue;
                ZombiesPlugin.Log.LogInfo($"Map prop '{propName}' found after {(i + 1) * 2} s");
                SceneProp(found);
                yield break;
            }
            SceneProp(null);
        }

        /// <summary>A copy of one of the map's own props, at its size in the map.</summary>
        private void SceneProp(GameObject template)
        {
            if (template == null)
            {
                ZombiesPlugin.Log.LogWarning($"Map prop '{Entry.What}' isn't on this map (or couldn't be found): shown as a box");
                return;
            }
            GameObject go = Instantiate(template);
            go.SetActive(true);
            // Its scripts (doors, loot, switches...) stay off: only the look
            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                mb.enabled = false;
            // EFT's culling switches a map prop's meshes off when it's out of sight, and the copy has nothing to switch
            // them back on (a chain fence came out invisible): every mesh on (its LOD group still picks the detail)
            var log = new System.Text.StringBuilder();
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                log.Append($" [{r.name}: {(r.enabled ? "on" : "off")}{(r.forceRenderingOff ? ",forcedOff" : "")}{(r.gameObject.activeInHierarchy ? "" : ",inactive")}," +
                           $"{r.shadowCastingMode},{(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "no material")}]");
                r.forceRenderingOff = false;
                r.enabled = true;
            }
            ZombiesPlugin.Log.LogInfo($"Map prop {template.name}:{log}");
            // In a match it stays as solid as the map's own (its real colliders, not a box round it)
            HasOwnColliders = _forMatch && go.GetComponentsInChildren<Collider>(true).Length > 0;
            Strip(go, HasOwnColliders);
            if (HasOwnColliders)
                foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                    c.enabled = true;
            Adopt(go.transform, template.transform.lossyScale.x, Quaternion.identity);
        }

        /// <summary>Any EFT item's model (takes a moment: its bundles load first).</summary>
        private IEnumerator ItemProp(string template)
        {
            EFT.InventoryLogic.Item item = AssetCatalog.MakeItem(template);
            if (item == null)
                yield break;
            GameObject go = null;
            yield return BoxGuns.MakeItemModel(item, made => go = made);
            if (go == null)
                yield break;
            if (this == null)
            {
                Destroy(go);
                yield break;
            }
            Adopt(go.transform, 1f, Quaternion.identity);
            Apply();
        }

        /// <summary>Only the look: no colliders (unless kept), no sounds, no scripts of its own.</summary>
        private static void Strip(GameObject go, bool keepColliders = false)
        {
            if (!keepColliders)
                foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                    Destroy(c);
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true))
                Destroy(rb);
            foreach (AudioSource a in go.GetComponentsInChildren<AudioSource>(true))
                a.enabled = false;
            // Bundled shaders that can't run here (pink) swapped for the game's own of the same name
            // sharedMaterials: reading .materials would copy every material of every placed thing (draw calls, memory)
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat == null || mat.shader == null || mat.shader.isSupported)
                        continue;
                    Shader gameShader = Shader.Find(mat.shader.name);
                    if (gameShader != null)
                        mat.shader = gameShader;
                }
        }

        /// <summary>A prefab from another of Kennerz's mods' bundles, if that mod is installed and has loaded it.</summary>
        private static GameObject OtherModPrefab(string bundleWord, string asset)
        {
            AssetBundle bundle = AssetBundle.GetAllLoadedAssetBundles().FirstOrDefault(b => b != null && b.name.Contains(bundleWord));
            return bundle != null ? bundle.LoadAsset<GameObject>(asset) : null;
        }
    }
}
