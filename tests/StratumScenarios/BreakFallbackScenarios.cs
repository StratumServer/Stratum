using System;
using System.Linq;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The break fallback still runs after a one-shot block behavior throws, and a
/// short failure cap does not ban a later creative break of the same block.
/// </summary>
public class BreakFallbackScenarios : AtlasScenarioBase
{
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
		await player.GiveItem("game:pickaxe-steel", 1);
		ItemSlot activeSlot = player.Player.InventoryManager.ActiveHotbarSlot;
		Assert.NotNull(activeSlot.Itemstack);
		CollectibleObject pickaxe = activeSlot.Itemstack.Collectible;
		int durabilityBefore = pickaxe.GetRemainingDurability(activeSlot.Itemstack);

		Block block = World.BlockAt(blockPos);
		BlockBehavior[] originalBehaviors = block.BlockBehaviors;
		var faultyBehavior = new OneShotBlockBehavior(block);
		try
		{
			block.BlockBehaviors = block.BlockBehaviors.Append(faultyBehavior).ToArray();

			DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
			await World.Ticks(5);

			Assert.True(player.IsConnected, "player was disconnected after block behavior threw an exception");
			Assert.Equal(2, faultyBehavior.InvocationCount);
			Assert.Equal("game:air", World.BlockAt(blockPos).Code.ToString());
			Assert.NotNull(activeSlot.Itemstack);
			Assert.Equal(durabilityBefore - 1, pickaxe.GetRemainingDurability(activeSlot.Itemstack));
		}
		finally
		{
			block.BlockBehaviors = originalBehaviors;
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_FallbackWhenSelectedBlockIsInFluidLayer()
	{
		ITestPlayer player = await World.JoinPlayer("brk-fluid-fb");
		BlockPos playerPos = World.Spawn.AddCopy(24, 1, 24);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:air", blockPos);
		Block fluidBlock = World.Api.World.GetBlock(new AssetLocation("game:lakeice"))!;
		Assert.True(fluidBlock.ForFluidsLayer && fluidBlock.SideSolid.Any, "lake ice must be selected from the fluid layer when no solid block is present");
		World.Api.World.BlockAccessor.SetBlock(fluidBlock.BlockId, blockPos, BlockLayersAccess.Fluid);
		await World.Ticks(5);
		Assert.Equal(0, World.Api.World.BlockAccessor.GetBlock(blockPos, BlockLayersAccess.Solid).BlockId);
		Assert.Equal(fluidBlock.BlockId, World.Api.World.BlockAccessor.GetBlock(blockPos, BlockLayersAccess.Fluid).BlockId);

		BlockBehavior[] originalBehaviors = fluidBlock.BlockBehaviors;
		var faultyBehavior = new OneShotBlockBehavior(fluidBlock);
		try
		{
			fluidBlock.BlockBehaviors = originalBehaviors.Append(faultyBehavior).ToArray();
			DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
			await World.Ticks(5);

			Assert.True(player.IsConnected, "player was disconnected after a fluid-layer behavior threw");
			Assert.Equal(2, faultyBehavior.InvocationCount);
			Block fluidAfterBreak = World.Api.World.BlockAccessor.GetBlock(blockPos, BlockLayersAccess.Fluid);
			Assert.NotEqual(fluidBlock.BlockId, fluidAfterBreak.BlockId);
			Assert.True(fluidAfterBreak.IsLiquid(), "breaking lake ice should restore liquid water in the fluid layer");
		}
		finally
		{
			fluidBlock.BlockBehaviors = originalBehaviors;
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task CreativeBreak_Should_Succeed_After_SurvivalFailuresAreCapped()
	{
		ITestPlayer player = await World.JoinPlayer("brk-creative-cap");
		BlockPos playerPos = World.Spawn.AddCopy(28, 1, 12);
		await player.TeleportTo(playerPos);
		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		await player.GiveItem("game:pickaxe-steel", 1);
		EnumGameMode originalGameMode = player.Player.WorldData.CurrentGameMode;
		player.Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
		Block block = World.BlockAt(blockPos);
		BlockBehavior[] original = block.BlockBehaviors;
		var failureBehavior = new SurvivalOnlyBreakBehavior(block);
		bool guardEnabled = SetBlockBreakGuardEnabled(false);
		try
		{
			block.BlockBehaviors = original.Append(failureBehavior).ToArray();
			for (int i = 0; i < 3; i++)
			{
				DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
				await World.Ticks(2);
			}

			Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());
			Assert.Equal(6, failureBehavior.OnBlockBrokenInvocationCount);
			Assert.Equal(0, failureBehavior.GetDropsInvocationCount);
			player.Player.WorldData.CurrentGameMode = EnumGameMode.Creative;
			DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
			await World.Ticks(5);
			Assert.Equal("game:air", World.BlockAt(blockPos).Code.ToString());
		}
		finally
		{
			player.Player.WorldData.CurrentGameMode = originalGameMode;
			block.BlockBehaviors = original;
			SetBlockBreakGuardEnabled(guardEnabled);
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_CapRepeatedGetDropsFailures()
	{
		ITestPlayer player = await World.JoinPlayer("brk-getdrops-cap");
		BlockPos playerPos = World.Spawn.AddCopy(34, 1, 24);
		await player.TeleportTo(playerPos);
		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		await player.GiveItem("game:pickaxe-steel", 1);
		EnumGameMode originalGameMode = player.Player.WorldData.CurrentGameMode;
		player.Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
		Block block = World.BlockAt(blockPos);
		BlockBehavior[] original = block.BlockBehaviors;
		var failureBehavior = new ThrowingGetDropsBehavior(block);
		bool guardEnabled = SetBlockBreakGuardEnabled(false);
		try
		{
			block.BlockBehaviors = original.Append(failureBehavior).ToArray();
			for (int i = 0; i < 4; i++)
			{
				DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
				await World.Ticks(2);
			}

			Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());
			Assert.Equal(6, failureBehavior.InvocationCount);
		}
		finally
		{
			player.Player.WorldData.CurrentGameMode = originalGameMode;
			block.BlockBehaviors = original;
			SetBlockBreakGuardEnabled(guardEnabled);
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_ExpireAndSweepInactiveFailureEntries()
	{
		ITestPlayer player = await World.JoinPlayer("brk-fail-sweep");
		BlockPos playerPos = World.Spawn.AddCopy(40, 1, 24);
		await player.TeleportTo(playerPos);
		BlockPos firstPos = playerPos.AddCopy(1, 0, 0);
		BlockPos secondPos = playerPos.AddCopy(3, 0, 0);
		World.SetBlock("game:rock-granite", firstPos);
		World.SetBlock("game:rock-granite", secondPos);
		await World.Ticks(5);
		await player.GiveItem("game:pickaxe-steel", 1);
		EnumGameMode originalGameMode = player.Player.WorldData.CurrentGameMode;
		player.Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
		Block firstBlock = World.BlockAt(firstPos);
		BlockBehavior[] firstOriginal = firstBlock.BlockBehaviors;
		var failureBehavior = new CappedBlockBehavior(firstBlock);
		bool guardEnabled = SetBlockBreakGuardEnabled(false);
		try
		{
			firstBlock.BlockBehaviors = firstOriginal.Append(failureBehavior).ToArray();
			for (int i = 0; i < 4; i++)
			{
				DispatchPacket(World, player, CreateBlockBreakPacket(firstPos));
				await World.Ticks(2);
			}

			Assert.Equal(6, failureBehavior.InvocationCount);
			await Task.Delay(6500);
			await World.Ticks(120);
			object blockSimulation = GetBlockSimulationSystem();
			Assert.Equal(0, GetPrivateCollectionCount(blockSimulation, "stratumFailedBreakAttempts"));
			Assert.Equal(0, GetPrivateCollectionCount(blockSimulation, "stratumLoggedBreakRefusals"));

			DispatchPacket(World, player, CreateBlockBreakPacket(secondPos));
			await World.Ticks(2);
			Assert.Equal(8, failureBehavior.InvocationCount);
			Assert.Equal(1, GetPrivateCollectionCount(blockSimulation, "stratumFailedBreakAttempts"));
			Assert.Equal(0, GetPrivateCollectionCount(blockSimulation, "stratumLoggedBreakRefusals"));

			DispatchPacket(World, player, CreateBlockBreakPacket(firstPos));
			await World.Ticks(2);
			Assert.Equal(10, failureBehavior.InvocationCount);
		}
		finally
		{
			player.Player.WorldData.CurrentGameMode = originalGameMode;
			firstBlock.BlockBehaviors = firstOriginal;
			SetBlockBreakGuardEnabled(guardEnabled);
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
		isDedicatedField.SetValue(server, true);

		dynamic clients = server.GetType().GetField("Clients")!.GetValue(server)!;
		object client = clients[player.Player.ClientId];

		Type rcpType = Type.GetType("Vintagestory.Server.ReceivedClientPacket, VintagestoryLib")!;
		object receivedPacket = Activator.CreateInstance(rcpType, client, packet, 1)!;

		MethodInfo dispatchMethod = server.GetType().GetMethod("DispatchClientPacket_mainthread", BindingFlags.Instance | BindingFlags.NonPublic)!;
		dispatchMethod.Invoke(server, new[] { receivedPacket });
	}

	private static bool SetBlockBreakGuardEnabled(bool enabled)
	{
		Type runtimeType = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib", throwOnError: true)!;
		object config = runtimeType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
		object hardening = config.GetType().GetProperty("Hardening")!.GetValue(config)!;
		PropertyInfo guardProperty = hardening.GetType().GetProperty("BlockBreakGuards")!;
		bool previous = (bool)guardProperty.GetValue(hardening)!;
		guardProperty.SetValue(hardening, enabled);
		return previous;
	}

	private object GetBlockSimulationSystem()
	{
		object server = World.Api.World;
		FieldInfo systemsField = server.GetType().GetField("Systems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
		Array systems = (Array)systemsField.GetValue(server)!;
		foreach (object system in systems)
		{
			if (system.GetType().Name == "ServerSystemBlockSimulation") return system;
		}

		throw new InvalidOperationException("ServerSystemBlockSimulation was not found");
	}

	private static int GetPrivateCollectionCount(object instance, string fieldName)
	{
		FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
		object collection = field.GetValue(instance)!;
		return (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;
	}

	private sealed class CappedBlockBehavior : BlockBehavior
	{
		public int InvocationCount { get; private set; }

		public CappedBlockBehavior(Block block) : base(block)
		{
		}

		public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier, ref EnumHandling handling)
		{
			InvocationCount++;
			throw new InvalidOperationException("Simulated capped block break failure");
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

	private sealed class SurvivalOnlyBreakBehavior : BlockBehavior
	{
		public int OnBlockBrokenInvocationCount { get; private set; }
		public int GetDropsInvocationCount { get; private set; }

		public SurvivalOnlyBreakBehavior(Block block) : base(block)
		{
		}

		public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier, ref EnumHandling handling)
		{
			OnBlockBrokenInvocationCount++;
			if (byPlayer?.WorldData?.CurrentGameMode != EnumGameMode.Creative)
			{
				throw new InvalidOperationException("Simulated survival break failure");
			}
		}

		public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, ref float dropChanceMultiplier, ref EnumHandling handling)
		{
			GetDropsInvocationCount++;
			throw new InvalidOperationException("Simulated GetDrops failure");
		}
	}

	private sealed class ThrowingGetDropsBehavior : BlockBehavior
	{
		public int InvocationCount { get; private set; }

		public ThrowingGetDropsBehavior(Block block) : base(block)
		{
		}

		public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, ref float dropChanceMultiplier, ref EnumHandling handling)
		{
			InvocationCount++;
			throw new InvalidOperationException("Simulated GetDrops failure");
		}
	}
}
