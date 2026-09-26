using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Issue #335: staff authority over player groups. The seeded stratum.json defines three kinds
/// so every rule under test has something to bite on: "faction" is exclusive, "squad" caps at
/// one member, and "utility" does not count as the same side for friendly fire. Kinds cannot be
/// set through /stratum set (they are a list), so they arrive through the fixture, seeded at the
/// current config version so the boot loads it without running a migration.
///
/// Every scenario drives real commands: group creation and invites run as the player through the
/// command API with an explicit Caller, staff actions run as the console. The class shares one
/// server boot, so each scenario uses its own group and player names.
/// </summary>
[AtlasDataFiles("fixtures/stratum-groups", TargetPath = "")]
public class GroupAdminScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task PlayerJoin_Should_BeRefused_When_AlreadyInAnotherFaction()
	{
		ITestPlayer red = await World.JoinPlayer("grp-red-owner");
		ITestPlayer blue = await World.JoinPlayer("grp-blue-owner");
		ITestPlayer recruit = await World.JoinPlayer("grp-recruit");

		await CreateGroup(red, "RedOne");
		await CreateGroup(blue, "BlueOne");
		await SetKind("RedOne", "faction");
		await SetKind("BlueOne", "faction");

		// First faction is fine.
		await Invite(red, "RedOne", recruit);
		TextCommandResult joinedRed = await ExecuteAs(recruit, "/group acceptinvite RedOne");
		Assert.Equal(EnumCommandStatus.Success, joinedRed.Status);

		// Second one is not, because the kind is exclusive.
		await Invite(blue, "BlueOne", recruit);
		TextCommandResult joinedBlue = await ExecuteAs(recruit, "/group acceptinvite BlueOne");
		Assert.Equal(EnumCommandStatus.Error, joinedBlue.Status);
		Assert.Contains("RedOne", joinedBlue.StatusMessage);

		// A non-exclusive kind alongside it still works, which is the whole point of kinds:
		// only factions are one-at-a-time, administrative groupings are not.
		await CreateGroup(red, "HelpDesk");
		await SetKind("HelpDesk", "utility");
		await Invite(red, "HelpDesk", recruit);
		TextCommandResult joinedUtility = await ExecuteAs(recruit, "/group acceptinvite HelpDesk");
		Assert.Equal(EnumCommandStatus.Success, joinedUtility.Status);

		await Leave(red, blue, recruit);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task StaffAdd_Should_MovePlayer_When_FactionConflicts()
	{
		// Two owners, not one: /group admin kind refuses to stamp an exclusive kind on a group
		// whose members already hold another group of that kind, and a single owner of both
		// groups would trip that rule before this scenario got to the part it is about.
		ITestPlayer redOwner = await World.JoinPlayer("grp-move-red");
		ITestPlayer blueOwner = await World.JoinPlayer("grp-move-blue");
		ITestPlayer moved = await World.JoinPlayer("grp-moved");

		await CreateGroup(redOwner, "RedTwo");
		await CreateGroup(blueOwner, "BlueTwo");
		await SetKind("RedTwo", "faction");
		await SetKind("BlueTwo", "faction");

		CommandResult intoRed = await World.ExecuteCommand($"/group admin add RedTwo {moved.Player.PlayerName}");
		Assert.True(intoRed.Ok, intoRed.Message);

		// A player join would be refused here. A staff add reassigns instead, because that is
		// what "put this player on blue" means during an event.
		CommandResult intoBlue = await World.ExecuteCommand($"/group admin add BlueTwo {moved.Player.PlayerName}");
		Assert.True(intoBlue.Ok, intoBlue.Message);
		Assert.Contains("RedTwo", intoBlue.Message);

		CommandResult redInfo = await World.ExecuteCommand("/group admin info RedTwo");
		Assert.Contains("Members", redInfo.Message);
		Assert.True(await MemberCount("RedTwo") == 1, "RedTwo should be down to its owner");
		Assert.True(await MemberCount("BlueTwo") == 2, "BlueTwo should hold its owner and the moved player");

		await Leave(redOwner, blueOwner, moved);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Leave_Should_BeRefused_When_MemberLocked()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-lock-owner");
		ITestPlayer member = await World.JoinPlayer("grp-locked");

		await CreateGroup(owner, "LockedSquad");
		await SetKind("LockedSquad", "faction");
		await StaffAdd("LockedSquad", member);

		CommandResult locked = await World.ExecuteCommand($"/group admin lock LockedSquad {member.Player.PlayerName}");
		Assert.True(locked.Ok, locked.Message);

		TextCommandResult leaveWhileLocked = await ExecuteAs(member, "/group leave LockedSquad");
		Assert.Equal(EnumCommandStatus.Error, leaveWhileLocked.Status);
		Assert.Contains("locked", leaveWhileLocked.StatusMessage);

		CommandResult unlocked = await World.ExecuteCommand($"/group admin unlock LockedSquad {member.Player.PlayerName}");
		Assert.True(unlocked.Ok, unlocked.Message);

		TextCommandResult leaveAfterUnlock = await ExecuteAs(member, "/group leave LockedSquad");
		Assert.Equal(EnumCommandStatus.Success, leaveAfterUnlock.Status);

		await Leave(owner, member);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Lock_Should_Expire_When_DurationPasses()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-expiry-owner");
		ITestPlayer member = await World.JoinPlayer("grp-expiring");

		await CreateGroup(owner, "TimedSquad");
		await SetKind("TimedSquad", "faction");
		await StaffAdd("TimedSquad", member);

		CommandResult locked = await World.ExecuteCommand($"/group admin lock TimedSquad {member.Player.PlayerName} 3s");
		Assert.True(locked.Ok, locked.Message);

		TextCommandResult duringLock = await ExecuteAs(member, "/group leave TimedSquad");
		Assert.Equal(EnumCommandStatus.Error, duringLock.Status);

		// The expiry is wall-clock, not tick-driven: nothing sweeps locks, they lapse the next
		// time one is read. Ticking here only keeps the server busy while it passes.
		await Task.Delay(TimeSpan.FromSeconds(4));
		await World.Ticks(10);

		TextCommandResult afterExpiry = await ExecuteAs(member, "/group leave TimedSquad");
		Assert.Equal(EnumCommandStatus.Success, afterExpiry.Status);

		await Leave(owner, member);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Kick_Should_BeRefused_When_MemberLocked()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-kick-owner");
		ITestPlayer member = await World.JoinPlayer("grp-kick-target");

		await CreateGroup(owner, "KickSquad");
		await SetKind("KickSquad", "faction");
		await StaffAdd("KickSquad", member);
		CommandResult locked = await World.ExecuteCommand($"/group admin lock KickSquad {member.Player.PlayerName}");
		Assert.True(locked.Ok, locked.Message);

		// Without this gate the group owner could void a staff lock from the group UI.
		TextCommandResult kick = await ExecuteAs(owner, $"/group kick KickSquad {member.Player.PlayerName}");
		Assert.Equal(EnumCommandStatus.Error, kick.Status);
		Assert.Contains("locked", kick.StatusMessage);

		// Staff removal is deliberate, so it clears the lock instead of tripping over it.
		CommandResult staffRemove = await World.ExecuteCommand($"/group admin remove KickSquad {member.Player.PlayerName}");
		Assert.True(staffRemove.Ok, staffRemove.Message);
		Assert.True(await MemberCount("KickSquad") == 1, "only the owner should remain");

		await Leave(owner, member);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task FrozenRoster_Should_BlockInviteLeaveAndDisband()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-freeze-owner");
		ITestPlayer member = await World.JoinPlayer("grp-freeze-mem");
		ITestPlayer outsider = await World.JoinPlayer("grp-freeze-out");

		await CreateGroup(owner, "FrozenTeam");
		await StaffAdd("FrozenTeam", member);

		CommandResult frozen = await World.ExecuteCommand("/group admin freeze FrozenTeam on");
		Assert.True(frozen.Ok, frozen.Message);

		TextCommandResult invite = await ExecuteAs(owner, $"/group invite FrozenTeam {outsider.Player.PlayerName}");
		Assert.Equal(EnumCommandStatus.Error, invite.Status);

		TextCommandResult leave = await ExecuteAs(member, "/group leave FrozenTeam");
		Assert.Equal(EnumCommandStatus.Error, leave.Status);

		TextCommandResult disband = await ExecuteAs(owner, "/group disband FrozenTeam");
		Assert.Equal(EnumCommandStatus.Error, disband.Status);

		CommandResult thawed = await World.ExecuteCommand("/group admin freeze FrozenTeam off");
		Assert.True(thawed.Ok, thawed.Message);

		TextCommandResult leaveAfterThaw = await ExecuteAs(member, "/group leave FrozenTeam");
		Assert.Equal(EnumCommandStatus.Success, leaveAfterThaw.Status);

		await Leave(owner, member, outsider);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Join_Should_BeRefused_When_KindMemberCapReached()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-cap-owner");
		ITestPlayer recruit = await World.JoinPlayer("grp-cap-recruit");

		// The squad kind caps at one member, and /group create leaves the owner in it.
		await CreateGroup(owner, "OneSeat");
		await SetKind("OneSeat", "squad");

		await Invite(owner, "OneSeat", recruit);
		TextCommandResult accept = await ExecuteAs(recruit, "/group acceptinvite OneSeat");
		Assert.Equal(EnumCommandStatus.Error, accept.Status);
		Assert.Contains("full", accept.StatusMessage);

		// Staff go past the cap deliberately; the audit log records that they did.
		CommandResult staffAdd = await World.ExecuteCommand($"/group admin add OneSeat {recruit.Player.PlayerName}");
		Assert.True(staffAdd.Ok, staffAdd.Message);
		Assert.True(await MemberCount("OneSeat") == 2, "the staff add should have gone through");

		await Leave(owner, recruit);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task SameSide_Should_FollowKindAndRelations()
	{
		ITestPlayer first = await World.JoinPlayer("grp-side-a");
		ITestPlayer second = await World.JoinPlayer("grp-side-b");

		// A shared group of a counting kind: same side, so friendly fire is off between them.
		await CreateGroup(first, "SideFaction");
		await SetKind("SideFaction", "faction");
		await StaffAdd("SideFaction", second);
		await World.Ticks(5);
		Assert.True(StratumPlayerGroups.OnSameSide(first.Player, second.Player));

		// The same two players sharing only an administrative group are not. Before kinds, an
		// admin group like this silently switched PvP off between everyone in it.
		CommandResult leaveFirst = await World.ExecuteCommand($"/group admin remove SideFaction {first.Player.PlayerName}");
		Assert.True(leaveFirst.Ok, leaveFirst.Message);
		CommandResult leaveSecond = await World.ExecuteCommand($"/group admin remove SideFaction {second.Player.PlayerName}");
		Assert.True(leaveSecond.Ok, leaveSecond.Message);

		await CreateGroup(first, "SideDesk");
		await SetKind("SideDesk", "utility");
		await StaffAdd("SideDesk", second);
		await World.Ticks(5);
		Assert.False(StratumPlayerGroups.OnSameSide(first.Player, second.Player));

		// Map privacy asks a different question, plain co-membership, and kinds do not change
		// its answer: the two still share a group, so they still see each other on the map.
		Assert.True(StratumPlayerGroups.SharesGroup(first.Player, second.Player));
		await Leave(first, second);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task AlliedFactions_Should_CountAsSameSide()
	{
		ITestPlayer first = await World.JoinPlayer("grp-ally-a");
		ITestPlayer second = await World.JoinPlayer("grp-ally-b");

		await CreateGroup(first, "AllyOne");
		await CreateGroup(second, "AllyTwo");
		await SetKind("AllyOne", "faction");
		await SetKind("AllyTwo", "faction");
		await World.Ticks(5);
		Assert.False(StratumPlayerGroups.OnSameSide(first.Player, second.Player));

		CommandResult allied = await World.ExecuteCommand("/group admin relation AllyOne AllyTwo ally");
		Assert.True(allied.Ok, allied.Message);
		Assert.True(StratumPlayerGroups.OnSameSide(first.Player, second.Player));

		// An alliance is a combat rule only. Map privacy trusts exact coordinates to people who
		// share a group, and an alliance between two groups must not widen that.
		Assert.False(StratumPlayerGroups.SharesGroup(first.Player, second.Player));

		// The relation is written to both sides, so it reads the same from either group.
		CommandResult fromOther = await World.ExecuteCommand("/group admin info AllyTwo");
		Assert.Contains("AllyOne", fromOther.Message);

		CommandResult enemies = await World.ExecuteCommand("/group admin relation AllyOne AllyTwo enemy");
		Assert.True(enemies.Ok, enemies.Message);
		Assert.False(StratumPlayerGroups.OnSameSide(first.Player, second.Player));

		await Leave(first, second);
	}

	/// <summary>
	/// Two players holding several groups each can have an allied pair and an enemy pair at the
	/// same time. The enemy pair wins, whichever group the lookup happens to reach first.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task EnemyRelation_Should_Outweigh_AllyRelation_AcrossGroups()
	{
		ITestPlayer first = await World.JoinPlayer("grp-prec-a");
		ITestPlayer second = await World.JoinPlayer("grp-prec-b");

		await CreateGroup(first, "PrecFaction");
		await CreateGroup(first, "PrecSquad");
		await CreateGroup(second, "PrecOther");
		await SetKind("PrecFaction", "faction");
		await SetKind("PrecSquad", "squad");
		await SetKind("PrecOther", "faction");

		CommandResult allied = await World.ExecuteCommand("/group admin relation PrecFaction PrecOther ally");
		Assert.True(allied.Ok, allied.Message);
		Assert.True(StratumPlayerGroups.OnSameSide(first.Player, second.Player));

		CommandResult enemies = await World.ExecuteCommand("/group admin relation PrecSquad PrecOther enemy");
		Assert.True(enemies.Ok, enemies.Message);
		Assert.False(StratumPlayerGroups.OnSameSide(first.Player, second.Player));

		CommandResult neutral = await World.ExecuteCommand("/group admin relation PrecSquad PrecOther neutral");
		Assert.True(neutral.Ok, neutral.Message);
		Assert.True(StratumPlayerGroups.OnSameSide(first.Player, second.Player));

		await Leave(first, second);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Disband_Should_BeRefused_When_MemberLocked()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-disb-owner");
		ITestPlayer member = await World.JoinPlayer("grp-disb-mem");

		await CreateGroup(owner, "DisbandLocked");
		await SetKind("DisbandLocked", "faction");
		await StaffAdd("DisbandLocked", member);

		// Disbanding would push a locked member out, which a kick is not allowed to do either.
		CommandResult lockMember = await World.ExecuteCommand($"/group admin lock DisbandLocked {member.Player.PlayerName}");
		Assert.True(lockMember.Ok, lockMember.Message);
		TextCommandResult withLockedMember = await ExecuteAs(owner, "/group disband DisbandLocked");
		Assert.Equal(EnumCommandStatus.Error, withLockedMember.Status);
		Assert.Contains("locked", withLockedMember.StatusMessage);

		// Nor can a locked owner disband their way out of their own lock.
		CommandResult unlockMember = await World.ExecuteCommand($"/group admin unlock DisbandLocked {member.Player.PlayerName}");
		Assert.True(unlockMember.Ok, unlockMember.Message);
		CommandResult lockOwner = await World.ExecuteCommand($"/group admin lock DisbandLocked {owner.Player.PlayerName}");
		Assert.True(lockOwner.Ok, lockOwner.Message);
		TextCommandResult withLockedOwner = await ExecuteAs(owner, "/group disband DisbandLocked");
		Assert.Equal(EnumCommandStatus.Error, withLockedOwner.Status);

		// Once staff lift the lock, disbanding works as vanilla.
		CommandResult unlockOwner = await World.ExecuteCommand($"/group admin unlock DisbandLocked {owner.Player.PlayerName}");
		Assert.True(unlockOwner.Ok, unlockOwner.Message);
		TextCommandResult request = await ExecuteAs(owner, "/group disband DisbandLocked");
		Assert.Equal(EnumCommandStatus.Success, request.Status);
		TextCommandResult confirm = await ExecuteAs(owner, "/group confirmdisband DisbandLocked");
		Assert.Equal(EnumCommandStatus.Success, confirm.Status);

		await Leave(owner, member);
	}

	/// <summary>
	/// /group create makes the creator the owner of the new group in the same step, so an
	/// exclusive Groups.DefaultKind has to be checked before the group exists.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Create_Should_BeRefused_When_DefaultKindExclusiveAndAlreadyMember()
	{
		ITestPlayer founder = await World.JoinPlayer("grp-create-dup");

		CommandResult setDefault = await World.ExecuteCommand("/stratum set Groups.DefaultKind faction");
		Assert.True(setDefault.Ok, setDefault.Message);
		try
		{
			await CreateGroup(founder, "CreateFirst");

			TextCommandResult second = await ExecuteAs(founder, "/group create CreateSecond");
			Assert.Equal(EnumCommandStatus.Error, second.Status);
			Assert.Contains("CreateFirst", second.StatusMessage);
		}
		finally
		{
			// The class shares one boot; later scenarios expect new groups to stay unclassified.
			await World.ExecuteCommand("/stratum set Groups.DefaultKind none");
		}

		await Leave(founder);
	}

	/// <summary>
	/// Commands.GroupAdmin is read on every call rather than captured when /group registers, so
	/// switching it off or changing its privilege takes effect without a restart.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task GroupAdmin_Should_FollowCommandAccessConfig_AtRuntime()
	{
		ITestPlayer player = await World.JoinPlayer("grp-access");

		// Test players may already hold manageotherplayergroups, so the refusal is proven with a
		// privilege no role grants, then lifted again, both without a restart.
		CommandResult raised = await World.ExecuteCommand("/stratum set Commands.GroupAdmin.Privilege stratum.nobodyhasthis");
		Assert.True(raised.Ok, raised.Message);
		try
		{
			TextCommandResult refused = await ExecuteAs(player, "/group admin kinds");
			Assert.Equal(EnumCommandStatus.Error, refused.Status);

			CommandResult lowered = await World.ExecuteCommand("/stratum set Commands.GroupAdmin.Privilege chat");
			Assert.True(lowered.Ok, lowered.Message);
			TextCommandResult allowed = await ExecuteAs(player, "/group admin kinds");
			Assert.Equal(EnumCommandStatus.Success, allowed.Status);

			CommandResult disabled = await World.ExecuteCommand("/stratum set Commands.GroupAdmin.Enabled false");
			Assert.True(disabled.Ok, disabled.Message);

			// Switched off means off for the console too.
			CommandResult offForConsole = await World.ExecuteCommand("/group admin kinds");
			Assert.False(offForConsole.Ok, offForConsole.Message);
		}
		finally
		{
			await World.ExecuteCommand("/stratum set Commands.GroupAdmin.Enabled true");
			await World.ExecuteCommand("/stratum set Commands.GroupAdmin.Privilege manageotherplayergroups");
		}

		await Leave(player);
	}

	/// <summary>
	/// A group tag has to follow ordinary membership changes, not only staff ones: a player who
	/// joins through an invite picks the tag up, and loses it again on /group leave.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Nametag_Should_Follow_OrdinaryJoinAndLeave()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-ntag-owner");
		ITestPlayer member = await World.JoinPlayer("grp-ntag-mem");

		await CreateGroup(owner, "BlueTagged");
		await SetKind("BlueTagged", "faction");
		CommandResult tagged = await World.ExecuteCommand("/group admin tag BlueTagged BLU");
		Assert.True(tagged.Ok, tagged.Message);

		await Invite(owner, "BlueTagged", member);
		TextCommandResult accepted = await ExecuteAs(member, "/group acceptinvite BlueTagged");
		Assert.Equal(EnumCommandStatus.Success, accepted.Status);
		await World.Ticks(5);
		Assert.Contains("[BLU]", NametagOf(member));

		TextCommandResult left = await ExecuteAs(member, "/group leave BlueTagged");
		Assert.Equal(EnumCommandStatus.Success, left.Status);
		await World.Ticks(5);
		Assert.DoesNotContain("[BLU]", NametagOf(member));

		await Leave(owner, member);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Nametag_Should_CarryGroupTag_When_TagSet()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-tag-owner");

		await CreateGroup(owner, "TaggedTeam");
		await SetKind("TaggedTeam", "faction");

		CommandResult tagged = await World.ExecuteCommand("/group admin tag TaggedTeam RED");
		Assert.True(tagged.Ok, tagged.Message);
		await World.Ticks(5);

		Assert.Contains("[RED]", NametagOf(owner));

		CommandResult cleared = await World.ExecuteCommand("/group admin tag TaggedTeam none");
		Assert.True(cleared.Ok, cleared.Message);
		await World.Ticks(5);

		Assert.DoesNotContain("[RED]", NametagOf(owner));

		await Leave(owner);
	}

	/// <summary>
	/// Vanilla /group addplayer never put the added player on the group's online roster, and a
	/// tag change only re-applies nametags for players on it. So a player added that way kept
	/// the old tag after a retag, until they reconnected.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Nametag_Should_FollowRetag_When_AddedThroughVanillaAddPlayer()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-retag-owner");
		ITestPlayer member = await World.JoinPlayer("grp-retag-mem");

		await CreateGroup(owner, "Retagged");
		await SetKind("Retagged", "faction");
		CommandResult tagged = await World.ExecuteCommand("/group admin tag Retagged RED");
		Assert.True(tagged.Ok, tagged.Message);

		// /group addplayer requires a player caller. Test players join with the server's
		// max-privilege role, so the owner holds manageotherplayergroups.
		TextCommandResult added = await ExecuteAs(owner, $"/group addplayer Retagged {member.Player.PlayerName} 1");
		Assert.Equal(EnumCommandStatus.Success, added.Status);
		await World.Ticks(5);
		Assert.Contains("[RED]", NametagOf(member));

		CommandResult retagged = await World.ExecuteCommand("/group admin tag Retagged BLU");
		Assert.True(retagged.Ok, retagged.Message);
		await World.Ticks(5);
		Assert.Contains("[BLU]", NametagOf(member));
		Assert.DoesNotContain("[RED]", NametagOf(member));

		await Leave(owner, member);
	}

	/// <summary>
	/// When /group addplayer moves a player out of another exclusive group, the client has to be
	/// told to drop that group. A single-group update only adds the new one, so the client kept
	/// showing the old group until reconnect. Only the full group list clears it.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task AddPlayer_Should_SendFullGroupList_When_DisplacingFromFaction()
	{
		// Two owners for the same reason as StaffAdd_Should_MovePlayer_When_FactionConflicts.
		ITestPlayer redOwner = await World.JoinPlayer("grp-ap-red");
		ITestPlayer blueOwner = await World.JoinPlayer("grp-ap-blue");
		ITestPlayer moved = await World.JoinPlayer("grp-ap-moved");

		await CreateGroup(redOwner, "RedListed");
		await CreateGroup(blueOwner, "BlueListed");
		await SetKind("RedListed", "faction");
		await SetKind("BlueListed", "faction");
		await StaffAdd("RedListed", moved);
		await World.Ticks(5);

		ChatProbe probe = ChatProbe.Attach(World, moved);
		TextCommandResult added = await ExecuteAs(blueOwner, $"/group addplayer BlueListed {moved.Player.PlayerName} 1");
		Assert.Equal(EnumCommandStatus.Success, added.Status);
		await World.Ticks(5);

		IReadOnlyList<IReadOnlyList<string>> listings = probe.NewGroupListings();
		Assert.NotEmpty(listings);
		IReadOnlyList<string> latest = listings[^1];
		Assert.Contains("BlueListed", latest);
		Assert.DoesNotContain("RedListed", latest);

		await Leave(redOwner, blueOwner, moved);
	}

	/// <summary>
	/// Re-adding a player who is already in the group, to change their level say, has to leave
	/// them on the online roster once. A second entry is harmless to membership but makes every
	/// walk over the roster do its work twice.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task StaffAdd_Should_ListPlayerOnlineOnce_When_AddedTwice()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-twice-owner");
		ITestPlayer member = await World.JoinPlayer("grp-twice-mem");

		await CreateGroup(owner, "AddedTwice");
		await StaffAdd("AddedTwice", member);
		CommandResult promoted = await World.ExecuteCommand($"/group admin add AddedTwice {member.Player.PlayerName} 2");
		Assert.True(promoted.Ok, promoted.Message);

		string online = await InfoRow("AddedTwice", "Online members");
		Assert.Equal(1, CountOccurrences(online, member.Player.PlayerName));

		await Leave(owner, member);
	}

	/// <summary>
	/// Access 0 is None, which is not a membership. Both staff routes used to store it anyway and
	/// report the player as added while they held nothing.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task StaffAdd_Should_RefuseAccessNone_OnBothRoutes()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-none-owner");
		ITestPlayer target = await World.JoinPlayer("grp-none-target");

		await CreateGroup(owner, "NoneLevel");

		CommandResult adminAdd = await World.ExecuteCommand($"/group admin add NoneLevel {target.Player.PlayerName} 0");
		Assert.False(adminAdd.Ok, adminAdd.Message);
		Assert.Contains("/group admin remove", adminAdd.Message);

		TextCommandResult vanillaAdd = await ExecuteAs(owner, $"/group addplayer NoneLevel {target.Player.PlayerName} 0");
		Assert.Equal(EnumCommandStatus.Error, vanillaAdd.Status);
		Assert.Contains("/group admin remove", vanillaAdd.StatusMessage);

		Assert.Equal(1, await MemberCount("NoneLevel"));
		Assert.DoesNotContain(target.Player.PlayerName, await InfoRow("NoneLevel", "Online members"));

		await Leave(owner, target);
	}

	/// <summary>
	/// Turning Exclusive on for a kind in stratum.json applies to the groups already carrying it,
	/// with no check, so a player can be left in two of them. /group admin kind refuses the same
	/// thing at runtime. The reload has to name who is affected rather than pass silently.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Reload_Should_ReportExclusiveConflicts_When_KindTurnsExclusive()
	{
		ITestPlayer ownerA = await World.JoinPlayer("grp-flip-a");
		ITestPlayer ownerB = await World.JoinPlayer("grp-flip-b");
		ITestPlayer member = await World.JoinPlayer("grp-flip-mem");

		await CreateGroup(ownerA, "FlipDeskA");
		await CreateGroup(ownerB, "FlipDeskB");
		await SetKind("FlipDeskA", "utility");
		await SetKind("FlipDeskB", "utility");
		await StaffAdd("FlipDeskA", member);
		await StaffAdd("FlipDeskB", member);

		string configPath = Path.Combine(GamePaths.Config, "stratum.json");
		string original = await File.ReadAllTextAsync(configPath);
		try
		{
			JsonNode config = JsonNode.Parse(original)!;
			foreach (JsonNode? kind in config["Groups"]!["Kinds"]!.AsArray())
			{
				if ((string?)kind!["Code"] == "utility")
				{
					kind["Exclusive"] = true;
				}
			}

			await File.WriteAllTextAsync(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			CommandResult reloaded = await World.ExecuteCommand("/stratum reload");
			string message = reloaded.Message ?? string.Empty;
			Assert.Contains("Exclusive group kinds", message);
			Assert.Contains(member.Player.PlayerName + " is in 2 groups of exclusive kind utility: FlipDeskA, FlipDeskB", message);
		}
		finally
		{
			await File.WriteAllTextAsync(configPath, original);
			await World.ExecuteCommand("/stratum reload");
		}

		await Leave(ownerA, ownerB, member);
	}

	/// <summary>
	/// /group info is the player-facing half. A player who is not staff has to be able to read
	/// the state that governs them: which kind the group is, its tag, whether the roster is
	/// frozen, who it is allied with or at war with, and whether they are personally locked in.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task GroupInfo_Should_ShowStateToPlayers()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-info-own");
		ITestPlayer other = await World.JoinPlayer("grp-info-oth");

		await CreateGroup(owner, "InfoRed");
		await CreateGroup(other, "InfoBlue");
		await SetKind("InfoRed", "faction");
		await SetKind("InfoBlue", "faction");

		CommandResult tagged = await World.ExecuteCommand("/group admin tag InfoRed RED");
		Assert.True(tagged.Ok, tagged.Message);
		CommandResult related = await World.ExecuteCommand("/group admin relation InfoRed InfoBlue enemy");
		Assert.True(related.Ok, related.Message);
		CommandResult locked = await World.ExecuteCommand($"/group admin lock InfoRed {owner.Player.PlayerName}");
		Assert.True(locked.Ok, locked.Message);

		// The owner holds no staff privilege here, so this is the ordinary player's view.
		TextCommandResult mine = await ExecuteAs(owner, "/group info InfoRed");
		Assert.Equal(EnumCommandStatus.Success, mine.Status);
		Assert.Contains("faction", mine.StatusMessage);
		Assert.Contains("[RED]", mine.StatusMessage);
		Assert.Contains("InfoBlue", mine.StatusMessage);
		Assert.Contains("enemy", mine.StatusMessage);
		Assert.Contains("locked", mine.StatusMessage);

		// Someone outside the group reads the same public state, but not another player's lock.
		TextCommandResult theirs = await ExecuteAs(other, "/group info InfoRed");
		Assert.Equal(EnumCommandStatus.Success, theirs.Status);
		Assert.Contains("InfoBlue", theirs.StatusMessage);
		Assert.DoesNotContain("Your membership", theirs.StatusMessage);

		CommandResult frozen = await World.ExecuteCommand("/group admin freeze InfoRed on");
		Assert.True(frozen.Ok, frozen.Message);

		TextCommandResult afterFreeze = await ExecuteAs(owner, "/group info InfoRed");
		Assert.Contains("frozen", afterFreeze.StatusMessage);

		// A group that was never touched by /group admin still reads exactly as vanilla did.
		await CreateGroup(other, "InfoPlain");
		TextCommandResult plain = await ExecuteAs(other, "/group info InfoPlain");
		Assert.Equal(EnumCommandStatus.Success, plain.Status);
		Assert.Contains("Members", plain.StatusMessage);
		Assert.DoesNotContain("Kind", plain.StatusMessage);
		Assert.DoesNotContain("Roster", plain.StatusMessage);
		Assert.DoesNotContain("Relations", plain.StatusMessage);

		await Leave(owner, other);
	}

	// ---------------------------------------------------------------- helpers

	/// <summary>
	/// Frees the slots again. The class shares one server boot and the world caps at 16
	/// players, so scenarios that each join two or three would run the server out of room
	/// part-way through the suite.
	/// </summary>
	private async Task Leave(params ITestPlayer[] players)
	{
		foreach (ITestPlayer player in players)
		{
			player.Player.Disconnect();
		}

		foreach (ITestPlayer player in players)
		{
			await World.Until(() => !player.IsConnected, timeoutTicks: 600);
		}

		await World.Ticks(5);
	}

	private static string NametagOf(ITestPlayer player)
	{
		return player.Player.Entity?.WatchedAttributes.GetTreeAttribute("nametag")?.GetString("name") ?? string.Empty;
	}

	private async Task CreateGroup(ITestPlayer owner, string groupName)
	{
		TextCommandResult result = await ExecuteAs(owner, $"/group create {groupName}");
		Assert.Equal(EnumCommandStatus.Success, result.Status);
	}

	private async Task SetKind(string groupName, string kind)
	{
		CommandResult result = await World.ExecuteCommand($"/group admin kind {groupName} {kind}");
		Assert.True(result.Ok, result.Message);
	}

	private async Task StaffAdd(string groupName, ITestPlayer player)
	{
		CommandResult result = await World.ExecuteCommand($"/group admin add {groupName} {player.Player.PlayerName}");
		Assert.True(result.Ok, result.Message);
	}

	private async Task Invite(ITestPlayer inviter, string groupName, ITestPlayer target)
	{
		TextCommandResult result = await ExecuteAs(inviter, $"/group invite {groupName} {target.Player.PlayerName}");
		Assert.Equal(EnumCommandStatus.Success, result.Status);
	}

	/// <summary>The value of one /group admin info row, markup included.</summary>
	private async Task<string> InfoRow(string groupName, string label)
	{
		CommandResult info = await World.ExecuteCommand($"/group admin info {groupName}");
		Assert.True(info.Ok, info.Message);

		string message = info.Message ?? string.Empty;
		int rowStart = message.IndexOf(label + ":", StringComparison.Ordinal);
		Assert.True(rowStart >= 0, $"no {label} row in: {message}");

		int rowEnd = message.IndexOf('\n', rowStart);
		return rowEnd < 0 ? message[rowStart..] : message[rowStart..rowEnd];
	}

	private static int CountOccurrences(string text, string value)
	{
		int count = 0;
		for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
		{
			count++;
		}

		return count;
	}

	/// <summary>Member count as /group admin info reports it, parsed off the "Members" row.</summary>
	private async Task<int> MemberCount(string groupName)
	{
		CommandResult info = await World.ExecuteCommand($"/group admin info {groupName}");
		Assert.True(info.Ok, info.Message);

		string message = info.Message ?? string.Empty;
		int rowStart = message.IndexOf("Members", StringComparison.Ordinal);
		Assert.True(rowStart >= 0, $"no Members row in: {message}");

		int digitStart = -1;
		for (int index = rowStart; index < message.Length; index++)
		{
			if (char.IsDigit(message[index]))
			{
				digitStart = index;
				break;
			}
		}

		Assert.True(digitStart >= 0, $"no member count in: {message}");
		int digitEnd = digitStart;
		while (digitEnd < message.Length && char.IsDigit(message[digitEnd]))
		{
			digitEnd++;
		}

		return int.Parse(message[digitStart..digitEnd]);
	}

	/// <summary>
	/// Runs a command as the player rather than the console. /group leans on the caller for
	/// ownership, group privileges and the chat group it was typed in, so the console's
	/// all-privileges caller would prove nothing here.
	/// </summary>
	private Task<TextCommandResult> ExecuteAs(ITestPlayer player, string command)
	{
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
}
