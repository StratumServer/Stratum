using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Vintagestory.API.Server;

// Shared player-group lookups for Stratum features that need "are these two players on the
// same team". Lives in VintagestoryAPI so both VintagestoryLib and the patched EntityPlayer
// can use it.
public static class StratumPlayerGroups
{
	// True when both players hold a live membership in the same player-created group, that is
	// a group made with /group create.
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
			if (membership.Key <= 0 || !IsLiveMembership(membership.Value))
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

	private static bool IsLiveMembership(PlayerGroupMembership membership)
	{
		return membership != null && membership.Level != EnumPlayerGroupMemberShip.None;
	}
}
