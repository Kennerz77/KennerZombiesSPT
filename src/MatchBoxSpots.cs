using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The mystery box's spots in a zombies match: the layout's BoxSpots (placed with F8). The box starts at a random one,
    /// never the one nearest the players' spawn (the original's rule) and never one in zone 2; every spot it isn't at shows the empty stand (BoxFake: the
    /// crates with the teddy on them). When the teddy sends it off, it lands at a random other spot. Outside a match the
    /// box uses the hideout's two F12 spots (ZombiesPlugin) instead.
    /// </summary>
    internal class MatchBoxSpots
    {
        private readonly List<(Vector3 position, Quaternion rotation)> _places;
        private static readonly System.Random Dice = new System.Random();
        private readonly BoxFake[] _fakes;

        public MysteryBox Box { get; private set; }

        /// <summary>The match's box spots (the last made), so nothing has to search the scene for the box.</summary>
        public static MatchBoxSpots Current { get; private set; }

        public MatchBoxSpots(List<(Vector3 position, Quaternion rotation)> places)
        {
            Current = this;
            _places = places;
            _fakes = new BoxFake[places.Count];
        }

        public int Count => _places.Count;

        public (Vector3 position, Quaternion rotation) Place(int spot) => _places[spot];

        /// <summary>A random spot other than this one.</summary>
        public int Other(int spot)
        {
            if (_places.Count < 2)
                return spot;
            // Any other spot, opened areas or not (the original: the box can land behind a door nobody has bought yet). Our own dice:
            // EFT reseeds Unity's Random on shots, which made rolls repeat (ZombiesPowerUps)
            int next = Dice.Next(0, _places.Count - 1);
            return next >= spot ? next + 1 : next;
        }

        /// <summary>The top of this spot's plank (the box stands on it), or null.</summary>
        public float? PlankTop(int spot)
        {
            if (spot < 0 || spot >= _fakes.Length)
                return null;
            if (_fakes[spot] == null)
                ShowFake(spot, true);
            return _fakes[spot]?.PlankTop;
        }

        public void ShowFake(int spot, bool on)
        {
            if (spot < 0 || spot >= _fakes.Length)
                return;
            // Every spot has its stand; "off" only takes the cases and teddy away (the box is there)
            if (_fakes[spot] == null)
            {
                GameObject prefab = ZombiesAssets.Fake();
                if (prefab == null)
                    return;
                _fakes[spot] = Object.Instantiate(prefab).AddComponent<BoxFake>();
                _fakes[spot].Init(_places[spot]);
            }
            if (_fakes[spot] != null)
                _fakes[spot].Show(on);
        }

        /// <summary>The box set out at its first spot: random, but never the one nearest where the players start.</summary>
        public void SetOut(Vector3 playersStart, int fixedSpot = -1)
        {
            GameObject prefab = ZombiesAssets.Box();
            if (prefab == null || _places.Count == 0)
            {
                ZombiesPlugin.Log.LogWarning("Zombies match: no mystery box (bundle not loaded or no box spots)");
                return;
            }
            int nearest = Enumerable.Range(0, _places.Count).OrderBy(i => Vector3.Distance(_places[i].position, playersStart)).First();
            // Never by the spawn, never in zone 2 (the user, 2026-11-02); if that leaves none, anywhere but by the spawn
            var choices = Enumerable.Range(0, _places.Count).Where(i => i != nearest && ZombiesZones.ZoneAt(_places[i].position) != 2).ToList();
            if (choices.Count == 0)
                choices = Enumerable.Range(0, _places.Count).Where(i => i != nearest || _places.Count == 1).ToList();
            int start = fixedSpot >= 0 && fixedSpot < _places.Count ? fixedSpot : choices[Dice.Next(0, choices.Count)];
            Box = Object.Instantiate(prefab).AddComponent<MysteryBox>();
            Box.Match = this;
            Box.Init(start, _places[start]);
            for (int i = 0; i < _places.Count; i++)
                ShowFake(i, i != start);
            ZombiesPlugin.Log.LogInfo($"Zombies match: mystery box at spot {start + 1} of {_places.Count} (spot {nearest + 1}, by the spawn, left out)");
        }

        /// <summary>The box to this spot at once (the host's pick), the stands at the rest.</summary>
        public void ForceSpot(int spot)
        {
            if (Box == null || spot < 0 || spot >= _places.Count || Box.Spot == spot)
                return;
            Box.PlaceAt(spot, _places[spot]);
            for (int i = 0; i < _places.Count; i++)
                ShowFake(i, i != spot);
            ZombiesPlugin.Log.LogInfo($"Zombies match: mystery box at the host's spot {spot + 1}");
        }

        // ---------- Fire Sale: a box on every spot  ----------

        private readonly List<MysteryBox> _extra = new List<MysteryBox>();

        /// <summary>Each power-up tick: extra boxes out on every other spot while a fire sale is on; once it's over,
        /// each goes as soon as nobody's using it (its spot's stand back).</summary>
        public void FireSale(bool on)
        {
            if (on && _extra.Count == 0 && Box != null)
            {
                GameObject prefab = ZombiesAssets.Box();
                if (prefab == null)
                    return;
                for (int i = 0; i < _places.Count; i++)
                {
                    if (i == Box.Spot)
                        continue;
                    MysteryBox extra = Object.Instantiate(prefab).AddComponent<MysteryBox>();
                    extra.FireSaleExtra = true;
                    extra.Match = this;
                    extra.Init(i, _places[i]);
                    ShowFake(i, false);
                    extra.ArriveHere();
                    _extra.Add(extra);
                }
                ZombiesPlugin.Log.LogInfo($"Zombies match: fire sale, {_extra.Count} more box(es) out");
                return;
            }
            if (on)
            {
                KeepFireSaleSpots();
                return;
            }
            for (int i = _extra.Count - 1; i >= 0; i--)
            {
                MysteryBox extra = _extra[i];
                if (extra != null && !extra.Idle)
                    continue;
                if (extra != null)
                {
                    int spot = extra.Spot;
                    Object.Destroy(extra.gameObject);
                    if (Box == null || Box.Spot != spot)
                        ShowFake(spot, true);
                }
                _extra.RemoveAt(i);
            }
        }

        // The main box moved during the fire sale (its teddy left once the fire sale was already on): the spot it left
        // gets a fire sale box, sped up (it had to wait), and an idle fire sale box where the main box landed goes
        // (user, 2026-11-09)
        private void KeepFireSaleSpots()
        {
            if (Box == null || _extra.Count == 0)
                return;
            GameObject prefab = null;
            for (int i = 0; i < _places.Count; i++)
            {
                MysteryBox here = _extra.FirstOrDefault(b => b != null && b.Spot == i);
                if (i == Box.Spot)
                {
                    if (here != null && here.Idle)
                    {
                        _extra.Remove(here);
                        Object.Destroy(here.gameObject);
                    }
                    continue;
                }
                if (here != null)
                    continue;
                prefab = prefab ?? ZombiesAssets.Box();
                if (prefab == null)
                    return;
                MysteryBox extra = Object.Instantiate(prefab).AddComponent<MysteryBox>();
                extra.FireSaleExtra = true;
                extra.Match = this;
                extra.Init(i, _places[i]);
                ShowFake(i, false);
                extra.ArriveHere(2.5f);
                _extra.Add(extra);
                ZombiesPlugin.Log.LogInfo($"Zombies match: fire sale box sped in at spot {i + 1} (the main box left it)");
            }
        }

        public void Clear()
        {
            foreach (MysteryBox extra in _extra)
                if (extra != null)
                    Object.Destroy(extra.gameObject);
            _extra.Clear();
            if (Box != null)
                Object.Destroy(Box.gameObject);
            foreach (BoxFake f in _fakes)
                if (f != null)
                    Object.Destroy(f.gameObject);
        }
    }
}
