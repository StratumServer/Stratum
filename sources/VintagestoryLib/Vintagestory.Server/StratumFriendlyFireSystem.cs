using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

// Group friendly fire toggle (issue #277). Owns StratumFriendlyFireHook.BlockGroupDamage and
// pushes the configured value into it at startup, on /friendlyfire, and after a live
// /stratum reload. The patched EntityPlayer.ShouldReceiveDamage reads the flag.
//
// "on" means group members can damage each other, matching how every other server platform
// reads the phrase: friendly fire on is the vanilla behaviour, friendly fire off protects
// the group.
internal sealed class StratumFriendlyFireSystem
{
	private readonly ServerMain server;

	private static StratumFriendlyFireConfig Cfg => StratumRuntime.Config?.FriendlyFire;

	public StratumFriendlyFireSystem(ServerMain server)
	{
		this.server = server;

		StratumRuntime.Config.EnsurePopulated();
		Apply(Cfg);

		if (StratumCommandRegistration.ShouldRegister(StratumRuntime.Config.Commands.FriendlyFire, "/friendlyfire", "Commands.FriendlyFire"))
		{
			CommandArgumentParsers parsers = server.api.commandapi.Parsers;
			server.api.commandapi.Create("friendlyfire")
				.WithDescription("Toggle whether players in the same player group can damage each other")
				.WithArgs(parsers.OptionalWordRange("mode", "on", "off", "toggle", "status"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleToggle);
		}
	}

	// Re-applies the configured value, called after StratumRuntime.Config is replaced by a live
	// /stratum reload so the hook doesn't keep serving a stale flag.
	public static void Apply(StratumFriendlyFireConfig cfg)
	{
		StratumFriendlyFireHook.BlockGroupDamage = cfg != null && !cfg.AllowGroupDamage;
		StratumHarmonyVisibility.WarnFriendlyFireConflicts();
	}

	private bool CheckAccess(TextCommandCallingArgs args, out TextCommandResult failure)
	{
		StratumRuntime.Config.EnsurePopulated();
		failure = null;

		if (!StratumRuntime.Config.Commands.Enabled)
		{
			failure = TextCommandResult.Error("Stratum commands are disabled.");
			return false;
		}

		StratumCommandAccessConfig access = StratumRuntime.Config.Commands.FriendlyFire;
		if (access == null || !access.Enabled)
		{
			failure = TextCommandResult.Error("/friendlyfire is disabled.");
			return false;
		}

		if (StratumCommandAccessCatalog.CallerHasAccess(args.Caller, server, access))
		{
			if (!StratumCommandCooldowns.TryUse(args.Caller, server, "friendlyfire", access, out TimeSpan remaining))
			{
				failure = TextCommandResult.Error("Wait " + Math.Ceiling(remaining.TotalSeconds).ToString(GlobalConstants.DefaultCultureInfo) + "s before using /friendlyfire again.");
				return false;
			}

			return true;
		}

		failure = TextCommandResult.Error("You do not have permission to use /friendlyfire.");
		return false;
	}

	private TextCommandResult HandleToggle(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, out TextCommandResult failure))
		{
			return failure;
		}

		StratumFriendlyFireConfig cfg = Cfg;
		if (cfg == null)
		{
			return TextCommandResult.Error("Config not ready yet.");
		}

		string mode = args[0] as string;
		if (string.IsNullOrEmpty(mode) || string.Equals(mode, "status", StringComparison.OrdinalIgnoreCase))
		{
			return TextCommandResult.Success(cfg.AllowGroupDamage
				? "Group friendly fire is on, players in the same group can damage each other."
				: "Group friendly fire is off, players in the same group cannot damage each other.");
		}

		bool next = string.Equals(mode, "toggle", StringComparison.OrdinalIgnoreCase) ? !cfg.AllowGroupDamage : string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase);
		cfg.AllowGroupDamage = next;
		Apply(cfg);
		StratumRuntime.SaveConfig();

		string message = next
			? "Group friendly fire enabled, players in the same group can damage each other again."
			: "Group friendly fire disabled, players in the same group can no longer damage each other.";
		StratumRuntime.LogAudit("friendlyfire " + (next ? "on" : "off") + " actor=" + args.Caller.GetName(), true);
		return TextCommandResult.Success(message);
	}
}
