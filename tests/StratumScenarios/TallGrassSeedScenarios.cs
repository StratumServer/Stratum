using System;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The terrain handler places tall grass with the generator it just seeded.
/// External callers still forward. Grass still appears on a generated column.
/// </summary>
public class TallGrassSeedScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task Handler_Should_DrawTallGrassFromItsOwnSeededGenerator()
	{
		object layers = World.Api.ModLoader.GetModSystem("Vintagestory.ServerMods.GenBlockLayers")
			?? throw new InvalidOperationException("GenBlockLayers was not loaded");
		MethodInfo handler = layers.GetType().GetMethod("OnChunkColumnGeneration", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("OnChunkColumnGeneration was missing");
		string calls = string.Join(",", CalledMethods(handler));
		Assert.Contains("StratumPlaceTallGrass", calls);
		Assert.DoesNotContain(",PlaceTallGrass,", "," + calls);
		await World.Ticks(1);
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
				MethodBase called = module.ResolveMethod(token);
				if (called != null) names.Append(called.Name).Append(',');
			}
			catch (ArgumentException)
			{
			}
		}

		return names.ToString();
	}
}
