using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Vintagestory.API.Server;

// Shared player-group lookups for Stratum features that need "are these two players on the
// same team". Lives in VintagestoryAPI so both VintagestoryLib and the patched EntityPlayer
// can use it.
//
// Two questions live here and they are deliberately kept apart. SharesGroup is plain
// co-membership, which is what map privacy trusts with exact coordinates. OnSameSide is the
// combat predicate: it drops groups whose kind does not count as a side and adds allied groups,
// neither of which should ever widen who can see a player on the map.
public static class StratumPlayerGroups
{
	public const int RelationEnemy = -1;

	public const int RelationNeutral = 0;

	public const int RelationAlly = 1;

	// Stratum #335: set by the server once group kinds exist. Returns false for a group whose
	// kind is marked CountsAsSameSide=false, so an administrative group ("newcomers", "event
	// signups") no longer switches friendly fire off between its members. Left null, every
	// group counts, which is the behaviour OnSameSide had before kinds.
	public static System.Func<int, bool> StratumGroupCountsAsSameSide;

	// Stratum #335: the side relation between two groups as far as combat is concerned:
	// RelationAlly, RelationEnemy or RelationNeutral. Returns neutral whenever the server does
	// not count allies as one side. Left null, every pair is neutral.
	public static System.Func<int, int, int> StratumGroupSideRelation;

	// True when both players hold a live membership in the same player-created group, that is
	// a group made with /group create. Kinds and relations play no part.
	public static bool SharesGroup(IPlayer a, IPlayer b)
	{
		if (a is not IServerPlayer serverA || b is not IServerPlayer serverB)
		{
			return false;
		}

		return SharesGroup(serverA.ServerData?.PlayerGroupMemberships, serverB.ServerData?.PlayerGroupMemberships);
	}

	// Overload on the raw membership maps so the overlap rule can be exercised without a live
	// player. Reads the dictionaries directly rather than IPlayer.Groups, which copies both
	// maps into new arrays on every call.
	//
	// Group Uids come from ServerConfig.NextPlayerGroupUid, which starts at 10 and only counts
	// up, so the "> 0" test also rules out GlobalConstants.DefaultChatGroups (general, server
	// info, damage log, info log, console; all <= 0). Those never appear in a membership map
	// anyway, since memberships are only ever created by ServerPlayerData.JoinGroup.
	public static bool SharesGroup(Dictionary<int, PlayerGroupMembership> a, Dictionary<int, PlayerGroupMembership> b)
	{
		return SharesGroup(a, b, sidesOnly: false);
	}

	// Stratum #335: true when the two players count as one side for friendly fire. That is a
	// shared group whose kind counts as a side, or failing that, groups the server has allied.
	public static bool OnSameSide(IPlayer a, IPlayer b)
	{
		if (a is not IServerPlayer serverA || b is not IServerPlayer serverB)
		{
			return false;
		}

		return OnSameSide(serverA.ServerData?.PlayerGroupMemberships, serverB.ServerData?.PlayerGroupMemberships);
	}

	// Precedence, in order:
	// 1. A shared group of a counting kind wins outright. Staff put both players on that team,
	//    and no relation between their other groups outranks it.
	// 2. Otherwise every pair of their counting groups is compared. Any enemy pair means not
	//    the same side, even if another pair is allied, so staff can carve one pairing out of a
	//    wider alliance without the answer depending on which group is looked at first.
	// 3. Otherwise at least one allied pair means the same side.
	public static bool OnSameSide(Dictionary<int, PlayerGroupMembership> a, Dictionary<int, PlayerGroupMembership> b)
	{
		if (SharesGroup(a, b, sidesOnly: true))
		{
			return true;
		}

		if (StratumGroupSideRelation == null || a == null || b == null)
		{
			return false;
		}

		// Both membership maps hold a handful of entries, so this walks them rather than
		// building a set: it runs on the melee damage path and should not allocate.
		bool allied = false;
		foreach (KeyValuePair<int, PlayerGroupMembership> mine in a)
		{
			if (!CountsForSide(mine))
			{
				continue;
			}

			foreach (KeyValuePair<int, PlayerGroupMembership> theirs in b)
			{
				if (!CountsForSide(theirs))
				{
					continue;
				}

				int relation = StratumGroupSideRelation(mine.Key, theirs.Key);
				if (relation == RelationEnemy)
				{
					return false;
				}

				allied |= relation == RelationAlly;
			}
		}

		return allied;
	}

	private static bool SharesGroup(Dictionary<int, PlayerGroupMembership> a, Dictionary<int, PlayerGroupMembership> b, bool sidesOnly)
	{
		if (a == null || b == null || a.Count == 0 || b.Count == 0)
		{
			return false;
		}

		if (a.Count > b.Count)
		{
			(a, b) = (b, a);
		}

		foreach (KeyValuePair<int, PlayerGroupMembership> membership in a)
		{
			if (membership.Key <= 0 || !IsLiveMembership(membership.Value) || (sidesOnly && !CountsAsSameSide(membership.Key)))
			{
				continue;
			}

			if (b.TryGetValue(membership.Key, out PlayerGroupMembership other) && IsLiveMembership(other))
			{
				return true;
			}
		}

		return false;
	}

	private static bool CountsForSide(KeyValuePair<int, PlayerGroupMembership> membership)
	{
		return membership.Key > 0 && IsLiveMembership(membership.Value) && CountsAsSameSide(membership.Key);
	}

	private static bool CountsAsSameSide(int groupUid)
	{
		return StratumGroupCountsAsSameSide == null || StratumGroupCountsAsSameSide(groupUid);
	}

	private static bool IsLiveMembership(PlayerGroupMembership membership)
	{
		return membership != null && membership.Level != EnumPlayerGroupMemberShip.None;
	}
}
