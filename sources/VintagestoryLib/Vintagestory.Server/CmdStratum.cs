using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;

namespace Vintagestory.Server;

internal class CmdStratum
{
	private readonly ServerMain server;

	public CmdStratum(ServerMain server)
	{
		this.server = server;
		server.api.commandapi.Create(StratumInfo.Id)
			.WithDesc("Show Stratum server information")
			.WithArgs(server.api.commandapi.Parsers.OptionalWord("status|version|update|health|reload|preflight|packets|performance|perf|timings|players|player|chunks|entities|queues|pathfinding|doctor|regions|ac|anticheat|access|chat|pregen|get|set|save"), server.api.commandapi.Parsers.OptionalWord("argument"), server.api.commandapi.Parsers.OptionalWord("detail"), server.api.commandapi.Parsers.OptionalWord("value1"), server.api.commandapi.Parsers.OptionalWord("value2"), server.api.commandapi.Parsers.OptionalWord("value3"), server.api.commandapi.Parsers.OptionalWord("value4"))
			.RequiresPrivilege(Privilege.controlserver)
			.HandleWith(HandleStratum);
	}

	private TextCommandResult HandleStratum(TextCommandCallingArgs args)
	{
		string action = args[0] as string;
		if (string.Equals(action, "reload", StringComparison.OrdinalIgnoreCase))
		{
			return HandleReload();
		}

		if (string.Equals(action, "preflight", StringComparison.OrdinalIgnoreCase))
		{
			return HandlePreflight();
		}

		if (string.Equals(action, "health", StringComparison.OrdinalIgnoreCase))
		{
			return HandleHealth();
		}

		if (string.Equals(action, "doctor", StringComparison.OrdinalIgnoreCase))
		{
			return HandleDoctor();
		}

		if (string.Equals(action, "version", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "update", StringComparison.OrdinalIgnoreCase))
		{
			return HandleVersion(args[1] as string);
		}

		if (string.Equals(action, "packets", StringComparison.OrdinalIgnoreCase))
		{
			return HandlePackets(args[1] as string, args[2] as string);
		}
		
		if (string.Equals(action, "ac", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "anticheat", StringComparison.OrdinalIgnoreCase))
		{
			return HandleAnticheat(args[1] as string);
		}

		if (string.Equals(action, "players", StringComparison.OrdinalIgnoreCase))
		{
			return HandlePlayers();
		}

		if (string.Equals(action, "access", StringComparison.OrdinalIgnoreCase))
		{
			return HandleAccess(args[1] as string, args[2] as string);
		}

		if (string.Equals(action, "chat", StringComparison.OrdinalIgnoreCase))
		{
			return HandleChat();
		}

		if (string.Equals(action, "pregen", StringComparison.OrdinalIgnoreCase))
		{
			return StratumRuntime.Pregen.HandleCommand(server, args);
		}

		if (string.Equals(action, "player", StringComparison.OrdinalIgnoreCase))
		{
			return HandlePlayer(args[1] as string);
		}

		if (string.Equals(action, "chunks", StringComparison.OrdinalIgnoreCase))
		{
			return HandleChunks();
		}

		if (string.Equals(action, "entities", StringComparison.OrdinalIgnoreCase))
		{
			return HandleEntities();
		}

		if (string.Equals(action, "regions", StringComparison.OrdinalIgnoreCase))
		{
			return HandleRegions();
		}

		if (string.Equals(action, "queues", StringComparison.OrdinalIgnoreCase))
		{
			return HandleQueues();
		}

		if (string.Equals(action, "pathfinding", StringComparison.OrdinalIgnoreCase))
		{
			return HandlePathfinding();
		}

		if (string.Equals(action, "performance", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "perf", StringComparison.OrdinalIgnoreCase))
		{
			return HandlePerformance();
		}

		if (string.Equals(action, "timings", StringComparison.OrdinalIgnoreCase))
		{
			return HandleTimings(args[1] as string);
		}

		if (string.Equals(action, "get", StringComparison.OrdinalIgnoreCase))
		{
			return HandleConfigGet(args[1] as string);
		}

		if (string.Equals(action, "set", StringComparison.OrdinalIgnoreCase))
		{
			return HandleConfigSet(args, callerName: args.Caller?.GetName() ?? "server");
		}

		if (string.Equals(action, "save", StringComparison.OrdinalIgnoreCase))
		{
			return HandleConfigSave();
		}

		if (action != null && action.Length > 0 && !string.Equals(action, "status", StringComparison.OrdinalIgnoreCase))
		{
			return TextCommandResult.Error("Usage: /stratum [status|version|update|health|reload|preflight|packets|performance|timings|players|player|chunks|entities|queues|pathfinding|doctor|regions|ac|access|chat|pregen|get|set|save]");
		}

		return HandleStatus();
	}

	private TextCommandResult HandleStatus()
	{
		int admittedPlayers = server.Clients.Values.Count(client => client.State.IsAdmitted());
		TimeSpan uptime = TimeSpan.FromMilliseconds(server.totalUpTime.ElapsedMilliseconds);
		StratumConfig config = StratumRuntime.Config;

		StringBuilder output = new StringBuilder(StratumCommandText.Title(StratumInfo.FullName));
		output.Append(StratumCommandText.Row("Base game", "Vintage Story " + StratumInfo.BaseGameVersion));
		output.Append(StratumCommandText.Row("Protocol mode", StratumInfo.ProtocolMode));
		output.Append(StratumCommandText.Row("Updates", StratumUpdateChecker.BuildReport()));
		output.Append(StratumCommandText.Row("Run phase", server.RunPhase.ToString()));
		output.Append(StratumCommandText.Row("Players", admittedPlayers + " / " + server.Config.MaxClients));
		output.Append(StratumCommandText.Row("Uptime", (int)uptime.TotalDays + "d " + uptime.Hours + "h " + uptime.Minutes + "m " + uptime.Seconds + "s"));
		output.Append(StratumCommandText.Row("Config", StratumRuntime.ConfigPath + " (" + StratumRuntime.LastLoadStatus + ")"));
		output.Append(StratumCommandText.Row("Preflight", StratumRuntime.LastPreflight.Summary));
		output.Append(StratumCommandText.Row("Packet limits", "enabled=" + (config.PacketLimits.Enabled ? "on" : "off") + ", drop=" + (config.PacketLimits.DropViolations ? "on" : "off") + ", window=" + config.PacketLimits.WindowSeconds + "s"));
		output.Append(StratumCommandText.Row("Performance", "chunkSending=" + (config.Performance.ChunkSending.Enabled ? "on" : "off") + ", chunkGeneration=" + (config.Performance.ChunkGeneration.Enabled ? "on" : "off") + ", pregen=" + (config.Performance.Pregen.Enabled ? "on" : "off") + ", entityTicking=" + (config.Performance.EntityTicking.Enabled ? "on" : "off") + ", autosaveSmoothing=" + (config.Performance.AutoSave.Enabled ? "on" : "off") + ", blockTicks=" + (config.Performance.BlockTicks.Enabled ? "on" : "off")));
		output.Append(StratumCommandText.Row("Client mod policy", "enabled=" + (config.ClientModPolicy.Enabled ? "on" : "off") + ", strictWhitelist=" + (config.ClientModPolicy.StrictWhitelist ? "on" : "off") + ", allowExtras=" + config.ClientModPolicy.AllowModIds.Count));
		output.Append(StratumCommandText.Row("Hardening", "packets=" + (config.Hardening.PacketMonitoring ? "on" : "off") + ", blockbreak=" + (config.Hardening.BlockBreakGuards ? "on" : "off") + ", inventory=" + (config.Hardening.InventoryGuards ? "on" : "off") + ", entities=" + (config.Hardening.EntityGuards ? "on" : "off")));
		output.Append(StratumCommandText.Row("Commands", "playerQoL=" + (config.Commands.Enabled ? "on" : "off") + ", tpaTimeout=" + config.Commands.TeleportRequests.TimeoutSeconds + "s, defaultHomes=" + config.Commands.Homes.DefaultMaxHomes + ", near=" + config.Commands.NearDefaultRadiusBlocks + "/" + config.Commands.NearMaxRadiusBlocks));
		output.Append(StratumCommandText.Row("Chat", "rolePrefixes=" + (config.Chat.Enabled && config.Chat.RolePrefixesEnabled ? "on" : "off") + ", urlLinks=" + (config.Chat.Enabled && config.Chat.LinkifyUrls ? "on" : "off") + ", configuredRoles=" + config.Chat.RolePrefixes.Count));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleVersion(string argument)
	{
		if (string.Equals(argument, "check", StringComparison.OrdinalIgnoreCase) || string.Equals(argument, "now", StringComparison.OrdinalIgnoreCase))
		{
			StratumUpdateCheckResult result = StratumUpdateChecker.CheckAsync(default).GetAwaiter().GetResult();
			return TextCommandResult.Success(FormatVersion(result));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title(StratumInfo.FullName));
		output.Append(StratumCommandText.Row("Base game", "Vintage Story " + StratumInfo.BaseGameVersion));
		output.Append(StratumCommandText.Row("Protocol mode", StratumInfo.ProtocolMode));
		output.Append(StratumCommandText.Row("Updates", StratumUpdateChecker.BuildReport()));
		output.Append(StratumCommandText.Row("Check now", "/stratum version check"));
		return TextCommandResult.Success(output.ToString());
	}

	private static string FormatVersion(StratumUpdateCheckResult result)
	{
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Version"));
		output.Append(StratumCommandText.Row("Current", result.CurrentVersion ?? StratumInfo.Version));
		if (!string.IsNullOrWhiteSpace(result.LatestVersion))
		{
			output.Append(StratumCommandText.Row("Latest", result.LatestVersion));
		}
		if (!string.IsNullOrWhiteSpace(result.ReleaseUrl))
		{
			output.Append(StratumCommandText.Row("Release", result.ReleaseUrl));
		}
		output.Append(StratumCommandText.Row("Status", StratumUpdateChecker.BuildReport()));
		return output.ToString();
	}

	private TextCommandResult HandleReload()
	{
		bool loaded = StratumRuntime.LoadOrCreateConfig(server, out string message);
		StratumPreflightReport report = StratumRuntime.RunPreflight();
		string result = (loaded ? StratumCommandText.Confirm("Stratum config reloaded", message) : StratumCommandText.Danger("Stratum config failed") + ": " + StratumCommandText.Escape(message)) + "\n" + FormatPreflight(report);
		if (loaded)
		{
			CmdStratumEssentials.RegisterConfiguredPrivileges(server);
			// Stratum: clear region ticking fallback state on reload (#10)
			var sim = server.Systems.OfType<ServerSystemEntitySimulation>().FirstOrDefault();
			sim?.StratumClearFallbackState();
			StratumRuntime.LogInfo($"config reloaded: {message}; preflight {report.Summary}");
		}
		else
		{
			StratumRuntime.LogWarning($"config reload failed: {message}; preflight {report.Summary}");
		}

		return loaded && report.Passed ? TextCommandResult.Success(result) : TextCommandResult.Error(result);
	}

	private TextCommandResult HandlePreflight()
	{
		StratumPreflightReport report = StratumRuntime.RunPreflight();
		string result = FormatPreflight(report);
		StratumRuntime.LogInfo("preflight requested: " + report.Summary);

		return report.Passed ? TextCommandResult.Success(result) : TextCommandResult.Error(result);
	}

	private TextCommandResult HandlePackets(string mode, string detail)
	{
		return TextCommandResult.Success(StratumRuntime.PacketLimiter.BuildReport(mode, detail) + "\n" + StratumRuntime.PacketBackPressure.BuildReport() + "\n" + StratumRuntime.BlockBreakGuard.BuildReport());
	}

	private TextCommandResult HandleAnticheat(string playerName)
	{
		return TextCommandResult.Success(StratumAnticheatReporter.BuildReport(playerName));
	}

	private TextCommandResult HandleHealth()
	{
		StatsCollection stats = server.StatsCollector[GameMath.Mod(server.StatsCollectorIndex - 1, server.StatsCollector.Length)];
		decimal mspt = stats.ticksTotal <= 0 ? 0m : decimal.Round((decimal)stats.tickTimeTotal / stats.ticksTotal, 2);
		decimal tps = stats.ticksTotal <= 0 ? 0m : decimal.Round((decimal)stats.ticksTotal / 2m, 2);
		decimal targetTps = server.Config.TickTime <= 0 ? 0m : decimal.Round(1000m / (decimal)server.Config.TickTime, 2);
		int players = server.Clients.Values.Count(client => client.State.IsAdmitted());
		int activeEntities = server.LoadedEntities.Values.Count(entity => entity.State != EnumEntityState.Inactive);
		string managedMemory = decimal.Round((decimal)GC.GetTotalMemory(forceFullCollection: false) / 1024m / 1024m, 1).ToString(GlobalConstants.DefaultCultureInfo);
		string processMemory = decimal.Round((decimal)Process.GetCurrentProcess().WorkingSet64 / 1024m / 1024m, 1).ToString(GlobalConstants.DefaultCultureInfo);
		string state = FormatHealthState(mspt, targetTps);

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Health"));
		output.Append(StratumCommandText.Row("State", state));
		output.Append(StratumCommandText.Row("Tick", "TPS=" + FormatMetric(tps) + " target=" + FormatMetric(targetTps) + " MSPT=" + FormatMetric(mspt) + " budget=" + server.Config.TickTime.ToString("0.##", GlobalConstants.DefaultCultureInfo) + "ms"));
		output.Append(StratumCommandText.Row("Players", players + "/" + server.Config.MaxClients + " queue=" + server.ConnectionQueue.Count));
		output.Append(StratumCommandText.Row("World", "chunks=" + server.loadedChunks.Count + " mapChunks=" + server.loadedMapChunks.Count + " entities=" + server.LoadedEntities.Count + " active=" + activeEntities));
		output.Append(StratumCommandText.Row("Queues", "fastChunk=" + server.fastChunkQueue.Count + " requestedColumns=" + server.ChunkColumnRequested.Count + " simpleLoads=" + server.simpleLoadRequests.Count + " peeks=" + server.peekChunkColumnQueue.Count));
		output.Append(StratumCommandText.Row("Memory", "managed=" + managedMemory + "MB process=" + processMemory + "MB"));
		output.Append(StratumCommandText.Row("Preflight", StratumRuntime.LastPreflight.Summary));
		output.Append(StratumCommandText.Row("Protection", "packets=" + (StratumRuntime.Config.Hardening.PacketMonitoring ? "on" : "off") + " blockBreak=" + (StratumRuntime.Config.Hardening.BlockBreakGuards ? "on" : "off") + " timings=" + (StratumRuntime.Timings.Enabled ? "running" : "stopped")));
		output.Append(StratumCommandText.Row("Next", "/stratum queues, /stratum chunks, /stratum entities, /stratum players, /stratum ac"));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleDoctor()
	{
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Doctor"));

		StatsCollection stats = server.StatsCollector[GameMath.Mod(server.StatsCollectorIndex - 1, server.StatsCollector.Length)];
		double mspt = stats.ticksTotal > 0 ? (double)stats.tickTimeTotal / stats.ticksTotal : 0;
		double tps = stats.ticksTotal > 0 ? (double)stats.ticksTotal / 2.0 : 0;
		double budget = server.Config.TickTime;

		// Tick: report when MSPT exceeds the configured tick budget.
		if (budget > 0 && mspt > budget)
		{
			output.Append(StratumCommandText.Row("Tick", "MSPT=" + mspt.ToString("0.#") + "ms budget=" + budget + "ms TPS=" + tps.ToString("0.#") + "; inspect /stratum timings"));
		}

		// Packets: report when back-pressure deferred packets this tick.
		var bp = StratumRuntime.PacketBackPressure.Snapshot();
		if (bp.LastTickDeferred > 0 || bp.LastQueueDepth > 0)
		{
			output.Append(StratumCommandText.Row("Packets", "queue=" + bp.LastQueueDepth + " deferred=" + bp.LastTickDeferred + " peak=" + bp.PeakQueueDepth + "; inspect /stratum packets"));
		}

		// Chunks: report when the send path skipped clients due to pressure.
		var perf = StratumRuntime.PerformanceStats.DoctorSnapshot();
		if (perf.SkippedOutboundPressure > 0)
		{
			output.Append(StratumCommandText.Row("Chunk send", "skippedPressure=" + perf.SkippedOutboundPressure + " pending=" + server.ChunkColumnRequested.Count + " fastQueue=" + server.fastChunkQueue.Count + "; inspect /stratum chunks"));
		}

		// Chunk generation: report when clients had generation deferred.
		if (perf.GenerationDeferredClients > 0)
		{
			output.Append(StratumCommandText.Row("Chunk gen", "deferredClients=" + perf.GenerationDeferredClients + "; inspect /stratum chunks"));
		}

		// Entities: report when throttling kicked in.
		if (StratumRuntime.PreviousTickOverloaded || perf.EntitiesThrottled > 0)
		{
			output.Append(StratumCommandText.Row("Entities", "throttled=" + perf.EntitiesThrottled + " overloaded=" + StratumRuntime.PreviousTickOverloaded + "; inspect /stratum entities"));
		}

		// Block ticks: report when listeners were skipped.
		if (perf.BlockListenersSkipped > 0)
		{
			output.Append(StratumCommandText.Row("Block ticks", "listenersSkipped=" + perf.BlockListenersSkipped + "; inspect /stratum perf"));
		}

		// Pathfinding: report when queue exceeds half capacity.
		try
		{
			var pf = server.api.ModLoader.GetModSystem("Vintagestory.Essentials.PathfindingAsync");
			var method = pf?.GetType().GetMethod("StratumBuildReport");
			if (method != null)
			{
				string report = method.Invoke(pf, null) as string ?? "";
				foreach (string line in report.Split('\n'))
				{
					if (line.StartsWith("Queue="))
					{
						string queueVal = line.Substring(6);
						string[] parts = queueVal.Split('/');
						if (parts.Length == 2 && int.TryParse(parts[0], out int depth) && int.TryParse(parts[1], out int max) && max > 0 && depth > max / 2)
						{
							output.Append(StratumCommandText.Row("Pathfinding", "queue=" + queueVal + "; inspect /stratum pathfinding"));
						}
						break;
					}
				}
			}
		}
		catch { }

		// Autosave: report when save is being delayed.
		if (perf.AutoSaveDelayed)
		{
			output.Append(StratumCommandText.Row("Autosave", "delayed " + perf.AutoSaveDelaySeconds + "s; inspect /stratum perf"));
		}

		// Pregen: report when paused due to server pressure.
		string pregenStatus = StratumRuntime.Pregen.ShortStatus;
		if (pregenStatus == "paused")
		{
			output.Append(StratumCommandText.Row("Pregen", "paused; inspect /stratum pregen"));
		}

		// Join queue: report when players are waiting.
		int connectionQueue = server.ConnectionQueue.Count;
		if (connectionQueue > 0)
		{
			output.Append(StratumCommandText.Row("Join queue", connectionQueue + "/" + server.Config.MaxClientsInQueue));
		}

		if (output.Length <= StratumCommandText.Title("Stratum Doctor").Length)
		{
			output.Append(StratumCommandText.Row("Status", "no pressure detected"));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandlePlayers()
	{
		ConnectedClient[] clients = server.Clients.Values
			.OrderBy(client => client.PlayerName)
			.ToArray();
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Players"));

		if (clients.Length == 0)
		{
			output.Append(StratumCommandText.Empty("\nNo clients connected."));
			return TextCommandResult.Success(output.ToString());
		}

		foreach (ConnectedClient client in clients)
		{
			string role = client.ServerData?.RoleCode ?? "unknown";
			string ping = client.LastPing > 0 ? ((int)(client.LastPing * 1000f)).ToString(GlobalConstants.DefaultCultureInfo) + "ms" : "n/a";
			long idleSeconds = Math.Max(0, (server.ElapsedMilliseconds - client.LastActivityTotalMs) / 1000);
			output.Append(StratumCommandText.Bullet(client.PlayerName, "state=" + client.State + " role=" + role + " ping=" + ping + " idle=" + idleSeconds + "s"));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleAccess(string mode, string value)
	{
		StratumRuntime.Config.EnsurePopulated();
		StratumCommandsConfig commands = StratumRuntime.Config.Commands;
		if (string.IsNullOrWhiteSpace(mode))
		{
			StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Command Access"));
			foreach (StratumCommandAccessEntry entry in StratumCommandAccessCatalog.Enumerate(commands).Where(entry => entry.CommandKey != "reply"))
			{
				output.Append(StratumCommandText.Bullet(entry.DisplayCommand, FormatAccess(entry.Access)));
			}

			return TextCommandResult.Success(output.ToString());
		}

		if (string.Equals(mode, "command", StringComparison.OrdinalIgnoreCase))
		{
			StratumCommandAccessEntry entry = StratumCommandAccessCatalog.Find(commands, value);
			if (entry == null)
			{
				return TextCommandResult.Error("Usage: /stratum access command &lt;command&gt;");
			}

			return TextCommandResult.Success(StratumCommandText.Title(entry.DisplayCommand) + "\n" + FormatAccessDetails(entry.Access));
		}

		if (string.Equals(mode, "role", StringComparison.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(value) || !server.Config.RolesByCode.ContainsKey(value))
			{
				return TextCommandResult.Error("Usage: /stratum access role &lt;role&gt;");
			}

			string[] allowed = StratumCommandAccessCatalog.Enumerate(commands)
				.Where(entry => StratumCommandAccessCatalog.RoleCanUse(server, value, entry.Access))
				.Select(entry => entry.DisplayCommand)
				.Distinct()
				.OrderBy(command => command)
				.ToArray();

			StringBuilder output = new StringBuilder(StratumCommandText.Title("Role Access: " + value));
			output.Append(StratumCommandText.Row("Commands", allowed.Length == 0 ? "none" : string.Join(", ", allowed)));
			return TextCommandResult.Success(output.ToString());
		}

		return TextCommandResult.Error("Usage: /stratum access [command &lt;command&gt;|role &lt;role&gt;]");
	}

	private TextCommandResult HandleChat()
	{
		StratumRuntime.Config.EnsurePopulated();
		StratumChatConfig chat = StratumRuntime.Config.Chat;
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Chat"));
		output.Append(StratumCommandText.Row("Enabled", chat.Enabled ? "true" : "false"));
		output.Append(StratumCommandText.Row("Role prefixes", chat.RolePrefixesEnabled ? "true" : "false"));
		output.Append(StratumCommandText.Row("URL links", chat.LinkifyUrls ? "true" : "false"));
		output.Append(StratumCommandText.Row("Prefix format", chat.PrefixFormat));

		foreach (KeyValuePair<string, StratumChatRolePrefixConfig> entry in chat.RolePrefixes.OrderBy(entry => entry.Key))
		{
			output.Append(StratumCommandText.Bullet(entry.Key, chat.PrefixFormat.Replace("{tag}", entry.Value.Tag) + " " + entry.Value.Color + (entry.Value.Enabled ? "" : " disabled")));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private static string FormatAccess(StratumCommandAccessConfig access)
	{
		return "enabled=" + (access.Enabled ? "true" : "false") + " privilege=" + (string.IsNullOrWhiteSpace(access.Privilege) ? "none" : access.Privilege);
	}

	private static string FormatAccessDetails(StratumCommandAccessConfig access)
	{
		return StratumCommandText.Row("Enabled", access.Enabled ? "true" : "false") +
			StratumCommandText.Row("Privilege", string.IsNullOrWhiteSpace(access.Privilege) ? "none" : access.Privilege);
	}

	private TextCommandResult HandlePlayer(string playerName)
	{
		if (string.IsNullOrWhiteSpace(playerName))
		{
			return TextCommandResult.Error("Usage: /stratum player &lt;online-player&gt;");
		}

		ConnectedClient client = server.Clients.Values.FirstOrDefault(candidate => string.Equals(candidate.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
		if (client == null)
		{
			return TextCommandResult.Error("No such online player: " + playerName);
		}

		ServerPlayer player = client.Player;
		string position = player?.Entity?.Pos == null ? "unknown" : $"{player.Entity.Pos.X:0.#}, {player.Entity.Pos.Y:0.#}, {player.Entity.Pos.Z:0.#}";
		string chunk = client.Position == null ? "unknown" : client.ChunkPos.ToString();
		string role = client.ServerData?.RoleCode ?? "unknown";
		string privileges = player == null ? "0" : player.Privileges.Length.ToString(GlobalConstants.DefaultCultureInfo);
		long connectedSeconds = Math.Max(0, (server.ElapsedMilliseconds - client.MillisecsAtConnect) / 1000);
		long idleSeconds = Math.Max(0, (server.ElapsedMilliseconds - client.LastActivityTotalMs) / 1000);

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Player: " + client.PlayerName));
		output.Append(StratumCommandText.Row("UID", client.SentPlayerUid));
		output.Append(StratumCommandText.Row("State", client.State + ", role=" + role + ", privileges=" + privileges));
		output.Append(StratumCommandText.Row("Game mode", player?.WorldData.CurrentGameMode.ToString() ?? "unknown"));
		output.Append(StratumCommandText.Row("Position", position + ", chunk=" + chunk));
		output.Append(StratumCommandText.Row("Ping", (int)(client.LastPing * 1000f) + "ms, connected=" + connectedSeconds + "s, idle=" + idleSeconds + "s"));
		output.Append(StratumCommandText.Row("Connection", "local=" + (client.IsLocalConnection ? "yes" : "no") + ", udp=" + (client.ServerDidReceiveUdp ? "yes" : "no") + ", fallbackTcp=" + (client.FallBackToTcp ? "yes" : "no")));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleChunks()
	{
		int sentChunks = server.Clients.Values.Sum(client => client.ChunkSent?.Count ?? 0);
		int sentMapChunks = server.Clients.Values.Sum(client => client.MapChunkSent?.Count ?? 0);
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Chunks"));
		output.Append(StratumCommandText.Row("Loaded", "chunks=" + server.loadedChunks.Count + " mapChunks=" + server.loadedMapChunks.Count + " forceLoadedColumns=" + server.forceLoadedChunkColumns.Count));
		output.Append(StratumCommandText.Row("Queued", "fastChunk=" + server.fastChunkQueue.Count + " requestedColumns=" + server.ChunkColumnRequested.Count + " simpleLoads=" + server.simpleLoadRequests.Count));
		output.Append(StratumCommandText.Row("Client sent caches", "chunks=" + sentChunks + " mapChunks=" + sentMapChunks));
		output.Append(StratumCommandText.Row("Budgets", "send=" + (StratumRuntime.Config.Performance.ChunkSending.Enabled ? "on" : "off") + " generation=" + (StratumRuntime.Config.Performance.ChunkGeneration.Enabled ? "on" : "off")));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleEntities()
	{
		int activeEntities = server.LoadedEntities.Values.Count(entity => entity.State != EnumEntityState.Inactive);
		string topTypes = string.Join(", ", server.LoadedEntities.Values
			.GroupBy(entity => entity.Code?.ToString() ?? "unknown")
			.OrderByDescending(group => group.Count())
			.Take(8)
			.Select(group => group.Key + ":" + group.Count()));

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Entities"));
		output.Append(StratumCommandText.Row("Loaded", "total=" + server.LoadedEntities.Count + " active=" + activeEntities));
		output.Append(StratumCommandText.Row("Throttling", (StratumRuntime.Config.Performance.EntityTicking.Enabled ? "on" : "off") + " far=" + StratumRuntime.Config.Performance.EntityTicking.FarEntityDistanceBlocks + " interval=" + StratumRuntime.Config.Performance.EntityTicking.FarEntityTickInterval));
		output.Append(StratumCommandText.Row("Top types", topTypes.Length == 0 ? "none" : topTypes));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleRegions()
	{
		var sim = server.Systems.OfType<ServerSystemEntitySimulation>().FirstOrDefault();
		if (sim == null)
		{
			return TextCommandResult.Success(StratumCommandText.Title("Stratum Regions") + StratumCommandText.Row("Status", "entity simulation not loaded"));
		}

		string report = sim.StratumBuildRegionReport();
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Regions"));
		foreach (string line in report.Split('\n'))
		{
			int eq = line.IndexOf('=');
			if (eq > 0)
			{
				output.Append(StratumCommandText.Row(line.Substring(0, eq), line.Substring(eq + 1)));
			}
		}
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleQueues()
	{
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Queues"));
		output.Append(StratumCommandText.Row("Players", "connectionQueue=" + server.ConnectionQueue.Count + "/" + server.Config.MaxClientsInQueue));
		output.Append(StratumCommandText.Row("Chunks", "fastChunk=" + server.fastChunkQueue.Count + " requestedColumns=" + server.ChunkColumnRequested.Count + " simpleLoads=" + server.simpleLoadRequests.Count + " peeks=" + server.peekChunkColumnQueue.Count + " existsChecks=" + server.testChunkExistsQueue.Count));
		output.Append(StratumCommandText.Row("Cleanup", "unloadedChunks=" + server.unloadedChunks.Count + " deleteColumns=" + server.deleteChunkColumns.Count + " deleteRegions=" + server.deleteMapRegions.Count));
		output.Append(StratumCommandText.Row("Autosave smoothing", (StratumRuntime.Config.Performance.AutoSave.Enabled ? "on" : "off") + " maxDelay=" + StratumRuntime.Config.Performance.AutoSave.MaxDelaySeconds + "s"));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandlePathfinding()
	{
		var pf = server.api.ModLoader.GetModSystem("Vintagestory.Essentials.PathfindingAsync");
		if (pf == null)
		{
			return TextCommandResult.Success(StratumCommandText.Title("Stratum Pathfinding") + StratumCommandText.Row("Status", "PathfindingAsync not loaded"));
		}

		var method = pf.GetType().GetMethod("StratumBuildReport");
		if (method == null)
		{
			return TextCommandResult.Success(StratumCommandText.Title("Stratum Pathfinding") + StratumCommandText.Row("Status", "metrics not available"));
		}

		string report = method.Invoke(pf, null) as string ?? "";
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Pathfinding"));
		foreach (string line in report.Split('\n'))
		{
			int eq = line.IndexOf('=');
			if (eq > 0)
			{
				output.Append(StratumCommandText.Row(line.Substring(0, eq), line.Substring(eq + 1)));
			}
		}
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandlePerformance()
	{
		return TextCommandResult.Success(StratumRuntime.PerformanceStats.BuildReport());
	}

	private TextCommandResult HandleTimings(string action)
	{
		if (string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
		{
			string message = StratumRuntime.Timings.Start();
			StratumRuntime.LogInfo("timings started");
			return TextCommandResult.Success(StratumCommandText.Confirm("Timings", message));
		}

		if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
		{
			string message = StratumRuntime.Timings.Stop();
			StratumRuntime.LogInfo("timings stopped");
			return TextCommandResult.Success(StratumCommandText.Confirm("Timings", message));
		}

		if (string.Equals(action, "reset", StringComparison.OrdinalIgnoreCase))
		{
			string message = StratumRuntime.Timings.Reset();
			StratumRuntime.LogInfo("timings reset");
			return TextCommandResult.Success(StratumCommandText.Confirm("Timings", message));
		}

		if (string.Equals(action, "listeners", StringComparison.OrdinalIgnoreCase))
		{
			return HandleTimingsListeners();
		}

		if (action != null && action.Length > 0 && !string.Equals(action, "report", StringComparison.OrdinalIgnoreCase))
		{
			return TextCommandResult.Error("Usage: /stratum timings [start|stop|reset|report|listeners]");
		}

		return TextCommandResult.Success(StratumRuntime.Timings.BuildReport());
	}

	private TextCommandResult HandleTimingsListeners()
	{
		List<GameTickListener> entityListeners = server.EventManager.GameTickListenersEntity;
		List<GameTickListenerBlock> blockListeners = server.EventManager.GameTickListenersBlock;
		StratumEventTickConfig evtCfg = StratumRuntime.Config.Performance.EventTick;

		// Group entity listeners by ProfilerName, count and collect intervals.
		Dictionary<string, (int Count, HashSet<int> Intervals)> entityGroups = new Dictionary<string, (int, HashSet<int>)>();
		for (int i = 0; i < entityListeners.Count; i++)
		{
			GameTickListener listener = entityListeners[i];
			if (listener == null) continue;
			string name = listener.ProfilerName ?? "unknown";
			if (!entityGroups.TryGetValue(name, out var group))
			{
				group = (0, new HashSet<int>());
				entityGroups[name] = group;
			}
			entityGroups[name] = (group.Count + 1, group.Intervals);
			group.Intervals.Add(listener.Millisecondinterval);
		}

		// Group block listeners by ProfilerName.
		Dictionary<string, (int Count, HashSet<int> Intervals)> blockGroups = new Dictionary<string, (int, HashSet<int>)>();
		for (int i = 0; i < blockListeners.Count; i++)
		{
			GameTickListenerBlock listener = blockListeners[i];
			if (listener == null) continue;
			string name = listener.ProfilerName ?? "unknown";
			if (!blockGroups.TryGetValue(name, out var group))
			{
				group = (0, new HashSet<int>());
				blockGroups[name] = group;
			}
			blockGroups[name] = (group.Count + 1, group.Intervals);
			group.Intervals.Add(listener.Millisecondinterval);
		}

		// Fetch slow-listener timing buckets from StratumTimings.
		List<(string Name, long Calls, double TotalMs, double MaxMs, long SlowCalls)> slowEntity = StratumRuntime.Timings.GetBucketsByPrefix("eventTick.gtEntity.slow.");
		List<(string Name, long Calls, double TotalMs, double MaxMs, long SlowCalls)> slowBlock = StratumRuntime.Timings.GetBucketsByPrefix("eventTick.gtBlock.slow.");
		Dictionary<string, (long Calls, double TotalMs, double MaxMs, long SlowCalls)> slowEntityMap = new Dictionary<string, (long, double, double, long)>();
		foreach (var entry in slowEntity)
		{
			slowEntityMap[entry.Name] = (entry.Calls, entry.TotalMs, entry.MaxMs, entry.SlowCalls);
		}
		Dictionary<string, (long Calls, double TotalMs, double MaxMs, long SlowCalls)> slowBlockMap = new Dictionary<string, (long, double, double, long)>();
		foreach (var entry in slowBlock)
		{
			slowBlockMap[entry.Name] = (entry.Calls, entry.TotalMs, entry.MaxMs, entry.SlowCalls);
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Listeners"));
		output.Append(StratumCommandText.Row("Entity listeners", entityGroups.Values.Sum(g => g.Count).ToString()));
		output.Append(StratumCommandText.Row("Block listeners", blockGroups.Values.Sum(g => g.Count).ToString()));
		output.Append(StratumCommandText.Row("Slow threshold", "entity=" + evtCfg.SlowEntityListenerThresholdMs + "ms block=" + evtCfg.SlowBlockListenerThresholdMs + "ms"));
		output.Append(StratumCommandText.Row("Adaptive throttle", (evtCfg.AdaptiveThrottleWhenOverloaded ? "on" : "off") + " mul=" + evtCfg.AdaptiveOverloadedMultiplier + " critical<=" + evtCfg.AdaptiveCriticalIntervalMs + "ms overloaded=" + (StratumRuntime.PreviousTickOverloaded ? "yes" : "no")));
		output.Append(StratumCommandText.Row("Timings", StratumRuntime.Timings.Enabled ? "running (data available)" : "stopped (start with /stratum timings start)"));

		output.Append("\n").Append(StratumCommandText.Title("Entity Listeners (by type)"));
		foreach (KeyValuePair<string, (int Count, HashSet<int> Intervals)> entry in entityGroups.OrderByDescending(e => e.Value.Count))
		{
			string intervals = string.Join(",", entry.Value.Intervals.OrderBy(ms => ms).Select(ms => ms + "ms"));
			string timing = "";
			if (slowEntityMap.TryGetValue(entry.Key, out var slow))
			{
				double avgMs = slow.Calls > 0 ? slow.TotalMs / slow.Calls : 0;
				timing = " avg=" + avgMs.ToString("0.##") + "ms max=" + slow.MaxMs.ToString("0.##") + "ms calls=" + slow.Calls + " slow=" + slow.SlowCalls;
			}
			output.Append(StratumCommandText.Bullet(entry.Key, "count=" + entry.Value.Count + " intervals=" + intervals + timing));
		}

		output.Append("\n").Append(StratumCommandText.Title("Block Listeners (by type)"));
		foreach (KeyValuePair<string, (int Count, HashSet<int> Intervals)> entry in blockGroups.OrderByDescending(e => e.Value.Count))
		{
			string intervals = string.Join(",", entry.Value.Intervals.OrderBy(ms => ms).Select(ms => ms + "ms"));
			string timing = "";
			if (slowBlockMap.TryGetValue(entry.Key, out var slow))
			{
				double avgMs = slow.Calls > 0 ? slow.TotalMs / slow.Calls : 0;
				timing = " avg=" + avgMs.ToString("0.##") + "ms max=" + slow.MaxMs.ToString("0.##") + "ms calls=" + slow.Calls + " slow=" + slow.SlowCalls;
			}
			output.Append(StratumCommandText.Bullet(entry.Key, "count=" + entry.Value.Count + " intervals=" + intervals + timing));
		}

		// Top offenders summary (same data /stratum doctor will show).
		string doctorLine = BuildSlowListenersDoctorLine(slowEntity, slowBlock);
		if (doctorLine != null)
		{
			output.Append("\n").Append(StratumCommandText.Row("Doctor", doctorLine));
		}

		return TextCommandResult.Success(output.ToString());
	}

	// Stratum start: slow listeners doctor summary (#14)
	internal static string BuildSlowListenersDoctorLine(
		List<(string Name, long Calls, double TotalMs, double MaxMs, long SlowCalls)> slowEntity,
		List<(string Name, long Calls, double TotalMs, double MaxMs, long SlowCalls)> slowBlock)
	{
		long totalSlow = 0;
		string worstName = null;
		double worstMax = 0;
		foreach (var entry in slowEntity)
		{
			totalSlow += entry.SlowCalls;
			if (entry.MaxMs > worstMax) { worstMax = entry.MaxMs; worstName = entry.Name; }
		}
		foreach (var entry in slowBlock)
		{
			totalSlow += entry.SlowCalls;
			if (entry.MaxMs > worstMax) { worstMax = entry.MaxMs; worstName = entry.Name; }
		}
		if (totalSlow == 0) return null;
		return "slowCalls=" + totalSlow + " worst=" + worstName + " (" + worstMax.ToString("0.#") + "ms); inspect /stratum timings listeners";
	}
	// Stratum end

	private TextCommandResult HandleConfigGet(string path)
	{
		StratumRuntime.Config.EnsurePopulated();
		StratumConfigPath.ResolveResult r = StratumConfigPath.Resolve(StratumRuntime.Config, path ?? string.Empty);
		if (r.Error != null) return TextCommandResult.Error(StratumCommandText.Warning("Config get") + " " + r.Error);

		StringBuilder output = new(StratumCommandText.Title("Stratum Config"));
		if (r.Property == null || !StratumConfigPath.IsScalar(r.Property.PropertyType))
		{
			object target = r.Value ?? StratumRuntime.Config;
			output.Append("\n").Append(StratumConfigPath.DescribeObject(target, r.PathBuilt));
		}
		else
		{
			output.Append(StratumCommandText.Row(r.PathBuilt, StratumConfigPath.FormatValue(r.Value)));
		}
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleConfigSet(TextCommandCallingArgs args, string callerName)
	{
		string path = args[1] as string;
		if (string.IsNullOrWhiteSpace(path))
		{
			return TextCommandResult.Error("Usage: /stratum set &lt;path&gt; &lt;value&gt;   e.g. /stratum set Performance.Pregen.PauseBelowTps 18");
		}

		// Join args[2..6] with spaces to allow string values containing spaces.
		StringBuilder rawValue = new();
		for (int i = 2; i < args.ArgCount; i++)
		{
			string piece = args[i] as string;
			if (string.IsNullOrEmpty(piece)) continue;
			if (rawValue.Length > 0) rawValue.Append(' ');
			rawValue.Append(piece);
		}
		if (rawValue.Length == 0)
		{
			return TextCommandResult.Error("Usage: /stratum set &lt;path&gt; &lt;value&gt;");
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumConfigPath.ResolveResult r = StratumConfigPath.Resolve(StratumRuntime.Config, path);
		if (r.Error != null) return TextCommandResult.Error(StratumCommandText.Warning("Config set") + " " + r.Error);
		if (r.Property == null || r.Owner == null)
		{
			return TextCommandResult.Error(StratumCommandText.Warning("Config set") + " '" + path + "' is a group, not a settable field. Try /stratum get " + path);
		}

		object previous = null;
		try { previous = r.Property.GetValue(r.Owner); } catch { }

		if (!StratumConfigPath.TrySet(r.Property, r.Owner, rawValue.ToString(), out string message))
		{
			return TextCommandResult.Error(StratumCommandText.Warning("Config set") + " " + message);
		}

		try
		{
			StratumRuntime.Config.EnsurePopulated();
			StratumRuntime.SaveConfig();
		}
		catch (Exception ex)
		{
			return TextCommandResult.Error(StratumCommandText.Warning("Saved in memory but failed to write file") + ": " + ex.Message);
		}

		string from = StratumConfigPath.FormatValue(previous);
		string to = StratumConfigPath.FormatValue(r.Property.GetValue(r.Owner));
		StratumRuntime.LogAudit($"config set {r.PathBuilt}: {from} -> {to} by {callerName}", true);

		StringBuilder output = new(StratumCommandText.Title("Stratum Config"));
		output.Append(StratumCommandText.Confirm("Saved", r.PathBuilt + " = " + to + " (was " + from + ")"));
		output.Append(StratumCommandText.Row("Note", "some changes apply immediately, others on next restart. /stratum reload re-reads from file."));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleConfigSave()
	{
		try
		{
			StratumRuntime.Config.EnsurePopulated();
			StratumRuntime.SaveConfig();
			return TextCommandResult.Success(StratumCommandText.Confirm("Stratum config", "saved to " + StratumRuntime.ConfigPath));
		}
		catch (Exception ex)
		{
			return TextCommandResult.Error(StratumCommandText.Warning("Save failed") + " " + ex.Message);
		}
	}

	private static string FormatPreflight(StratumPreflightReport report)
	{
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Preflight"));
		output.Append(StratumCommandText.Row("Summary", report.Summary));

		foreach (string error in report.Errors)
		{
			output.Append(StratumCommandText.RawRow("Error", StratumCommandText.Danger(error)));
		}

		foreach (string warning in report.Warnings)
		{
			output.Append(StratumCommandText.RawRow("Warning", StratumCommandText.Warning(warning)));
		}

		return output.ToString();
	}

	private static string FormatHealthState(decimal mspt, decimal targetTps)
	{
		if (mspt <= 0 || targetTps <= 0)
		{
			return "collecting";
		}

		decimal tickBudget = 1000m / targetTps;
		if (mspt > tickBudget * 1.5m)
		{
			return "busy";
		}

		if (mspt > tickBudget)
		{
			return "watch";
		}

		return "healthy";
	}

	private static string FormatMetric(decimal value)
	{
		return value <= 0 ? "collecting" : value.ToString(GlobalConstants.DefaultCultureInfo);
	}
}
