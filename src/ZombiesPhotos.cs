using System.Collections;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// A photo tour of the zombies map for checking it without walking it (dev): with "Photo tour on start" on, as a zombies
    /// match starts the player is put at each viewpoint of plugins\Kennerz_ZM_Core\shots.txt in turn
    /// ("name x y z yaw pitch" per line: feet position, yaw 0 looks along +Z, pitch positive looks down) and the screen is
    /// saved as shots\&lt;name&gt;.png. The player is put back where the match placed them. "Chalk pictures on start" does
    /// the same for the chalk outlines of every gun (ZombiesChalk.Sheet).
    /// </summary>
    internal static class ZombiesPhotos
    {
        public static ConfigEntry<bool> TourOnStart, ChalkOnStart;
        private static bool _tourDone, _chalkDone;

        public static void Bind(ConfigFile config, string section)
        {
            TourOnStart = config.Bind(section, "Photo tour on start", false, "Dev: as a zombies match starts, take the pictures listed in plugins\\Kennerz_ZM_Core\\shots.txt (saved to its shots folder).");
            ChalkOnStart = config.Bind(section, "Chalk pictures on start", false, "Dev: as a zombies match starts, draw the chalk outline of every gun to plugins\\Kennerz_ZM_Core\\chalk.");
        }

        /// <summary>Called once the match is built and the player is in.</summary>
        public static void OnMatchBuilt(MonoBehaviour host)
        {
            if (TourOnStart?.Value == true && !_tourDone)
            {
                _tourDone = true;
                host.StartCoroutine(Tour());
            }
            if (ChalkOnStart?.Value == true && !_chalkDone)
            {
                _chalkDone = true;
                host.StartCoroutine(ChalkLater());
            }
        }

        private static IEnumerator ChalkLater()
        {
            yield return new WaitForSecondsRealtime(25f);
            yield return ZombiesChalk.Sheet();
        }

        private static IEnumerator Tour()
        {
            string file = Path.Combine(ZombiesPlugin.Dir, "shots.txt");
            if (!File.Exists(file))
            {
                ZombiesPlugin.Log.LogWarning($"Photo tour: no {file}");
                yield break;
            }
            // Wait for the loading screen to end and the player to be in
            float until = Time.realtimeSinceStartup + 240f;
            while (Time.realtimeSinceStartup < until && !(ZombiesPlugin.RaidStarted && Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null))
                yield return null;
            yield return new WaitForSecondsRealtime(12f);
            Player me = Singleton<GameWorld>.Instance.MainPlayer;
            if (me == null)
                yield break;
            string dir = Path.Combine(ZombiesPlugin.Dir, "shots");
            Directory.CreateDirectory(dir);
            Vector3 home = me.Transform.position;
            CultureInfo f = CultureInfo.InvariantCulture;
            int n = 0;
            foreach (string line in File.ReadAllLines(file))
            {
                string[] s = line.Trim().Split(' ');
                // A line "POWER" turns the power on (machines, lights, doors that open with it) and waits for the lights to flicker on
                if (s.Length == 1 && s[0].Equals("POWER", System.StringComparison.OrdinalIgnoreCase))
                {
                    ZombiesMatch.PowerOn();
                    yield return new WaitForSecondsRealtime(14f);
                    continue;
                }
                if (s.Length < 6)
                    continue;
                string name = s[0];
                var at = new Vector3(float.Parse(s[1], f), float.Parse(s[2], f), float.Parse(s[3], f));
                float yaw = float.Parse(s[4], f), pitch = float.Parse(s[5], f);
                me.Teleport(at, false);
                for (int i = 0; i < 3; i++)
                {
                    me.MovementContext.Rotation = new Vector2(yaw, pitch);
                    yield return new WaitForSecondsRealtime(0.7f);
                }
                yield return new WaitForEndOfFrame();
                var shot = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                shot.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                shot.Apply();
                File.WriteAllBytes(Path.Combine(dir, name + ".png"), shot.EncodeToPNG());
                Object.Destroy(shot);
                n++;
                yield return new WaitForSecondsRealtime(0.8f);
            }
            me.Teleport(home, false);
            ZombiesPlugin.Log.LogInfo($"Photo tour: {n} pictures in {dir}");
            ZombiesPlugin.Notify($"Photo tour done: {n} pictures.");
        }
    }
}
