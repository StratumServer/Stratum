using System;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Two chiseled blocks open at once must not share one voxel grid.
/// </summary>
public class MicroblockEditScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task BeginEdit_Should_HandOutDistinctGrids()
	{
		ITestPlayer player = await World.JoinPlayer("chisel-grids");
		await player.TeleportTo(World.Spawn);
		BlockPos firstPos = World.Spawn.AddCopy(2, 1, 2);
		BlockPos secondPos = World.Spawn.AddCopy(3, 1, 2);
		World.SetBlock("game:chiseledblock", firstPos);
		World.SetBlock("game:chiseledblock", secondPos);
		await World.Ticks(5);

		object first = World.Api.World.BlockAccessor.GetBlockEntity(firstPos)
			?? throw new InvalidOperationException("the first chiseled block has no block entity");
		object second = World.Api.World.BlockAccessor.GetBlockEntity(secondPos)
			?? throw new InvalidOperationException("the second chiseled block has no block entity");
		MethodInfo begin = first.GetType().GetMethod("BeginEdit")
			?? throw new InvalidOperationException("BeginEdit was not on " + first.GetType().Name);
		object[] firstArgs = new object[2];
		object[] secondArgs = new object[2];
		begin.Invoke(first, firstArgs);
		begin.Invoke(second, secondArgs);

		Assert.NotSame(firstArgs[0], secondArgs[0]);
		Assert.NotSame(firstArgs[1], secondArgs[1]);
	}
}
