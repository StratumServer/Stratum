using System.Text.RegularExpressions;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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

		TextCommandResult selfService = await ExecuteAs(player, $"/class change {FirstClass}");
		Assert.Equal(EnumCommandStatus.Success, selfService.Status);
		Assert.Equal(FirstClass, CurrentClass(player));

		TextCommandResult requested = await ExecuteAs(player, $"/class change {SecondClass} picked the wrong one");
		Assert.Equal(EnumCommandStatus.Success, requested.Status);
		Assert.Contains("Request", requested.StatusMessage);
		Assert.Equal(FirstClass, CurrentClass(player));

		CommandResult approve = await World.ExecuteCommand($"/classrequests approve {RequestId(requested)}");
		Assert.True(approve.Ok, approve.Message);
		Assert.Equal(SecondClass, CurrentClass(player));

		TextCommandResult deniedRequest = await ExecuteAs(player, $"/class change {ThirdClass}");
		Assert.Equal(EnumCommandStatus.Success, deniedRequest.Status);
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

		TextCommandResult requested = await ExecuteAs(player, $"/class change {target}");
		Assert.Equal(EnumCommandStatus.Success, requested.Status);
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

	private static int RequestId(TextCommandResult result)
	{
		Match match = Regex.Match(result.StatusMessage ?? string.Empty, @"Request #(\d+)");
		Assert.True(match.Success, $"no request id in: {result.StatusMessage}");
		return int.Parse(match.Groups[1].Value);
	}

	private Task<TextCommandResult> ExecuteAs(ITestPlayer player, string command)
	{
		// IWorldSession.ExecuteCommand runs as the console, which is not a player and cannot
		// use /class.
		var completion = new TaskCompletionSource<TextCommandResult>();
		World.Api.ChatCommands.ExecuteUnparsed(
			command,
			new TextCommandCallingArgs
			{
				Caller = new Caller
				{
					Type = EnumCallerType.Player,
					Player = player.Player,
					FromChatGroupId = GlobalConstants.GeneralChatGroup,
				},
			},
			result => completion.TrySetResult(result));
		return completion.Task;
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
