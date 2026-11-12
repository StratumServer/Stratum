using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Three behaviors that were listed as not tested.
///
/// Moderation with a connected player: /note add, list and delete, /mute and /unmute, and
/// /jail, /unjail and /jailstatus. Each one runs as a real command through a joined staff
/// player (ITestPlayer.ExecuteCommand, the player's real role) against a second joined player.
/// The mute scenario checks the effect on chat itself, not only the command's reply: the
/// muted player's line goes through the real chat packet path (ITestPlayer.Say) and a third
/// joined player must not receive it, then must receive the next one after /unmute.
///
/// Anticheat punishment escalation (StratumAnticheatPunishments, StratumAnticheatHistory) and
/// the client mod whitelist (StratumRuntime.BuildClientModWhitelist) are internal to
/// VintagestoryLib, and the test project references only VintagestoryAPI with no
/// InternalsVisibleTo toward it. They are reached by reflection on the lib the in-process
/// server loaded, as ChunkDataLayerDecodeEquivalenceScenarios and StoryStructureBoundaryScenarios
/// already do. That runs the production code itself; a rename or signature change fails the
/// lookup with a message naming the member, never a silent pass.
///
/// Punishment escalation is checked twice: HighestDueTier as a pure function, and Evaluate with
/// a joined player, which is the exact call StratumAnticheatReporter.RecordViolation makes once
/// per confirmed violation. That second half asserts the persisted history record and the
/// real effects of each tier (inventory dropped, player frozen, /jailstatus reporting the jail).
/// The ban tier is kept out of reach in the Evaluate scenarios on purpose: it bans and
/// disconnects the player, and the pure-function scenario already covers its threshold.
///
/// The whole class shares one server boot, so every scenario uses its own player names and
/// restores the config values it changed.
/// </summary>
public class ModerationAndPolicyVerificationScenarios : AtlasScenarioBase
{
	private const string HistoryKey = "stratum.anticheat.history.v1";
	private const string Flint = "game:flint";
	private const int OutOfReach = 100000;

	// Tier ordinals of StratumAnticheatPunishments.Tier, as stored in HighestPunishmentApplied.
	private const int TierNone = 0;
	private const int TierDropInventory = 1;
	private const int TierFreeze = 2;
	private const int TierJail = 3;
	private const int TierBan = 4;

	// ------------------------------------------------------------------ gap A: moderation

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Note_Should_ListAddedNote_And_DropItAfterDelete_When_TargetConnected()
	{
		ITestPlayer staff = await World.JoinPlayer("mod-note-staff");
		ITestPlayer target = await World.JoinPlayer("mod-note-target");
		string name = target.Player.PlayerName;
		Assert.True(target.IsConnected);

		CommandResult empty = await staff.ExecuteCommand($"/note {name} list");
		Assert.True(empty.Ok, empty.Message);
		Assert.Contains("has no active staff notes", empty.Message);

		CommandResult added = await staff.ExecuteCommand($"/note {name} add traded stolen ore at the market");
		Assert.True(added.Ok, added.Message);
		int noteId = ParseRecordId(added.Message);

		CommandResult listed = await staff.ExecuteCommand($"/note {name} list");
		Assert.True(listed.Ok, listed.Message);
		Assert.Contains("traded stolen ore at the market", listed.Message);
		Assert.Contains("#" + noteId, listed.Message);
		Assert.Contains(staff.Player.PlayerName, listed.Message);
		Assert.DoesNotContain("has no active staff notes", listed.Message);

		CommandResult deleted = await staff.ExecuteCommand($"/note {name} delete {noteId}");
		Assert.True(deleted.Ok, deleted.Message);

		CommandResult afterDelete = await staff.ExecuteCommand($"/note {name} list");
		Assert.True(afterDelete.Ok, afterDelete.Message);
		Assert.Contains("has no active staff notes", afterDelete.Message);
		Assert.DoesNotContain("traded stolen ore at the market", afterDelete.Message);

		// Deleting the same id twice is refused: the note is really gone, not hidden from the list.
		CommandResult deletedAgain = await staff.ExecuteCommand($"/note {name} delete {noteId}");
		Assert.Equal(EnumCommandStatus.Error, deletedAgain.Status);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Mute_Should_BlockPlayerChat_And_Unmute_Should_RestoreIt()
	{
		ITestPlayer staff = await World.JoinPlayer("mod-mute-staff");
		ITestPlayer speaker = await World.JoinPlayer("mod-mute-speaker");
		ITestPlayer listener = await World.JoinPlayer("mod-mute-hear");
		string name = speaker.Player.PlayerName;

		// Control: before any mute the listener receives the speaker's line. Without this the
		// absence below could only mean the listener was never sent chat at all.
		await speaker.Say("chat-before-mute-7f3a");
		await UntilOrFail(
			() => ListenerHeard(listener, "chat-before-mute-7f3a"),
			() => "the listener never received the speaker's line before the mute: " + string.Join(" | ", listener.Client.ChatLines()));

		CommandResult muted = await staff.ExecuteCommand($"/mute {name} 10m spamming the trade channel");
		Assert.True(muted.Ok, muted.Message);

		CommandResult muteStatus = await staff.ExecuteCommand($"/mutestatus {name}");
		Assert.True(muteStatus.Ok, muteStatus.Message);
		Assert.Contains("spamming the trade channel", muteStatus.Message);

		speaker.Client.Clear();
		listener.Client.Clear();
		await SayPastChatRateLimit(speaker, "chat-while-muted-7f3a");
		await World.Ticks(40);

		// The server refused the line: the speaker was told why, and the listener never got it.
		Assert.Contains(speaker.Client.ChatLines(), line => line.Contains("You are muted until", StringComparison.Ordinal));
		Assert.False(
			ListenerHeard(listener, "chat-while-muted-7f3a"),
			"the muted player's line reached the listener: " + string.Join(" | ", listener.Client.ChatLines()));

		CommandResult unmuted = await staff.ExecuteCommand($"/unmute {name} served");
		Assert.True(unmuted.Ok, unmuted.Message);

		CommandResult afterUnmute = await staff.ExecuteCommand($"/mutestatus {name}");
		Assert.True(afterUnmute.Ok, afterUnmute.Message);
		Assert.Contains("is not muted", afterUnmute.Message);

		speaker.Client.Clear();
		listener.Client.Clear();
		await SayPastChatRateLimit(speaker, "chat-after-unmute-7f3a");
		await UntilOrFail(
			() => ListenerHeard(listener, "chat-after-unmute-7f3a"),
			() => "the listener did not receive the speaker's line after /unmute: " + string.Join(" | ", listener.Client.ChatLines()));
		Assert.DoesNotContain(speaker.Client.ChatLines(), line => line.Contains("You are muted until", StringComparison.Ordinal));
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task JailStatus_Should_FollowJailAndUnjail_When_TargetConnected()
	{
		ITestPlayer staff = await World.JoinPlayer("mod-jail-staff");
		ITestPlayer target = await World.JoinPlayer("mod-jail-target");
		string name = target.Player.PlayerName;
		await SetJailAtStaff(staff);

		CommandResult before = await staff.ExecuteCommand($"/jailstatus {name}");
		Assert.True(before.Ok, before.Message);
		Assert.Contains("is not jailed", before.Message);

		CommandResult jailed = await staff.ExecuteCommand($"/jail {name} griefing the spawn bridge");
		Assert.True(jailed.Ok, jailed.Message);

		CommandResult during = await staff.ExecuteCommand($"/jailstatus {name}");
		Assert.True(during.Ok, during.Message);
		Assert.DoesNotContain("is not jailed", during.Message);
		Assert.Contains("Jail Status", during.Message);
		Assert.Contains("jailed", during.Message);
		Assert.Contains("griefing the spawn bridge", during.Message);
		Assert.Contains(staff.Player.PlayerName, during.Message);

		CommandResult released = await staff.ExecuteCommand($"/unjail {name} served");
		Assert.True(released.Ok, released.Message);

		CommandResult after = await staff.ExecuteCommand($"/jailstatus {name}");
		Assert.True(after.Ok, after.Message);
		Assert.Contains("is not jailed", after.Message);

		// A second release is refused, so the state really flipped back rather than only the text.
		CommandResult releasedAgain = await staff.ExecuteCommand($"/unjail {name}");
		Assert.Equal(EnumCommandStatus.Error, releasedAgain.Status);
	}

	// ------------------------------------------------------------------ gap B: punishment ladder

	/// <summary>
	/// HighestDueTier alone, with the ladder 3 / 5 / 8 / 12. Below the first threshold nothing is
	/// due; at each threshold the next tier is due exactly once; a count that has passed several
	/// thresholds since the last applied tier returns only the highest of them; and a tier
	/// already applied (or a higher one) is never due again however far the count goes.
	/// </summary>
	[AtlasScenario(TimeoutMs = 120_000)]
	public void HighestDueTier_Should_FollowLadder_ApplyEachTierOnce_AndSkipOnJumps()
	{
		Anticheat ac = new(World);
		object config = ac.NewPunishmentConfig(drop: 3, freeze: 5, jail: 8, ban: 12);

		// Below the drop threshold.
		for (int flags = 0; flags < 3; flags++)
		{
			Assert.Equal(TierNone, ac.HighestDueTier(config, flags, TierNone));
		}

		// At each threshold, with the tier below it already applied.
		Assert.Equal(TierDropInventory, ac.HighestDueTier(config, 3, TierNone));
		Assert.Equal(TierFreeze, ac.HighestDueTier(config, 5, TierDropInventory));
		Assert.Equal(TierJail, ac.HighestDueTier(config, 8, TierFreeze));
		Assert.Equal(TierBan, ac.HighestDueTier(config, 12, TierJail));

		// Between thresholds, and at a threshold whose tier was already applied: nothing again.
		Assert.Equal(TierNone, ac.HighestDueTier(config, 3, TierDropInventory));
		Assert.Equal(TierNone, ac.HighestDueTier(config, 4, TierDropInventory));
		Assert.Equal(TierNone, ac.HighestDueTier(config, 7, TierFreeze));
		Assert.Equal(TierNone, ac.HighestDueTier(config, 12, TierBan));
		Assert.Equal(TierNone, ac.HighestDueTier(config, 1000, TierBan));

		// A lower tier is never due once a higher one was applied.
		Assert.Equal(TierNone, ac.HighestDueTier(config, 5, TierJail));

		// One jump past several thresholds returns only the highest tier it reached.
		Assert.Equal(TierJail, ac.HighestDueTier(config, 9, TierNone));
		Assert.Equal(TierJail, ac.HighestDueTier(config, 9, TierDropInventory));
		Assert.Equal(TierBan, ac.HighestDueTier(config, 12, TierNone));
		Assert.Equal(TierBan, ac.HighestDueTier(config, 50, TierFreeze));

		// Walking the count up one flag at a time, as Evaluate does, applying what is due: each
		// tier fires exactly once, at its own threshold.
		int applied = TierNone;
		var firedAt = new Dictionary<int, int>();
		for (int flags = 1; flags <= 40; flags++)
		{
			int due = ac.HighestDueTier(config, flags, applied);
			if (due != TierNone)
			{
				Assert.False(firedAt.ContainsKey(due), $"tier {due} fired twice, again at {flags} flags");
				firedAt[due] = flags;
				applied = due;
			}
		}

		Assert.Equal(new Dictionary<int, int> { [TierDropInventory] = 3, [TierFreeze] = 5, [TierJail] = 8, [TierBan] = 12 }, firedAt);
	}

	/// <summary>
	/// Evaluate with a joined player, called the way RecordViolation calls it. Checks that the
	/// count only grows while punishments are enabled, that each tier is applied once at its
	/// threshold with its real effect, that a later flag between thresholds re-applies nothing,
	/// and that the count does not expire: a record whose last flag is years old keeps counting
	/// from where it was.
	///
	/// Enabled is flipped by reflection inside blocks with no await, so no tick, and therefore no
	/// genuine anticheat flag from the engine, can interleave with the calls being counted.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Evaluate_Should_EscalateOncePerTier_CountOnlyWhileEnabled_AndNotExpire()
	{
		ITestPlayer staff = await World.JoinPlayer("ac-ladder-staff");
		ITestPlayer target = await World.JoinPlayer("ac-ladder");
		await SetJailAtStaff(staff);
		Anticheat ac = new(World);
		Assert.False(ac.Enabled, "punishments must be off by default");

		try
		{
			await SetLadder(drop: 3, freeze: 5, jail: 8, ban: OutOfReach);
			await target.GiveItem(Flint, 1);
			Assert.True(HoldsFlint(target), "the item to be dropped never reached the hotbar");
			Assert.Equal((0, TierNone), History(target));

			// Disabled: Evaluate records nothing.
			ac.Flag(target, 5);
			Assert.Equal((0, TierNone), History(target));

			ac.Enabled = true;
			try
			{
				ac.Flag(target, 2);
				Assert.Equal((2, TierNone), History(target));
				Assert.True(HoldsFlint(target), "the inventory was dropped below the threshold");

				ac.Flag(target, 1);
				Assert.Equal((3, TierDropInventory), History(target));
				Assert.False(HoldsFlint(target), "the drop tier did not drop the inventory");
				Assert.False(ac.IsFrozen(target));

				ac.Flag(target, 1);
				Assert.Equal((4, TierDropInventory), History(target));

				ac.Flag(target, 1);
				Assert.Equal((5, TierFreeze), History(target));
				Assert.True(ac.IsFrozen(target), "the freeze tier did not freeze the player");

				ac.Flag(target, 2);
				Assert.Equal((7, TierFreeze), History(target));

				ac.Flag(target, 1);
				Assert.Equal((8, TierJail), History(target));

				// Past the last reachable threshold the count keeps growing and nothing re-applies.
				ac.Flag(target, 3);
				Assert.Equal((11, TierJail), History(target));
			}
			finally
			{
				ac.Enabled = false;
			}

			// Disabled again: the count stops.
			ac.Flag(target, 4);
			Assert.Equal((11, TierJail), History(target));

			// Age the record. Nothing in the history is time-windowed, so the next flag counts on top.
			AgeHistory(target, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
			ac.Enabled = true;
			try
			{
				ac.Flag(target, 1);
			}
			finally
			{
				ac.Enabled = false;
			}

			Assert.Equal((12, TierJail), History(target));

			// The jail tier went through the real /jail path.
			CommandResult status = await staff.ExecuteCommand($"/jailstatus {target.Player.PlayerName}");
			Assert.True(status.Ok, status.Message);
			Assert.DoesNotContain("is not jailed", status.Message);
			Assert.Contains("Jail Status", status.Message);
		}
		finally
		{
			ac.Enabled = false;
			ac.Unfreeze(target);
			await World.ExecuteCommand($"/unjail {target.Player.PlayerName}");
			await SetLadder(drop: 15, freeze: 25, jail: 40, ban: 60);
		}
	}

	/// <summary>
	/// A burst that passes several thresholds at once. With RecordViolation adding one flag per
	/// call, that happens when the ladder is lowered under a count that already sits above it
	/// (a staff member tightening the ladder at runtime). The one flag that follows must apply
	/// only the highest tier it reached, Jail, and skip the drop and freeze tiers below it, and
	/// a later flag must not go back and apply them either.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Evaluate_Should_ApplyOnlyHighestTier_When_OneFlagPassesSeveralThresholds()
	{
		ITestPlayer staff = await World.JoinPlayer("ac-burst-staff");
		ITestPlayer target = await World.JoinPlayer("ac-burst");
		await SetJailAtStaff(staff);
		Anticheat ac = new(World);

		try
		{
			await SetLadder(drop: 20, freeze: 30, jail: 40, ban: OutOfReach);
			await target.GiveItem(Flint, 1);
			Assert.True(HoldsFlint(target), "the item that must survive the skip never reached the hotbar");

			ac.Enabled = true;
			try
			{
				ac.Flag(target, 9);
			}
			finally
			{
				ac.Enabled = false;
			}

			Assert.Equal((9, TierNone), History(target));

			await SetLadder(drop: 3, freeze: 5, jail: 8, ban: OutOfReach);

			ac.Enabled = true;
			try
			{
				ac.Flag(target, 1);
				Assert.Equal((10, TierJail), History(target));
				Assert.True(HoldsFlint(target), "the skipped drop tier dropped the inventory anyway");
				Assert.False(ac.IsFrozen(target), "the skipped freeze tier froze the player anyway");

				ac.Flag(target, 5);
				Assert.Equal((15, TierJail), History(target));
				Assert.True(HoldsFlint(target), "a later flag went back and applied the drop tier");
				Assert.False(ac.IsFrozen(target), "a later flag went back and applied the freeze tier");
			}
			finally
			{
				ac.Enabled = false;
			}

			CommandResult status = await staff.ExecuteCommand($"/jailstatus {target.Player.PlayerName}");
			Assert.True(status.Ok, status.Message);
			Assert.Contains("Jail Status", status.Message);
		}
		finally
		{
			ac.Enabled = false;
			ac.Unfreeze(target);
			await World.ExecuteCommand($"/unjail {target.Player.PlayerName}");
			await SetLadder(drop: 15, freeze: 25, jail: 40, ban: 60);
		}
	}

	// ------------------------------------------------------------------ gap C: client mod whitelist

	[AtlasScenario(TimeoutMs = 120_000)]
	public void ClientModWhitelist_Should_HoldCoreMods_TrimmedAllowIds_AndUniversalModsWithVersion()
	{
		ModPolicy policy = new();
		try
		{
			// Off, or not strict: no whitelist at all, the vanilla blacklist path is used instead.
			policy.Set(enabled: false, strict: true, includeUniversal: true, allow: ["extramod"]);
			Assert.Null(policy.Build([("servermod", "1.0.0")], null));
			policy.Set(enabled: true, strict: false, includeUniversal: true, allow: ["extramod"]);
			Assert.Null(policy.Build([("servermod", "1.0.0")], null));

			// Nothing configured, no universal mods: exactly the three core mods.
			policy.Set(enabled: true, strict: true, includeUniversal: true, allow: []);
			AssertWhitelist(["game", "creative", "survival"], policy.Build([], null));

			// The full case. AllowModIds entries are trimmed and kept as written (case and an
			// explicit @version included), blank ones are dropped. Universal mods become
			// modid@version, trimmed, or the bare modid without a version; one without a modid is
			// dropped.
			policy.Set(enabled: true, strict: true, includeUniversal: true, allow: ["  extramod  ", "OtherMod@1.2.3", "", "   "]);
			string[]? full = policy.Build(
				[("servermod", "1.0.0"), (" spaced ", " 2.0 "), ("noversion", ""), (null, "9.9"), ("   ", "1.0")],
				null);
			AssertWhitelist(
				["game", "creative", "survival", "extramod", "OtherMod@1.2.3", "servermod@1.0.0", "spaced@2.0", "noversion"],
				full);

			// The server's own ModIdWhiteList (serverconfig.json) is merged in the same way.
			AssertWhitelist(
				["game", "creative", "survival", "extramod", "OtherMod@1.2.3", "servermod@1.0.0", "cfgmod"],
				policy.Build([("servermod", "1.0.0")], [" cfgmod "]));

			// IncludeServerUniversalMods off: the universal mods are left out, the rest is unchanged.
			policy.Set(enabled: true, strict: true, includeUniversal: false, allow: ["  extramod  ", "OtherMod@1.2.3"]);
			AssertWhitelist(
				["game", "creative", "survival", "extramod", "OtherMod@1.2.3"],
				policy.Build([("servermod", "1.0.0"), ("noversion", "")], null));

			// The set ignores case, so an allow entry that repeats a core mod adds no second entry.
			policy.Set(enabled: true, strict: true, includeUniversal: true, allow: ["GAME"]);
			AssertWhitelist(["game", "creative", "survival"], policy.Build([], null));
		}
		finally
		{
			policy.Restore();
		}
	}

	// ------------------------------------------------------------------ helpers

	private static void AssertWhitelist(string[] expectedEntries, string[]? actual)
	{
		Assert.NotNull(actual);
		string[] expected = expectedEntries.OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase).ToArray();
		Assert.Equal(expected, actual);
	}

	private async Task SetJailAtStaff(ITestPlayer staff)
	{
		await staff.TeleportTo(World.Spawn.AddCopy(6, 1, 6));
		CommandResult set = await staff.ExecuteCommand("/setjail");
		Assert.True(set.Ok, set.Message);
	}

	/// <summary>
	/// Sets the punishment ladder through /stratum set, the runtime path staff use. EnsureSane
	/// clamps each threshold to be at least the one below it after every set, so the upper three
	/// are raised out of the way first and the targets are then written bottom up; any order
	/// then reaches the requested values. The values are read back to be sure no clamp moved one.
	/// </summary>
	private async Task SetLadder(int drop, int freeze, int jail, int ban)
	{
		foreach ((string key, int value) in new[]
		{
			("BanAfterFlags", OutOfReach), ("JailAfterFlags", OutOfReach), ("FreezeAfterFlags", OutOfReach),
			("DropInventoryAfterFlags", drop), ("FreezeAfterFlags", freeze), ("JailAfterFlags", jail), ("BanAfterFlags", ban),
		})
		{
			CommandResult result = await World.ExecuteCommand($"/stratum set Anticheat.Punishments.{key} {value}");
			Assert.True(result.Ok, $"/stratum set Anticheat.Punishments.{key} {value}: {result.Message}");
		}

		Anticheat ac = new(World);
		Assert.Equal((drop, freeze, jail, ban), ac.Ladder);
	}

	/// <summary>
	/// Chat.RateLimit drops, silently, a player's line sent less than MinimumIntervalMs (750 by
	/// default) after their previous one, before any chat handler runs. Without this wait the
	/// muted line would be dropped by the rate limit instead of refused by the mute, and the
	/// listener's silence would prove nothing. The limit reads Environment.TickCount64, so the
	/// wait is wall-clock, not ticks.
	/// </summary>
	private static async Task SayPastChatRateLimit(ITestPlayer speaker, string line)
	{
		await Task.Delay(TimeSpan.FromMilliseconds(1200));
		await speaker.Say(line);
	}

	private static bool ListenerHeard(ITestPlayer listener, string token)
	{
		return listener.Client.ChatLines().Any(line => line.Contains(token, StringComparison.Ordinal));
	}

	private static bool HoldsFlint(ITestPlayer player)
	{
		ItemStack? stack = player.Player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
		return stack?.Collectible?.Code?.ToString() == Flint;
	}

	private static (int Total, int Applied) History(ITestPlayer player)
	{
		Dictionary<string, string>? data = player.Player.ServerData?.CustomPlayerData;
		if (data == null || !data.TryGetValue(HistoryKey, out string? json) || string.IsNullOrWhiteSpace(json))
		{
			return (0, TierNone);
		}

		JsonNode record = JsonNode.Parse(json)!;
		return ((int)record["TotalFlags"]!, (int)record["HighestPunishmentApplied"]!);
	}

	private static void AgeHistory(ITestPlayer player, DateTime utc)
	{
		Dictionary<string, string> data = player.Player.ServerData.CustomPlayerData;
		JsonNode record = JsonNode.Parse(data[HistoryKey])!;
		record["LastFlagUtc"] = utc;
		record["LastPunishmentUtc"] = utc;
		data[HistoryKey] = record.ToJsonString();
	}

	/// <summary>
	/// The record id out of the "#N" in a command reply. Markup is stripped first: the reply's
	/// font tags carry hex colors that also start with '#'.
	/// </summary>
	private static int ParseRecordId(string? message)
	{
		string text = Regex.Replace(message ?? string.Empty, "<[^>]*>", string.Empty);
		Match match = Regex.Match(text, @"#(\d+)\b");
		Assert.True(match.Success, $"no #id in: {message}");
		return int.Parse(match.Groups[1].Value);
	}

	private async Task UntilOrFail(Func<bool> condition, Func<string> failure)
	{
		try
		{
			await World.Until(condition, timeoutTicks: 200);
		}
		catch (ScenarioTimeoutException)
		{
			Assert.Fail(failure());
		}
	}

	private static Type LibType(string name)
	{
		Type? type = Type.GetType(name + ", VintagestoryLib");
		Assert.True(type != null, $"{name} not found in the loaded VintagestoryLib.");
		return type!;
	}

	private static MethodInfo Method(Type type, string name, BindingFlags flags)
	{
		MethodInfo? method = type.GetMethod(name, flags);
		Assert.True(method != null, $"{type.Name}.{name} is missing: renamed or removed.");
		return method!;
	}

	private static PropertyInfo Property(Type type, string name)
	{
		PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
		Assert.True(property != null, $"{type.Name}.{name} is missing: renamed or removed.");
		return property!;
	}

	private static object StratumConfig()
	{
		Type runtime = LibType("Vintagestory.Server.StratumRuntime");
		return Property(runtime, "Config").GetValue(null)!;
	}

	/// <summary>Reflection handle on StratumAnticheatPunishments and its live config.</summary>
	private sealed class Anticheat
	{
		private readonly IWorldSession world;
		private readonly Type punishments = LibType("Vintagestory.Server.StratumAnticheatPunishments");
		private readonly Type staffState = LibType("Vintagestory.Server.StratumStaffCommandState");
		private readonly Type configType = LibType("Vintagestory.Server.StratumAnticheatPunishmentConfig");

		public Anticheat(IWorldSession world)
		{
			this.world = world;
		}

		private object LiveConfig
		{
			get
			{
				object config = StratumConfig();
				object anticheat = Property(config.GetType(), "Anticheat").GetValue(config)!;
				return Property(anticheat.GetType(), "Punishments").GetValue(anticheat)!;
			}
		}

		public bool Enabled
		{
			get => (bool)Property(configType, "Enabled").GetValue(LiveConfig)!;
			set => Property(configType, "Enabled").SetValue(LiveConfig, value);
		}

		public (int Drop, int Freeze, int Jail, int Ban) Ladder
		{
			get
			{
				object config = LiveConfig;
				return (
					(int)Property(configType, "DropInventoryAfterFlags").GetValue(config)!,
					(int)Property(configType, "FreezeAfterFlags").GetValue(config)!,
					(int)Property(configType, "JailAfterFlags").GetValue(config)!,
					(int)Property(configType, "BanAfterFlags").GetValue(config)!);
			}
		}

		/// <summary>A standalone config, not the live one, for the pure HighestDueTier checks.</summary>
		public object NewPunishmentConfig(int drop, int freeze, int jail, int ban)
		{
			object config = Activator.CreateInstance(configType)!;
			Property(configType, "Enabled").SetValue(config, true);
			Property(configType, "DropInventoryAfterFlags").SetValue(config, drop);
			Property(configType, "FreezeAfterFlags").SetValue(config, freeze);
			Property(configType, "JailAfterFlags").SetValue(config, jail);
			Property(configType, "BanAfterFlags").SetValue(config, ban);
			return config;
		}

		public int HighestDueTier(object config, int totalFlags, int applied)
		{
			MethodInfo method = Method(punishments, "HighestDueTier", BindingFlags.NonPublic | BindingFlags.Static);
			Type tier = method.GetParameters()[2].ParameterType;
			object result = method.Invoke(null, [config, totalFlags, Enum.ToObject(tier, applied)])!;
			return Convert.ToInt32(result);
		}

		/// <summary>Calls Evaluate <paramref name="count"/> times, as that many confirmed violations would.</summary>
		public void Flag(ITestPlayer player, int count)
		{
			MethodInfo evaluate = Method(punishments, "Evaluate", BindingFlags.Public | BindingFlags.Static);
			object server = world.Api.World;
			Assert.Equal("ServerMain", server.GetType().Name);
			for (int index = 0; index < count; index++)
			{
				evaluate.Invoke(null, [server, player.Player]);
			}
		}

		public bool IsFrozen(ITestPlayer player)
		{
			return (bool)Method(staffState, "IsFrozen", BindingFlags.Public | BindingFlags.Static).Invoke(null, [player.Player.PlayerUID])!;
		}

		public void Unfreeze(ITestPlayer player)
		{
			Method(staffState, "Unfreeze", BindingFlags.Public | BindingFlags.Static).Invoke(null, [player.Player.PlayerUID]);
		}
	}

	/// <summary>
	/// Reflection handle on StratumRuntime.BuildClientModWhitelist and the live ClientModPolicy,
	/// which it reads. Restore() puts the policy back the way the boot left it.
	/// </summary>
	private sealed class ModPolicy
	{
		private readonly object policy;
		private readonly Type policyType;
		private readonly Type modIdType = LibType("Packet_ModId");
		private readonly MethodInfo build = Method(LibType("Vintagestory.Server.StratumRuntime"), "BuildClientModWhitelist", BindingFlags.Public | BindingFlags.Static);
		private readonly bool originalEnabled;
		private readonly bool originalStrict;
		private readonly bool originalInclude;
		private readonly List<string> originalAllow;

		public ModPolicy()
		{
			object config = StratumConfig();
			policy = Property(config.GetType(), "ClientModPolicy").GetValue(config)!;
			policyType = policy.GetType();
			originalEnabled = (bool)Property(policyType, "Enabled").GetValue(policy)!;
			originalStrict = (bool)Property(policyType, "StrictWhitelist").GetValue(policy)!;
			originalInclude = (bool)Property(policyType, "IncludeServerUniversalMods").GetValue(policy)!;
			originalAllow = new List<string>((List<string>)Property(policyType, "AllowModIds").GetValue(policy)!);
		}

		public void Set(bool enabled, bool strict, bool includeUniversal, List<string> allow)
		{
			Property(policyType, "Enabled").SetValue(policy, enabled);
			Property(policyType, "StrictWhitelist").SetValue(policy, strict);
			Property(policyType, "IncludeServerUniversalMods").SetValue(policy, includeUniversal);
			Property(policyType, "AllowModIds").SetValue(policy, allow);
		}

		public string[]? Build((string? Modid, string? Version)[] universalMods, string[]? serverConfigWhitelist)
		{
			IList mods = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(modIdType))!;
			foreach ((string? modid, string? version) in universalMods)
			{
				object mod = Activator.CreateInstance(modIdType)!;
				modIdType.GetField("Modid")!.SetValue(mod, modid);
				modIdType.GetField("Version")!.SetValue(mod, version);
				mods.Add(mod);
			}

			return (string[]?)build.Invoke(null, [mods, serverConfigWhitelist]);
		}

		public void Restore()
		{
			Set(originalEnabled, originalStrict, originalInclude, originalAllow);
		}
	}
}
