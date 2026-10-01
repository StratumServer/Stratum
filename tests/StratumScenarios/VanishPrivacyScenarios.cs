using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Two ends of issue #312. The first scenario is the per-caller half of the vanish rule: /near
/// must still list the world for the vanished staff member and must not list the vanished staff
/// member for anyone else. It runs each /near through ITestPlayer.ExecuteCommand, which carries
/// the caller's real role and privileges, because IWorldSession.ExecuteCommand runs as the
/// console, which is an admin and would see everything regardless of the filter under test.
///
/// The second is the reconnect window fixed by this PR. Vanish persists in player data and is
/// now restored in FinalizePlayerIdentification, before the first SpawnEntity and before
/// ServerReady, so a vanished player reconnecting announces nothing. Restoring it later, from
/// OnPlayerJoin, leaves a window of several seconds in which nearby players get a join message, a
/// entity spawn with an exact position and a nametag, position updates, and then a despawn. This
/// scenario fails against that older code, which is the reason it exists. The reconnect assertion
/// checks what the observer's client was sent (the entity on any of its three paths, and the
/// player data), rather than relying only on the separate join message.
/// </summary>
public class VanishPrivacyScenarios : AtlasScenarioBase
{
	/// <summary>
	/// How long to wait before reading "the observer never received X". 31 passes is the slowest
	/// measured arrival of an entity on any path; 60 leaves a margin for a loaded run.
	/// </summary>
	private const int AbsenceWindowTicks = 60;

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Near_Should_HideVanishedStaff_When_CalledByPlainPlayer()
	{
		ITestPlayer observer = await World.JoinPlayer("vanish-obs");
		ITestPlayer staff = await World.JoinPlayer("vanish-mod");
		await GrantVanishRole(staff);
		// A dummy-socket test player rides IsSinglePlayerClient, which
		// ServerMain.HandleRequestJoin promotes to the server's max-privilege role on join. Demote
		// the observer explicitly so it actually lacks stratum.vanish, or it can already see
		// vanished staff and the negative assertion below proves nothing.
		await DemoteToPlainPlayer(observer);

		// JoinPlayer scatters players near spawn; pin both so the /near radius is not the
		// variable under test.
		await observer.TeleportTo(World.Spawn.AddCopy(0, 1, 0));
		await staff.TeleportTo(World.Spawn.AddCopy(4, 1, 0));
		await World.Ticks(10);

		CommandResult before = await observer.ExecuteCommand("/near 60");
		Assert.Contains("vanish-mod", before.Message);

		CommandResult vanish = await staff.ExecuteCommand("/vanish on");
		Assert.True(vanish.Ok, vanish.Message);
		await World.Ticks(10);

		// The vanished player still sees the world.
		CommandResult fromStaff = await staff.ExecuteCommand("/near 60");
		Assert.Contains("vanish-obs", fromStaff.Message);

		// The plain observer does not see the vanished player.
		CommandResult fromObserver = await observer.ExecuteCommand("/near 60");
		Assert.DoesNotContain("vanish-mod", fromObserver.Message);

		CommandResult unvanish = await staff.ExecuteCommand("/vanish off");
		Assert.True(unvanish.Ok, unvanish.Message);
		await World.Ticks(10);
		CommandResult after = await observer.ExecuteCommand("/near 60");
		Assert.Contains("vanish-mod", after.Message);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task VanishedReconnect_Should_NotAnnounceJoin_When_StillVanished()
	{
		ITestPlayer observer = await World.JoinPlayer("reconnect-obs");
		ITestPlayer staff = await World.JoinPlayer("reconnect-mod");
		await GrantVanishRole(staff);
		await DemoteToPlainPlayer(observer);

		CommandResult vanish = await staff.ExecuteCommand("/vanish on");
		Assert.True(vanish.Ok, vanish.Message);
		await World.Ticks(10);

		staff.Player.Disconnect();
		await World.Until(() => !staff.IsConnected, timeoutTicks: 600);

		// Start from "nothing received yet". Clear() forgets everything the observer was sent so far,
		// the original non-vanished join of "reconnect-mod" included: its entity, its player data and
		// its join announcement.
		observer.Client.Clear();

		ITestPlayer rejoined = await RejoinAfterDisconnect("reconnect-mod");
		ITestPlayer plain = await World.JoinPlayer("reconnect-plain");
		Assert.True(rejoined.IsConnected, "the vanished player did not come back");
		Assert.True(plain.IsConnected);

		// A client gets an entity on one of three paths and the slowest takes dozens of passes, so
		// wait the whole AbsenceWindowTicks (see its documentation) before reading an absence.
		await World.Ticks(AbsenceWindowTicks);

		// The control first: the same observer, the same window, a player who is not vanished. If this
		// fails, the absence below would only mean "nothing was captured".
		Assert.True(
			observer.Client.HasReceivedEntity(plain.Player.Entity.EntityId),
			"the observer never received the plain player's entity, so its observations prove nothing");
		Assert.True(observer.Client.HasReceivedPlayerData(plain.Player.PlayerUID));

		// The vanished player's entity (spawn, tracked range or join list: HasReceivedEntity is the
		// union of the three) and its player data (packet 41) must not have reached the observer.
		Assert.False(
			observer.Client.HasReceivedEntity(rejoined.Player.Entity.EntityId),
			$"the vanished player's entity reached the observer: {Describe(observer, rejoined.Player.Entity.EntityId)}");
		Assert.False(observer.Client.HasReceivedPlayerData(rejoined.Player.PlayerUID));

		IReadOnlyList<string> announcements = observer.Client.Chat()
			.Where(line => line.Type == EnumChatType.JoinLeave)
			.Select(line => line.Message)
			.ToList();
		Assert.DoesNotContain(announcements, line => line.Contains("reconnect-mod", StringComparison.Ordinal));
		Assert.Contains(announcements, line => line.Contains("reconnect-plain", StringComparison.Ordinal));
	}

	private async Task GrantVanishRole(ITestPlayer player)
	{
		// stratum.vanish lives in the staff privilege set, which the sumod role carries. The
		// console caller (IWorldSession.ExecuteCommand) has grantrevoke.
		CommandResult result = await World.ExecuteCommand($"/player {player.Player.PlayerName} role sumod");
		Assert.True(result.Ok, $"could not promote {player.Player.PlayerName}: {result.Message}");
		await World.Until(() => player.Player.HasPrivilege("stratum.vanish"), timeoutTicks: 200);
	}

	private async Task DemoteToPlainPlayer(ITestPlayer player)
	{
		// suplayer is ServerConfig.DefaultRoleCode: the ordinary survival-player role, which does
		// not carry stratum.vanish (only sumod, crmod and admin do).
		CommandResult result = await World.ExecuteCommand($"/player {player.Player.PlayerName} role suplayer");
		Assert.True(result.Ok, $"could not demote {player.Player.PlayerName}: {result.Message}");
		await World.Until(() => !player.Player.HasPrivilege("stratum.vanish"), timeoutTicks: 200);
	}

	private static string Describe(ITestPlayer observer, long entityId)
	{
		return string.Join(", ", observer.Client.EntityArrivals().Where(a => a.EntityId == entityId));
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
