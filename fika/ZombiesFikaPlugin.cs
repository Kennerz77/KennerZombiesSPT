using System;
using Comfort.Common;
using EFT.InventoryLogic;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using KennerZombies;

namespace KennerZombiesFika
{
    /// <summary>
    /// The zombies mode over Fika: one packet type carrying ZombiesNet's messages (the host passes a client's on to
    /// everyone), and EFT items packed with Fika's own item writer so a new item on one player reaches the others'
    /// games. Nothing in Fika is changed: its public events and packet registration only.
    /// </summary>
    /// <summary>
    /// Loaded by Kennerz_ZM_Core only when Fika is installed (it ships as Kennerz_ZM_CoreFika.bridge, a file BepInEx
    /// doesn't scan, so without Fika nothing here is ever looked at), then <see cref="Start"/> is called.
    /// </summary>
    public static class FikaBridge
    {
        private static IFikaNetworkManager _manager;
        private static bool _isServer;

        public static void Start()
        {
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManager);
            FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(e => Online(e.IsServer));
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(_ => Offline());
            ZombiesNet.ItemToBytes = Pack;
            ZombiesNet.BytesToItem = Unpack;
            // Fika's lobby, read only: not started yet, and how many are in (the zombies lobby screen shows them)
            ZombiesNet.FikaWaiting = () => Singleton<IFikaGame>.Instantiated && Singleton<IFikaGame>.Instance?.GameController is BaseGameController gc && !gc.RaidStarted;
            ZombiesFikaParty.Start();
            // A Fika headless (Fika's headless plugin sets this before any raid): host logic only, no player of its own
            ZombiesNet.Headless = FikaBackendUtils.IsHeadless;
            if (ZombiesNet.Headless)
                BepInEx.Logging.Logger.CreateLogSource("Kennerz-ZM_Core Fika").LogInfo("Zombies over Fika: this game is a Fika headless (hosts matches, plays none)");
            ZombiesNet.FikaPlayers = () => Singleton<IFikaNetworkManager>.Instantiated ? Singleton<IFikaNetworkManager>.Instance.PlayerAmount : 1;
        }

        private static void OnManager(FikaNetworkManagerCreatedEvent e)
        {
            _manager = e.Manager;
            _isServer = _manager is FikaServer;
            if (_isServer)
                _manager.RegisterPacket<ZombiesPacket, NetPeer>((p, _) => ZombiesNet.Receive(p.Data, true));
            else
                _manager.RegisterPacket<ZombiesPacket>(p => ZombiesNet.Receive(p.Data, false));
            ZombiesNet.IsHost = _isServer;
            ZombiesNet.Headless = FikaBackendUtils.IsHeadless;
            ZombiesNet.HeadlessRequester = !_isServer && FikaBackendUtils.IsHeadlessRequester;
            ZombiesNet.HeadlessMatch = FikaBackendUtils.IsHeadless || (!_isServer && FikaBackendUtils.IsHeadlessGame);
            ZombiesNet.Sender = Send;
            // The manager only exists for a Fika raid: online from here (the zombies kit goes out as it's put on)
            ZombiesNet.Online = true;
            // The party's leader hosting: its members can join now
            if (_isServer)
                ZombiesNet.FikaServerUp();
            // The leader had a Fika headless host it and is in as a client now: the members join the same match
            else if (FikaBackendUtils.IsHeadlessRequester)
                ZombiesNet.FikaHeadlessUp(FikaBackendUtils.GroupId);
        }

        private static void Online(bool isServer)
        {
            ZombiesNet.IsHost = isServer;
            ZombiesNet.Online = _manager != null;
            ZombiesNet.RaidStarted();
            BepInEx.Logging.Logger.CreateLogSource("Kennerz-ZM_Core Fika").LogInfo($"Zombies over Fika: in the raid as the {(isServer ? "host" : "client")}");
        }

        private static void Offline()
        {
            ZombiesNet.Online = false;
            ZombiesNet.IsHost = true;
            ZombiesNet.HeadlessMatch = false;
            ZombiesNet.HeadlessRequester = false;
        }

        private static void Send(byte[] data)
        {
            if (_manager == null)
                return;
            var packet = new ZombiesPacket { Data = data };
            _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered, _isServer);
        }

        private static byte[] Pack(Item item)
        {
            var w = new NetDataWriter();
            w.PutItem(item);
            return w.CopyData();
        }

        private static Item Unpack(byte[] bytes) => new NetDataReader(bytes).GetItem();
    }

    /// <summary>One zombies message (ZombiesNet's bytes).</summary>
    public struct ZombiesPacket : INetSerializable
    {
        public byte[] Data;

        public void Serialize(NetDataWriter writer) => writer.PutBytesWithLength(Data ?? Array.Empty<byte>());

        public void Deserialize(NetDataReader reader) => Data = reader.GetBytesWithLength();
    }
}
