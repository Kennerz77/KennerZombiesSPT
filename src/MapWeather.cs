using BepInEx.Configuration;
using EFT.Weather;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The map's weather, held: clouds, rain, fog and wind set and kept there, the way EFT's own storm event does it
    /// (the weather controller's debug weather, copied from the raid's and switched on). A layout saves it, the map
    /// editor sets it, and a zombies match holds it. Release hands the weather back to the raid's own.
    /// </summary>
    internal static class MapWeather
    {
        /// <summary>The weather being held (null: the raid's own).</summary>
        public static Settings Held { get; private set; }
        private static bool _warned;

        public class Settings
        {
            public float Clouds = 0.6f;  // -1 clear .. 1 overcast
            public float Rain = 0.2f;    // 0 .. 1
            public float Fog = 0.02f;    // 0.001 .. 0.255
            public float Wind = 0.2f;    // 0 .. 1
        }

        /// <summary>Sets the weather and keeps it there; false when the map has no weather system.</summary>
        public static bool Hold(Settings s)
        {
            WeatherController controller = WeatherController.Instance;
            if (controller == null || controller.WeatherDebug == null)
            {
                if (!_warned)
                    ZombiesPlugin.Log.LogWarning($"Map weather: can't hold it, {(controller == null ? "the map has no WeatherController" : "its WeatherDebug is missing")}");
                _warned = true;
                return false;
            }
            WeatherDebug w = controller.WeatherDebug;
            // Start from the raid's own (temperature, wind high up), then set ours
            if (!w.Enabled)
                w.CopyParams(controller.WeatherCurve);
            w.CloudDensity = s.Clouds;
            w.Rain = s.Rain;
            w.Fog = UnityEngine.Mathf.Clamp(s.Fog, 0.001f, 0.255f);
            w.WindMagnitude = s.Wind;
            w.LightningThunderProbability = 0f;
            w.Enabled = true;
            if (Held == null)
            {
                ZombiesPlugin.Log.LogInfo($"Map weather: held (clouds {s.Clouds}, rain {s.Rain}, fog {s.Fog}, wind {s.Wind}) on {controller.name}");
                ZombiesPlugin.Instance.StartCoroutine(FitRainLater());
                if (!_reported)
                {
                    _reported = true;
                    ZombiesPlugin.Instance.StartCoroutine(ReportLater());
                }
            }
            Held = s;
            return true;
        }

        private static bool _reported;

        /// <summary>Half the rain area's width round the player (m), and its height below / above them.</summary>
        private const float RainHalfWidth = 250f, RainBelow = 100f, RainAbove = 160f;

        // Once the player is in: the rain fitted to where they are
        private static System.Collections.IEnumerator FitRainLater()
        {
            float until = Time.time + 120f;
            while (Time.time < until && Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer == null)
                yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(2f);
            try { FitRain(); }
            catch (System.Exception e) { ZombiesPlugin.Log.LogWarning($"Map weather: rain area not fitted ({e.Message})"); }
            try { FixDrops(); }
            catch (System.Exception e) { ZombiesPlugin.Log.LogWarning($"Map weather: rain drops not fixed ({e.Message})"); }
        }

        private static Texture2D _drop;

        /// <summary>
        /// EFT's rain drew no drops on zm_stronghold (the user, 2026-11-08, again and again): the SDK's weather rig gives
        /// EFT's drop material (TarkovSDK/Weather/Generated/Materials/RainFallDropsClose_df6c.mat) the SDK's own drop
        /// picture, a thin faint streak (average brightness 5 of 255, mostly empty), where Customs' material has a soft
        /// white blob (average 142) that EFT's shader stretches into streaks. At the material's 0.1 alpha the streak
        /// came out at ~4%: invisible. Customs' picture (hud\zm_raindrop.png, from the rip) is put on the drop material,
        /// EFT's working copy of it and the drops renderer once the player is in. Our mod only, the map is untouched.
        /// (The map's own fix: point that material's _MainTex at Customs/Texture2D/RainDrop_0.png.)
        /// </summary>
        public static void FixDrops()
        {
            if (_drop == null)
            {
                string file = System.IO.Path.Combine(ZombiesPlugin.Dir, "hud", "zm_raindrop.png");
                if (!System.IO.File.Exists(file))
                {
                    ZombiesPlugin.Log.LogWarning("Map weather: hud/zm_raindrop.png missing, rain drops left as they are");
                    return;
                }
                _drop = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                ImageConversion.LoadImage(_drop, System.IO.File.ReadAllBytes(file));
                _drop.wrapMode = TextureWrapMode.Clamp;
                Object.DontDestroyOnLoad(_drop);
            }
            foreach (RainController rc in Object.FindObjectsOfType<RainController>())
            {
                if (rc == null || !rc.isActiveAndEnabled || !(F(rc, "_rainFallDrops") is Component drops) || drops == null)
                    continue;
                var mats = new System.Collections.Generic.HashSet<Material>();
                foreach (object o in new[] { F(drops, "_close"), F(drops, "_closeCopy") })
                    if (o is Material m && m != null)
                        mats.Add(m);
                if (F(drops, "_rainRenderer") is Renderer r && r != null && r.sharedMaterial != null)
                    mats.Add(r.sharedMaterial);
                foreach (Material m in mats)
                {
                    Texture was = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                    m.SetTexture("_MainTex", _drop);
                    ZombiesPlugin.Log.LogInfo($"Map weather: rain drops {m.name} ({m.shader?.name}) on {PathOf(rc.transform)}: picture {(was != null ? $"{was.name} {was.width}x{was.height}" : "none")} -> Customs' drop");
                }
            }
        }

        /// <summary>
        /// EFT draws falling rain only inside the map's rain area (LevelSettings.RainBounds): its depth photographer
        /// takes one picture from above of that area, and the rain shader hides every drop under something in it.
        /// zm_stronghold's area was the SDK's default 220 m square round (0, 0, 0), and the play area is at (208, -146),
        /// outside it: rain heard (the ambience) but never seen. When the player stands outside the area, it's set
        /// round them and the picture taken again. A map whose area already holds the player is left alone.
        /// </summary>
        public static void FitRain()
        {
            Vector3? at = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer?.Position;
            LevelSettings level = Comfort.Common.Singleton<LevelSettings>.Instance;
            if (at == null || level == null)
                return;
            Bounds b = level.RainBounds;
            Vector3 p = at.Value;
            bool inside = b.size.x > 1f && p.x > b.min.x + 20f && p.x < b.max.x - 20f && p.z > b.min.z + 20f && p.z < b.max.z - 20f
                && p.y > b.min.y && p.y < b.max.y - 10f;
            if (inside)
            {
                ZombiesPlugin.Log.LogInfo($"Map weather: rain area {b.center} size {b.size} holds the player, left as it is");
                return;
            }
            Bounds fitted = new Bounds(new Vector3(p.x, p.y + (RainAbove - RainBelow) / 2f, p.z), new Vector3(RainHalfWidth * 2f, RainAbove + RainBelow, RainHalfWidth * 2f));
            level.RainBounds = fitted;
            DepthPhotograper photo = DepthPhotograper.Instance;
            if (photo == null)
                foreach (RainController rc in Object.FindObjectsOfType<RainController>())
                    if (F(rc, "_depthPhotograper") is DepthPhotograper d)
                    {
                        photo = d;
                        break;
                    }
            if (photo == null)
            {
                ZombiesPlugin.Log.LogWarning($"Map weather: rain area moved to {fitted.center} but there's no depth photographer to redo");
                return;
            }
            photo.Render();
            ZombiesPlugin.Log.LogInfo($"Map weather: rain area was {b.center} size {b.size} (player at {p}, outside it): now {fitted.center} size {fitted.size}, depth photo redone");
        }

        private static System.Collections.IEnumerator ReportLater()
        {
            yield return new WaitForSeconds(45f);
            try { Report(); }
            catch (System.Exception e) { ZombiesPlugin.Log.LogWarning($"Rain report failed: {e}"); }
        }

        private static object F(object o, string field) => o == null ? null : HarmonyLib.AccessTools.Field(o.GetType(), field)?.GetValue(o);

        // Every number, colour and switch of a settings object, on one line
        private static void Dump(System.Text.StringBuilder sb, string what, object o)
        {
            if (o == null)
            {
                sb.Append($"  {what}: none\n");
                return;
            }
            sb.Append($"  {what}:");
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (System.Reflection.FieldInfo f in o.GetType().GetFields(all))
            {
                if (!(f.FieldType == typeof(float) || f.FieldType == typeof(bool) || f.FieldType == typeof(int) || f.FieldType == typeof(Color)
                      || f.FieldType == typeof(Vector3) || f.FieldType.IsEnum))
                    continue;
                try
                {
                    object v = f.GetValue(o);
                    sb.Append($" {f.Name}={(v is float x ? x.ToString("0.####") : v)}");
                }
                catch
                {
                }
            }
            sb.Append("\n");
        }

        /// <summary>The weather/rain/light report now, in any raid (F12 button): a normal raid's numbers to compare with.</summary>
        public static void ReportNow()
        {
            try { Report(); }
            catch (System.Exception e) { ZombiesPlugin.Log.LogWarning($"Weather report failed: {e}"); }
        }

        private static string PathOf(Transform t)
        {
            string p = t.name;
            for (Transform x = t.parent; x != null; x = x.parent)
                p = x.name + "/" + p;
            return $"{t.gameObject.scene.name}:{p}";
        }

        /// <summary>
        /// The world's light (the user, 2026-11-08: "lighting still seems off, like the double sun"): every sky rig
        /// (TOD_Sky) and every directional light, on or off, with the scene it came from, plus Unity's render settings.
        /// Two live skies or two live suns is the double-sun look of 2026-11-02.
        /// </summary>
        private static void Lights(System.Text.StringBuilder sb)
        {
            sb.Append("Light report:\n");
            foreach (TOD_Sky sky in Resources.FindObjectsOfTypeAll<TOD_Sky>())
            {
                if (sky == null || !sky.gameObject.scene.IsValid())
                    continue;
                sb.Append($"  sky {PathOf(sky.transform)} active {sky.gameObject.activeInHierarchy} enabled {sky.enabled}" +
                          (sky == TOD_Sky.Instance ? " (TOD_Sky.Instance)" : "") + "\n");
            }
            foreach (Light l in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (l == null || l.type != LightType.Directional || !l.gameObject.scene.IsValid())
                    continue;
                sb.Append($"  sun {PathOf(l.transform)} active {l.gameObject.activeInHierarchy} enabled {l.enabled} intensity {l.intensity:0.###} " +
                          $"colour {l.color} shadows {l.shadows} strength {l.shadowStrength:0.##} turn {l.transform.eulerAngles} mask {l.cullingMask:X}\n");
            }
            foreach (WeatherController w in Resources.FindObjectsOfTypeAll<WeatherController>())
                if (w != null && w.gameObject.scene.IsValid())
                    sb.Append($"  weather controller {PathOf(w.transform)} active {w.gameObject.activeInHierarchy}" + (w == WeatherController.Instance ? " (Instance)" : "") + "\n");
            sb.Append($"  render settings: sun {(RenderSettings.sun == null ? "none" : PathOf(RenderSettings.sun.transform))}, ambient {RenderSettings.ambientMode} " +
                      $"{RenderSettings.ambientLight} x{RenderSettings.ambientIntensity:0.##}, sky {RenderSettings.ambientSkyColor} equator {RenderSettings.ambientEquatorColor} " +
                      $"ground {RenderSettings.ambientGroundColor}, skybox {(RenderSettings.skybox == null ? "none" : RenderSettings.skybox.name + " (" + RenderSettings.skybox.shader?.name + ")")}, " +
                      $"fog {RenderSettings.fog} {RenderSettings.fogMode} {RenderSettings.fogColor} density {RenderSettings.fogDensity}, reflection x{RenderSettings.reflectionIntensity:0.##}\n");
            var lm = LightmapSettings.lightmaps;
            sb.Append($"  lightmaps {lm?.Length ?? 0}, probes {(LightmapSettings.lightProbes == null ? 0 : LightmapSettings.lightProbes.count)}\n");
            Dump(sb, "world fog (camera TOD_Scattering)", ZombiesLook.WorldFog);
            if (TOD_Sky.Instance is TOD_Sky sky0)
            {
                Dump(sb, "TOD atmosphere", sky0.Atmosphere);
                Dump(sb, "TOD day", sky0.Day);
                Dump(sb, "TOD light", sky0.Light);
                foreach (Component c in sky0.GetComponentsInChildren<Component>(true))
                    if (c != null && !(c is Transform))
                        sb.Append($"  sky part {PathOf(c.transform)}: {c.GetType().Name}" + (c is Renderer r ? $" enabled {r.enabled} material {r.sharedMaterial?.name} ({r.sharedMaterial?.shader?.name})" : "") + "\n");
            }
            if (TOD_Sky.Instance is TOD_Sky s)
                sb.Append($"  TOD: hour {s.Cycle.Hour:0.##}, day light {s.Day.LightIntensity:0.##}, ambient x{s.Day.AmbientMultiplier:0.##}, light source {(s.Components?.LightSource == null ? "none" : PathOf(s.Components.LightSource.transform))}\n");
        }

        /// <summary>
        /// What EFT's rain is doing (the rain was heard but never seen on zm_stronghold): the weather controller's
        /// rain controller, its season state, the falling drops' renderer, its shader and layer against the camera,
        /// and whether the depth photographer thinks the camera is under cover.
        /// </summary>
        public static void Report()
        {
            var sb = new System.Text.StringBuilder("Rain report:\n");
            WeatherController wc = WeatherController.Instance;
            sb.Append($"  WeatherController {(wc == null ? "none" : wc.name)} debug {(wc?.WeatherDebug?.Enabled == true ? "on" : "off")} rain {wc?.WeatherDebug?.Rain} curve rain {wc?.WeatherCurve?.Rain} clouds {wc?.WeatherCurve?.Cloudiness}\n");
            sb.Append($"  rain area {Comfort.Common.Singleton<LevelSettings>.Instance?.RainBounds.center} size {Comfort.Common.Singleton<LevelSettings>.Instance?.RainBounds.size}, _MapStart {Shader.GetGlobalVector("_MapStart")}, _MapScale {Shader.GetGlobalVector("_MapScale")}, depth map {(Shader.GetGlobalTexture("_WeatherDepthMap") is Texture t ? t.width + "x" + t.height : "none")}, player {Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer?.Position}\n");
            sb.Append($"  global _RainIntensity {Shader.GetGlobalFloat("_RainIntensity")}\n");
            Camera cam = Camera.main;
            sb.Append($"  camera {(cam == null ? "none" : cam.name)} at {cam?.transform.position} mask {(cam == null ? 0 : cam.cullingMask):X}\n");
            var all = Object.FindObjectsOfType<RainController>(true);
            sb.Append($"  RainControllers in scene: {all.Length}\n");
            foreach (RainController rc in all)
            {
                object state = F(rc, "State") ?? HarmonyLib.AccessTools.Property(rc.GetType(), "State")?.GetValue(rc);
                sb.Append($"  - {Path(rc.transform)} active {rc.gameObject.activeInHierarchy} enabled {rc.enabled} linked {(wc != null && ReferenceEquals(F(wc, "RainController"), rc))} state {state?.GetType().Name} intensity {F(state, "Intensity") ?? HarmonyLib.AccessTools.Property(state?.GetType(), "Intensity")?.GetValue(state)} underRain {HarmonyLib.AccessTools.Property(rc.GetType(), "IsCameraUnderRain")?.GetValue(rc)}\n");
                var drops = F(rc, "_rainFallDrops") as RainFallDrops;
                if (drops == null)
                    sb.Append("      drops: none\n");
                else
                {
                    sb.Append($"      drops {Path(drops.transform)} active {drops.gameObject.activeInHierarchy} enabled {drops.enabled} intensity {drops.Intensity} count {drops.Count} density {drops.MinMaxDensity} alpha {drops.DropsAlphaClose}\n");
                    foreach (MeshRenderer r in drops.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        Material m = r.sharedMaterial;
                        sb.Append($"      renderer {r.name} enabled {r.enabled} layer {r.gameObject.layer} (camera sees it: {cam != null && (cam.cullingMask & (1 << r.gameObject.layer)) != 0}) at {r.transform.position} bounds {r.bounds.size} material {m?.name} shader {m?.shader?.name} supported {m?.shader?.isSupported} queue {m?.renderQueue}\n");
                    }
                    var close = F(drops, "_close") as Material;
                    sb.Append($"      _close {close?.name} shader {close?.shader?.name} supported {close?.shader?.isSupported}\n");
                }
                object photo = F(rc, "_depthPhotograper");
                sb.Append($"      depth photographer {(photo == null ? "none" : ((Component)photo).name)} enabled {(photo as Behaviour)?.enabled}\n");
                foreach (string f in new[] { "_rainSplashController", "_rippleController", "_wetRenderer", "_snowFlakes" })
                {
                    var c = F(rc, f) as Behaviour;
                    sb.Append($"      {f}: {(c == null ? "none" : $"{c.name} active {c.gameObject.activeInHierarchy} enabled {c.enabled}")}\n");
                }
            }
            Lights(sb);
            ZombiesPlugin.Log.LogInfo(sb.ToString());
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            for (Transform x = t.parent; x != null; x = x.parent)
                p = x.name + "/" + p;
            return p;
        }

        /// <summary>The raid's own weather again.</summary>
        public static void Release()
        {
            WeatherController controller = WeatherController.Instance;
            if (controller?.WeatherDebug != null)
                controller.WeatherDebug.Enabled = false;
            Held = null;
        }
    }

    /// <summary>
    /// Rain that comes and goes on a held map weather: dry spells and rainy spells, fading in and out, the rain at most
    /// the map's own Rain. F12 "8. Rain". For now on a timer; once the match has rounds it can follow them instead.
    /// </summary>
    internal class RainCycle : MonoBehaviour
    {
        private static ConfigEntry<bool> _on;
        private static ConfigEntry<float> _dryMinutes, _rainMinutes, _fadeSeconds;

        private bool _raining = true;
        private float _spellEnds, _rain = -1f, _next;

        public static void Bind(ConfigFile config, string section)
        {
            _on = config.Bind(section, "Rain comes and goes (dry spells)", false,
                "On a map with its weather held (zombies map): rainy spells and dry spells instead of rain all the time.");
            _rainMinutes = config.Bind(section, "Rainy spell (minutes)", 2.28f, new ConfigDescription(
                "About how long it rains (each spell is 70-130% of this).", new AcceptableValueRange<float>(0.5f, 20f)));
            _dryMinutes = config.Bind(section, "Dry spell (minutes)", 3.46f, new ConfigDescription(
                "About how long it stays dry (each spell is 70-130% of this).", new AcceptableValueRange<float>(0.5f, 20f)));
            _fadeSeconds = config.Bind(section, "Fade (seconds)", 30f, new ConfigDescription(
                "How long the rain takes to start or stop.", new AcceptableValueRange<float>(1f, 120f)));
        }

        private void Update()
        {
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.5f;
            MapWeather.Settings held = MapWeather.Held;
            WeatherController controller = WeatherController.Instance;
            if (held == null || controller == null || controller.WeatherDebug == null || !controller.WeatherDebug.Enabled)
            {
                _rain = -1f;
                return;
            }
            if (!_on.Value)
            {
                if (_rain >= 0f)
                    controller.WeatherDebug.Rain = held.Rain;
                _rain = -1f;
                return;
            }
            if (_rain < 0f)
            {
                // Starts as the map has it, raining, for one spell
                _rain = held.Rain;
                _raining = true;
                _spellEnds = Time.time + Spell(_rainMinutes.Value);
            }
            if (Time.time >= _spellEnds)
            {
                _raining = !_raining;
                _spellEnds = Time.time + Spell(_raining ? _rainMinutes.Value : _dryMinutes.Value);
            }
            float target = _raining ? held.Rain : 0f;
            _rain = Mathf.MoveTowards(_rain, target, Mathf.Max(held.Rain, 0.01f) * 0.5f / _fadeSeconds.Value);
            controller.WeatherDebug.Rain = _rain;
        }

        private static float Spell(float minutes) => minutes * 60f * Random.Range(0.7f, 1.3f);
    }
}
