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
		player.Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
		Block block = World.BlockAt(blockPos);
		BlockBehavior[] original = block.BlockBehaviors;
		try
		{
			block.BlockBehaviors = original.Append(new SurvivalOnlyBreakBehavior(block)).ToArray();
			for (int i = 0; i < 3; i++)
			{
				DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
				await World.Ticks(2);
			}

			Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());
			player.Player.WorldData.CurrentGameMode = EnumGameMode.Creative;
			DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
			await World.Ticks(5);
			Assert.Equal("game:air", World.BlockAt(blockPos).Code.ToString());
		}
		finally
		{
			player.Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
			block.BlockBehaviors = original;
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
		public SurvivalOnlyBreakBehavior(Block block) : base(block)
		{
		}

		public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier, ref EnumHandling handling)
		{
			if (byPlayer?.WorldData?.CurrentGameMode != EnumGameMode.Creative)
			{
				throw new InvalidOperationException("Simulated survival break failure");
			}
		}

		public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, ref float dropChanceMultiplier, ref EnumHandling handling)
		{
			throw new InvalidOperationException("Simulated GetDrops failure");
		}
	}
}
