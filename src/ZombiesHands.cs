using System;
using System.Collections;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// One rule for everything of ours that changes what's in a player's hands (box and wall-buy guns, Pack-a-Punch,
    /// perk drinks): never under a running reload, and a gun never leaves its slot while the hands still hold it.
    /// (User, 2026-11-10: the box's M700 taken 2 s into an RSh-12 reload; the put-away hadn't answered in 2.5 s, the RSh
    /// was pulled out anyway, its reload finished on a gun that was gone, the teammate's game lost track of its rounds
    /// and every later swap failed.)
    /// Settle: a running reload is skipped to its end at once (EFT's own FastForward, as Fika uses on draws: the rounds go
    /// in), so a buy or the box feels instant; if that doesn't take within 1 s, the reload is waited out (8 s at most).
    /// PutAway: EFT's put-away, waited for; if it never answers, put away again past EFT's queue of hand changes.
    /// </summary>
    internal static class ZombiesHands
    {
        public static bool Reloading(Player p) =>
            p != null && p.HandsController is Player.FirearmController fc && fc.IsInReloadOperation();

        public static IEnumerator Settle(Player p, string why)
        {
            if (!Reloading(p))
                yield break;
            float start = Time.time;
            try
            {
                if (p.HandsController is Player.FirearmController fc)
                    fc.CurrentOperation?.FastForward();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies hands: {why}: skipping the reload refused ({e.Message}): waiting for it instead");
            }
            float until = start + 1f;
            while (Time.time < until && Reloading(p))
                yield return null;
            bool skipped = !Reloading(p);
            until = Time.time + 8f;
            while (Time.time < until && Reloading(p))
                yield return null;
            ZombiesPlugin.Log.LogInfo($"Zombies hands: {why}: the reload {(skipped ? "skipped to its end" : "waited out")} in {Time.time - start:0.0} s"
                                      + (Reloading(p) ? " (still going: went ahead)" : ""));
        }

        /// <summary>The hands emptied; Result says whether they really are empty after.</summary>
        public class Outcome
        {
            public bool Empty;
        }

        public static IEnumerator PutAway(Player p, string why, Outcome outcome)
        {
            outcome.Empty = p?.HandsController is Player.EmptyHandsController;
            if (p == null || outcome.Empty)
                yield break;
            bool emptied = false;
            try
            {
                p.SetEmptyHands(r =>
                {
                    emptied = true;
                    if (r.Failed)
                        ZombiesPlugin.Log.LogWarning($"Zombies hands: {why}: put-away failed: {r.Error}");
                });
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies hands: {why}: put-away refused ({e.Message})");
            }
            float until = Time.time + 4f;
            while (!emptied && Time.time < until && !(p.HandsController is Player.EmptyHandsController))
                yield return null;
            if (!(p.HandsController is Player.EmptyHandsController))
            {
                ZombiesPlugin.Log.LogWarning($"Zombies hands: {why}: the hands never put away in 4 s: put away again, past EFT's queue of hand changes");
                try
                {
                    p.Proceed(true, (Callback<IEmptyHandsController>)(_ => { }), false);
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies hands: {why}: a fresh empty-hands controller couldn't be made ({e.Message})");
                }
                until = Time.time + 1.5f;
                while (Time.time < until && !(p.HandsController is Player.EmptyHandsController))
                    yield return null;
            }
            outcome.Empty = p.HandsController is Player.EmptyHandsController;
            if (!outcome.Empty)
                ZombiesPlugin.Log.LogWarning($"Zombies hands: {why}: the hands still hold {p.HandsController?.Item?.LocalizedName()}");
        }
    }
}
