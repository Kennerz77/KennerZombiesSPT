using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using JsonType;
using HarmonyLib;
using KennerZombies;

namespace KennerZombiesFika
{
    /// <summary>
    /// The zombies party's two Fika steps, done the way Fika's own buttons do them (Fika itself unchanged; its UI
    /// internals reached by name, so a Fika update shows up as a logged failure, not a crash):
    /// - the leader: Fika's START on the accept screen (hosts the raid: CreateMatch, then the accept button);
    /// - a member: Fika's join from the main menu (JoinMatch with the leader's profile id, the raid settings for the
    ///   leader's map, then the game's LocalGameMatching), with no map picked on their own screen.
    /// </summary>
    internal static class ZombiesFikaParty
    {
        private static BepInEx.Logging.ManualLogSource Log => _log ??= BepInEx.Logging.Logger.CreateLogSource("Kennerz-ZM_Core Fika");
        private static BepInEx.Logging.ManualLogSource _log;
        private static bool _joining;

        public static void Start()
        {
            ZombiesNet.FikaHostNow = HostNow;
            ZombiesNet.FikaJoin = (hostId, map) => _ = Join(hostId, map);
        }

        // Fika's START (the host's own button, inside its raid panel on the accept screen)
        private static bool HostNow(bool allowHeadless)
        {
            try
            {
                Type mmType = AccessTools.TypeByName("Fika.Core.UI.Custom.MatchMakerUIScript");
                UnityEngine.Object mm = mmType != null ? UnityEngine.Object.FindObjectOfType(mmType) : null;
                if (mm == null)
                    return false;
                object ui = Traverse.Create(mm).Field("_fikaMatchMakerUi").GetValue();
                var start = ui != null ? Traverse.Create(ui).Field("StartButton").GetValue<UnityEngine.UI.Button>() : null;
                if (start == null)
                    return false;
                // A Fika headless free on the server: it hosts and the leader joins it as a client (Fika's own
                // "use headless" toggle; Fika greys it when none is free). None: the leader's game hosts, as before.
                var headless = Traverse.Create(ui).Field("DedicatedToggle").GetValue<UnityEngine.UI.Toggle>();
                bool useHeadless = allowHeadless && headless != null && headless.interactable;
                if (headless != null)
                    headless.isOn = useHeadless;
                Log.LogInfo($"Zombies party: pressing Fika's START ({(useHeadless ? "on a Fika headless" : "hosted by this game")})");
                start.onClick.Invoke();
                return true;
            }
            catch (Exception e)
            {
                Log.LogError($"Zombies party: couldn't press Fika's START ({e.Message})");
                return true; // don't keep trying
            }
        }

        private static async Task Join(string hostId, string map)
        {
            if (_joining)
                return;
            _joining = true;
            try
            {
                if (!TarkovApplication.Exist(out TarkovApplication app) || app.Session == null)
                {
                    Log.LogError("Zombies party: no game session to join with");
                    return;
                }
                IEftSession session = app.Session;
                LocationSettings.Location location = session.LocationSettings.locations.Values.FirstOrDefault(l => l.Id == map);
                if (location == null)
                {
                    Log.LogError($"Zombies party: map {map} not found");
                    return;
                }
                Type backend = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
                Set(backend, "Profile", session.Profile);
                Type mmType = AccessTools.TypeByName("Fika.Core.UI.Custom.MatchMakerUIScript");
                MethodInfo join = AccessTools.Method(mmType, "JoinMatch");
                if (backend == null || join == null)
                {
                    Log.LogError("Zombies party: Fika's join isn't where it was (Fika updated?)");
                    return;
                }
                Log.LogInfo($"Zombies party: joining {hostId}'s raid on {map}");
                bool ok = await (Task<bool>)join.Invoke(null, new object[] { session.Profile.Id, hostId, null, false });
                if (!ok)
                {
                    Log.LogWarning("Zombies party: Fika couldn't join the raid");
                    return;
                }
                var raidSettings = new RaidSettings
                {
                    Side = ESideType.Pmc,
                    PlayersSpawnPlace = EPlayersSpawnPlace.SamePlace,
                    MetabolismDisabled = false,
                    BotSettings = default,
                    WavesSettings = default,
                    TimeAndWeatherSettings = default,
                    SelectedDateTime = EDateTime.CURR,
                    SelectedLocation = location,
                    isInTransition = false,
                    RaidMode = ERaidMode.Local,
                    IsPveOffline = true,
                    OnlinePveRaid = false,
                };
                Set(backend, "IsScav", false);
                var menu = Traverse.Create(app).Field("_menuOperation").GetValue<MainMenuShowOperation>();
                Traverse.Create(app).Field("_raidSettings").SetValue(raidSettings);
                if (menu?.MatchmakerPlayersController != null)
                    menu.MatchmakerPlayersController.MatchingStartTime = DateTimeExtensions.Now;
                await app.LocalGameMatching(raidSettings.TimeAndWeatherSettings);
            }
            catch (Exception e)
            {
                Log.LogError($"Zombies party: joining failed: {e}");
            }
            finally
            {
                _joining = false;
            }
        }

        // A static field or property of Fika's, set by name
        private static void Set(Type t, string name, object value)
        {
            if (t == null)
                return;
            var tr = Traverse.Create(t);
            if (tr.Property(name).PropertyExists())
                tr.Property(name).SetValue(value);
            else if (tr.Field(name).FieldExists())
                tr.Field(name).SetValue(value);
        }
    }
}
