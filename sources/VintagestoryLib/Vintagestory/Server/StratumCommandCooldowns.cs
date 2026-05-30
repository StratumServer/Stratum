using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal static class StratumCommandCooldowns
{
	private static readonly Dictionary<string, DateTime> LastUseUtcByCommandAndUid = new Dictionary<string, DateTime>(StringComparer.Ordinal);

	public static bool TryUse(Caller caller, ServerMain server, string command, StratumCommandAccessConfig access, out TimeSpan remaining)
	{
		remaining = TimeSpan.Zero;
		if (caller == null || access == null || access.CooldownSeconds <= 0 || caller.Type == EnumCallerType.Console)
		{
			return true;
		}

		IServerPlayer player = caller.Player as IServerPlayer;
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return true;
		}

		if (access.CooldownBypassForStaff && StratumCommandAccessCatalog.PlayerHasAccess(player, StratumRuntime.Config.Commands.StaffChat))
		{
			return true;
		}

		DateTime nowUtc = DateTime.UtcNow;
		string key = command.Trim().ToLowerInvariant() + "|" + player.PlayerUID;
		if (LastUseUtcByCommandAndUid.TryGetValue(key, out DateTime lastUseUtc))
		{
			TimeSpan cooldown = TimeSpan.FromSeconds(access.CooldownSeconds);
			TimeSpan elapsed = nowUtc - lastUseUtc;
			if (elapsed < cooldown)
			{
				remaining = cooldown - elapsed;
				return false;
			}
		}

		LastUseUtcByCommandAndUid[key] = nowUtc;
		return true;
	}
}