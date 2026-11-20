using System.Text.RegularExpressions;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Issue #278: /class and /classrequests. The first scenario walks one player through the whole
/// ladder: a self-service change while they still have one, a request once they have run out, an
/// approval that switches the class at runtime, and a denial that leaves it alone. The second
/// covers the approval of a player who is offline, which must apply on their next join.
///
/// Each test player gets the createCharacter mod data by hand: a dummy-socket player never goes
/// through the character dialog, and /class refuses a player who has not created a character.
/// The approvals run as the console, the only caller the command must always accept.
/// </summary>
public class ClassChangeScenarios : AtlasScenarioBase
{
	private const string FirstClass = "hunter";
	private const string SecondClass = "tailor";
	private const string ThirdClass = "clockmaker";

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task ClassChange_Should_UseSelfServiceThenRequests_When_AllowanceRunsOut()
	{
		await SetSelfServiceChanges(1);
		ITestPlayer player = await JoinWithCharacter("class-ladder");

		CommandResult selfService = await player.ExecuteCommand($"/class change {FirstClass}");
		Assert.True(selfService.Ok, selfService.Message);
		Assert.Equal(FirstClass, CurrentClass(player));

		CommandResult requested = await player.ExecuteCommand($"/class change {SecondClass} picked the wrong one");
		Assert.True(requested.Ok, requested.Message);
		Assert.Contains("Request", requested.Message);
		Assert.Equal(FirstClass, CurrentClass(player));

		CommandResult approve = await World.ExecuteCommand($"/classrequests approve {RequestId(requested)}");
		Assert.True(approve.Ok, approve.Message);
		Assert.Equal(SecondClass, CurrentClass(player));

		CommandResult deniedRequest = await player.ExecuteCommand($"/class change {ThirdClass}");
		Assert.True(deniedRequest.Ok, deniedRequest.Message);
		CommandResult deny = await World.ExecuteCommand($"/classrequests deny {RequestId(deniedRequest)} not this season");
		Assert.True(deny.Ok, deny.Message);
		Assert.Equal(SecondClass, CurrentClass(player));

		// A settled request cannot be settled again.
		CommandResult again = await World.ExecuteCommand($"/classrequests approve {RequestId(deniedRequest)}");
		Assert.False(again.Ok);
		Assert.Equal(SecondClass, CurrentClass(player));
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Approval_Should_ApplyOnNextJoin_When_PlayerWasOffline()
	{
		await SetSelfServiceChanges(0);
		ITestPlayer player = await JoinWithCharacter("class-offline");
		string before = CurrentClass(player);
		string target = before == FirstClass ? SecondClass : FirstClass;

		CommandResult requested = await player.ExecuteCommand($"/class change {target}");
		Assert.True(requested.Ok, requested.Message);
		int requestId = RequestId(requested);

		player.Player.Disconnect();
		await World.Until(() => !player.IsConnected, timeoutTicks: 600);

		CommandResult approve = await World.ExecuteCommand($"/classrequests approve {requestId}");
		Assert.True(approve.Ok, approve.Message);
		Assert.Contains("next join", approve.Message);

		ITestPlayer rejoined = await RejoinAfterDisconnect("class-offline");
		await World.Until(() => CurrentClass(rejoined) == target, timeoutTicks: 200);

		CommandResult info = await World.ExecuteCommand($"/classrequests info {requestId}");
		Assert.True(info.Ok, info.Message);
		Assert.Contains("UTC", info.Message.Substring(info.Message.IndexOf("Applied", StringComparison.Ordinal)));
	}

	private async Task SetSelfServiceChanges(int changes)
	{
		CommandResult result = await World.ExecuteCommand($"/stratum set Commands.ClassChangeSettings.SelfServiceChanges {changes}");
		Assert.True(result.Ok, $"could not set SelfServiceChanges: {result.Message}");
	}

	private async Task<ITestPlayer> JoinWithCharacter(string name)
	{
		ITestPlayer player = await World.JoinPlayer(name);
		player.Player.SetModData("createCharacter", true);
		return player;
	}

	private static string CurrentClass(ITestPlayer player)
	{
		return player.Player.Entity.WatchedAttributes.GetString("characterClass");
	}

	private static int RequestId(CommandResult result)
	{
		Match match = Regex.Match(result.Message, @"Request #(\d+)");
		Assert.True(match.Success, $"no request id in: {result.Message}");
		return int.Parse(match.Groups[1].Value);
	}

	private async Task<ITestPlayer> RejoinAfterDisconnect(string name)
	{
		// JoinPlayer frees the joined-name claim from a game-thread check that runs a few ticks
		// after IsConnected flips, not synchronously with the disconnect.
		for (int attempt = 0; attempt < 30; attempt++)
		{
			try
			{
				return await World.JoinPlayer(name);
			}
			catch (AtlasSetupException)
			{
				await World.Ticks(10);
			}
		}

		throw new InvalidOperationException($"'{name}' never became rejoinable after the disconnect.");
	}
}
