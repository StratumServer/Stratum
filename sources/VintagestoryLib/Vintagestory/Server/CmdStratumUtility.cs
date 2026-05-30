using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal class CmdStratumUtility
{
	private readonly ServerMain server;

	public CmdStratumUtility(ServerMain server)
	{
		this.server = server;
		server.api.commandapi.Create("tps")
			.WithDescription("Show server tick rate")
			.RequiresPrivilege(Privilege.controlserver)
			.HandleWith(HandleTps);

		server.api.commandapi.Create("uptime")
			.WithDescription("Show server uptime")
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleUptime);
	}

	private TextCommandResult HandleTps(TextCommandCallingArgs args)
	{
		StatsCollection stats = server.StatsCollector[GameMath.Mod(server.StatsCollectorIndex - 1, server.StatsCollector.Length)];
		if (stats.ticksTotal <= 0)
		{
			return TextCommandResult.Success(StratumCommandText.Title("TPS") + StratumCommandText.Row("Status", "collecting data"));
		}

		decimal tps = decimal.Round((decimal)stats.ticksTotal / 2m, 2);
		decimal mspt = decimal.Round((decimal)stats.tickTimeTotal / stats.ticksTotal, 2);
		return TextCommandResult.Success(StratumCommandText.Title("TPS") + StratumCommandText.Row("TPS", tps.ToString()) + StratumCommandText.Row("MSPT", mspt.ToString()));
	}

	private TextCommandResult HandleUptime(TextCommandCallingArgs args)
	{
		TimeSpan uptime = TimeSpan.FromMilliseconds(server.totalUpTime.ElapsedMilliseconds);
		return TextCommandResult.Success(StratumCommandText.Title("Uptime") + StratumCommandText.Row("Elapsed", (int)uptime.TotalDays + "d " + uptime.Hours + "h " + uptime.Minutes + "m " + uptime.Seconds + "s"));
	}
}