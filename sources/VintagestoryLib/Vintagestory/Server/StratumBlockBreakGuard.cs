using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Vintagestory.Server;

internal sealed class StratumBlockBreakGuard
{
	private readonly object gate = new object();
	private readonly Dictionary<int, ClientBreakState> clients = new Dictionary<int, ClientBreakState>();
	private long totalObservedBreaks;
	private long totalRejectedBreaks;
	private long totalKickedBreaks;

	public (long Observed, long Rejected, long Kicked) Snapshot()
	{
		lock (gate) return (totalObservedBreaks, totalRejectedBreaks, totalKickedBreaks);
	}

	public void ObserveMining(ServerMain server, ServerPlayer player, float dt)
	{
		if (!IsEnabled() || player.WorldData.CurrentGameMode != EnumGameMode.Survival)
		{
			ForgetClient(player.ClientId);
			return;
		}

		BlockSelection selection = player.CurrentBlockSelection;
		if (!player.Entity.Controls.LeftMouseDown || selection?.Position == null)
		{
			ResetBreak(player.ClientId);
			return;
		}

		Block block = GetBreakBlock(server, selection.Position);
		if (block == null || block.Id == 0)
		{
			ResetBreak(player.ClientId);
			return;
		}

		ItemSlot activeSlot = player.inventoryMgr.ActiveHotbarSlot;
		int heldItemId = activeSlot?.Itemstack?.Id ?? 0;
		float resistance = Math.Max(0f, block.GetResistance(server.BlockAccessor, selection.Position));
		float damagePerSecond = CalculateDamagePerSecond(server, player, selection, block, activeSlot);
		float damage = Math.Max(0f, dt) * damagePerSecond;

		lock (gate)
		{
			ClientBreakState state = GetOrCreateState(player.ClientId);
			if (!state.IsTracking(selection.Position, block.Id, heldItemId))
			{
				state.Start(selection.Position, block.Id, heldItemId, server.ElapsedMilliseconds);
			}

			state.RequiredResistance = resistance;
			state.DamagePerSecond = damagePerSecond;
			state.ObservedDamage += damage;
			state.LastObservedMs = server.ElapsedMilliseconds;
		}
	}

	public bool TryAcceptBreak(ServerMain server, ConnectedClient client, BlockSelection requestedSelection, out string disconnectReason)
	{
		disconnectReason = null;
		StratumConfig stratumConfig = StratumRuntime.Config;
		stratumConfig.EnsurePopulated();
		StratumBlockBreakGuardsConfig config = stratumConfig.BlockBreakGuards;
		if (!stratumConfig.Hardening.BlockBreakGuards || !config.Enabled || client.Player.WorldData.CurrentGameMode != EnumGameMode.Survival)
		{
			return true;
		}

		Block block = GetBreakBlock(server, requestedSelection.Position);
		if (block == null || block.Id == 0)
		{
			return true;
		}

		ServerPlayer player = client.Player;
		ItemSlot activeSlot = player.inventoryMgr.ActiveHotbarSlot;
		int heldItemId = activeSlot?.Itemstack?.Id ?? 0;
		float resistance = Math.Max(0f, block.GetResistance(server.BlockAccessor, requestedSelection.Position));
		float damagePerSecond = CalculateDamagePerSecond(server, player, requestedSelection, block, activeSlot);
		float expectedBreakSeconds = damagePerSecond <= 0f ? float.MaxValue : resistance / damagePerSecond;

		if (resistance <= 0f || expectedBreakSeconds <= Math.Max(0f, config.MinimumTrackedBreakSeconds))
		{
			return true;
		}

		string reason = null;
		bool shouldKick = false;
		long now = server.ElapsedMilliseconds;

		lock (gate)
		{
			ClientBreakState state = GetOrCreateState(client.Id);
			BlockSelection currentSelection = player.CurrentBlockSelection;
			bool hasTrackedProgress = state.IsTracking(requestedSelection.Position, block.Id, heldItemId);
			if (config.RequireServerSelection && !hasTrackedProgress && !SamePosition(currentSelection?.Position, requestedSelection.Position))
			{
				reason = $"break target {requestedSelection.Position} does not match server selection {FormatPos(currentSelection?.Position)}";
			}
			else if (!hasTrackedProgress)
			{
				reason = $"no tracked mining progress for {requestedSelection.Position}";
			}
			else
			{
				float ratio = Math.Max(0.1f, Math.Min(1f, config.RequiredProgressRatio));
				float requiredDamage = resistance * ratio;
				float observedWithGrace = state.ObservedDamage + Math.Max(0f, config.GraceSeconds) * Math.Max(state.DamagePerSecond, damagePerSecond);
				if (observedWithGrace < requiredDamage)
				{
					reason = $"insufficient mining progress {observedWithGrace:0.###}/{requiredDamage:0.###} on {requestedSelection.Position}";
				}
			}

			if (reason == null)
			{
				totalObservedBreaks++;
				state.ClearBreak();
				return true;
			}

			totalRejectedBreaks++;
			state.RegisterViolation(now, Math.Max(1, config.ViolationWindowSeconds));
			shouldKick = config.KickViolations && config.KickAfterViolations > 0 && state.ViolationsInWindow >= config.KickAfterViolations;
			if (shouldKick)
			{
				totalKickedBreaks++;
				disconnectReason = string.IsNullOrWhiteSpace(config.KickMessage) ? "Disconnected by Stratum block break protection" : config.KickMessage;
			}

			if (config.LogViolations && state.ShouldLog(now))
			{
				string action = shouldKick ? "kick" : (config.DropViolations ? "drop" : "monitor");
				StratumRuntime.LogAudit($"block-break action={action} client={client.Id} player={player.PlayerName} block={block.Code} pos={requestedSelection.Position} reason={reason}", true);
			}
		}

		return !config.DropViolations && !shouldKick;
	}

	public void ForgetClient(int clientId)
	{
		lock (gate)
		{
			clients.Remove(clientId);
		}
	}

	public string BuildReport()
	{
		StratumBlockBreakGuardsConfig config = StratumRuntime.Config.BlockBreakGuards;
		lock (gate)
		{
			return $"Block break guards: enabled={(config.Enabled ? "on" : "off")}, drop={(config.DropViolations ? "on" : "off")}, kick={(config.KickViolations ? "on" : "off")}, kickAfter={config.KickAfterViolations}, observed={totalObservedBreaks}, rejected={totalRejectedBreaks}, kicked={totalKickedBreaks}";
		}
	}

	private static bool IsEnabled()
	{
		StratumConfig config = StratumRuntime.Config;
		config.EnsurePopulated();
		return config.Hardening.BlockBreakGuards && config.BlockBreakGuards.Enabled;
	}

	private ClientBreakState GetOrCreateState(int clientId)
	{
		if (!clients.TryGetValue(clientId, out ClientBreakState state))
		{
			state = new ClientBreakState();
			clients[clientId] = state;
		}

		return state;
	}

	private void ResetBreak(int clientId)
	{
		lock (gate)
		{
			if (clients.TryGetValue(clientId, out ClientBreakState state))
			{
				state.ClearBreak();
			}
		}
	}

	private static Block GetBreakBlock(ServerMain server, BlockPos pos)
	{
		Block fluidLayerBlock = server.WorldMap.RelaxedBlockAccess.GetBlock(pos, 2);
		return !fluidLayerBlock.SideSolid.Any ? server.WorldMap.RelaxedBlockAccess.GetBlock(pos, 1) : server.Blocks[fluidLayerBlock.BlockId];
	}

	private static float CalculateDamagePerSecond(ServerMain server, ServerPlayer player, BlockSelection selection, Block block, ItemSlot activeSlot)
	{
		ItemStack itemStack = activeSlot?.Itemstack;
		if (itemStack == null)
		{
			if (block.GetRequiredMiningTier(server.World, selection.Position) > 0)
			{
				return 0f;
			}

			float speed = 1f;
			foreach (BlockBehavior behavior in block.BlockBehaviors)
			{
				speed *= behavior.GetMiningSpeedModifier(server.World, selection.Position, player);
			}

			return speed;
		}

		EnumBlockMaterial material = block.GetBlockMaterial(server.BlockAccessor, selection.Position);
		Dictionary<EnumBlockMaterial, float> miningSpeeds = itemStack.Collectible.GetMiningSpeeds(activeSlot);
		int requiredMiningTier = block.GetRequiredMiningTier(server.World, selection.Position);
		if (requiredMiningTier > 0 && (itemStack.Collectible.GetToolTier(activeSlot) < requiredMiningTier || miningSpeeds == null || !miningSpeeds.ContainsKey(material)))
		{
			return 0f;
		}

		float itemSpeed = Math.Max(0f, itemStack.Collectible.GetMiningSpeed(itemStack, selection, block, player));
		if (requiredMiningTier == 0)
		{
			foreach (BlockBehavior behavior in block.BlockBehaviors)
			{
				itemSpeed *= behavior.GetMiningSpeedModifier(server.World, selection.Position, player);
			}
		}

		return itemSpeed;
	}

	private static bool SamePosition(BlockPos left, BlockPos right)
	{
		return left != null && right != null && left.Equals(right);
	}

	private static string FormatPos(BlockPos pos)
	{
		return pos == null ? "none" : pos.ToString();
	}

	private sealed class ClientBreakState
	{
		private BlockPos position;
		private int blockId;
		private int heldItemId;
		private long violationWindowStartedMs;
		private long lastViolationLogMs = long.MinValue;

		public float RequiredResistance { get; set; }

		public float DamagePerSecond { get; set; }

		public float ObservedDamage { get; set; }

		public long LastObservedMs { get; set; }

		public int ViolationsInWindow { get; private set; }

		public bool IsTracking(BlockPos pos, int forBlockId, int forHeldItemId)
		{
			return position != null && position.Equals(pos) && blockId == forBlockId && heldItemId == forHeldItemId;
		}

		public void Start(BlockPos pos, int forBlockId, int forHeldItemId, long nowMs)
		{
			position = pos.Copy();
			blockId = forBlockId;
			heldItemId = forHeldItemId;
			ObservedDamage = 0f;
			DamagePerSecond = 0f;
			RequiredResistance = 0f;
			LastObservedMs = nowMs;
		}

		public void ClearBreak()
		{
			position = null;
			blockId = 0;
			heldItemId = 0;
			ObservedDamage = 0f;
			DamagePerSecond = 0f;
			RequiredResistance = 0f;
			LastObservedMs = 0;
		}

		public void RegisterViolation(long nowMs, int windowSeconds)
		{
			if (nowMs - violationWindowStartedMs > windowSeconds * 1000L)
			{
				violationWindowStartedMs = nowMs;
				ViolationsInWindow = 0;
			}

			ViolationsInWindow++;
		}

		public bool ShouldLog(long nowMs)
		{
			if (nowMs - lastViolationLogMs < 2000)
			{
				return false;
			}

			lastViolationLogMs = nowMs;
			return true;
		}
	}
}