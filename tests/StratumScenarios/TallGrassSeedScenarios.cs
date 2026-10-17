using System.Collections;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// With the terrain split off, tall grass is drawn from the generator that just
/// seeded the column. Generating another column first does not change it, and a
/// public PlaceTallGrass call during that stage uses the same seed.
/// </summary>
[AtlasWorld(WorldType = "standard", Seed = 7351)]
public class TallGrassSeedScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task SplitOff_Should_KeepTallGrassOnTheColumnSeed()
	{
		await World.Ticks(1);
		object layers = World.Api.ModLoader.GetModSystem("Vintagestory.ServerMods.GenBlockLayers")
			?? throw new InvalidOperationException("GenBlockLayers was not loaded");
		MethodInfo handler = layers.GetType().GetMethod("OnChunkColumnGeneration", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("OnChunkColumnGeneration was missing");
		string calls = CalledMethods(handler);
		Assert.Contains("PlaceTallGrass", calls);
		Assert.DoesNotContain("StratumPlaceTallGrass", calls);

		await AssertSerialStageSetsTheFlag();

		const int chunkX = 8;
		const int chunkZ = 8;
		BlockPos anchor = new BlockPos(chunkX * 32 + 4, 120, chunkZ * 32 + 4);
		World.Api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions { KeepLoaded = true });
		await World.Until(
			() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor) != null,
			timeoutTicks: 1200);

		int mapChunksY = World.Api.WorldManager.MapSizeY / 32;
		IServerChunk[] chunks = new IServerChunk[mapChunksY];
		for (int cy = 0; cy < mapChunksY; cy++)
		{
			chunks[cy] = (IServerChunk)World.Api.World.BlockAccessor.GetChunk(chunkX, cy, chunkZ)!;
		}

		int surfaceY = 1;
		for (int y = World.Api.WorldManager.MapSizeY - 2; y > 1; y--)
		{
			if (World.BlockAt(new BlockPos(anchor.X, y, anchor.Z)).Id != 0)
			{
				surfaceY = y;
				break;
			}
		}

		Block fertile = FertileBlock();
		MethodInfo place = layers.GetType().GetMethod("PlaceTallGrass")!;
		int[] alone = Draw(layers, place, chunks, surfaceY, fertile, seed: 4);
		int[] otherColumn = DrawOnWorker(layers, place, chunks, surfaceY, fertile, seed: 900);
		PolluteWorker(layers, place, chunks, surfaceY);
		int[] after = Draw(layers, place, chunks, surfaceY, fertile, seed: 4);
		Assert.Equal(alone, after);
		Assert.NotEqual(alone, otherColumn);
		Assert.Contains(alone, id => id != 0);
	}

	private async Task AssertSerialStageSetsTheFlag()
	{
		object server = World.Api.World;
		object chunkThread = server.GetType().GetField("chunkThread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
		object supply = chunkThread.GetType().GetField("loadsavechunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunkThread)!;
		object handler = supply.GetType().GetField("worldgenHandler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(supply)!;
		Array lists = (Array)handler.GetType().GetField("OnChunkColumnGen")!.GetValue(handler)!;
		IList terrain = (IList)lists.GetValue(1)!;
		object[] saved = new object[terrain.Count];
		terrain.CopyTo(saved, 0);

		Type compat = Type.GetType("Vintagestory.Server.StratumWorldgenCompat, VintagestoryLib")!;
		MethodInfo setFallback = compat.GetProperty("TerrainRequiresSerialFallback")!.GetSetMethod(true)!;
		bool previous = (bool)compat.GetProperty("TerrainRequiresSerialFallback")!.GetValue(null)!;
		MethodInfo run = supply.GetType().GetMethod("runGenerators", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Type requestType = Type.GetType("Vintagestory.Server.ChunkColumnLoadRequest, VintagestoryLib")!;
		object request = Activator.CreateInstance(requestType, 1L, 8, 8, 0, 1, server)!;
		bool saw = false;
		ChunkColumnGenerationDelegate probe = _ => saw = StratumTerrainSplit.SerialTerrainStageActive;
		terrain.Clear();
		terrain.Add(probe);
		setFallback.Invoke(null, new object[] { true });
		try
		{
			run.Invoke(supply, new[] { request, 1 });
			Assert.True(saw);
			Assert.False(StratumTerrainSplit.SerialTerrainStageActive);
		}
		finally
		{
			setFallback.Invoke(null, new object[] { previous });
			StratumTerrainSplit.SerialTerrainStageActive = false;
			terrain.Clear();
			foreach (object item in saved)
			{
				terrain.Add(item);
			}
		}

		await World.Ticks(1);
	}

	private int[] Draw(object layers, MethodInfo place, IServerChunk[] chunks, int surfaceY, Block fertile, int seed)
	{
		int[] placed = new int[8];
		for (int i = 0; i < placed.Length; i++)
		{
			int localX = 4 + i;
			BlockPos ground = new BlockPos(8 * 32 + localX, surfaceY, 8 * 32 + 5);
			BlockPos above = ground.UpCopy();
			World.SetBlock(fertile.Code.ToString(), ground);
			World.SetBlock("game:air", above);
			Seed(layers, seed + i, 4);
			StratumTerrainSplit.SerialTerrainStageActive = true;
			try
			{
				place.Invoke(layers, new object[] { localX, surfaceY, 5, chunks, 1f, 1f, 20f, 0f, 0 });
			}
			finally
			{
				StratumTerrainSplit.SerialTerrainStageActive = false;
			}

			placed[i] = World.BlockAt(above).Id;
		}

		return placed;
	}

	private int[] DrawOnWorker(object layers, MethodInfo place, IServerChunk[] chunks, int surfaceY, Block fertile, int seed)
	{
		object workers = layers.GetType().GetField("stratumWorkers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(layers)!;
		object worker = workers.GetType().GetProperty("Current")!.GetValue(workers)!;
		int[] placed = new int[8];
		for (int i = 0; i < placed.Length; i++)
		{
			int localX = 4 + i;
			BlockPos ground = new BlockPos(8 * 32 + localX, surfaceY, 8 * 32 + 5);
			BlockPos above = ground.UpCopy();
			World.SetBlock(fertile.Code.ToString(), ground);
			World.SetBlock("game:air", above);
			Seed(worker, seed + i, 4);
			place.Invoke(layers, new object[] { localX, surfaceY, 5, chunks, 1f, 1f, 20f, 0f, 0 });
			placed[i] = World.BlockAt(above).Id;
		}

		return placed;
	}

	private void PolluteWorker(object layers, MethodInfo place, IServerChunk[] chunks, int surfaceY)
	{
		for (int i = 0; i < 24; i++)
		{
			Seed(layers, 200 + i, 90);
			place.Invoke(layers, new object[] { 4, surfaceY, 5, chunks, 1f, 1f, 20f, 0f, 0 });
		}
	}

	private static void Seed(object layers, int x, int z)
	{
		object rnd = layers.GetType().GetField("rnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(layers)!;
		MethodInfo init = rnd.GetType().GetMethod("InitPositionSeed", new[] { typeof(int), typeof(int) })!;
		init.Invoke(rnd, new object[] { x, z });
	}

	private Block FertileBlock()
	{
		foreach (Block block in World.Api.World.Blocks)
		{
			if (block != null && block.Fertility >= 30 && block.Fertility <= 70 && block.Id != 0)
			{
				return block;
			}
		}

		throw new InvalidOperationException("no block with fertility between 30 and 70");
	}

	private static string CalledMethods(MethodInfo method)
	{
		byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
		Module module = method.Module;
		var names = new System.Text.StringBuilder();
		for (int i = 0; i < il.Length - 4; i++)
		{
			if (il[i] != 0x28 && il[i] != 0x6F) continue;
			int token = BitConverter.ToInt32(il, i + 1);
			try
			{
				MethodBase called = module.ResolveMethod(token)!;
				names.Append(called.Name).Append(',');
			}
			catch (ArgumentException)
			{
			}
		}

		return names.ToString();
	}
}
