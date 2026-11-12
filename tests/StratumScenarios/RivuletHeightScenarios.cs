using System;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A complete column draws rivulet heights from MapSizeY - surfaceY, the vanilla bound.
/// </summary>
public class RivuletHeightScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task CompleteColumn_Should_UseTheVanillaRivuletHeightBound()
	{
		await World.Ticks(1);
		Type rivulets = Type.GetType("Vintagestory.ServerMods.GenRivulets, VSEssentials")
			?? throw new InvalidOperationException("GenRivulets was not loaded");
		MethodInfo bound = rivulets.GetMethod("StratumRivuletAboveSurfaceHeight", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
			?? throw new InvalidOperationException("the rivulet height bound was not on GenRivulets");

		int mapSizeY = World.Api.WorldManager.MapSizeY;
		int surfaceY = 121;
		int actual = (int)bound.Invoke(null, new object[] { mapSizeY, surfaceY })!;
		int vanilla = mapSizeY - surfaceY;
		int previousOffByOne = Math.Min(mapSizeY - 2, mapSizeY - 1) - surfaceY + 1;
		Assert.Equal(vanilla, actual);
		Assert.NotEqual(previousOffByOne, actual);
	}
}
