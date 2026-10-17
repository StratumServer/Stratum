using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Identical stacks that land together become one entity. A different item, a different
/// durability, or a different dropper stays its own entity. Despawning the pile removes
/// the whole quantity.
/// </summary>
public class GroundItemMergeScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task IdenticalStones_Should_BecomeOnePile()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("ground-merge");
		await World.Ticks(2);
		Vec3d pos = DropPos(player);
		Item stone = RequireItem(api, "game:stone-granite", "game:stone");
		StartTimings();
		for (int i = 0; i < 40; i++)
		{
			Entity entity = api.World.SpawnItemEntity(new ItemStack(stone, 1), pos, new Vec3d());
			((EntityItem)entity).ByPlayerUid = "stratum-merge";
		}

		await World.Ticks(40);
		EntityItem[] piles = Piles(api, stone.Code.Path, "stratum-merge");
		long mergedMs = ItemTypeMs();
		Assert.True(piles.Length == 1 && piles[0].Itemstack.StackSize == 40, "merged piles=" + piles.Length + " itemMs=" + mergedMs);

		SetEnabled(false);
		try
		{
			StartTimings();
			Vec3d apartPos = pos.AddCopy(0, 0, 2);
			for (int i = 0; i < 40; i++)
			{
				Entity entity = api.World.SpawnItemEntity(new ItemStack(stone, 1), apartPos, new Vec3d());
				((EntityItem)entity).ByPlayerUid = "stratum-merge-off";
			}

			await World.Ticks(40);
			int apartCount = Piles(api, stone.Code.Path, "stratum-merge-off").Length;
			long apartMs = ItemTypeMs();
			Assert.True(apartCount == 40, "separate piles=" + apartCount + " itemMs=" + apartMs + " mergedMs=" + mergedMs);
			Console.WriteLine("ground-merge itemMs merged=" + mergedMs + " apart=" + apartMs);
		}
		finally
		{
			SetEnabled(true);
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task DifferentIdentity_Should_StaySeparate()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("ground-identity");
		await World.Ticks(2);
		Vec3d pos = DropPos(player);
		Item stone = RequireItem(api, "game:stone-granite", "game:stone");
		Item tool = api.World.GetItem(new AssetLocation("game:pickaxe-iron"));
		Spawn(api, new ItemStack(stone, 1), pos, "dropper-a");
		Spawn(api, new ItemStack(stone, 1), pos, "dropper-b");
		if (tool != null)
		{
			ItemStack worn = new ItemStack(tool, 1);
			worn.Attributes.SetInt("durability", 10);
			ItemStack fresh = new ItemStack(tool, 1);
			fresh.Attributes.SetInt("durability", tool.Durability);
			Spawn(api, worn, pos, "dropper-a");
			Spawn(api, fresh, pos, "dropper-a");
		}

		await World.Ticks(40);
		Assert.Single(Piles(api, stone.Code.Path, "dropper-a"));
		Assert.Single(Piles(api, stone.Code.Path, "dropper-b"));
		if (tool != null)
		{
			Assert.Equal(2, Piles(api, tool.Code.Path, "dropper-a").Length);
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task DespawningThePile_Should_RemoveTheWholeQuantity()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("ground-despawn");
		await World.Ticks(2);
		Vec3d pos = DropPos(player);
		Item stone = RequireItem(api, "game:stone-granite", "game:stone");
		for (int i = 0; i < 40; i++)
		{
			Spawn(api, new ItemStack(stone, 1), pos, "stratum-despawn");
		}

		await World.Ticks(40);
		EntityItem[] piles = Piles(api, stone.Code.Path, "stratum-despawn");
		Assert.Single(piles);
		Assert.Equal(40, piles[0].Itemstack.StackSize);
		piles[0].Die(EnumDespawnReason.Removed);
		await World.Ticks(2);
		Assert.Empty(Piles(api, stone.Code.Path, "stratum-despawn"));
	}

	private static void Spawn(ICoreServerAPI api, ItemStack stack, Vec3d pos, string dropper)
	{
		Entity entity = api.World.SpawnItemEntity(stack, pos, new Vec3d());
		((EntityItem)entity).ByPlayerUid = dropper;
	}

	private static Vec3d DropPos(ITestPlayer player)
	{
		return player.Player.Entity.Pos.XYZ.AddCopy(1.2, 0.2, 0.2);
	}

	private static Item RequireItem(ICoreServerAPI api, params string[] codes)
	{
		foreach (string code in codes)
		{
			Item item = api.World.GetItem(new AssetLocation(code));
			if (item != null)
			{
				return item;
			}
		}

		throw new InvalidOperationException("none of the item codes resolved: " + string.Join(", ", codes));
	}

	private static EntityItem[] Piles(ICoreServerAPI api, string path, string dropper)
	{
		List<EntityItem> found = new List<EntityItem>();
		foreach (Entity entity in api.World.LoadedEntities.Values)
		{
			if (entity is not EntityItem item || !item.Alive || item.Itemstack?.Collectible?.Code?.Path != path)
			{
				continue;
			}

			if (item.ByPlayerUid == dropper)
			{
				found.Add(item);
			}
		}

		return found.ToArray();
	}

	private static void SetEnabled(bool enabled)
	{
		object config = GroundConfig();
		config.GetType().GetProperty("Enabled")!.SetValue(config, enabled);
	}

	private static object GroundConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
			?? throw new InvalidOperationException("StratumRuntime was not loaded");
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
		return performance.GetType().GetProperty("GroundItems")!.GetValue(performance)!;
	}

	private static void StartTimings()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!;
		object timings = runtime.GetProperty("Timings")!.GetValue(null)!;
		timings.GetType().GetMethod("Start")!.Invoke(timings, null);
	}

	private static long ItemTypeMs()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!;
		object timings = runtime.GetProperty("Timings")!.GetValue(null)!;
		string report = (string)timings.GetType().GetMethod("BuildReport")!.Invoke(timings, null)!;
		foreach (string line in report.Split('\n'))
		{
			if (!line.Contains("entity.type.item "))
			{
				continue;
			}

			int total = line.IndexOf("total=", StringComparison.Ordinal);
			int ms = line.IndexOf("ms", total, StringComparison.Ordinal);
			if (total >= 0 && ms > total)
			{
				string number = line.Substring(total + 6, ms - (total + 6));
				if (double.TryParse(number, out double value))
				{
					return (long)value;
				}
			}
		}

		return 0;
	}
}
