using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Impostor.Api;
using Impostor.Api.Events.Managers;
using Impostor.Api.Net;
using Impostor.Api.Net.Custom;
using Impostor.Api.Net.Inner;
using Impostor.Api.Net.Messages.Rpcs;
using Impostor.Server.Events.Player;
using Impostor.Server.Net.Inner.Objects.ShipStatus;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

namespace Impostor.Server.Net.Inner.Objects.Components
{
    internal partial class InnerCustomNetworkTransform : InnerNetObject
    {
        private static readonly Vector2 ColliderOffset = new Vector2(0f, -0.4f);

        private readonly ILogger<InnerCustomNetworkTransform> _logger;
        private readonly InnerPlayerControl _playerControl;
        private readonly IEventManager _eventManager;
        private readonly ObjectPool<PlayerMovementEvent> _pool;

        private ushort _lastSequenceId;
        private AirshipSpawnState _spawnState;
        private bool _spawnSnapAllowed;
        private bool _initialSpawn;

        public InnerCustomNetworkTransform(ICustomMessageManager<ICustomRpc> customMessageManager, Game game, ILogger<InnerCustomNetworkTransform> logger, InnerPlayerControl playerControl, IEventManager eventManager, ObjectPool<PlayerMovementEvent> pool) : base(customMessageManager, game)
        {
            _logger = logger;
            _playerControl = playerControl;
            _eventManager = eventManager;
            _pool = pool;
            _spawnSnapAllowed = false;
            _initialSpawn = true;
        }

        private enum AirshipSpawnState : byte
        {
            PreSpawn,
            SelectingSpawn,
            Spawned,
        }

        public Vector2 Position { get; private set; }

        public override ValueTask<bool> SerializeAsync(IMessageWriter writer, bool initialState)
        {
            if (initialState)
            {
                writer.Write(_lastSequenceId);
                writer.Write(Position);
                return new ValueTask<bool>(true);
            }

            writer.Write(_lastSequenceId);

            // Impostor doesn't keep a memory of positions, so just send the last one
            writer.WritePacked(1);
            writer.Write(Position);
            return new ValueTask<bool>(true);
        }

        public override async ValueTask DeserializeAsync(IClientPlayer sender, IClientPlayer? target, IMessageReader reader, bool initialState)
        {
            var sequenceId = reader.ReadUInt16();

            if (initialState)
            {
                _lastSequenceId = sequenceId;
                await SetPositionAsync(sender, reader.ReadVector2());
            }
            else
            {
                if (!await ValidateOwnership(CheatContext.Deserialize, sender) || !await ValidateBroadcast(CheatContext.Deserialize, sender, target))
                {
                    return;
                }

                var positions = reader.ReadPackedInt32();

                for (var i = 0; i < positions; i++)
                {
                    var position = reader.ReadVector2();
                    var newSid = (ushort)(sequenceId + i);
                    if (SidGreaterThan(newSid, _lastSequenceId))
                    {
                        _lastSequenceId = newSid;
                        await SetPositionAsync(sender, position);
                    }
                }
            }
        }

        public override async ValueTask<bool> HandleRpcAsync(ClientPlayer sender, ClientPlayer? target, RpcCalls call, IMessageReader reader)
        {
            if (call == RpcCalls.SnapTo)
            {
                if (!await ValidateOwnership(call, sender))
                {
                    return false;
                }

                Rpc21SnapTo.Deserialize(reader, out var position, out var minSid);

                _logger.LogInformation("SnapTo for {0}", NetId);
                if (_spawnSnapAllowed)
                {
                    _logger.LogInformation("SnapTo allowed for {0} {1}", NetId, _spawnSnapAllowed);
                    _spawnSnapAllowed = false;

                    // Check if the snap position is the expected spawn point
                    var expectedPosition = Game.GameNet.ShipStatus?.GetSpawnLocation(_playerControl, Game.PlayerCount, _initialSpawn);
                    _logger.LogTrace("{0} / {1}", position, expectedPosition);
                    if (!Approximately(position, expectedPosition ?? Vector2.Zero))
                    {
                        if (await sender.Client.ReportCheatAsync(call, "Failed to snap to the correct spawn point"))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                if (Game.GameNet.ShipStatus is InnerAirshipStatus airshipStatus)
                {
                    // As part of airship spawning, clients are sending snap to -25 40 to move themself out of view
                    if (_spawnState == AirshipSpawnState.PreSpawn && Approximately(position, airshipStatus.PreSpawnLocation))
                    {
                        _spawnState = AirshipSpawnState.SelectingSpawn;
                        return true;
                    }

                    // Once the spawn has been selected, the client sends a second snap to the select spawn location
                    if (_spawnState == AirshipSpawnState.SelectingSpawn && airshipStatus.SpawnLocations.Any(location => Approximately(position, location)))
                    {
                        _spawnState = AirshipSpawnState.Spawned;
                        return true;
                    }
                }

                if (!await ValidateCanVent(call, sender, _playerControl.PlayerInfo))
                {
                    return false;
                }

                if (Game.GameNet.ShipStatus == null)
                {
                    // Cannot perform vent position check on unknown ship statuses
                    if (await sender.Client.ReportCheatAsync(call, "Failed vent position check on unknown map"))
                    {
                        return false;
                    }
                }
                else
                {
                    var vents = Game.GameNet.ShipStatus!.Data.Vents.Values;

                    var vent = vents.SingleOrDefault(x => Approximately(x.Position, position + ColliderOffset));

                    if (vent == null)
                    {
                        if (await sender.Client.ReportCheatAsync(call, "Failed vent position check"))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        await _eventManager.CallAsync(new PlayerVentEvent(Game, sender, _playerControl, vent));
                    }
                }

                await SnapToAsync(sender, position, minSid);
                return true;
            }

            return await base.HandleRpcAsync(sender, target, call, reader);
        }

        internal async ValueTask SetPositionAsync(IClientPlayer sender, Vector2 position)
        {
            Position = position;

            var playerMovementEvent = _pool.Get();
            playerMovementEvent.Reset(Game, sender, _playerControl);
            await _eventManager.CallAsync(playerMovementEvent);
            _pool.Return(playerMovementEvent);
        }

        internal void OnPlayerSpawn(bool initialSpawn)
        {
            _logger.LogInformation("Allowing spawn snap for {0}", NetId);
            _spawnSnapAllowed = true;
            _initialSpawn = initialSpawn;
            _spawnState = AirshipSpawnState.PreSpawn;
        }

        private static bool SidGreaterThan(ushort newSid, ushort prevSid)
        {
            var num = (ushort)(prevSid + (uint)short.MaxValue);

            return (int)prevSid < (int)num
                ? newSid > prevSid && newSid <= num
                : newSid > prevSid || newSid <= num;
        }

        private static bool Approximately(Vector2 a, Vector2 b, float tolerance = 0.1f)
        {
            var abs = Vector2.Abs(a - b);
            return abs.X <= tolerance && abs.Y <= tolerance;
        }

        private ValueTask SnapToAsync(IClientPlayer sender, Vector2 position, ushort minSid)
        {
            if (!SidGreaterThan(minSid, _lastSequenceId))
            {
                return default;
            }

            _lastSequenceId = minSid;
            return SetPositionAsync(sender, position);
        }
    }
}
