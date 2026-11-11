using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace KennerZombies
{
    /// <summary>
    /// A picture of each player's own character, face on, for their row in the scores: taken once they're in a zombies
    /// match with the zombies kit on, so masks, balaclavas and glasses show. A one-frame camera in front of the head, a
    /// soft light on the face, only the player's own layers drawn. EFT hides your own head from your first-person camera
    /// (shadows only), so those parts are shown for that one frame. Every client takes the pictures itself from the
    /// players it sees, so nothing is sent over the network.
    /// </summary>
    internal static class PlayerPortrait
    {
        private const int Size = 256;
        private static readonly Dictionary<string, Texture2D> Faces = new Dictionary<string, Texture2D>();

        public static Texture2D Get(Player player) =>
            player != null && Faces.TryGetValue(player.ProfileId, out Texture2D face) ? face : null;

        private static readonly Dictionary<string, float> NextTry = new Dictionary<string, float>();

        /// <summary>
        /// A teammate's picture, taken on this game once they've been in the match a while (their zombies kit on, their
        /// model loaded): every client photographs the players it sees. Tried again every 10 s until it works.
        /// </summary>
        public static Texture2D GetOrTake(Player player)
        {
            Texture2D face = Get(player);
            if (face != null || player == null || !ZombiesMatch.LoadedIn)
                return face;
            if (!NextTry.TryGetValue(player.ProfileId, out float next))
            {
                NextTry[player.ProfileId] = Time.time + 8f;
                return null;
            }
            if (Time.time < next)
                return null;
            NextTry[player.ProfileId] = Time.time + 10f;
            Capture(player);
            return Get(player);
        }

        /// <summary>Your own picture again the first time you look roughly level (within 8 degrees), for up to 30 s.</summary>
        public static System.Collections.IEnumerator RetakeWhenLevel(Player player)
        {
            float until = Time.time + 30f;
            while (Time.time < until && player != null)
            {
                float pitch = player.MovementContext != null ? Mathf.Abs(Mathf.DeltaAngle(0f, player.MovementContext.Rotation.y)) : 90f;
                if (pitch < 8f)
                {
                    Capture(player);
                    yield break;
                }
                yield return new WaitForSeconds(0.25f);
            }
        }

        public static void Clear()
        {
            foreach (Texture2D t in Faces.Values)
                if (t != null)
                    UnityEngine.Object.Destroy(t);
            Faces.Clear();
            NextTry.Clear();
        }

        public static void Capture(Player player)
        {
            Transform head = player?.PlayerBones?.Head?.Original;
            if (head == null)
                return;
            List<Renderer> shown = null;
            GameObject camGo = null, lightGo = null;
            RenderTexture rt = null;
            try
            {
                Renderer[] renderers = player.GetComponentsInChildren<Renderer>(true);
                shown = renderers.Where(r => r.shadowCastingMode == ShadowCastingMode.ShadowsOnly).ToList();
                foreach (Renderer r in shown)
                    r.shadowCastingMode = ShadowCastingMode.On;
                int mask = 0;
                foreach (Renderer r in renderers)
                    mask |= 1 << r.gameObject.layer;

                Vector3 forward = player.Transform.forward;
                forward.y = 0f;
                forward = forward.sqrMagnitude > 0.01f ? forward.normalized : Vector3.forward;
                Vector3 face = head.position + Vector3.up * 0.03f;

                camGo = new GameObject("zm_portrait_camera");
                Camera cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.transform.position = face + forward * 0.8f + Vector3.up * 0.02f;
                cam.transform.LookAt(face);
                cam.fieldOfView = 24f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 3f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.04f, 0.04f, 0.05f, 1f);
                cam.cullingMask = mask;

                lightGo = new GameObject("zm_portrait_light");
                Light light = lightGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 2.5f;
                light.intensity = 2.6f;
                light.color = new Color(1f, 0.95f, 0.88f);
                light.cullingMask = mask;
                light.transform.position = face + forward * 0.6f + Vector3.up * 0.25f;

                // The same light for every player's picture wherever they stand (the user, 2026-11-07): a fixed soft
                // ambient for the one frame, a key light from the front left and a fill from the right
                var fillGo = new GameObject("zm_portrait_fill");
                Light fill = fillGo.AddComponent<Light>();
                fill.type = LightType.Point;
                fill.range = 2.5f;
                fill.intensity = 1.1f;
                fill.color = new Color(0.85f, 0.9f, 1f);
                fill.cullingMask = mask;
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                fillGo.transform.position = face + forward * 0.6f + right * 0.5f;
                light.transform.position = face + forward * 0.6f - right * 0.35f + Vector3.up * 0.25f;
                var ambientMode = RenderSettings.ambientMode;
                Color ambient = RenderSettings.ambientLight;
                float ambientIntensity = RenderSettings.ambientIntensity;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.45f);
                RenderSettings.ambientIntensity = 1f;
                rt = RenderTexture.GetTemporary(Size, Size, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                try { cam.Render(); }
                finally
                {
                    RenderSettings.ambientMode = ambientMode;
                    RenderSettings.ambientLight = ambient;
                    RenderSettings.ambientIntensity = ambientIntensity;
                    UnityEngine.Object.Destroy(fillGo);
                }
                RenderTexture before = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                tex.Apply();
                RenderTexture.active = before;
                if (Faces.TryGetValue(player.ProfileId, out Texture2D old) && old != null)
                    UnityEngine.Object.Destroy(old);
                Faces[player.ProfileId] = tex;
                ZombiesPlugin.Log.LogInfo($"Scores: picture taken of {player.Profile?.Nickname}");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Scores: couldn't take the picture: {e}");
            }
            finally
            {
                if (shown != null)
                    foreach (Renderer r in shown)
                        if (r != null)
                            r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                if (rt != null)
                    RenderTexture.ReleaseTemporary(rt);
                if (camGo != null)
                    UnityEngine.Object.Destroy(camGo);
                if (lightGo != null)
                    UnityEngine.Object.Destroy(lightGo);
            }
        }
    }
}
