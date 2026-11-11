using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The stutter hunt (the user: moving and looking go in steps in a match, smooth in a normal raid): each of the
    /// match's per-frame jobs is timed, with the memory it takes (garbage the collector must clear; Unity's collector
    /// stops the whole game while it runs). Every 5 s the log gets the frame rate, the worst frames, how often the
    /// collector ran, and the jobs that cost the most time and memory.
    /// </summary>
    internal static class ZombiesProfile
    {
        private class Cost
        {
            public double Ms, WorstMs;
            public long Bytes;
            public int Calls;
        }

        private static readonly Dictionary<string, Cost> Costs = new Dictionary<string, Cost>();
        private static readonly Stopwatch Watch = new Stopwatch();
        private static float _since = -1f, _worstFrame;
        private static int _frames, _slowFrames, _gcStart, _gcInSlow;
        private static int _lastGc;


        /// <summary>Runs one job, timed.</summary>
        public static void Measure(string name, Action job)
        {
            long before = GC.GetTotalMemory(false);
            Watch.Restart();
            try
            {
                job();
            }
            finally
            {
                Watch.Stop();
                long after = GC.GetTotalMemory(false);
                if (!Costs.TryGetValue(name, out Cost c))
                    Costs[name] = c = new Cost();
                double ms = Watch.Elapsed.TotalMilliseconds;
                c.Ms += ms;
                c.WorstMs = Math.Max(c.WorstMs, ms);
                c.Calls++;
                if (after > before)
                    c.Bytes += after - before;
            }
        }

        /// <summary>Once a frame (the match's Update).</summary>
        public static void Frame()
        {
            float dt = Time.unscaledDeltaTime;
            int gc = GC.CollectionCount(0);
            if (_since < 0f)
            {
                _since = Time.unscaledTime;
                _gcStart = gc;
                _lastGc = gc;
                return;
            }
            _frames++;
            _worstFrame = Mathf.Max(_worstFrame, dt);
            if (dt > 0.034f)
            {
                _slowFrames++;
                if (gc != _lastGc)
                    _gcInSlow++;
            }
            _lastGc = gc;
            float span = Time.unscaledTime - _since;
            if (span < 5f)
                return;
            var sb = new StringBuilder();
            sb.Append($"[profile] {_frames / span:0} fps over {span:0.0} s, worst frame {_worstFrame * 1000f:0} ms, {_slowFrames} frames over 34 ms " +
                      $"({_gcInSlow} with a garbage collection), collections {gc - _gcStart}, heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            foreach (var kv in Costs.OrderByDescending(k => k.Value.Ms).Take(6))
                sb.Append($"\n   {kv.Key}: {kv.Value.Ms / Math.Max(1, _frames):0.00} ms/frame (worst {kv.Value.WorstMs:0.0}), {kv.Value.Bytes / span / 1024f:0} KB/s");
            foreach (var kv in Costs.Where(k => k.Value.Bytes / span > 100 * 1024).OrderByDescending(k => k.Value.Bytes).Take(4))
                sb.Append($"\n   memory: {kv.Key} {kv.Value.Bytes / span / 1024f:0} KB/s");
            ZombiesPlugin.Log.LogInfo(sb.ToString());
            Costs.Clear();
            _since = Time.unscaledTime;
            _frames = _slowFrames = _gcInSlow = 0;
            _gcStart = gc;
            _worstFrame = 0f;
        }
    }
}
