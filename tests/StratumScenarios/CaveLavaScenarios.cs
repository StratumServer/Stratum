using System;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A cave step that places lava clears the rock in that cell. The delayed
/// write flushes the empty solid, and the fluid stays.
/// </summary>
public class CaveLavaScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task CarvedLava_Should_NotKeepItsRock()
	{
		int chunkX = 24;
		int chunkZ = 24;
		BlockPos anchor = new BlockPos(chunkX * 32, 4, chunkZ * 32);
		World.Api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions { KeepLoaded = true });
		await World.Until(() => World.Api.World.BlockAccessor.GetChunk(chunkX, 0, chunkZ) != null, timeoutTicks: 800);

		object caves = World.Api.ModLoader.GetModSystem("Vintagestory.ServerMods.GenCaves")
			?? throw new InvalidOperationException("GenCaves was not loaded");
		IServerChunk[] chunks = new IServerChunk[World.Api.WorldManager.MapSizeY / 32];
		for (int i = 0; i < chunks.Length; i++)
		{
			chunks[i] = (IServerChunk)World.Api.World.BlockAccessor.GetChunk(chunkX, i, chunkZ);
		}
		Assert.NotNull(chunks[0]);

		MethodInfo begin = caves.GetType().GetMethod("StratumBeginColumn", BindingFlags.Instance | BindingFlags.NonPublic)!;
		MethodInfo carve = caves.GetType().GetMethod("SetBlocks", BindingFlags.Instance | BindingFlags.NonPublic)!;
		MethodInfo flush = caves.GetType().GetMethod("StratumFlushPendingWrites", BindingFlags.Instance | BindingFlags.NonPublic)!;
		begin.Invoke(caves, new object[] { chunks.Length });

		ushort[] terrain = chunks[0].MapChunk.WorldGenTerrainHeightMap;
		ushort[] rain = chunks[0].MapChunk.RainHeightMap;
		carve.Invoke(caves, new object[] { chunks, 4f, 4f, 16d, 6d, 16d, terrain, rain, chunkX, chunkZ, false });
		flush.Invoke(caves, new object[] { chunks });

		int lava = 0;
		int sealedLava = 0;
		IBlockAccessor blocks = World.Api.World.BlockAccessor;
		for (int x = 8; x <= 24; x++)
		{
			for (int z = 8; z <= 24; z++)
			{
				for (int y = 1; y < 12; y++)
				{
					BlockPos pos = new BlockPos(chunkX * 32 + x, y, chunkZ * 32 + z);
					Block fluid = blocks.GetBlock(pos, BlockLayersAccess.Fluid);
					if (fluid?.Code?.Path == null || !fluid.Code.Path.Contains("lava")) continue;
					lava++;
					if (blocks.GetBlock(pos, BlockLayersAccess.Solid).BlockId != 0) sealedLava++;
				}
			}
		}

		Assert.True(lava > 0, "the carve did not leave a lava cell");
		Assert.Equal(0, sealedLava);
	}
}
