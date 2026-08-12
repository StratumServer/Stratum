using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

// Cooperative PvE combat toggle. EntityBehaviorHealth (VSEssentials) puts every entity that
// takes damage on a 500ms "invulnerable" activity timer, so a second hit from anyone else
// inside that window does zero damage regardless of source. That is fine for a single attacker,
// but it means a group fighting one strong creature only ever lands one effective hit every
// 500ms combined, not per player: the second, third, and further attackers' hits are wasted the
// moment they land inside the first attacker's window.
//
// StratumCoopCombatHook (in VintagestoryAPI, reachable from both this assembly and VSEssentials)
// is the cross-assembly bridge: this system owns the value and pushes it in from config, the
// behavior reads it instead of the hardcoded 500 for any entity that isn't a player. Player
// invulnerability (join protection, PvP) is untouched, this only ever affects creatures.
internal sealed class StratumCoopCombatSystem
{
	private readonly ServerMain server;

	private static StratumCoopCombatConfig Cfg => StratumRuntime.Config?.CoopCombat;

	public StratumCoopCombatSystem(ServerMain server)
	{
		this.server = server;

		StratumRuntime.Config.EnsurePopulated();
		Apply(Cfg);

		if (StratumCommandRegistration.ShouldRegister(StratumRuntime.Config.Commands.CoopCombat, "/pvecombat", "Commands.CoopCombat"))
		{
			CommandArgumentParsers parsers = server.api.commandapi.Parsers;
			server.api.commandapi.Create("pvecombat")
				.WithDescription("Toggle cooperative PvE combat (lowers creature hit invulnerability so multiple players can damage the same target)")
				.WithArgs(parsers.OptionalWordRange("mode", "on", "off", "toggle", "status"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleToggle);
		}
	}

	// Re-applies the configured window, called after StratumRuntime.Config is replaced by a
	// live /stratum reload so the hook doesn't keep serving a stale value.
	public static void Apply(StratumCoopCombatConfig cfg)
	{
		StratumCoopCombatHook.CreatureInvulnerableMs = cfg?.Enabled == true ? cfg.CreatureInvulnerableMs : 500;
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

		StratumCommandAccessConfig access = StratumRuntime.Config.Commands.CoopCombat;
		if (access == null || !access.Enabled)
		{
			failure = TextCommandResult.Error("/pvecombat is disabled.");
			return false;
		}

		if (StratumCommandAccessCatalog.CallerHasAccess(args.Caller, server, access))
		{
			if (!StratumCommandCooldowns.TryUse(args.Caller, server, "pvecombat", access, out TimeSpan remaining))
			{
				failure = TextCommandResult.Error("Wait " + Math.Ceiling(remaining.TotalSeconds).ToString(GlobalConstants.DefaultCultureInfo) + "s before using /pvecombat again.");
				return false;
			}

			return true;
		}

		failure = TextCommandResult.Error("You do not have permission to use /pvecombat.");
		return false;
	}

	private TextCommandResult HandleToggle(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, out TextCommandResult failure))
		{
			return failure;
		}

		StratumCoopCombatConfig cfg = Cfg;
		if (cfg == null)
		{
			return TextCommandResult.Error("Config not ready yet.");
		}

		string mode = args[0] as string;
		if (string.IsNullOrEmpty(mode) || string.Equals(mode, "status", System.StringComparison.OrdinalIgnoreCase))
		{
			return TextCommandResult.Success(cfg.Enabled
				? "Cooperative PvE combat is on, creature invulnerability " + cfg.CreatureInvulnerableMs + "ms."
				: "Cooperative PvE combat is off, vanilla 500ms creature invulnerability.");
		}

		bool next = string.Equals(mode, "toggle", System.StringComparison.OrdinalIgnoreCase) ? !cfg.Enabled : string.Equals(mode, "on", System.StringComparison.OrdinalIgnoreCase);
		cfg.Enabled = next;
		Apply(cfg);
		StratumRuntime.SaveConfig();

		string message = next
			? "Cooperative PvE combat enabled, creature invulnerability now " + cfg.CreatureInvulnerableMs + "ms."
			: "Cooperative PvE combat disabled, back to vanilla 500ms.";
		StratumRuntime.LogAudit("pvecombat " + (next ? "on" : "off") + " actor=" + args.Caller.GetName(), true);
		return TextCommandResult.Success(message);
	}
}
