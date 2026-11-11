using System;
using Comfort.Common;
using EFT;

namespace KennerZombies
{
    /// <summary>
    /// The map's time of day: set to an hour and held there (EFT's raid clock stopped: its GameDateTime reset with a
    /// time factor of 0, which the sky and light follow). The map editor sets it; a layout saves it, and a zombies
    /// match holds it. Leaving the raid ends it; Release lets the clock run on from the held hour.
    /// </summary>
    internal static class MapTime
    {
        private static GameDateTime _held;
        private static float _factor = 7f;

        /// <summary>The raid's hour now (0-24), or null outside a raid.</summary>
        public static float? Hour
        {
            get
            {
                GameDateTime clock = Clock;
                if (clock == null)
                    return null;
                DateTime now = clock.Calculate();
                return (float)now.TimeOfDay.TotalHours;
            }
        }

        /// <summary>Sets the raid's clock to the hour (0-24) and stops it there.</summary>
        public static void Hold(float hour)
        {
            GameDateTime clock = Clock;
            if (clock == null)
                return;
            // Remember how fast the clock ran before it was first stopped
            if (_held != clock)
            {
                _held = clock;
                if (clock.TimeFactor > 0f)
                    _factor = clock.TimeFactor;
            }
            DateTime now = clock.Calculate();
            DateTime at = now.Date + TimeSpan.FromHours(Math.Max(0f, Math.Min(hour, 23.99f)));
            clock.Reset(DateTimeExtensions.UtcNow, at, 0f, true);
        }

        /// <summary>The clock isn't at this hour (stopped there), or isn't held.</summary>
        public static bool Off(float hour)
        {
            GameDateTime clock = Clock;
            if (clock == null)
                return false;
            if (_held != clock || clock.TimeFactor > 0f)
                return true;
            double now = clock.Calculate().TimeOfDay.TotalHours;
            return Math.Abs(now - Math.Max(0f, Math.Min(hour, 23.99f))) > 0.02;
        }

        /// <summary>The clock runs on from where it stands, at its old speed.</summary>
        public static void Release()
        {
            GameDateTime clock = Clock;
            if (clock == null || _held != clock)
                return;
            clock.Reset(DateTimeExtensions.UtcNow, clock.Calculate(), _factor, true);
            _held = null;
        }

        public static bool Held => _held != null && _held == Clock;

        private static GameDateTime Clock => Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.GameDateTime : null;
    }
}
