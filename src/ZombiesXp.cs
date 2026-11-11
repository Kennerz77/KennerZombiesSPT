using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// A zombies match's XP and Dr. K points (the user, 2026-11-08: "our own screen that tallies up xp based on
    /// the players actions ... downs take away xp ... the total converts to Dr Kennerz points ... for the future rewards
    /// system"). Your own match only (each player's game sends its own). The XP per thing is in Rates: balance there.
    /// Points = XP / XpPerPoint, rounded down. Sent once per match to the server (POST /kzm/points), which keeps the
    /// profile's total; the reply (the new total) shows on the tally screen (ZombiesReport).
    /// </summary>
    internal static class ZombiesXp
    {
        // What each thing is worth (first pass, to balance later)
        private static readonly (string what, Func<ZombiesStats.Stats, int, int> count, int xp)[] Rates =
        {
            ("Rounds survived", (s, r) => r, 100),
            ("Zombies killed", (s, r) => s.Kills, 10),
            ("Headshot kills", (s, r) => s.Headshots, 5),
            ("Knife kills", (s, r) => s.KnifeKills, 10),
            ("Barrier boards rebuilt", (s, r) => s.Boards, 3),
            ("Doors and debris opened", (s, r) => s.Doors, 40),
            ("Points spent (per 1,000)", (s, r) => s.Spent / 1000, 25),
            ("Teammates revived", (s, r) => s.Revives, 75),
            ("Times downed", (s, r) => s.Downs, -150),
        };

        public const int XpPerPoint = 10;

        public class Line
        {
            public string What;
            public int Count, Each, Xp;
        }

        public class Result
        {
            public List<Line> Lines = new List<Line>();
            public int Xp, Points;
            public string MatchId;
            // From the server (null until it answers, or if it couldn't be reached)
            public long? Total;
            public string Error;
        }

        /// <summary>Your match worked out (every rate, even the zero ones, so the screen shows the whole tally).</summary>
        public static Result Work(ZombiesStats.Stats s, int rounds)
        {
            var r = new Result { MatchId = Guid.NewGuid().ToString("N") };
            if (s == null)
                s = new ZombiesStats.Stats();
            foreach (var (what, count, xp) in Rates)
            {
                int n = Mathf.Max(0, count(s, rounds));
                r.Lines.Add(new Line { What = what, Count = n, Each = xp, Xp = n * xp });
                r.Xp += n * xp;
            }
            // Out by the exfil: the bonus on everything above (ZombiesExfil, F12 "23. Exfil")
            if (ZombiesExfil.Escaped && r.Xp > 0)
            {
                int bonus = Mathf.RoundToInt(r.Xp * ZombiesExfil.BonusFactor);
                r.Lines.Add(new Line { What = $"EXFIL BONUS (+{ZombiesExfil.BonusFactor * 100f:0}%)", Count = 1, Each = bonus, Xp = bonus });
                r.Xp += bonus;
            }
            r.Xp = Mathf.Max(0, r.Xp);
            r.Points = r.Xp / XpPerPoint;
            return r;
        }

        private class Reply
        {
            [JsonProperty("total")] public long Total;
            [JsonProperty("added")] public int Added;
        }

        /// <summary>Banked on the server (once; the server ignores a match id it already has).</summary>
        public static void Send(Result r, string map, int rounds)
        {
            if (r == null)
                return;
            string json = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["op"] = "add", ["points"] = r.Points, ["xp"] = r.Xp, ["map"] = map ?? "", ["rounds"] = rounds, ["match"] = r.MatchId,
            });
            Task.Run(() =>
            {
                try
                {
                    Reply reply = JsonConvert.DeserializeObject<Reply>(RequestHandler.PostJson("/kzm/points", json));
                    r.Total = reply?.Total;
                    ZombiesPlugin.Log.LogInfo($"Zombies XP: {r.Xp} XP = {r.Points} Dr. K points banked (total {reply?.Total})");
                }
                catch (Exception e)
                {
                    r.Error = "Points not banked: server not reachable";
                    ZombiesPlugin.Log.LogWarning($"Zombies XP: couldn't bank the points ({e.Message})");
                }
            });
        }
    }
}
