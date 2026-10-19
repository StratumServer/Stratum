using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// An empty chest does the perish scan once. A second tick with no slot change
/// and no neighbour change does not scan again.
/// </summary>
public class ChestTickScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task EmptyChest_Should_SkipThePerishScan_UntilSomethingChanges()
	{
		await World.Ticks(1);
		BlockPos pos = World.Spawn.AddCopy(3, 1, 3);
		World.SetBlock("game:chest-east", pos);
		await World.Ticks(5);
		object blockEntity = World.Api.World.BlockAccessor.GetBlockEntity(pos);
		Assert.NotNull(blockEntity);
		object container = Container(blockEntity);
		Tick(container);
		Assert.Equal(1, FullTicks(container));
		Tick(container);
		Assert.Equal(1, FullTicks(container));

		container.GetType().GetMethod("StratumNoteNeighbour")!.Invoke(container, null);
		Tick(container);
		Assert.Equal(2, FullTicks(container));
	}

	private static object Container(object blockEntity)
	{
		FieldInfo field = null;
		for (Type type = blockEntity.GetType(); type != null && field == null; type = type.BaseType)
		{
			field = type.GetField("container", BindingFlags.Instance | BindingFlags.NonPublic);
		}
		return field!.GetValue(blockEntity)!;
	}

	private static void Tick(object container)
	{
		container.GetType().GetMethod("OnTick")!.Invoke(container, new object[] { 1f });
	}

	private static int FullTicks(object container)
	{
		return (int)container.GetType()
			.GetField("StratumFullTicks", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(container)!;
	}
}
