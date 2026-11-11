using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Tips on the zombies loading screen (bottom left, under the lore picture): the mode's own tricks, one after another
    /// in a shuffled order, each for a few seconds with a soft cross-fade. A map adds its own with &lt;map&gt;_tips.txt beside
    /// its layout (one tip a line; lines starting with # are skipped), shuffled in with the rest. Keep them true to the
    /// mode: the numbers here are the code's (prices, points, slaps).
    /// </summary>
    internal static class ZombiesTips
    {
        private const float ShowSeconds = 7f, FadeSeconds = 0.6f;

        private static readonly string[] General =
        {
            "In the early rounds, let zombies climb in through the barricades before you kill them: only zombies that are inside can drop power-ups.",
            "Swapping to your other gun is faster than reloading.",
            "Some 115 rocks are hidden around the map. Find them all and something special starts playing...",
            "Rebuilding a barricade pays 10 points a board, up to 500 points a round.",
            "Knife kills are worth 130 points, headshot kills 100, other kills 50.",
            "Go prone in front of a perk machine for 100 free points, once per machine.",
            "Three slaps and you're down. Jugger-Nog lets you take five.",
            "Get clear of the zombies for a few seconds and your slaps heal.",
            "You lose every perk you hold if you go down.",
            "Playing solo? Quick Revive picks you back up when you go down. You can buy it three times a match.",
            "Speed Cola: reloads twice as fast and gets your gun out quicker.",
            "Double Tap Root Beer: twice the damage and a faster rate of fire.",
            "Stamin-Up: run faster and sprint for longer.",
            "Deadshot Daiquiri: your shots spread far less, so firing from the hip hits where you look.",
            "Mule Kick: carry a third gun.",
            "Without PHD Flopper, your own explosions hurt you. With it, your knife explodes too.",
            "Widow's Wine: when a zombie hits you, one of your grenades bursts into webs that hold every zombie nearby.",
            "Pack-a-Punch (5000) makes a gun far stronger. Take it back within 15 seconds or it's gone.",
            "The mystery box costs 950. If the teddy bear shows up, the box moves somewhere else.",
            "Fire Sale: the mystery box costs 10 points while it lasts.",
            "Max Ammo refills every gun you carry and tops your grenades up to four.",
            "Nuke: every zombie on the map dies, and you get 400 points.",
            "Carpenter: every barricade is boarded up again, and you get 200 points.",
            "Insta-Kill: any hit kills a zombie while it lasts.",
            "Double Points: everything pays double while it lasts.",
            "You get two grenades back every round. Hold the grenade key to cook one: it goes off five seconds after the pin comes out.",
            "Your own grenades and explosions can hurt you (two slaps if you're right next to the blast), but never your teammates. PHD Flopper makes you immune.",
            "From round 15, and every 5 rounds after, the exfil lever can be pulled. Hold the area until the bar fills, then get into the exfil zone to escape with bonus XP.",
            "During the exfil the zombies never stop coming, and they all sprint. If nobody holds the area, the bar drains and the exfil can fail.",
            "Throw a Cymbal Monkey and every zombie nearby goes for it instead of you.",
            "Every bullet that hits a zombie pays 10 points, so wounding before you finish them pays more.",
            "Zombies get tougher every round. Pack-a-Punch keeps your guns worth using.",
            "From round 18, some zombies start to sprint.",
            "Aim for the head: headshots do three times the damage.",
            "Every board slows them down: each one takes a zombie about three seconds to tear off.",
            "A downed teammate gets back up if you hold {use} next to them for four seconds, two with Quick Revive.",
            "Going down isn't the end: you still have a pistol, so keep shooting while you crawl.",
            "Doors and debris cost points, but they open new areas, and new zombies come with them.",
            "The chalk outline on a wall shows the gun you can buy there.",
            "Your knife hits hard up close: it saves ammo in the early rounds.",
            "Playing solo? Press Esc to pause the match.",
        };

        private static List<string> _deck = new List<string>();
        private static float _since;
        private static GUIStyle _head, _body, _shadow;

        /// <summary>A loading screen starts: the tips shuffled again, the map's own in with them.</summary>
        public static void Begin(string location)
        {
            var all = new List<string>(General);
            try
            {
                string layout = MapEditor.FileFor(location);
                string file = string.IsNullOrEmpty(layout) ? null : Path.Combine(Path.GetDirectoryName(layout) ?? "", location + "_tips.txt");
                if (file != null && File.Exists(file))
                {
                    var own = File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)).ToList();
                    all.AddRange(own);
                    ZombiesPlugin.Log.LogInfo($"Zombies tips: {own.Count} of {location}'s own added");
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies tips: the map's tips couldn't be read: {e.Message}");
            }
            var random = new System.Random();
            _deck = all.OrderBy(_ => random.Next()).ToList();
            _since = Time.realtimeSinceStartup;
        }

        /// <summary>The tip of the moment, bottom left, fitted beside the loading bar (which starts at barX).</summary>
        public static void Draw(float alpha, float s, float barX)
        {
            if (_deck.Count == 0)
                return;
            float t = Time.realtimeSinceStartup - _since;
            int index = (int)(t / ShowSeconds) % _deck.Count;
            float into = t % ShowSeconds;
            float fade = Mathf.Clamp01(Mathf.Min(into, ShowSeconds - into) / FadeSeconds);
            if (_head == null)
            {
                _head = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft };
                _body = new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.UpperLeft };
                _shadow = new GUIStyle(_body);
            }
            _head.fontSize = Mathf.RoundToInt(18f * s);
            _body.fontSize = _shadow.fontSize = Mathf.RoundToInt(20f * s);
            float a = alpha * fade;
            _head.normal.textColor = new Color(0.85f, 0.15f, 0.12f, a);
            _body.normal.textColor = new Color(1f, 1f, 1f, a);
            _shadow.normal.textColor = new Color(0f, 0f, 0f, a * 0.8f);

            float left = 40f * s, width = Mathf.Max(220f * s, Mathf.Min(560f * s, barX - left - 40f * s));
            // Keys in the tips are the player's own zombies keys
            string tip = _deck[index].Replace("{use}", ZombiesControls.KeyName(ZombiesControls.Act.Use));
            float height = _body.CalcHeight(new GUIContent(tip), width);
            float bottom = Screen.height - 34f * s;
            var body = new Rect(left, bottom - height, width, height);
            GUI.Label(new Rect(left, body.y - 28f * s, width, 26f * s), "TIP", _head);
            GUI.Label(new Rect(body.x + 1.5f * s, body.y + 1.5f * s, body.width, body.height), tip, _shadow);
            GUI.Label(body, tip, _body);
        }
    }
}
