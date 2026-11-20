using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The four config toggles from issue #307: two hardening reach guards on incoming packets
/// and two physics tuning switches. Every scenario here flips its own key mid-run with
/// "/stratum set", so the "applies at once, no restart" claim is executed rather than
/// assumed, and every scenario restores the key afterwards because the whole class shares
/// one server boot.
///
/// DevCommandBlock_Should_StayOutOfReach_When_BlockEntityGuardTurnedOff is expected to FAIL
/// on the current head. IsBlockEntityPacketInRange is shared by two callers: the block
/// entity packet path and TryAllowDevCommandBlockUse, the gate on the dev command block use
/// packet (id 24112). Turning Hardening.BlockEntityPacketRangeGuard off to let a mod drive
/// containers from a distance therefore also removes the reach limit on remote command block
/// triggering for players without controlserver. The scenario keeps the correct expectation.
/// </summary>
public class ConfigurableGuardScenarios : AtlasScenarioBase
{
	private const string EntityGuardKey = "Hardening.EntityPacketRangeGuard";
	private const string BlockEntityGuardKey = "Hardening.BlockEntityPacketRangeGuard";
	private const string HysteresisKey = "Performance.Physics.EntityTrackingHysteresisEnabled";
	private const string ClientListReuseKey = "Performance.Physics.ClientListReuseEnabled";

	/// <summary>
	/// How far past the sender's own reach the far probes sit. Both guards measure against the
	/// sender's PickingRange plus a slack constant, 8 blocks for entities and 4 for blocks, and
	/// PickingRange is whatever the joined player carries, not the vanilla 4.5 on this harness.
	/// So the distance is derived from the live value rather than hardcoded.
	/// </summary>
	private const int OutOfReachMargin = 40;

	/// <summary>Packet_Client ids of the two handlers under test, from ServerMain.PacketHandlers.</summary>
	private const int EntityPacketClientId = 31;
	private const int BlockEntityPacketClientId = 22;

	/// <summary>
	/// The inner packet id the probes count. Above EnumBlockEntityPacketId.Open (1000) so the
	/// block entity path's open-inventory-session check short circuits, and far away from
	/// anything vanilla acts on, including the dev command block id 24112.
	/// </summary>
	private const int ProbePacketId = 9001;

	/// <summary>Hysteresis outer radius, PhysicsManager.StratumTrackingOuterRangeMultiplier squared away.</summary>
	private const double OuterRangeMultiplier = 1.1;

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task EntityPacket_Should_ReachFarEntity_When_GuardTurnedOff()
	{
		ITestPlayer player = await World.JoinPlayer("grd-entpkt");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(2);
		BlockPos anchor = player.Position;

		int farDistance = OutOfReachDistance(player);
		BlockPos farPos = anchor.AddCopy(farDistance, 1, 0);
		await KeepColumnLoaded(farPos);

		Entity near = World.SpawnEntity("game:strawdummy", anchor.AddCopy(2, 1, 0));
		Entity far = World.SpawnEntity("game:strawdummy", farPos);
		var nearProbe = new EntityPacketProbe(near);
		var farProbe = new EntityPacketProbe(far);
		near.SidedProperties.Behaviors.Add(nearProbe);
		far.SidedProperties.Behaviors.Add(farProbe);
		await World.Ticks(5);

		try
		{
			Assert.True(
				World.Api.World.GetEntityById(far.EntityId) != null,
				"the far entity left the world before the probe ran; the kept-loaded column did not hold");

			DispatchEntityPacket(player, near.EntityId);
			DispatchEntityPacket(player, far.EntityId);
			await World.Ticks(2);

			Assert.True(
				nearProbe.Deliveries == 1,
				$"the entity packet never reached an entity 2 blocks away with the guard on: {nearProbe.Deliveries} deliveries");
			Assert.True(
				farProbe.Deliveries == 0,
				$"EntityPacketRangeGuard let a packet through to an entity {farDistance} blocks away: "
				+ $"{farProbe.Deliveries} deliveries");

			CommandResult off = await World.ExecuteCommand($"/stratum set {EntityGuardKey} false");
			Assert.True(off.Ok, $"/stratum set {EntityGuardKey} false failed: {off.Message}");

			DispatchEntityPacket(player, near.EntityId);
			DispatchEntityPacket(player, far.EntityId);
			await World.Ticks(2);

			Assert.True(
				farProbe.Deliveries == 1,
				$"{EntityGuardKey}=false did not apply without a restart: the far entity still received "
				+ $"{farProbe.Deliveries} packets");
			Assert.True(
				nearProbe.Deliveries == 2,
				$"the near entity stopped receiving packets once the guard was off: {nearProbe.Deliveries} deliveries");
		}
		finally
		{
			// No assert on the restore: this runs with a failure possibly in flight and must
			// not replace it. The on-state checks above are what would catch a stuck flag.
			await World.ExecuteCommand($"/stratum set {EntityGuardKey} true");
			near.SidedProperties.Behaviors.Remove(nearProbe);
			far.SidedProperties.Behaviors.Remove(farProbe);
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task BlockEntityPacket_Should_ReachFarBlockEntity_When_GuardTurnedOff()
	{
		ITestPlayer player = await World.JoinPlayer("grd-bepkt");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(2);
		BlockPos anchor = player.Position;

		int farDistance = OutOfReachDistance(player);
		BlockPos nearPos = anchor.AddCopy(2, 0, 0);
		BlockPos farPos = anchor.AddCopy(farDistance, 0, 0);
		await KeepColumnLoaded(farPos);

		BlockEntityPacketProbe nearProbe = PlaceProbeBlockEntity(nearPos);
		BlockEntityPacketProbe farProbe = PlaceProbeBlockEntity(farPos);
		await World.Ticks(5);

		try
		{
			DispatchBlockEntityPacket(player, nearPos);
			DispatchBlockEntityPacket(player, farPos);
			await World.Ticks(2);

			Assert.True(
				nearProbe.Deliveries == 1,
				$"the block entity packet never reached a block entity 2 blocks away with the guard on: "
				+ $"{nearProbe.Deliveries} deliveries");
			Assert.True(
				farProbe.Deliveries == 0,
				$"BlockEntityPacketRangeGuard let a packet through to a block entity {farDistance} blocks away: "
				+ $"{farProbe.Deliveries} deliveries");

			CommandResult off = await World.ExecuteCommand($"/stratum set {BlockEntityGuardKey} false");
			Assert.True(off.Ok, $"/stratum set {BlockEntityGuardKey} false failed: {off.Message}");

			DispatchBlockEntityPacket(player, nearPos);
			DispatchBlockEntityPacket(player, farPos);
			await World.Ticks(2);

			Assert.True(
				farProbe.Deliveries == 1,
				$"{BlockEntityGuardKey}=false did not apply without a restart: the far block entity still "
				+ $"received {farProbe.Deliveries} packets");
			Assert.True(
				nearProbe.Deliveries == 2,
				$"the near block entity stopped receiving packets once the guard was off: {nearProbe.Deliveries} deliveries");
		}
		finally
		{
			await World.ExecuteCommand($"/stratum set {BlockEntityGuardKey} true");
			nearProbe.Blockentity.Behaviors.Remove(nearProbe);
			farProbe.Blockentity.Behaviors.Remove(farProbe);
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task DevCommandBlock_Should_StayOutOfReach_When_BlockEntityGuardTurnedOff()
	{
		ITestPlayer player = await World.JoinPlayer("grd-devcmd");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(2);
		BlockPos anchor = player.Position;

		// Atlas players ride a dummy socket, and PlayerDataManager hands single player clients
		// the highest privilege role. controlserver skips the reach check outright, so the
		// scenario would be vacuous without the demotion. It comes before the geometry because
		// a role carries a game mode, and a game mode carries a picking range.
		string previousRole = player.Player.Role.Code;
		player.Player.SetRole("suplayer");

		try
		{
			int farDistance = OutOfReachDistance(player);
			BlockPos farPos = anchor.AddCopy(farDistance, 0, 0);
			await KeepColumnLoaded(farPos);

			Assert.False(
				player.Player.HasPrivilege(Privilege.controlserver),
				"the test player still holds controlserver, so the reach check is never reached; setup is invalid");

			// Without this, a claim over the far position would refuse the use on its own and the
			// scenario would pass while the reach limit was gone.
			EnumWorldAccessResponse access =
				World.Api.World.Claims.TestAccess(player.Player, farPos, EnumBlockAccessFlags.BuildOrBreak);
			Assert.True(
				access == EnumWorldAccessResponse.Granted,
				$"the far position is not unclaimed land the demoted player may build on ({access}), so a "
				+ "refusal would not prove the reach limit held; setup is invalid");

			Assert.False(
				InvokeDevCommandBlockUse(player, farPos),
				$"a player without controlserver was allowed to trigger a dev command block {farDistance} "
				+ "blocks away while the guard was on; setup is invalid");

			CommandResult off = await World.ExecuteCommand($"/stratum set {BlockEntityGuardKey} false");
			Assert.True(off.Ok, $"/stratum set {BlockEntityGuardKey} false failed: {off.Message}");

			Assert.False(
				InvokeDevCommandBlockUse(player, farPos),
				$"{BlockEntityGuardKey}=false also removed the reach limit on the dev command block use "
				+ $"packet (id 24112): a player without controlserver triggered one {farDistance} blocks "
				+ "away on unclaimed land. TryAllowDevCommandBlockUse shares IsBlockEntityPacketInRange "
				+ "with the block entity packet path, so one config key opens two doors. The block entity "
				+ "toggle must not reach the dev command block gate.");
		}
		finally
		{
			await World.ExecuteCommand($"/stratum set {BlockEntityGuardKey} true");
			player.Player.SetRole(previousRole);
		}
	}

	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task TrackedEntity_Should_Despawn_When_HysteresisTurnedOff()
	{
		ITestPlayer player = await World.JoinPlayer("grd-hyst");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(2);

		double trackingRange = Math.Sqrt(TrackingRangeSq());
		// Halfway into the margin: outside the tracking range, inside the outer radius.
		double bandDistance = trackingRange * ((1.0 + OuterRangeMultiplier) / 2.0);
		BlockPos anchor = player.Position;
		await KeepColumnLoaded(anchor.AddCopy((int)bandDistance, 0, 0));

		// Tracking starts inside the range: the hysteresis branch only keeps an entity that
		// was already tracked, it never adds one.
		Entity probe = World.SpawnEntity("game:strawdummy", anchor.AddCopy(2, 1, 0));
		object client = ClientOf(player);
		await World.Until(() => TrackedEntities(client).Contains(probe.EntityId), timeoutTicks: 600);

		try
		{
			Assert.True(
				probe.SimulationRange <= trackingRange,
				$"the probe's SimulationRange ({probe.SimulationRange}) exceeds the tracking range "
				+ $"({trackingRange:F1}), so UpdateTrackedEntityState re-tracks it inside the margin and "
				+ "the hysteresis branch is not what keeps it; setup is invalid");

			Assert.True(
				await StaysTracked(player, probe, client, bandDistance, 90),
				$"a tracked entity {bandDistance:F1} blocks out, inside the outer radius "
				+ $"({trackingRange * OuterRangeMultiplier:F1}), was dropped while {HysteresisKey} is on");

			CommandResult off = await World.ExecuteCommand($"/stratum set {HysteresisKey} false");
			Assert.True(off.Ok, $"/stratum set {HysteresisKey} false failed: {off.Message}");

			Assert.False(
				await StaysTracked(player, probe, client, bandDistance, 90),
				$"{HysteresisKey}=false did not apply without a restart: the entity stayed tracked "
				+ $"{bandDistance:F1} blocks out, past the {trackingRange:F1} block tracking range");

			Assert.True(
				World.Api.World.GetEntityById(probe.EntityId) != null,
				"the probe left the world during the measurement, so the drop cannot be read as a "
				+ "hysteresis decision; setup is invalid");
		}
		finally
		{
			await World.ExecuteCommand($"/stratum set {HysteresisKey} true");
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task PhysicsClientList_Should_BeReallocated_When_ReuseTurnedOff()
	{
		await World.Ticks(3);
		object? firstOn = ClientListRef();
		await World.Ticks(3);
		object? secondOn = ClientListRef();

		Assert.True(firstOn != null, "PhysicsManager.ClientList is still null after three ticks");
		Assert.True(
			ReferenceEquals(firstOn, secondOn),
			$"PhysicsManager.ClientList was reallocated across physics ticks while {ClientListReuseKey} is on");

		try
		{
			CommandResult off = await World.ExecuteCommand($"/stratum set {ClientListReuseKey} false");
			Assert.True(off.Ok, $"/stratum set {ClientListReuseKey} false failed: {off.Message}");

			await World.Ticks(3);
			object? firstOff = ClientListRef();
			await World.Ticks(3);
			object? secondOff = ClientListRef();

			Assert.False(
				ReferenceEquals(firstOff, secondOff),
				$"{ClientListReuseKey}=false did not apply without a restart: PhysicsManager.ClientList is "
				+ "still the same instance two physics ticks apart");
		}
		finally
		{
			await World.ExecuteCommand($"/stratum set {ClientListReuseKey} true");
		}
	}

	/// <summary>Distance at which both guards must refuse a packet from this player.</summary>
	private static int OutOfReachDistance(ITestPlayer player)
	{
		return (int)Math.Ceiling(player.Player.WorldData.PickingRange) + OutOfReachMargin;
	}

	/// <summary>
	/// Force-loads the column holding <paramref name="pos"/> and waits for it. Nothing else keeps
	/// a column 100 blocks from the only player alive, and an unloaded chunk would make both
	/// packet paths bail on their own chunk check instead of on the range guard.
	/// </summary>
	private async Task KeepColumnLoaded(BlockPos pos)
	{
		World.Api.WorldManager.LoadChunkColumnPriority(
			pos.X / 32,
			pos.Z / 32,
			new ChunkLoadOptions { KeepLoaded = true });
		await World.Until(
			() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(pos) != null,
			timeoutTicks: 600);
	}

	/// <summary>
	/// Puts a counted block entity at <paramref name="pos"/>. "Generic" is VSEssentials'
	/// do-nothing block entity class, so the probe counts deliveries without any vanilla
	/// handler deciding to ignore the packet first.
	/// </summary>
	private BlockEntityPacketProbe PlaceProbeBlockEntity(BlockPos pos)
	{
		World.SetBlock("game:rock-granite", pos);
		World.Api.World.BlockAccessor.SpawnBlockEntity("Generic", pos);
		BlockEntity? be = World.Api.World.BlockAccessor.GetBlockEntity(pos);
		Assert.True(be != null, $"no block entity was spawned at {pos}; setup is invalid");

		var probe = new BlockEntityPacketProbe(be!);
		be!.Behaviors.Add(probe);
		return probe;
	}

	/// <summary>
	/// Holds the probe at an exact distance from the player and reports whether it survived
	/// <paramref name="ticks"/> ticks in the client's tracked set. Re-pinning every tick keeps
	/// the geometry exact: the probe is in mid air over whatever terrain the far column has, and
	/// the player can drift a little too.
	/// </summary>
	private async Task<bool> StaysTracked(
		ITestPlayer player, Entity probe, object client, double distance, int ticks)
	{
		for (int i = 0; i < ticks; i++)
		{
			EntityPos anchorPos = player.Entity.Pos;
			probe.Pos.SetPos(anchorPos.X + distance, anchorPos.Y, anchorPos.Z);
			probe.Pos.Motion.Set(0, 0, 0);

			await World.Ticks(1);

			if (!TrackedEntities(client).Contains(probe.EntityId))
			{
				return false;
			}
		}

		return true;
	}

	private void DispatchEntityPacket(ITestPlayer player, long entityId)
	{
		Type innerType = Type.GetType("Packet_EntityPacket, VintagestoryLib")!;
		dynamic inner = Activator.CreateInstance(innerType)!;
		inner.EntityId = entityId;
		inner.Packetid = ProbePacketId;
		inner.Data = new byte[0];

		dynamic packet = NewClientPacket(EntityPacketClientId);
		packet.EntityPacket = inner;
		Dispatch(player, (object)packet);
	}

	private void DispatchBlockEntityPacket(ITestPlayer player, BlockPos pos)
	{
		Type innerType = Type.GetType("Packet_BlockEntityPacket, VintagestoryLib")!;
		dynamic inner = Activator.CreateInstance(innerType)!;
		inner.X = pos.X;
		inner.Y = pos.Y;
		inner.Z = pos.Z;
		inner.Packetid = ProbePacketId;
		inner.Data = new byte[0];

		dynamic packet = NewClientPacket(BlockEntityPacketClientId);
		packet.BlockEntityPacket = inner;
		Dispatch(player, (object)packet);
	}

	private static dynamic NewClientPacket(int id)
	{
		Type packetType = Type.GetType("Packet_Client, VintagestoryLib")!;
		dynamic packet = Activator.CreateInstance(packetType)!;
		packet.Id = id;
		return packet;
	}

	/// <summary>
	/// Hands the packet to the server exactly where the network thread would, so the real handler
	/// chain runs: packet limiter, handler table, then the guard under test. IsDedicatedServer is
	/// left alone on purpose, so a throw inside a handler surfaces as a failure rather than as a
	/// kicked player.
	/// </summary>
	private void Dispatch(ITestPlayer player, object packet)
	{
		object server = World.Api.World;
		Type receivedType = Type.GetType("Vintagestory.Server.ReceivedClientPacket, VintagestoryLib")!;
		object received = Activator.CreateInstance(receivedType, ClientOf(player), packet, 1)!;

		MethodInfo dispatch = server.GetType().GetMethod(
			"DispatchClientPacket_mainthread", BindingFlags.Instance | BindingFlags.NonPublic)!;
		dispatch.Invoke(server, new[] { received });
	}

	/// <summary>
	/// Calls the dev command block gate directly. The packet path around it (packet id 24112,
	/// handled before the block entity target lookup) adds nothing this scenario can read back,
	/// because a refused use and a use on a block that is not a command block both end the same
	/// way: silently.
	/// </summary>
	private bool InvokeDevCommandBlockUse(ITestPlayer player, BlockPos pos)
	{
		object blockSimulation = ServerSystem("ServerSystemBlockSimulation");
		MethodInfo method = blockSimulation.GetType().GetMethod(
			"TryAllowDevCommandBlockUse", BindingFlags.Instance | BindingFlags.NonPublic)!;
		return (bool)method.Invoke(blockSimulation, new[] { pos, ClientOf(player) })!;
	}

	private object? ClientListRef()
	{
		object physicsManager = PhysicsManager();
		FieldInfo field = physicsManager.GetType().GetField(
			"ClientList", BindingFlags.Instance | BindingFlags.NonPublic)!;
		return field.GetValue(physicsManager);
	}

	private object PhysicsManager()
	{
		object entitySimulation = ServerSystem("ServerSystemEntitySimulation");
		FieldInfo field = entitySimulation.GetType().GetField(
			"physicsManager", BindingFlags.Instance | BindingFlags.NonPublic)!;
		return field.GetValue(entitySimulation)!;
	}

	private int TrackingRangeSq()
	{
		object entitySimulation = ServerSystem("ServerSystemEntitySimulation");
		FieldInfo field = entitySimulation.GetType().GetField(
			"trackingRangeSq", BindingFlags.Instance | BindingFlags.NonPublic)!;
		return (int)field.GetValue(entitySimulation)!;
	}

	private object ServerSystem(string typeName)
	{
		object server = World.Api.World;
		FieldInfo field = server.GetType().GetField(
			"Systems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
		foreach (object system in (Array)field.GetValue(server)!)
		{
			if (system.GetType().Name == typeName)
			{
				return system;
			}
		}

		throw new InvalidOperationException($"{typeName} is not among the server's systems");
	}

	private object ClientOf(ITestPlayer player)
	{
		object server = World.Api.World;
		dynamic clients = server.GetType().GetField("Clients")!.GetValue(server)!;
		return clients[player.Player.ClientId];
	}

	private static HashSet<long> TrackedEntities(object client)
	{
		return (HashSet<long>)client.GetType().GetField("TrackedEntities")!.GetValue(client)!;
	}

	/// <summary>Counts entity packets that reached the entity, past ServerMain's range guard.</summary>
	private sealed class EntityPacketProbe : EntityBehavior
	{
		public int Deliveries;

		public EntityPacketProbe(Entity entity)
			: base(entity)
		{
		}

		public override void OnReceivedClientPacket(
			IServerPlayer player, int packetid, byte[] data, ref EnumHandling handled)
		{
			if (packetid == ProbePacketId)
			{
				Deliveries++;
			}
		}

		public override string PropertyName() => "stratum:entitypacketprobe";
	}

	/// <summary>Counts block entity packets that reached the block entity, past the block guard.</summary>
	private sealed class BlockEntityPacketProbe : BlockEntityBehavior
	{
		public int Deliveries;

		public BlockEntityPacketProbe(BlockEntity blockentity)
			: base(blockentity)
		{
		}

		public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
		{
			if (packetid == ProbePacketId)
			{
				Deliveries++;
			}
		}
	}
}
