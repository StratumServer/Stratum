using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A Terrain peek has to run both halves of the split terrain pass, and the
/// returned area stays the centre column. Peeks above Terrain also use vanilla's
/// area, one ring smaller than indev. The world has to be standard: superflat
/// never splits the Terrain pass, so soil would appear even with the bug.
/// </summary>
[AtlasWorld(WorldType = "standard", Seed = 7351)]
public class TerrainPeekScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task TerrainPeek_Should_IncludeSoilOnTheCentreColumn()
	{
		int chunkX = 140;
		int chunkZ = 150;
		Dictionary<Vec2i, IServerChunk[]>? peeked = null;
		World.Api.WorldManager.PeekChunkColumn(chunkX, chunkZ, new ChunkPeekOptions
		{
			UntilPass = EnumWorldGenPass.Terrain,
			OnGenerated = columns => peeked = columns
		});
		await World.Until(() => peeked != null, timeoutTicks: 2400);
		Assert.NotNull(peeked);

		Assert.Single(peeked);
		IServerChunk[] chunks = Assert.Single(peeked.Values);
		int soil = 0;
		foreach (IServerChunk chunk in chunks)
		{
			if (chunk?.Data == null) continue;
			int length = chunk.Data.Length;
			for (int i = 0; i < length; i++)
			{
				int id = chunk.Data[i];
				if (id == 0) continue;
				Block block = World.Api.World.Blocks[id];
				if (block != null && block.BlockMaterial == EnumBlockMaterial.Soil)
				{
					soil++;
				}
			}
		}

		Assert.True(soil > 0, "a Terrain peek was still granite with no soil");
	}
}
