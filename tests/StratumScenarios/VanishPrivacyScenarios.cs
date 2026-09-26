using System.Collections;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Two ends of issue #312. The first scenario is the per-caller half of the vanish rule: /near
/// must still list the world for the vanished staff member and must not list the vanished staff
/// member for anyone else. It runs each /near through the command API with an explicit Caller,
/// because IWorldSession.ExecuteCommand runs as the console, which is an admin and would see
/// everything regardless of the filter under test.
///
/// The second is the reconnect window fixed by this PR. Vanish persists in player data and is
/// now restored in FinalizePlayerIdentification, before the first SpawnEntity and before
/// ServerReady, so a vanished player reconnecting announces nothing. Restoring it later, from
/// OnPlayerJoin, leaves a window of several seconds in which nearby players get a join message, a
/// entity spawn with an exact position and a nametag, position updates, and then a despawn. This
/// scenario fails against that older code, which is the reason it exists. The reconnect assertion
/// checks the spawn packet directly, rather than relying only on the separate join message.
/// </summary>
public class VanishPrivacyScenarios : AtlasScenarioBase
{
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

		TextCommandResult before = await ExecuteAs(observer, "/near 60");
		Assert.Contains("vanish-mod", before.StatusMessage);

		TextCommandResult vanish = await ExecuteAs(staff, "/vanish on");
		Assert.Equal(EnumCommandStatus.Success, vanish.Status);
		await World.Ticks(10);

		// The vanished player still sees the world.
		TextCommandResult fromStaff = await ExecuteAs(staff, "/near 60");
		Assert.Contains("vanish-obs", fromStaff.StatusMessage);

		// The plain observer does not see the vanished player.
		TextCommandResult fromObserver = await ExecuteAs(observer, "/near 60");
		Assert.DoesNotContain("vanish-mod", fromObserver.StatusMessage);

		TextCommandResult unvanish = await ExecuteAs(staff, "/vanish off");
		Assert.Equal(EnumCommandStatus.Success, unvanish.Status);
		await World.Ticks(10);
		TextCommandResult after = await ExecuteAs(observer, "/near 60");
		Assert.Contains("vanish-mod", after.StatusMessage);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task VanishedReconnect_Should_NotAnnounceJoin_When_StillVanished()
	{
		ITestPlayer observer = await World.JoinPlayer("reconnect-obs");
		ITestPlayer staff = await World.JoinPlayer("reconnect-mod");
		await GrantVanishRole(staff);
		await DemoteToPlainPlayer(observer);

		TextCommandResult vanish = await ExecuteAs(staff, "/vanish on");
		Assert.Equal(EnumCommandStatus.Success, vanish.Status);
		await World.Ticks(10);

		staff.Player.Disconnect();
		await World.Until(() => !staff.IsConnected, timeoutTicks: 600);

		// Snapshot the observer's outbound queue AFTER the disconnect, so only the rejoin's own
		// traffic is inspected.
		ChatProbe probe = ChatProbe.Attach(World, observer);

		ITestPlayer rejoined = await RejoinAfterDisconnect("reconnect-mod");
		await World.Ticks(60);

		Assert.True(rejoined.IsConnected, "the vanished player did not come back");
		Assert.DoesNotContain(rejoined.Player.Entity.EntityId, probe.NewEntitySpawnIds());
		IReadOnlyList<string> announcements = probe.NewJoinLeaveMessages();
		Assert.DoesNotContain(
			announcements,
			line => line.Contains("reconnect-mod", StringComparison.Ordinal));

		// Sanity check: the probe is wired up correctly and does see a join it should see, so an
		// empty result above means "no message was sent", not "the probe caught nothing".
		ITestPlayer plain = await World.JoinPlayer("reconnect-plain");
		await World.Ticks(60);
		Assert.True(plain.IsConnected);
		Assert.Contains(
			probe.NewJoinLeaveMessages(),
			line => line.Contains("reconnect-plain", StringComparison.Ordinal));
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

	private Task<TextCommandResult> ExecuteAs(ITestPlayer player, string command)
	{
		// IWorldSession.ExecuteCommand runs as the console, an admin with every privilege and no
		// world position: useless for a per-caller visibility rule like /near or /vanish.
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

/// <summary>
/// Reads the packets the server actually sent to one test player: chat lines, entity spawns
/// and group lists.
/// </summary>
/// <remarks>
/// Atlas has no chat sink and the server has no hook on the outgoing path
/// (joinLeaveDeathMessage -> SendMessageToGeneral/SendMessageToGroup -> SendMessage ->
/// SendPacket), so this reads the one place the traffic lands: the player's dummy socket.
/// Nothing ever drains those buffers, so everything the server sent is still queued, in order.
/// Reflection rather than a typed reference on purpose: this project references only
/// VintagestoryAPI, and adding VintagestoryLib would put decompiled-tree types in signatures
/// that xUnit reflects over during discovery, before Atlas installs its AssemblyResolve hook
/// (see BootScenarios.Server_Should_RunPatchedLib_When_Built for the same constraint).
/// </remarks>
internal sealed class ChatProbe
{
	private const int ChatLinePacketId = 8;
	private const int EntitySpawnPacketId = 34;
	private const int PlayerGroupsPacketId = 49;
	private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;

	private readonly IEnumerable queue;
	private readonly object gate;
	private readonly int baseline;

	private ChatProbe(IEnumerable queue, object gate, int baseline)
	{
		this.queue = queue;
		this.gate = gate;
		this.baseline = baseline;
	}

	public static ChatProbe Attach(IWorldSession world, ITestPlayer player)
	{
		object server = world.Api.World;
		dynamic clients = server.GetType().GetField("Clients")!.GetValue(server)!;
		object client = clients[player.Player.ClientId];
		object socket = client.GetType().GetProperty("Socket")!.GetValue(client)!;

		object network = socket.GetType().GetField("network", Internal)!.GetValue(socket)!;
		object gate = network.GetType().GetField("ClientReceiveBufferLock", Internal)!.GetValue(network)!;
		var queue = (IEnumerable)network.GetType().GetField("ClientReceiveBuffer", Internal)!.GetValue(network)!;

		int baseline;
		lock (gate)
		{
			baseline = ((ICollection)queue).Count;
		}

		return new ChatProbe(queue, gate, baseline);
	}

	public IReadOnlyList<string> NewJoinLeaveMessages()
	{
		var messages = new List<string>();
		foreach (dynamic packet in NewPackets())
		{
			if ((int)packet.Id != ChatLinePacketId)
			{
				continue;
			}

			object? chatline = packet.Chatline;
			if (chatline == null)
			{
				continue;
			}

			dynamic line = chatline;
			if ((int)line.ChatType == (int)EnumChatType.JoinLeave)
			{
				messages.Add((string)line.Message);
			}
		}

		return messages;
	}

	public IReadOnlyList<long> NewEntitySpawnIds()
	{
		var ids = new List<long>();
		foreach (dynamic packet in NewPackets())
		{
			if ((int)packet.Id != EntitySpawnPacketId || packet.EntitySpawn == null)
			{
				continue;
			}

			dynamic spawn = packet.EntitySpawn;
			int count = (int)spawn.EntityCount;
			for (int index = 0; index < count; index++)
			{
				ids.Add((long)spawn.Entity[index].EntityId);
			}
		}

		return ids;
	}

	/// <summary>
	/// The group names in each full group list (packet 49, SendPlayerGroups) the player was sent,
	/// oldest first. A single-group update (packet 50, SendPlayerGroup) is not a listing and is
	/// left out: only the full list tells the client to drop a group it no longer holds.
	/// </summary>
	public IReadOnlyList<IReadOnlyList<string>> NewGroupListings()
	{
		var listings = new List<IReadOnlyList<string>>();
		foreach (dynamic packet in NewPackets())
		{
			if ((int)packet.Id != PlayerGroupsPacketId || packet.PlayerGroups == null)
			{
				continue;
			}

			dynamic groups = packet.PlayerGroups;
			int count = (int)groups.GroupsCount;
			var names = new List<string>();
			for (int index = 0; index < count; index++)
			{
				names.Add((string)groups.Groups[index].Name);
			}

			listings.Add(names);
		}

		return listings;
	}

	private IReadOnlyList<object> NewPackets()
	{
		var payloads = new List<(byte[] Data, int Length)>();
		lock (gate)
		{
			int index = 0;
			foreach (object item in queue)
			{
				if (index++ < baseline)
				{
					continue;
				}

				dynamic entry = item;
				payloads.Add(((byte[])entry.Data, (int)entry.Length));
			}
		}

		Type serializerType = Type.GetType("Packet_ServerSerializer, VintagestoryLib")!;
		Type packetType = Type.GetType("Packet_Server, VintagestoryLib")!;
		MethodInfo deserialize = serializerType.GetMethod("DeserializeBuffer")!;

		var packets = new List<object>();
		foreach ((byte[] data, int length) in payloads)
		{
			packets.Add(deserialize.Invoke(null, new object?[] { data, length, Activator.CreateInstance(packetType) })!);
		}

		return packets;
	}
}
