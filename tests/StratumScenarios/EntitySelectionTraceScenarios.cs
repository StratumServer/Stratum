using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The occlusion raytrace is what feeds OnBeingLookedAt. With a playing client and the
/// pass left on, a few server ticks have to record at least one trace.
/// </summary>
public class EntitySelectionTraceScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task PlayingClient_Should_Trace_When_OcclusionIsOn()
	{
		await World.JoinPlayer("select-on");
		long before = SelectionTraces(World.Api.World);
		await World.Ticks(10);
		Assert.True(SelectionTraces(World.Api.World) > before, "a playing client produced no occlusion traces");
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task HeldBreak_Should_AdvanceToTheBlockBehind_WithoutTurning()
	{
		ITestPlayer player = await World.JoinPlayer("select-mine");
		player.Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
		object selection = SelectionConfig();
		PropertyInfo baseSkip = selection.GetType().GetProperty("BaseSkipTicks")!;
		PropertyInfo maxSkip = selection.GetType().GetProperty("MaxSkipTicks")!;
		int previousBase = (int)baseSkip.GetValue(selection)!;
		int previousMax = (int)maxSkip.GetValue(selection)!;
		baseSkip.SetValue(selection, 1000);
		maxSkip.SetValue(selection, 1000);
		try
		{
			await player.TeleportTo(World.Spawn);
			await player.GiveItem("game:shovel-iron", 1);
			player.Player.WorldData.PickingRange = 8f;
			await World.Ticks(1);
			object entity = player.Player.Entity;
			object pos = entity.GetType().GetProperty("Pos")!.GetValue(entity)!;
			object serverPos = entity.GetType().GetProperty("ServerPos")!.GetValue(entity)!;
			PropertyInfo standX = pos.GetType().GetProperty("X")!;
			PropertyInfo standY = pos.GetType().GetProperty("Y")!;
			PropertyInfo standZ = pos.GetType().GetProperty("Z")!;
			double lifted = (double)standY.GetValue(pos)! + 4;
			standY.SetValue(pos, lifted);
			player.Player.Entity.ServerPos.Y = lifted;
			int fx = (int)Math.Floor((double)standX.GetValue(pos)!);
			int fy = (int)Math.Floor(lifted);
			int fz = (int)Math.Floor((double)standZ.GetValue(pos)!);
			BlockPos first = new BlockPos(fx + 2, fy, fz);
			BlockPos second = new BlockPos(fx + 3, fy, fz);
			World.SetBlock("game:soil-medium-normal", first);
			World.SetBlock("game:soil-medium-normal", second);
			AimOnce(pos, serverPos, -MathF.PI / 2f, -0.4f);
			HoldMouse(player, true);
			await World.Ticks(1);
			Assert.Equal(Format(first), Format(SelectedBlock(entity)));

			Break(player, first);
			await World.Ticks(1);
			Assert.Equal("game:air", World.BlockAt(first).Code.ToString());
			Assert.Equal(Format(second), Format(SelectedBlock(entity)));

			Break(player, second);
			await World.Ticks(1);
			Assert.Equal("game:air", World.BlockAt(second).Code.ToString());
		}
		finally
		{
			HoldMouse(player, false);
			baseSkip.SetValue(selection, previousBase);
			maxSkip.SetValue(selection, previousMax);
		}
	}

	private static void AimOnce(object pos, object serverPos, float yaw, float pitch)
	{
		PropertyInfo yawProperty = pos.GetType().GetProperty("Yaw")!;
		PropertyInfo pitchProperty = pos.GetType().GetProperty("Pitch")!;
		yawProperty.SetValue(pos, yaw);
		pitchProperty.SetValue(pos, pitch);
		yawProperty.SetValue(serverPos, yaw);
		pitchProperty.SetValue(serverPos, pitch);
	}

	private static BlockPos SelectedBlock(object entity)
	{
		object selection = entity.GetType().GetField("BlockSelection")!.GetValue(entity)
			?? throw new InvalidOperationException("the player has no block selection");
		object selected = selection.GetType().GetField("Position")!.GetValue(selection)!;
		int x = (int)selected.GetType().GetField("X")!.GetValue(selected)!;
		int y = (int)selected.GetType().GetField("Y")!.GetValue(selected)!;
		int z = (int)selected.GetType().GetField("Z")!.GetValue(selected)!;
		return new BlockPos(x, y, z);
	}

	private static string Format(BlockPos pos) => pos.X + "," + pos.Y + "," + pos.Z;

	private void Break(ITestPlayer player, BlockPos block)
	{
		object packet = CreateBlockBreakPacket(block);
		object server = World.Api.World;
		server.GetType().GetField("<IsDedicatedServer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, true);
		dynamic clients = server.GetType().GetField("Clients")!.GetValue(server)!;
		object client = clients[player.Player.ClientId];
		Type rcpType = Type.GetType("Vintagestory.Server.ReceivedClientPacket, VintagestoryLib")!;
		object received = Activator.CreateInstance(rcpType, client, packet, 1)!;
		server.GetType().GetMethod("DispatchClientPacket_mainthread", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, new[] { received });
	}

	private static void HoldMouse(ITestPlayer player, bool down)
	{
		object controls = player.Player.Entity.ServerControls;
		controls.GetType().GetProperty("LeftMouseDown")!.SetValue(controls, down);
	}

	private static object CreateBlockBreakPacket(BlockPos pos)
	{
		Type breakType = Type.GetType("Packet_ClientBlockPlaceOrBreak, VintagestoryLib")!;
		dynamic breakPacket = Activator.CreateInstance(breakType)!;
		breakPacket.Mode = 0;
		breakPacket.X = pos.X;
		breakPacket.Y = pos.InternalY;
		breakPacket.Z = pos.Z;
		breakPacket.OnBlockFace = (int)BlockFacing.NORTH.Index;
		Type packetClientType = Type.GetType("Packet_Client, VintagestoryLib")!;
		dynamic packet = Activator.CreateInstance(packetClientType)!;
		packet.Id = 3;
		packet.BlockPlaceOrBreak = breakPacket;
		return packet;
	}

	private static object SelectionConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
			?? throw new InvalidOperationException("StratumRuntime was not in the loaded lib");
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
		return performance.GetType().GetProperty("EntitySelection")!.GetValue(performance)!;
	}

	internal static long SelectionTraces(object server)
	{
		object runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!
			.GetProperty("PerformanceStats")!.GetValue(null)!;
		FieldInfo field = runtime.GetType().GetField("totalEntitySelectionTraces", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("totalEntitySelectionTraces was not on the stats");
		return (long)field.GetValue(runtime)!;
	}
}
