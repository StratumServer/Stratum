using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Vintagestory.Server;

// Slow movement cheats need world checks, not just speed caps.
internal static class StratumMovementGuard
{
	private static readonly object gate = new object();
	private static readonly Dictionary<string, MoveState> states = new Dictionary<string, MoveState>(StringComparer.Ordinal);

	public static void ForgetPlayer(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return;
		}

		lock (gate)
		{
			states.Remove(key);
		}
	}

	// Runs after entity.Pos was updated from the packet. False means the caller should send a correction.
	public static bool TryAccept(ServerMain server, ServerPlayer player, EntityPlayer entity, string key, out string disconnectReason)
	{
		disconnectReason = null;
		if (server == null || player == null || entity?.Pos == null || string.IsNullOrEmpty(key))
		{
			return true;
		}

		StratumConfig stratumConfig = StratumRuntime.Config;
		stratumConfig.EnsurePopulated();
		StratumMovementAnticheatConfig config = stratumConfig.Anticheat.Movement;
		if (!stratumConfig.Anticheat.Enabled || !config.Enabled || (!config.DetectFlight && !config.DetectNoclip))
		{
			return true;
		}

		if (player.WorldData.CurrentGameMode == EnumGameMode.Creative
			|| player.WorldData.CurrentGameMode == EnumGameMode.Spectator
			|| player.WorldData.FreeMove
			|| player.WorldData.NoClip)
		{
			ResetToCurrent(key, entity);
			return true;
		}

		BlockPos feetCell = entity.Pos.AsBlockPos;
		if (feetCell == null)
		{
			return true;
		}

		long now = server.ElapsedMilliseconds;
		bool supported = IsSupported(server, entity, feetCell, config);
		bool embedded = config.DetectNoclip && IsEmbedded(server, entity, feetCell);
		bool waterWalkCandidate = config.DetectFlight && config.DetectWaterWalk && IsWaterWalkCandidate(server, entity, feetCell, config);

		MoveState state;
		lock (gate)
		{
			if (!states.TryGetValue(key, out state))
			{
				state = new MoveState();
				states[key] = state;
			}
		}

		bool hasPreviousY = state.HasLastY;
		double previousY = hasPreviousY ? state.LastY : entity.Pos.Y;

		if (waterWalkCandidate)
		{
			double yDelta = hasPreviousY ? Math.Abs(entity.Pos.Y - previousY) : double.MaxValue;
			state.WaterWalkTicks = yDelta <= 0.03 ? state.WaterWalkTicks + 1 : 1;
			if (state.WaterWalkTicks >= Math.Max(3, config.WaterWalkConsecutiveTicks))
			{
				ResetAirborne(state);
				state.WaterWalkTicks = 0;
				Rubberband(entity, state);
				StratumAnticheatReporter.RecordMovementViolation(server, player, entity.Pos.AsBlockPos, "water walk: standing on liquid", out disconnectReason);
				return false;
			}
		}
		else
		{
			state.WaterWalkTicks = 0;
		}

		if (supported && !embedded && !waterWalkCandidate && IsClearBody(server, entity, feetCell))
		{
			state.HasSafePos = true;
			state.SafeX = entity.Pos.X;
			state.SafeY = entity.Pos.Y;
			state.SafeZ = entity.Pos.Z;
		}

		state.HasLastY = true;
		state.LastY = entity.Pos.Y;

		if (config.DetectNoclip)
		{
			state.EmbeddedTicks = embedded ? state.EmbeddedTicks + 1 : 0;
			if (state.EmbeddedTicks >= Math.Max(2, config.NoclipConsecutiveTicks))
			{
				state.EmbeddedTicks = 0;
				ResetAirborne(state);
				Rubberband(entity, state);
				StratumAnticheatReporter.RecordMovementViolation(server, player, entity.Pos.AsBlockPos, "noclip: chest inside solid blocks", out disconnectReason);
				return false;
			}
		}

		if (config.DetectFlight)
		{
			if (supported)
			{
				ResetAirborne(state);
			}
			else if (!state.Airborne)
			{
				state.Airborne = true;
				state.AirborneSinceMs = now;
				state.AirborneStartY = entity.Pos.Y;
				state.AirborneLowestY = entity.Pos.Y;
				state.AirborneHadDescent = false;
			}
			else
			{
				state.AirborneLowestY = Math.Min(state.AirborneLowestY, entity.Pos.Y);
				if (state.AirborneStartY - state.AirborneLowestY >= config.FlightRequiredDescentBlocks)
				{
					state.AirborneHadDescent = true;
				}

				double airborneSeconds = (now - state.AirborneSinceMs) / 1000.0;
				if (airborneSeconds >= config.FlightMinAirborneSeconds && !state.AirborneHadDescent)
				{
					ResetAirborne(state);
					Rubberband(entity, state);
					StratumAnticheatReporter.RecordMovementViolation(server, player, entity.Pos.AsBlockPos, "flight: airborne without descent", out disconnectReason);
					return false;
				}
			}
		}

		state.HasLastY = true;
		state.LastY = entity.Pos.Y;
		return true;
	}

	private static void ResetAirborne(MoveState state)
	{
		state.Airborne = false;
		state.AirborneSinceMs = 0;
		state.AirborneStartY = 0;
		state.AirborneLowestY = 0;
		state.AirborneHadDescent = false;
	}

	private static void Rubberband(EntityPlayer entity, MoveState state)
	{
		if (state.HasSafePos)
		{
			entity.Pos.X = state.SafeX;
			entity.Pos.Y = state.SafeY;
			entity.Pos.Z = state.SafeZ;
		}

		entity.Pos.Motion.Set(0.0, 0.0, 0.0);
	}

	private static void ResetToCurrent(string key, EntityPlayer entity)
	{
		lock (gate)
		{
			if (!states.TryGetValue(key, out MoveState state))
			{
				state = new MoveState();
				states[key] = state;
			}

			ResetAirborne(state);
			state.EmbeddedTicks = 0;
			state.WaterWalkTicks = 0;
			state.HasLastY = true;
			state.LastY = entity.Pos.Y;
			state.HasSafePos = true;
			state.SafeX = entity.Pos.X;
			state.SafeY = entity.Pos.Y;
			state.SafeZ = entity.Pos.Z;
		}
	}

	// Unloaded blocks count as supported. Guessing wrong there would make chunk-load timing look like cheating.
	private static bool IsSupported(ServerMain server, EntityPlayer entity, BlockPos feetCell, StratumMovementAnticheatConfig config)
	{
		var blocks = server.WorldMap.RelaxedBlockAccess;
		int feetY = feetCell.Y;
		BlockPos probe = feetCell.Copy();

		for (int dy = -1; dy <= 1; dy++)
		{
			probe.Y = feetY + dy;
			Block block = blocks.GetBlock(probe);
			if (block == null)
			{
				return true;
			}

			if (block.Climbable)
			{
				return true;
			}

			Block fluid = blocks.GetBlock(probe, 2);
			if (fluid != null && fluid.IsLiquid())
			{
				return true;
			}
		}

		if (HasGroundContact(server, entity, feetCell, config))
		{
			return true;
		}

		int depth = Math.Max(0, config.FlightGroundScanDepth);
		for (int d = 1; d <= depth; d++)
		{
			probe.Y = feetY - d;
			Block block = blocks.GetBlock(probe);
			if (block == null)
			{
				return true;
			}

			Cuboidf[] boxes = block.GetCollisionBoxes(blocks, probe);
			if (boxes != null && boxes.Length > 0)
			{
				return true;
			}
		}

		return false;
	}

	private static bool HasGroundContact(ServerMain server, EntityPlayer entity, BlockPos feetCell, StratumMovementAnticheatConfig config)
	{
		var blocks = server.WorldMap.RelaxedBlockAccess;
		BlockPos groundCell = feetCell.Copy();
		groundCell.Y--;

		Block block = blocks.GetBlock(groundCell);
		if (block == null)
		{
			return true;
		}

		Cuboidf[] boxes = block.GetCollisionBoxes(blocks, groundCell);
		if (boxes == null || boxes.Length == 0)
		{
			return false;
		}

		double localY = entity.Pos.Y - groundCell.Y;
		foreach (Cuboidf box in boxes)
		{
			if (box != null && localY >= box.Y2 && localY - box.Y2 <= config.GroundContactTolerance)
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsWaterWalkCandidate(ServerMain server, EntityPlayer entity, BlockPos feetCell, StratumMovementAnticheatConfig config)
	{
		var blocks = server.WorldMap.RelaxedBlockAccess;

		if (HasGroundContact(server, entity, feetCell, config))
		{
			return false;
		}

		Block feetFluid = blocks.GetBlock(feetCell, 2);
		if (feetFluid != null && feetFluid.IsLiquid())
		{
			double localFeetY = entity.Pos.Y - feetCell.Y;
			return !entity.Swimming && localFeetY >= 0.92;
		}

		BlockPos belowCell = feetCell.Copy();
		belowCell.Y--;
		Block belowFluid = blocks.GetBlock(belowCell, 2);
		if (belowFluid != null && belowFluid.IsLiquid())
		{
			double localY = entity.Pos.Y - belowCell.Y;
			return !entity.Swimming && localY >= 0.92 && localY <= 1.2;
		}

		return false;
	}

	// Chest point only. Feet/head checks produce bad noise around normal partial blocks.
	private static bool IsEmbedded(ServerMain server, EntityPlayer entity, BlockPos feetCell)
	{
		double px = entity.Pos.X;
		double py = entity.Pos.Y;
		double pz = entity.Pos.Z;
		double chestWorldY = py + 0.9;

		int chestCellWorldY = (int)Math.Floor(chestWorldY);
		int feetCellWorldY = (int)Math.Floor(py);

		BlockPos chestCell = feetCell.Copy();
		chestCell.Y = feetCell.Y + (chestCellWorldY - feetCellWorldY);

		var blocks = server.WorldMap.RelaxedBlockAccess;
		Block block = blocks.GetBlock(chestCell);
		if (block == null)
		{
			return false;
		}

		Cuboidf[] boxes = block.GetCollisionBoxes(blocks, chestCell);
		if (boxes == null || boxes.Length == 0)
		{
			return false;
		}

		double lx = px - Math.Floor(px);
		double ly = chestWorldY - chestCellWorldY;
		double lz = pz - Math.Floor(pz);
		foreach (Cuboidf box in boxes)
		{
			if (box == null)
			{
				continue;
			}

			if (lx >= box.X1 && lx <= box.X2 && ly >= box.Y1 && ly <= box.Y2 && lz >= box.Z1 && lz <= box.Z2)
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsClearBody(ServerMain server, EntityPlayer entity, BlockPos feetCell)
	{
		return !PointInsideCollision(server, entity, feetCell, 0.2)
			&& !PointInsideCollision(server, entity, feetCell, 0.9)
			&& !PointInsideCollision(server, entity, feetCell, 1.65);
	}

	private static bool PointInsideCollision(ServerMain server, EntityPlayer entity, BlockPos feetCell, double offsetY)
	{
		double px = entity.Pos.X;
		double py = entity.Pos.Y + offsetY;
		double pz = entity.Pos.Z;
		int pointCellWorldY = (int)Math.Floor(py);
		int feetCellWorldY = (int)Math.Floor(entity.Pos.Y);

		BlockPos pointCell = feetCell.Copy();
		pointCell.Y = feetCell.Y + (pointCellWorldY - feetCellWorldY);

		var blocks = server.WorldMap.RelaxedBlockAccess;
		Block block = blocks.GetBlock(pointCell);
		if (block == null)
		{
			return false;
		}

		Cuboidf[] boxes = block.GetCollisionBoxes(blocks, pointCell);
		if (boxes == null || boxes.Length == 0)
		{
			return false;
		}

		double lx = px - Math.Floor(px);
		double ly = py - pointCellWorldY;
		double lz = pz - Math.Floor(pz);
		foreach (Cuboidf box in boxes)
		{
			if (box != null && lx >= box.X1 && lx <= box.X2 && ly >= box.Y1 && ly <= box.Y2 && lz >= box.Z1 && lz <= box.Z2)
			{
				return true;
			}
		}

		return false;
	}

	private sealed class MoveState
	{
		public bool Airborne;
		public long AirborneSinceMs;
		public double AirborneStartY;
		public double AirborneLowestY;
		public bool AirborneHadDescent;
		public int EmbeddedTicks;
		public int WaterWalkTicks;
		public bool HasLastY;
		public double LastY;
		public bool HasSafePos;
		public double SafeX;
		public double SafeY;
		public double SafeZ;
	}
}
