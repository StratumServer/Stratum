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
		CollectibleBehavior[] originalBehaviors = pickaxe.CollectibleBehaviors;
		var faultyBehavior = new FaultyDamageBehavior(pickaxe);
		int durabilityBefore = pickaxe.GetRemainingDurability(activeSlot.Itemstack);
		try
		{
			pickaxe.CollectibleBehaviors = pickaxe.CollectibleBehaviors.Append(faultyBehavior).ToArray();

			object packet = CreateBlockBreakPacket(blockPos);
			DispatchPacket(World, player, packet);
			await World.Ticks(5);

			Assert.True(faultyBehavior.DamageInvoked, "FaultyDamageBehavior.OnDamageItem was not invoked");
			Assert.True(player.IsConnected, "player was disconnected after collectible behavior threw an exception");
			Assert.Equal("game:air", World.BlockAt(blockPos).Code.ToString());
			Assert.NotNull(activeSlot.Itemstack);
			Assert.Equal(durabilityBefore - 1, pickaxe.GetRemainingDurability(activeSlot.Itemstack));
		}
		finally
		{
			pickaxe.CollectibleBehaviors = originalBehaviors;
		}
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
		CollectibleBehavior[] originalBehaviors = pickaxe.CollectibleBehaviors;
		var faultyBehavior = new FaultyBrokenWithBehavior(pickaxe, EnumHandling.PassThrough);
		try
		{
			pickaxe.CollectibleBehaviors = pickaxe.CollectibleBehaviors.Append(faultyBehavior).ToArray();

			object packet = CreateBlockBreakPacket(blockPos);
			DispatchPacket(World, player, packet);
			await World.Ticks(5);

			Assert.True(faultyBehavior.BrokenWithInvoked, "FaultyBrokenWithBehavior.OnBlockBrokenWith was not invoked");
			Assert.True(player.IsConnected, "player was disconnected after OnBlockBrokenWith behavior threw an exception");
			Assert.Equal("game:air", World.BlockAt(blockPos).Code.ToString());
		}
		finally
		{
			pickaxe.CollectibleBehaviors = originalBehaviors;
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_PreserveBreakVeto_When_CollectibleBehaviorThrowsAfterVeto()
	{
		ITestPlayer player = await World.JoinPlayer("brk-veto");
		BlockPos playerPos = World.Spawn.AddCopy(6, 1, 6);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());

		await player.GiveItem("game:pickaxe-iron", 1);
		ItemSlot activeSlot = player.Player.InventoryManager.ActiveHotbarSlot;
		Assert.NotNull(activeSlot.Itemstack);

		CollectibleObject pickaxe = activeSlot.Itemstack.Collectible;
		int durabilityBefore = pickaxe.GetRemainingDurability(activeSlot.Itemstack);
		CollectibleBehavior[] originalBehaviors = pickaxe.CollectibleBehaviors;
		FaultyBrokenWithBehavior faultyVetoBehavior = new FaultyBrokenWithBehavior(pickaxe, EnumHandling.PreventDefault);
		SentinelBrokenWithBehavior sentinelBehavior = new SentinelBrokenWithBehavior(pickaxe);
		try
		{
			pickaxe.CollectibleBehaviors = originalBehaviors.Append(faultyVetoBehavior).Append(sentinelBehavior).ToArray();

			object packet = CreateBlockBreakPacket(blockPos);
			DispatchPacket(World, player, packet);
			await World.Ticks(5);

			Assert.True(faultyVetoBehavior.BrokenWithInvoked, "FaultyBrokenWithBehavior was not invoked");
			Assert.True(player.IsConnected, "player was disconnected");
			Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());
			Assert.True(sentinelBehavior.BrokenWithInvoked, "PreventDefault should not stop subsequent behaviors");
			Assert.NotNull(activeSlot.Itemstack);
			Assert.Equal(durabilityBefore, pickaxe.GetRemainingDurability(activeSlot.Itemstack));
		}
		finally
		{
			pickaxe.CollectibleBehaviors = originalBehaviors;
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_ClearToolSlotWhenFailedDamageBehaviorReachesZeroDurability()
	{
		ITestPlayer player = await World.JoinPlayer("brk-damage-zero");
		BlockPos playerPos = World.Spawn.AddCopy(12, 1, 12);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());

		await player.GiveItem("game:pickaxe-iron", 1);
		ItemSlot activeSlot = player.Player.InventoryManager.ActiveHotbarSlot;
		Assert.NotNull(activeSlot.Itemstack);

		CollectibleObject pickaxe = activeSlot.Itemstack.Collectible;
		pickaxe.SetDurability(activeSlot.Itemstack, 1);
		CollectibleBehavior[] originalBehaviors = pickaxe.CollectibleBehaviors;
		FaultyDamageBehavior faultyBehavior = new FaultyDamageBehavior(pickaxe);
		try
		{
			pickaxe.CollectibleBehaviors = pickaxe.CollectibleBehaviors.Append(faultyBehavior).ToArray();

			DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
			await World.Ticks(5);

			Assert.True(faultyBehavior.DamageInvoked, "FaultyDamageBehavior.OnDamageItem was not invoked");
			Assert.True(player.IsConnected, "player was disconnected after collectible behavior threw an exception");
			Assert.Null(activeSlot.Itemstack);
		}
		finally
		{
			pickaxe.CollectibleBehaviors = originalBehaviors;
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_StopFollowingCollectibleBehaviorsAfterFailedPreventSubsequentVeto()
	{
		ITestPlayer player = await World.JoinPlayer("brk-veto-subseq");
		BlockPos playerPos = World.Spawn.AddCopy(14, 1, 14);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());

		await player.GiveItem("game:pickaxe-iron", 1);
		ItemSlot activeSlot = player.Player.InventoryManager.ActiveHotbarSlot;
		Assert.NotNull(activeSlot.Itemstack);

		CollectibleObject pickaxe = activeSlot.Itemstack.Collectible;
		int durabilityBefore = pickaxe.GetRemainingDurability(activeSlot.Itemstack);
		CollectibleBehavior[] originalBehaviors = pickaxe.CollectibleBehaviors;
		FaultyBrokenWithBehavior faultyVetoBehavior = new FaultyBrokenWithBehavior(pickaxe, EnumHandling.PreventSubsequent);
		SentinelBrokenWithBehavior sentinelBehavior = new SentinelBrokenWithBehavior(pickaxe);
		try
		{
			pickaxe.CollectibleBehaviors = originalBehaviors.Append(faultyVetoBehavior).Append(sentinelBehavior).ToArray();

			DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
			await World.Ticks(5);

			Assert.True(faultyVetoBehavior.BrokenWithInvoked, "faulty veto behavior was not invoked");
			Assert.False(sentinelBehavior.BrokenWithInvoked, "PreventSubsequent did not stop the next behavior");
			Assert.True(player.IsConnected, "player was disconnected after OnBlockBrokenWith behavior threw an exception");
			Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());
			Assert.NotNull(activeSlot.Itemstack);
			Assert.Equal(durabilityBefore, pickaxe.GetRemainingDurability(activeSlot.Itemstack));
		}
		finally
		{
			pickaxe.CollectibleBehaviors = originalBehaviors;
		}
	}
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_NotRetryAfterSolidLayerWasRemoved()
	{
		ITestPlayer player = await World.JoinPlayer("brk-fluid-layer");
		BlockPos playerPos = World.Spawn.AddCopy(16, 1, 16);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());

		Block block = World.BlockAt(blockPos);
		Block water = World.Api.World.GetBlock(new AssetLocation("game:water-still-7"))!;
		int fluidBlockId = water.BlockId;
		World.Api.World.BlockAccessor.SetBlock(fluidBlockId, blockPos, BlockLayersAccess.Fluid);
		BlockBehavior[] originalBehaviors = block.BlockBehaviors;
		var faultyBehavior = new RemoveSolidThenThrowBehavior(block);
		try
		{
			block.BlockBehaviors = block.BlockBehaviors.Append(faultyBehavior).ToArray();

			DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
			Assert.Equal(1, faultyBehavior.InvocationCount);
			Assert.Equal(0, World.Api.World.BlockAccessor.GetBlock(blockPos, BlockLayersAccess.Solid).BlockId);
			Assert.Equal(fluidBlockId, World.Api.World.BlockAccessor.GetBlock(blockPos, BlockLayersAccess.Fluid).BlockId);
			await World.Ticks(5);

			Assert.True(player.IsConnected, "player was disconnected after block behavior threw an exception");
		}
		finally
		{
			block.BlockBehaviors = originalBehaviors;
		}
	}


	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_ContainRepeatedBlockBehaviorException()
	{
		ITestPlayer player = await World.JoinPlayer("brk-fallback");
		BlockPos playerPos = World.Spawn.AddCopy(8, 1, 8);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());

		Block block = World.BlockAt(blockPos);
		BlockBehavior[] originalBehaviors = block.BlockBehaviors;
		var faultyBehavior = new FaultyBlockBehavior(block);
		try
		{
			block.BlockBehaviors = block.BlockBehaviors.Append(faultyBehavior).ToArray();

			object packet = CreateBlockBreakPacket(blockPos);
			DispatchPacket(World, player, packet);
			await World.Ticks(5);

			Assert.True(player.IsConnected, "player was disconnected after block behavior threw an exception");
			Assert.Equal(2, faultyBehavior.InvocationCount);
			Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());
		}
		finally
		{
			block.BlockBehaviors = originalBehaviors;
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_CompleteFallbackAfterOneShotBlockBehaviorException()
	{
		ITestPlayer player = await World.JoinPlayer("brk-fallback1");
		BlockPos playerPos = World.Spawn.AddCopy(10, 1, 10);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());

		Block block = World.BlockAt(blockPos);
		BlockBehavior[] originalBehaviors = block.BlockBehaviors;
		var faultyBehavior = new OneShotBlockBehavior(block);
		try
		{
			block.BlockBehaviors = block.BlockBehaviors.Append(faultyBehavior).ToArray();

			object packet = CreateBlockBreakPacket(blockPos);
			DispatchPacket(World, player, packet);
			await World.Ticks(5);

			Assert.True(player.IsConnected, "player was disconnected after block behavior threw an exception");
			Assert.Equal(2, faultyBehavior.InvocationCount);
			Assert.Equal("game:air", World.BlockAt(blockPos).Code.ToString());
		}
		finally
		{
			block.BlockBehaviors = originalBehaviors;
		}
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
		FieldInfo isDedicatedField = server.GetType().GetField("<IsDedicatedServer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Assert.NotNull(isDedicatedField);
		isDedicatedField.SetValue(server, true);

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
			bhHandling = EnumHandling.PreventDefault;
			throw new NullReferenceException("Simulated Toolsmith NRE during item damage");
		}
	}

	private sealed class FaultyBrokenWithBehavior : CollectibleBehavior
	{
		private readonly EnumHandling _handlingBeforeThrow;
		public bool BrokenWithInvoked { get; private set; }

		public FaultyBrokenWithBehavior(CollectibleObject collObj, EnumHandling handlingBeforeThrow = EnumHandling.PassThrough) : base(collObj)
		{
			_handlingBeforeThrow = handlingBeforeThrow;
		}

		public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier, ref EnumHandling bhHandling)
		{
			BrokenWithInvoked = true;
			bhHandling = _handlingBeforeThrow;
			throw new InvalidOperationException("Simulated external mod exception during OnBlockBrokenWith");
		}
	}

	private sealed class SentinelBrokenWithBehavior : CollectibleBehavior
	{
		public bool BrokenWithInvoked { get; private set; }

		public SentinelBrokenWithBehavior(CollectibleObject collObj) : base(collObj)
		{
		}

		public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier, ref EnumHandling bhHandling)
		{
			BrokenWithInvoked = true;
			bhHandling = EnumHandling.PassThrough;
			return true;
		}
	}

	private sealed class RemoveSolidThenThrowBehavior : BlockBehavior
	{
		public int InvocationCount { get; private set; }

		public RemoveSolidThenThrowBehavior(Block block) : base(block)
		{
		}

		public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier, ref EnumHandling handling)
		{
			InvocationCount++;
			world.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Solid);
			throw new InvalidOperationException("Simulated failure after solid-layer removal");
		}
	}
	private sealed class FaultyBlockBehavior : BlockBehavior
	{
		public int InvocationCount { get; private set; }

		public FaultyBlockBehavior(Block block) : base(block)
		{
		}

		public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier, ref EnumHandling handling)
		{
			InvocationCount++;
			throw new InvalidOperationException("Simulated deterministic block break failure");
		}
	}

	private sealed class OneShotBlockBehavior : BlockBehavior
	{
		public int InvocationCount { get; private set; }

		public OneShotBlockBehavior(Block block) : base(block)
		{
		}

		public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier, ref EnumHandling handling)
		{
			InvocationCount++;
			if (InvocationCount == 1) throw new InvalidOperationException("Simulated recoverable block break failure");
			handling = EnumHandling.PassThrough;
		}
	}
}
