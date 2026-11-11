using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's grenade indicator: every live M67 within <see cref="Range"/> m shows as a see-through grenade picture on a
    /// ring round the crosshair, on the side it lies (top = in front of you, bottom = behind), with a small arrow
    /// pointing out at it. It's more solid and pulses faster the closer it is, and goes when the grenade blows.
    /// </summary>
    internal static class ZombiesGrenadeIndicator
    {
        private const float Range = 10f;
        private static readonly List<Grenade> Live = new List<Grenade>();
        private static GUIStyle _arrow;

        /// <summary>A grenade's fuse started (thrown): watch it.</summary>
        public static void Thrown(Grenade g)
        {
            if (g != null && !Live.Contains(g))
                Live.Add(g);
        }

        public static void Clear() => Live.Clear();

        public static void Draw()
        {
            Live.RemoveAll(g => g == null || !g.gameObject.activeInHierarchy);
            if (Live.Count == 0)
                return;
            Camera cam = ZombiesInteraction.ActiveCamera;
            if (cam == null)
                return;
            float s = Screen.height / 1080f;
            Vector2 centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float ring = 110f * s, size = 46f * s;
            Texture2D picture = ZombiesGrenades.Picture;
            if (_arrow == null)
                _arrow = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _arrow.fontSize = Mathf.RoundToInt(22f * s);
            Matrix4x4 before = GUI.matrix;
            foreach (Grenade g in Live)
            {
                Vector3 to = g.transform.position - cam.transform.position;
                float d = to.magnitude;
                if (d > Range)
                    continue;
                Vector3 local = cam.transform.InverseTransformDirection(to);
                float angle = Mathf.Atan2(local.x, local.z);
                Vector2 dir = new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle));
                Vector2 at = centre + dir * ring;
                float near = 1f - Mathf.Clamp01((d - 1f) / (Range - 1f));
                float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * Mathf.Lerp(5f, 14f, near));
                float alpha = Mathf.Lerp(0.3f, 0.8f, near) * pulse;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                var box = new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size);
                if (picture != null)
                    GUI.DrawTexture(box, picture, ScaleMode.ScaleToFit);
                else
                    GUI.Label(box, "●", _arrow);
                // The arrow just outside the picture, pointing out at the grenade
                Vector2 tip = centre + dir * (ring + size * 0.75f);
                GUI.matrix = before;
                GUIUtility.RotateAroundPivot(angle * Mathf.Rad2Deg, tip);
                GUI.color = new Color(1f, 0.85f, 0.3f, alpha);
                GUI.Label(new Rect(tip.x - 15f * s, tip.y - 15f * s, 30f * s, 30f * s), "▲", _arrow);
                GUI.matrix = before;
            }
            GUI.color = Color.white;
        }
    }
}
