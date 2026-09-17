using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Regression scenarios for issue #282: unhandled exceptions during item damage or block breaking
/// must not disconnect the player on dedicated servers.
/// </summary>
public class ItemBreakResilienceScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_NotDisconnectPlayer_When_CollectibleBehaviorThrowsOnDamage()
	{
		ITestPlayer player = await World.JoinPlayer("brk-damage");
		BlockPos playerPos = World.Spawn.AddCopy(2, 1, 2);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());

		await player.GiveItem("game:pickaxe-iron", 1);
		ItemSlot activeSlot = player.Player.InventoryManager.ActiveHotbarSlot;
		Assert.NotNull(activeSlot.Itemstack);

		CollectibleObject pickaxe = activeSlot.Itemstack.Collectible;
		var faultyBehavior = new FaultyDamageBehavior(pickaxe);
		pickaxe.CollectibleBehaviors = pickaxe.CollectibleBehaviors.Append(faultyBehavior).ToArray();

		object packet = CreateBlockBreakPacket(blockPos);
		DispatchPacket(World, player, packet);
		await World.Ticks(5);

		Assert.True(faultyBehavior.DamageInvoked, "FaultyDamageBehavior.OnDamageItem was not invoked");
		Assert.True(player.IsConnected, "player was disconnected after collectible behavior threw an exception");
		Assert.Equal("game:air", World.BlockAt(blockPos).Code.ToString());
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_NotDisconnectPlayer_When_CollectibleBehaviorThrowsOnBlockBrokenWith()
	{
		ITestPlayer player = await World.JoinPlayer("brk-block");
		BlockPos playerPos = World.Spawn.AddCopy(4, 1, 4);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());

		await player.GiveItem("game:pickaxe-iron", 1);
		ItemSlot activeSlot = player.Player.InventoryManager.ActiveHotbarSlot;
		Assert.NotNull(activeSlot.Itemstack);

		CollectibleObject pickaxe = activeSlot.Itemstack.Collectible;
		var faultyBehavior = new FaultyBrokenWithBehavior(pickaxe);
		pickaxe.CollectibleBehaviors = pickaxe.CollectibleBehaviors.Append(faultyBehavior).ToArray();

		object packet = CreateBlockBreakPacket(blockPos);
		DispatchPacket(World, player, packet);
		await World.Ticks(5);

		Assert.True(faultyBehavior.BrokenWithInvoked, "FaultyBrokenWithBehavior.OnBlockBrokenWith was not invoked");
		Assert.True(player.IsConnected, "player was disconnected after OnBlockBrokenWith behavior threw an exception");
		Assert.Equal("game:air", World.BlockAt(blockPos).Code.ToString());
	}

	private static object CreateBlockBreakPacket(BlockPos pos)
	{
		Type packetClientType = Type.GetType("Packet_Client, VintagestoryLib")!;
		Type breakType = Type.GetType("Packet_ClientBlockPlaceOrBreak, VintagestoryLib")!;

		dynamic breakPacket = Activator.CreateInstance(breakType)!;
		breakPacket.Mode = 0;
		breakPacket.X = pos.X;
		breakPacket.Y = pos.Y;
		breakPacket.Z = pos.Z;
		breakPacket.OnBlockFace = (int)BlockFacing.UP.Index;

		dynamic packet = Activator.CreateInstance(packetClientType)!;
		packet.Id = 3;
		packet.BlockPlaceOrBreak = breakPacket;

		return packet;
	}

	private static void DispatchPacket(IWorldSession world, ITestPlayer player, object packet)
	{
		object server = world.Api.World;
		FieldInfo? isDedicatedField = server.GetType().GetField("<IsDedicatedServer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
		isDedicatedField?.SetValue(server, true);

		dynamic clients = server.GetType().GetField("Clients")!.GetValue(server)!;
		object client = clients[player.Player.ClientId];

		Type rcpType = Type.GetType("Vintagestory.Server.ReceivedClientPacket, VintagestoryLib")!;
		object receivedPacket = Activator.CreateInstance(rcpType, client, packet, 1)!;

		MethodInfo dispatchMethod = server.GetType().GetMethod("DispatchClientPacket_mainthread", BindingFlags.Instance | BindingFlags.NonPublic)!;
		dispatchMethod.Invoke(server, new[] { receivedPacket });
	}

	private sealed class FaultyDamageBehavior : CollectibleBehavior
	{
		public bool DamageInvoked { get; private set; }

		public FaultyDamageBehavior(CollectibleObject collObj) : base(collObj)
		{
		}

		public override void OnDamageItem(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, ref int amount, ref EnumHandling bhHandling)
		{
			DamageInvoked = true;
			throw new NullReferenceException("Simulated Toolsmith NRE during item damage");
		}
	}

	private sealed class FaultyBrokenWithBehavior : CollectibleBehavior
	{
		public bool BrokenWithInvoked { get; private set; }

		public FaultyBrokenWithBehavior(CollectibleObject collObj) : base(collObj)
		{
		}

		public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier, ref EnumHandling bhHandling)
		{
			BrokenWithInvoked = true;
			throw new InvalidOperationException("Simulated external mod exception during OnBlockBrokenWith");
		}
	}
}
