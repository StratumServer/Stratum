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
		if (!player.Entity.Controls.LeftMouseDown)
		{
			ParkBreak(player.ClientId, server.ElapsedMilliseconds);
			return;
		}

		// Stratum start: record left-click hold even when raytrace misses
		long now = server.ElapsedMilliseconds;
		if (selection?.Position == null)
		{
			lock (gate)
			{
				ClientBreakState state = GetOrCreateState(player.ClientId);
				if (state.LeftMouseHeldSinceMs == 0)
				{
					state.LeftMouseHeldSinceMs = now;
				}
			}
			return;
		}
		// Stratum end

		Block block = GetBreakBlock(server, selection.Position);
		if (block == null || block.Id == 0)
		{
			ClearBreak(player.ClientId);
			return;
		}

		if (block.EntityClass != null && IsMicroblockEntity(server.WorldMap.GetBlockEntity(selection.Position)))
		{
			ClearBreak(player.ClientId);
			return;
		}

		StratumBlockBreakGuardsConfig config = StratumRuntime.Config.BlockBreakGuards;
		ItemSlot activeSlot = player.inventoryMgr.ActiveHotbarSlot;
		float resistance = Math.Max(0f, block.GetResistance(server.BlockAccessor, selection.Position));
		float damagePerSecond = CalculateDamagePerSecond(server, player, selection, block, activeSlot);
		float damage = Math.Max(0f, dt) * damagePerSecond;

		lock (gate)
		{
			ClientBreakState state = GetOrCreateState(player.ClientId);
			state.ExpireRemembered(now, config);
			if (!state.IsTracking(selection.Position, block.Id))
			{
				state.ParkCurrent(now, config);
				state.Start(selection.Position, block.Id, now, config);
			}

			if (state.LeftMouseHeldSinceMs == 0)
			{
				state.LeftMouseHeldSinceMs = now;
			}
			state.RequiredResistance = resistance;
			state.DamagePerSecond = damagePerSecond;
			state.ObservedDamage += damage;
			state.LastObservedMs = now;
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
		long now = server.ElapsedMilliseconds;

		// Nuker / veinmine check. Runs before the per-block progress checks below so it also catches bursts of instant-break blocks
		// Only returns false when it has escalated to a kick
		if (CheckMultiBreak(server, client, player, requestedSelection.Position, requestedSelection.HitPosition, now, out disconnectReason))
		{
			return false;
		}

		if (block.EntityClass != null && IsMicroblockEntity(server.WorldMap.GetBlockEntity(requestedSelection.Position)))
		{
			return true;
		}

		ItemSlot activeSlot = player.inventoryMgr.ActiveHotbarSlot;
		float resistance = Math.Max(0f, block.GetResistance(server.BlockAccessor, requestedSelection.Position));
		float damagePerSecond = CalculateDamagePerSecond(server, player, requestedSelection, block, activeSlot);
		float expectedBreakSeconds = damagePerSecond <= 0f ? float.MaxValue : resistance / damagePerSecond;

		if (resistance <= 0f || expectedBreakSeconds <= Math.Max(0f, config.MinimumTrackedBreakSeconds))
		{
			return true;
		}

		string reason = null;
		bool canLog = false;

		lock (gate)
		{
			ClientBreakState state = GetOrCreateState(client.Id);
			state.ExpireRemembered(now, config);
			BlockSelection currentSelection = player.CurrentBlockSelection;
			bool hasTrackedProgress = state.IsTracking(requestedSelection.Position, block.Id);
			float rememberedDamage = hasTrackedProgress ? 0f : state.GetRememberedDamage(requestedSelection.Position, block.Id, now, config);
			if (config.RequireServerSelection && !hasTrackedProgress && rememberedDamage <= 0f && !SamePosition(currentSelection?.Position, requestedSelection.Position))
			{
				reason = $"break target {requestedSelection.Position} does not match server selection {FormatPos(currentSelection?.Position)}";
			}
			else if (!hasTrackedProgress && rememberedDamage <= 0f)
			{
				// When the server raytrace misses the target block (grass cover, fluid
				// layer, position sync lag), ObserveMining records the hold start but
				// accumulates no damage. Use that hold duration as proof of mining intent.
				float ratio = Math.Max(0.1f, Math.Min(1f, config.RequiredProgressRatio));
				float requiredSeconds = expectedBreakSeconds * ratio;
				float heldSeconds = state.LeftMouseHeldSinceMs > 0 ? (now - state.LeftMouseHeldSinceMs) / 1000f : 0f;
				float heldWithGrace = heldSeconds + Math.Max(0f, config.GraceSeconds);
				if (heldWithGrace < requiredSeconds)
				{
					reason = $"no tracked mining progress for {requestedSelection.Position}";
				}
				
			}
			else
			{
				float ratio = Math.Max(0.1f, Math.Min(1f, config.RequiredProgressRatio));
				float requiredDamage = resistance * ratio;
				float observedDamage = hasTrackedProgress ? state.ObservedDamage : rememberedDamage;
				float observedWithGrace = observedDamage + Math.Max(0f, config.GraceSeconds) * Math.Max(state.DamagePerSecond, damagePerSecond);
				if (observedWithGrace < requiredDamage)
				{
					reason = $"insufficient mining progress {observedWithGrace:0.###}/{requiredDamage:0.###} on {requestedSelection.Position}";
				}
			}

			if (reason == null)
			{
				totalObservedBreaks++;
				state.ClearBreak(requestedSelection.Position, block.Id);
				return true;
			}

			totalRejectedBreaks++;
			canLog = config.LogViolations && state.ShouldLog(now);
		}

		// Route the violation through the shared anticheat reporter so staff alerting and
		// confirmed-cheat kicking follow the same per-player rolling window as the other
		// signals. Called outside the gate lock since the reporter takes its own lock and
		// may broadcast staff chat.
		bool shouldKick = StratumAnticheatReporter.RecordBlockBreakViolation(server, player, requestedSelection.Position, reason, out disconnectReason);
		if (shouldKick)
		{
			lock (gate)
			{
				totalKickedBreaks++;
			}
		}

		if (canLog)
		{
			string action = shouldKick ? "kick" : (config.DropViolations ? "drop" : "monitor");
			StratumRuntime.LogAudit($"block-break action={action} client={client.Id} player={player.PlayerName} block={block.Code} pos={requestedSelection.Position} reason={reason}", true);
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
		StratumBlockBreakProgressAnticheatConfig kickConfig = StratumRuntime.Config.Anticheat.BlockBreakProgress;
		lock (gate)
		{
			return $"Block break guards: enabled={(config.Enabled ? "on" : "off")}, drop={(config.DropViolations ? "on" : "off")}, kick={(kickConfig.KickConfirmedCheats ? "on" : "off")}, kickAfter={kickConfig.KickAfterViolations}, observed={totalObservedBreaks}, rejected={totalRejectedBreaks}, kicked={totalKickedBreaks}";
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

	private void ParkBreak(int clientId, long nowMs)
	{
		lock (gate)
		{
			if (clients.TryGetValue(clientId, out ClientBreakState state))
			{
				state.ParkCurrent(nowMs, StratumRuntime.Config.BlockBreakGuards);
				state.LeftMouseHeldSinceMs = 0;
			}
		}
	}

	private void ClearBreak(int clientId)
	{
		lock (gate)
		{
			if (clients.TryGetValue(clientId, out ClientBreakState state))
			{
				state.ClearCurrentBreak();
			}
		}
	}

	// Flags a burst too many distinct break positions from one client inside the configured window. 
	// Records one violation per burst, returns true only when the reporter escalates to a kick.
	private bool CheckMultiBreak(ServerMain server, ConnectedClient client, ServerPlayer player, BlockPos pos, Vec3d hit, long now, out string disconnectReason)
	{
		disconnectReason = null;
		StratumMultiBreakAnticheatConfig mb = StratumRuntime.Config.Anticheat.MultiBreak;
		if (mb == null || !StratumRuntime.Config.Anticheat.Enabled || !mb.Enabled)
		{
			return false;
		}

		bool centerHit = IsCenterHit(hit);
		int distinct;
		int centerHits;
		lock (gate)
		{
			ClientBreakState state = GetOrCreateState(client.Id);
			distinct = state.RegisterRecentBreak(pos, centerHit, now, Math.Max(1, mb.WindowMs), mb.MaxTrackedBreaks);
			if (distinct <= mb.MaxBreaksInWindow || now < state.MultiBreakCooldownUntilMs)
			{
				return false;
			}

			centerHits = state.CountCenterHits();

			// One burst one violation, cool down and clear so the rest of the wave stays quiet.
			state.MultiBreakCooldownUntilMs = now + Math.Max(1, mb.WindowMs);
			state.ClearRecentBreaks();
		}

		bool fingerprinted = distinct > 0 && centerHits >= distinct;
		return StratumAnticheatReporter.RecordMultiBreakViolation(server, player, pos, distinct, fingerprinted, out disconnectReason);
	}

	private static bool IsCenterHit(Vec3d hit)
	{
		if (hit == null)
		{
			return false;
		}

		const double t = 0.02;
		return Math.Abs(hit.X - 0.5) < t && Math.Abs(hit.Y - 0.5) < t && Math.Abs(hit.Z - 0.5) < t;
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

	private static bool IsMicroblockEntity(BlockEntity blockEntity)
	{
		for (Type type = blockEntity?.GetType(); type != null; type = type.BaseType)
		{
			if (type.FullName == "Vintagestory.GameContent.BlockEntityMicroBlock")
			{
				return true;
			}
		}

		return false;
	}

	private sealed class ClientBreakState
	{
		private readonly Dictionary<BreakKey, RememberedBreak> rememberedBreaks = new Dictionary<BreakKey, RememberedBreak>();
		private readonly List<RecentBreak> recentBreaks = new List<RecentBreak>();
		private BlockPos position;
		private int blockId;
		private long lastViolationLogMs = -2000;

		public long MultiBreakCooldownUntilMs { get; set; }

		public float RequiredResistance { get; set; }

		public float DamagePerSecond { get; set; }

		public float ObservedDamage { get; set; }

		public long LastObservedMs { get; set; }

		public long LeftMouseHeldSinceMs { get; set; } // tracks when left-click started regardless of raytrace

		public bool IsTracking(BlockPos pos, int forBlockId)
		{
			return position != null && position.Equals(pos) && blockId == forBlockId;
		}

		// Registers a break at pos and returns the number of distinct block positions broken within the last windowMs
		public int RegisterRecentBreak(BlockPos pos, bool centerHit, long nowMs, int windowMs, int maxTracked)
		{
			int drop = 0;
			while (drop < recentBreaks.Count && nowMs - recentBreaks[drop].Ms > windowMs)
			{
				drop++;
			}

			if (drop > 0)
			{
				recentBreaks.RemoveRange(0, drop);
			}

			bool already = false;
			for (int i = 0; i < recentBreaks.Count; i++)
			{
				if (recentBreaks[i].X == pos.X && recentBreaks[i].Y == pos.Y && recentBreaks[i].Z == pos.Z)
				{
					already = true;
					break;
				}
			}

			if (!already && recentBreaks.Count < maxTracked)
			{
				recentBreaks.Add(new RecentBreak(nowMs, pos.X, pos.Y, pos.Z, centerHit));
			}

			return recentBreaks.Count;
		}

		public void ClearRecentBreaks()
		{
			recentBreaks.Clear();
		}

		public int CountCenterHits()
		{
			int count = 0;
			for (int i = 0; i < recentBreaks.Count; i++)
			{
				if (recentBreaks[i].Center)
				{
					count++;
				}
			}

			return count;
		}

		private readonly struct RecentBreak
		{
			public readonly long Ms;
			public readonly int X;
			public readonly int Y;
			public readonly int Z;
			public readonly bool Center;

			public RecentBreak(long ms, int x, int y, int z, bool center)
			{
				Ms = ms;
				X = x;
				Y = y;
				Z = z;
				Center = center;
			}
		}

		public void Start(BlockPos pos, int forBlockId, long nowMs, StratumBlockBreakGuardsConfig config)
		{
			position = pos.Copy();
			blockId = forBlockId;
			ObservedDamage = GetRememberedDamage(pos, forBlockId, nowMs, config);
			DamagePerSecond = 0f;
			RequiredResistance = 0f;
			LastObservedMs = nowMs;
		}

		public void ParkCurrent(long nowMs, StratumBlockBreakGuardsConfig config)
		{
			if (position == null)
			{
				return;
			}

			if (ObservedDamage > 0f && config.PartialProgressRetentionSeconds > 0f && config.MaxRememberedPartialBreaksPerClient > 0)
			{
				float maxRememberedDamage = RequiredResistance > 0f ? RequiredResistance * config.MaxRememberedProgressRatio : ObservedDamage;
				RememberedBreak remembered = new RememberedBreak
				{
					Damage = Math.Max(0f, Math.Min(ObservedDamage, maxRememberedDamage)),
					LastObservedMs = nowMs
				};

				rememberedBreaks[new BreakKey(position, blockId)] = remembered;
				TrimRemembered(config.MaxRememberedPartialBreaksPerClient);
			}

			ClearCurrentBreak();
		}

		public float GetRememberedDamage(BlockPos pos, int forBlockId, long nowMs, StratumBlockBreakGuardsConfig config)
		{
			if (pos == null || config.PartialProgressRetentionSeconds <= 0f)
			{
				return 0f;
			}

			BreakKey key = new BreakKey(pos, forBlockId);
			if (!rememberedBreaks.TryGetValue(key, out RememberedBreak remembered))
			{
				return 0f;
			}

			if (nowMs - remembered.LastObservedMs > config.PartialProgressRetentionSeconds * 1000f)
			{
				rememberedBreaks.Remove(key);
				return 0f;
			}

			return Math.Max(0f, remembered.Damage);
		}

		public void ExpireRemembered(long nowMs, StratumBlockBreakGuardsConfig config)
		{
			if (rememberedBreaks.Count == 0)
			{
				return;
			}

			if (config.PartialProgressRetentionSeconds <= 0f)
			{
				rememberedBreaks.Clear();
				return;
			}

			long maxAgeMs = (long)(config.PartialProgressRetentionSeconds * 1000f);
			List<BreakKey> expired = null;
			foreach (KeyValuePair<BreakKey, RememberedBreak> entry in rememberedBreaks)
			{
				if (nowMs - entry.Value.LastObservedMs > maxAgeMs)
				{
					expired ??= new List<BreakKey>();
					expired.Add(entry.Key);
				}
			}

			if (expired == null)
			{
				return;
			}

			foreach (BreakKey key in expired)
			{
				rememberedBreaks.Remove(key);
			}
		}

		public void ClearBreak(BlockPos pos, int forBlockId)
		{
			rememberedBreaks.Remove(new BreakKey(pos, forBlockId));
			if (IsTracking(pos, forBlockId))
			{
				ClearCurrentBreak();
			}
		}

		public void ClearCurrentBreak()
		{
			position = null;
			blockId = 0;
			ObservedDamage = 0f;
			DamagePerSecond = 0f;
			RequiredResistance = 0f;
			LastObservedMs = 0;
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

		private void TrimRemembered(int maxEntries)
		{
			while (rememberedBreaks.Count > maxEntries)
			{
				BreakKey oldestKey = default;
				long oldestMs = long.MaxValue;
				foreach (KeyValuePair<BreakKey, RememberedBreak> entry in rememberedBreaks)
				{
					if (entry.Value.LastObservedMs < oldestMs)
					{
						oldestKey = entry.Key;
						oldestMs = entry.Value.LastObservedMs;
					}
				}

				rememberedBreaks.Remove(oldestKey);
			}
		}
	}

	private readonly struct BreakKey : IEquatable<BreakKey>
	{
		private readonly int x;
		private readonly int y;
		private readonly int z;
		private readonly int dimension;
		private readonly int blockId;

		public BreakKey(BlockPos pos, int blockId)
		{
			x = pos.X;
			y = pos.Y;
			z = pos.Z;
			dimension = pos.dimension;
			this.blockId = blockId;
		}

		public bool Equals(BreakKey other)
		{
			return x == other.x && y == other.y && z == other.z && dimension == other.dimension && blockId == other.blockId;
		}

		public override bool Equals(object obj)
		{
			return obj is BreakKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			unchecked
			{
				int hash = x;
				hash = (hash * 397) ^ y;
				hash = (hash * 397) ^ z;
				hash = (hash * 397) ^ dimension;
				hash = (hash * 397) ^ blockId;
				return hash;
			}
		}
	}

	private struct RememberedBreak
	{
		public float Damage;
		public long LastObservedMs;
	}
}
