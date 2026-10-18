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
	public async Task Mining_Should_Trace_And_BothBreaksStick_When_TheSkipIsHigh()
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
			object stand = player.Player.Entity.Pos;
			PropertyInfo standX = stand.GetType().GetProperty("X")!;
			PropertyInfo standY = stand.GetType().GetProperty("Y")!;
			PropertyInfo standZ = stand.GetType().GetProperty("Z")!;
			double lifted = (double)standY.GetValue(stand)! + 4;
			standY.SetValue(stand, lifted);
			player.Player.Entity.ServerPos.Y = lifted;
			int fx = (int)Math.Floor((double)standX.GetValue(stand)!);
			int fy = (int)Math.Floor(lifted);
			int fz = (int)Math.Floor((double)standZ.GetValue(stand)!);
			BlockPos first = new BlockPos(fx + 2, fy, fz);
			BlockPos second = new BlockPos(fx + 3, fy, fz);
			World.SetBlock("game:soil-medium-normal", first);
			World.SetBlock("game:soil-medium-normal", second);
			await World.Ticks(2);
			HoldMouse(player, true);
			await BreakAimed(player, first);
			await BreakAimed(player, second);
			Assert.Equal("game:air", World.BlockAt(first).Code.ToString());
			Assert.Equal("game:air", World.BlockAt(second).Code.ToString());
		}
		finally
		{
			HoldMouse(player, false);
			baseSkip.SetValue(selection, previousBase);
			maxSkip.SetValue(selection, previousMax);
		}
	}

	private async Task BreakAimed(ITestPlayer player, BlockPos block)
	{
		object entity = player.Player.Entity;
		object pos = entity.GetType().GetProperty("Pos")!.GetValue(entity)!;
		object serverPos = entity.GetType().GetProperty("ServerPos")!.GetValue(entity)!;
		PropertyInfo x = pos.GetType().GetProperty("X")!;
		PropertyInfo y = pos.GetType().GetProperty("Y")!;
		PropertyInfo z = pos.GetType().GetProperty("Z")!;
		PropertyInfo yaw = pos.GetType().GetProperty("Yaw")!;
		PropertyInfo pitch = pos.GetType().GetProperty("Pitch")!;
		(float yawValue, double ox, double oz)[] stands =
		{
			(0f, 0, -1.4),
			(MathF.PI, 0, 1.4),
			(MathF.PI / 2f, -1.4, 0),
			(-MathF.PI / 2f, 1.4, 0)
		};
		bool aimed = false;
		string last = "none";
		long tracesBefore = SelectionTraces(World.Api.World);
		foreach ((float yawValue, double ox, double oz) in stands)
		{
			foreach (float pitchValue in new[] { -0.9f, -0.4f, 0f, 0.4f, 0.9f })
			{
				yaw.SetValue(pos, yawValue);
				pitch.SetValue(pos, pitchValue);
				yaw.SetValue(serverPos, yawValue);
				pitch.SetValue(serverPos, pitchValue);
				await World.Ticks(1);
				last = "pos " + x.GetValue(pos) + "," + y.GetValue(pos) + "," + z.GetValue(pos);
				object selection = entity.GetType().GetField("BlockSelection")!.GetValue(entity);
				if (selection == null)
				{
					last += " sel null";
					continue;
				}
				object selected = selection.GetType().GetField("Position")!.GetValue(selection)!;
				int sx = (int)selected.GetType().GetField("X")!.GetValue(selected)!;
				int sy = (int)selected.GetType().GetField("Y")!.GetValue(selected)!;
				int sz = (int)selected.GetType().GetField("Z")!.GetValue(selected)!;
				last = sx + "," + sy + "," + sz;
				if (sx == block.X && sy == block.Y && sz == block.Z)
				{
					aimed = true;
					break;
				}
			}
			if (aimed) break;
		}
		Assert.True(aimed, "the mouse-down trace did not select " + block + " last " + last + " traces " + (SelectionTraces(World.Api.World) - tracesBefore));
		object packet = CreateBlockBreakPacket(block);
		object server = World.Api.World;
		server.GetType().GetField("<IsDedicatedServer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, true);
		dynamic clients = server.GetType().GetField("Clients")!.GetValue(server)!;
		object client = clients[player.Player.ClientId];
		Type rcpType = Type.GetType("Vintagestory.Server.ReceivedClientPacket, VintagestoryLib")!;
		object received = Activator.CreateInstance(rcpType, client, packet, 1)!;
		server.GetType().GetMethod("DispatchClientPacket_mainthread", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, new[] { received });
		await World.Ticks(3);
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
