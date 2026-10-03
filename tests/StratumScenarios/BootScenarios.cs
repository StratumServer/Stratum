using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Fundamentals: the server boots this repo's patched lib, the clock advances, a block
/// write reads back, a joined player survives ticking, and an unknown command still
/// reports the vanilla error code. Most of that is not Stratum-specific behavior, which
/// is the point: a fork that breaks one of these breaks every server running it. The
/// last two scenarios are the exception, and they only check registration: the fork's
/// own command groups have to be reachable at all.
/// </summary>
public class BootScenarios : AtlasScenarioBase
{
	[AtlasScenario]
	public void Server_Should_RunPatchedLib_When_Built()
	{
		// Guards the worst false green available here. Built without
		// -p:EmbedPatchedFiles=true, the launcher has nothing to overlay and the server
		// runs the downloaded vanilla VintagestoryLib.dll: every scenario below would
		// pass while testing vanilla. StratumRuntime exists only in this repo's lib.
		// The type probe is what forces the load, so read the location after it or the
		// message can say "<not loaded>" about an assembly that resolves fine.
		bool patched = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib") != null;
		string loaded = AppDomain.CurrentDomain.GetAssemblies()
			.FirstOrDefault(a => a.GetName().Name == "VintagestoryLib")?.Location ?? "<not loaded>";
		Assert.True(
			patched,
			"the loaded VintagestoryLib.dll carries no Vintagestory.Server.StratumRuntime, so it is not "
			+ "this repo's build. Rebuild with -p:EmbedPatchedFiles=true and rerun "
			+ $"--stratum-prepare-only before testing. Loaded from: {loaded}");
	}

	[AtlasScenario]
	public async Task Server_Should_BootAndAdvanceClock_When_Ticked()
	{
		// The game calendar pauses on an empty server, so the boot check reads the
		// server clock instead.
		long before = World.Api.World.ElapsedMilliseconds;
		await World.Ticks(30);
		Assert.True(World.Api.World.ElapsedMilliseconds > before, "server clock did not advance");
	}

	[AtlasScenario]
	public async Task SetBlock_Should_ReadBackSameCode_When_Placed()
	{
		BlockPos pos = World.Spawn.AddCopy(2, 1, 2);
		World.SetBlock("game:rock-granite", pos);
		await World.Ticks(5);
		Assert.Equal("game:rock-granite", World.BlockAt(pos).Code.ToString());
	}

	[AtlasScenario]
	public async Task JoinedPlayer_Should_StayConnected_When_ServerTicks()
	{
		// This doubles as a packet-policing check: StratumPacketLimiter has no
		// single-player exemption, so a kicked test player shows up as IsConnected false.
		ITestPlayer player = await World.JoinPlayer("boot-smoke");
		await World.Ticks(100);
		Assert.True(player.IsConnected, "test player was disconnected");
	}

	[AtlasScenario]
	public async Task UnknownCommand_Should_ReportErrorCode_When_Executed()
	{
		// Argument handling and command output are covered far better by
		// scripts/smoke-test.sh, which pipes commands into a real server and checks the
		// log. This one stays because the error code is a hard-coded contract that the
		// fork's command access layer sits in front of.
		CommandResult result = await World.ExecuteCommand("/nosuchcommandanywhere");

		Assert.False(result.Ok);
		Assert.Equal("nosuchcommand", result.ErrorCode);
	}

	[AtlasScenario]
	public async Task TimeAdd_Should_AdvanceCalendar_When_Executed()
	{
		double before = World.Calendar.TotalHours;
		CommandResult result = await World.ExecuteCommand("/time add 2");

		Assert.True(result.Ok, $"/time add failed: {result.Message}");
		await World.Until(() => World.Calendar.TotalHours > before, timeoutTicks: 100);
	}

	[AtlasScenario]
	public void StratumCommand_Should_BeRegistered_When_ServerStarts()
	{
		Assert.True(
			World.Api.ChatCommands.Get("stratum") != null,
			"/stratum is not registered with the server's chat command API");
	}

	[AtlasScenario]
	public void HomeCommands_Should_BeRegistered_When_ServerStarts()
	{
		// Registration is the contract under test, so ask the command API directly rather
		// than executing: the console caller has no world position and /sethome would
		// error anyway. smoke-test.sh probes /kit and /friendlyfire but nothing in the
		// homes and tpa group, so a registration regression there would otherwise pass
		// everything the repo runs.
		Assert.True(
			World.Api.ChatCommands.Get("sethome") != null,
			"/sethome is not registered with the server's chat command API");
	}
}
