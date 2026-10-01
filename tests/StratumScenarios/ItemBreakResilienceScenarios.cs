using System;
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
		int didBreakCount = 0;
		BlockBrokenDelegate didBreakHandler = (_, _, _) => didBreakCount++;
		World.Api.Event.DidBreakBlock += didBreakHandler;
		try
		{
			pickaxe.CollectibleBehaviors = originalBehaviors.Append(faultyVetoBehavior).Append(sentinelBehavior).ToArray();

			object packet = CreateBlockBreakPacket(blockPos);
			DispatchPacket(World, player, packet);
			await World.Ticks(5);

			Assert.True(faultyVetoBehavior.BrokenWithInvoked, "FaultyBrokenWithBehavior was not invoked");
			Assert.True(player.IsConnected, "player was disconnected");
			Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());
			Assert.Equal(0, didBreakCount);
			Assert.True(sentinelBehavior.BrokenWithInvoked, "PreventDefault should not stop subsequent behaviors");
			Assert.NotNull(activeSlot.Itemstack);
			Assert.Equal(durabilityBefore, pickaxe.GetRemainingDurability(activeSlot.Itemstack));
		}
		finally
		{
			World.Api.Event.DidBreakBlock -= didBreakHandler;
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
		ILogger logger = GetServerLogger();
		int loggedBreakFailures = 0;
		var loggedMessages = new List<string>();
		int didBreakCount = 0;
		BlockBrokenDelegate didBreakHandler = (_, _, _) => didBreakCount++;
		World.Api.Event.DidBreakBlock += didBreakHandler;
		LogEntryDelegate logEntry = (logType, message, _) =>
		{
			if (logType == EnumLogType.Error) loggedMessages.Add(message);
			if (logType == EnumLogType.Error &&
				message.StartsWith("Exception thrown during {0}", StringComparison.Ordinal))
			{
				loggedBreakFailures++;
			}
		};
		logger.EntryAdded += logEntry;
		try
		{
			block.BlockBehaviors = block.BlockBehaviors.Append(faultyBehavior).ToArray();

			object packet = CreateBlockBreakPacket(blockPos);
			DispatchPacket(World, player, packet);
			await World.Ticks(5);

			Assert.True(player.IsConnected, "player was disconnected after block behavior threw an exception");
			Assert.Equal(2, faultyBehavior.InvocationCount);
			Assert.True(loggedBreakFailures == 2, $"both original and fallback failures should be logged; errors: {string.Join(" | ", loggedMessages)}");
			Assert.True(didBreakCount == 0, "DidBreakBlock must not fire when both the original and fallback break fail");

			DispatchPacket(World, player, packet);
			await World.Ticks(5);

			Assert.True(player.IsConnected, "player was disconnected after a repeated block behavior exception");
			Assert.Equal(4, faultyBehavior.InvocationCount);
			Assert.True(loggedBreakFailures == 2, $"repeated failures should be capped after two callback types; errors: {string.Join(" | ", loggedMessages)}");
			Assert.True(didBreakCount == 0, "a failed break must not be reported as successful on retry");
			Assert.Equal("game:rock-granite", World.BlockAt(blockPos).Code.ToString());
		}
		finally
		{
			World.Api.Event.DidBreakBlock -= didBreakHandler;
			logger.EntryAdded -= logEntry;
			block.BlockBehaviors = originalBehaviors;
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_NotRetryOldBlockOrDuplicateDropsAfterReplacement()
	{
		ITestPlayer player = await World.JoinPlayer("brk-replaced");
		player.Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
		BlockPos playerPos = World.Spawn.AddCopy(18, 1, 18);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		Block block = World.BlockAt(blockPos);
		Block replacement = World.Api.World.GetBlock(new AssetLocation("game:soil-medium-normal"))!;
		BlockBehavior[] originalBehaviors = block.BlockBehaviors;
		var behavior = new SpawnDropsThenReplaceThenThrowBehavior(block, replacement.BlockId);
		await player.GiveItem("game:pickaxe-steel", 1);
		ItemSlot activeSlot = player.Player.InventoryManager.ActiveHotbarSlot;
		Assert.NotNull(activeSlot.Itemstack);
		int heldTier = activeSlot.Itemstack!.Collectible.GetToolTier(activeSlot);
		int requiredTier = block.GetRequiredMiningTier(World.Api.World, blockPos);
		Assert.True(heldTier >= requiredTier, $"test pickaxe must meet mining tier; held={heldTier} required={requiredTier} item={activeSlot.Itemstack.Collectible.Code}");
		int itemCountBefore = CountNearbyItemEntities(blockPos);
		int itemCountAfterFirstDrop = itemCountBefore;
		behavior.AfterDropSpawned = () => itemCountAfterFirstDrop = CountNearbyItemEntities(blockPos);
		try
		{
			block.BlockBehaviors = block.BlockBehaviors.Append(behavior).ToArray();

			// This packet simulates an instant break, so bypass progress validation for this focused recovery scenario.
			bool blockBreakGuardEnabled = SetBlockBreakGuardEnabled(false);
			try
			{
				DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
			}
			finally
			{
				SetBlockBreakGuardEnabled(blockBreakGuardEnabled);
			}
			await World.Ticks(5);

			Assert.True(player.IsConnected, "player was disconnected after the block behavior threw");
			Assert.True(behavior.InvocationCount == 1, $"the replaced block must not be retried through its stale Block instance; invoked {behavior.InvocationCount}, current block {World.BlockAt(blockPos).Code}");
			Assert.True(replacement.Code.Equals(World.BlockAt(blockPos).Code), "the replacement block must remain in the world");
			Assert.True(itemCountAfterFirstDrop > itemCountBefore, "the survival break must spawn at least one item entity");
			Assert.True(itemCountAfterFirstDrop == CountNearbyItemEntities(blockPos), "the failed original break must not spawn its drops twice");
		}
		finally
		{
			block.BlockBehaviors = originalBehaviors;
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_LogCollectibleBehaviorOncePerCallback()
	{
		ITestPlayer player = await World.JoinPlayer("brk-coll-log");
		BlockPos playerPos = World.Spawn.AddCopy(20, 1, 20);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		await player.GiveItem("game:pickaxe-iron", 1);
		CollectibleObject pickaxe = player.Player.InventoryManager.ActiveHotbarSlot.Itemstack!.Collectible;
		CollectibleBehavior[] originalBehaviors = pickaxe.CollectibleBehaviors;
		var faultyBehavior = new FaultyMultiCallbackBehavior(pickaxe);
		ILogger logger = World.Api.World.Logger;
		var loggedMessages = new List<string>();
		LogEntryDelegate logEntry = (logType, message, _) =>
		{
			if (logType != EnumLogType.Error || !message.StartsWith("Exception thrown in CollectibleBehavior", StringComparison.Ordinal)) return;
			loggedMessages.Add(message);
		};
		logger.EntryAdded += logEntry;
		try
		{
			pickaxe.CollectibleBehaviors = originalBehaviors.Append(faultyBehavior).ToArray();
			DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
			await World.Ticks(5);

			Assert.True(faultyBehavior.BrokenWithInvoked, "OnBlockBrokenWith should be invoked");
			Assert.True(faultyBehavior.DamageInvoked, "DamageItem should be invoked after the block break");
			Assert.True(loggedMessages.Count == 2, $"the same behavior type must be logged independently for each callback; messages: {string.Join(" | ", loggedMessages)}");
		}
		finally
		{
			logger.EntryAdded -= logEntry;
			pickaxe.CollectibleBehaviors = originalBehaviors;
		}
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BlockBreak_Should_CapRepeatedCollectibleBehaviorLogs()
	{
		ITestPlayer player = await World.JoinPlayer("brk-coll-cap");
		BlockPos playerPos = World.Spawn.AddCopy(22, 1, 22);
		await player.TeleportTo(playerPos);

		BlockPos blockPos = playerPos.AddCopy(1, 0, 0);
		World.SetBlock("game:rock-granite", blockPos);
		await World.Ticks(5);
		await player.GiveItem("game:pickaxe-iron", 1);
		CollectibleObject pickaxe = player.Player.InventoryManager.ActiveHotbarSlot.Itemstack!.Collectible;
		CollectibleBehavior[] originalBehaviors = pickaxe.CollectibleBehaviors;
		var faultyBehavior = new FaultyRepeatedLogBehavior(pickaxe);
		var loggedBehaviorFailures = new List<string>();
		ILogger logger = World.Api.World.Logger;
		LogEntryDelegate logEntry = (logType, message, _) =>
		{
			if (logType == EnumLogType.Error && message.StartsWith("Exception thrown in CollectibleBehavior", StringComparison.Ordinal)) loggedBehaviorFailures.Add(message);
		};
		logger.EntryAdded += logEntry;
		try
		{
			pickaxe.CollectibleBehaviors = originalBehaviors.Append(faultyBehavior).ToArray();
			for (int i = 0; i < 3; i++)
			{
				DispatchPacket(World, player, CreateBlockBreakPacket(blockPos));
				await World.Ticks(1);
			}

			Assert.True(faultyBehavior.InvocationCount == 3);
			Assert.True(loggedBehaviorFailures.Count == 1, $"repeated failures in one callback should emit only one full stack trace; found {loggedBehaviorFailures.Count}");
			Assert.True(World.BlockAt(blockPos).Code.ToString() == "game:rock-granite", "the explicit PreventDefault veto remains intact");
		}
		finally
		{
			logger.EntryAdded -= logEntry;
			pickaxe.CollectibleBehaviors = originalBehaviors;
		}
	}

	private int CountNearbyItemEntities(BlockPos center)
	{
		var area = new Cuboidi(center.X - 4, center.Y - 4, center.Z - 4, center.X + 4, center.Y + 4, center.Z + 4);
		return World.EntitiesIn(area).OfType<EntityItem>().Count();
	}

	private static ILogger GetServerLogger()
	{
		Type serverMainType = Type.GetType("Vintagestory.Server.ServerMain, VintagestoryLib", throwOnError: true)!;
		FieldInfo loggerField = serverMainType.GetField("Logger", BindingFlags.Public | BindingFlags.Static)!;
		return (ILogger)loggerField.GetValue(null)!;
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
		public int InvocationCount { get; private set; }
		public bool BrokenWithInvoked => InvocationCount > 0;

		public FaultyBrokenWithBehavior(CollectibleObject collObj, EnumHandling handlingBeforeThrow = EnumHandling.PassThrough) : base(collObj)
		{
			_handlingBeforeThrow = handlingBeforeThrow;
		}

		public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier, ref EnumHandling bhHandling)
		{
			InvocationCount++;
			bhHandling = _handlingBeforeThrow;
			throw new InvalidOperationException("Simulated external mod exception during OnBlockBrokenWith");
		}
	}

	private sealed class FaultyMultiCallbackBehavior : CollectibleBehavior
	{
		public bool BrokenWithInvoked { get; private set; }
		public bool DamageInvoked { get; private set; }

		public FaultyMultiCallbackBehavior(CollectibleObject collObj) : base(collObj)
		{
		}

		public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier, ref EnumHandling bhHandling)
		{
			BrokenWithInvoked = true;
			throw new InvalidOperationException("Simulated external mod exception during OnBlockBrokenWith");
		}

		public override void OnDamageItem(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, ref int amount, ref EnumHandling bhHandling)
		{
			DamageInvoked = true;
			throw new InvalidOperationException("Simulated external mod exception during OnDamageItem");
		}
	}

	private sealed class FaultyRepeatedLogBehavior : CollectibleBehavior
	{
		public int InvocationCount { get; private set; }

		public FaultyRepeatedLogBehavior(CollectibleObject collObj) : base(collObj)
		{
		}

		public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier, ref EnumHandling bhHandling)
		{
			InvocationCount++;
			bhHandling = EnumHandling.PreventDefault;
			throw new InvalidOperationException("Simulated repeated behavior failure");
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
			throw new RepeatedBlockBehaviorException("Simulated deterministic block break failure");
		}
	}

	private sealed class RepeatedBlockBehaviorException : Exception
	{
		public RepeatedBlockBehaviorException(string message) : base(message)
		{
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

	private sealed class SpawnDropsThenReplaceThenThrowBehavior : BlockBehavior
	{
		private readonly int _replacementBlockId;
		public int InvocationCount { get; private set; }
		public Action? AfterDropSpawned { get; set; }

		public SpawnDropsThenReplaceThenThrowBehavior(Block block, int replacementBlockId) : base(block)
		{
			_replacementBlockId = replacementBlockId;
		}

		public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier, ref EnumHandling handling)
		{
			InvocationCount++;
			if (InvocationCount > 1) return;

			block.SpawnDropsAndRemoveBlock(world, pos, byPlayer, dropQuantityMultiplier);
			AfterDropSpawned?.Invoke();
			world.BlockAccessor.SetBlock(_replacementBlockId, pos, BlockLayersAccess.Solid);
			throw new InvalidOperationException("Simulated failure after drops spawned and block was replaced");
		}
	}
}

public sealed class CollectibleApiCompatibilityTests
{
	[Fact]
	public void Collectible_Should_KeepTheOriginalWalkBehaviorsExtensionSignature()
	{
		MethodInfo? method = typeof(CollectibleObject).GetMethod("WalkBehaviors", BindingFlags.Instance | BindingFlags.NonPublic);

		Assert.NotNull(method);
		Assert.True(method!.IsFamily, "WalkBehaviors must remain protected for existing collectible subclasses");
		Assert.Equal(typeof(void), method.ReturnType);
		Assert.Equal(new[] { typeof(CollectibleBehaviorDelegate), typeof(Action) }, Array.ConvertAll(method.GetParameters(), parameter => parameter.ParameterType));
	}
}
