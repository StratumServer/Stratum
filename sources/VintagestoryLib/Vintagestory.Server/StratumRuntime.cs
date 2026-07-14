using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json;
using Vintagestory.API.Config;

namespace Vintagestory.Server;

internal static class StratumRuntime
{
	public const string LogPrefix = "[Stratum]";

	private static readonly object AuditLock = new object();

	public static StratumConfig Config { get; private set; } = StratumConfig.CreateDefault();

	public static StratumPacketLimiter PacketLimiter { get; } = new StratumPacketLimiter();

	public static StratumPacketBackPressure PacketBackPressure { get; } = new StratumPacketBackPressure();

	public static StratumBlockBreakGuard BlockBreakGuard { get; } = new StratumBlockBreakGuard();

	public static StratumPerformanceStats PerformanceStats { get; } = new StratumPerformanceStats();

	public static StratumTimings Timings { get; } = new StratumTimings();

	public static StratumPregenManager Pregen { get; } = new StratumPregenManager();

	public static StratumAdaptiveRadiusController AdaptiveRadius { get; private set; }
	public static string ConfigPath { get; private set; } = "stratum.json";

	public static string CommandsConfigPath { get; private set; } = "stratum-commands.json";

	public static string PerformanceConfigPath { get; private set; } = "stratum-performance.json";

	public static DateTime LastLoadedUtc { get; private set; }

	public static string LastLoadStatus { get; private set; } = "not loaded";

	public static StratumPreflightReport LastPreflight { get; private set; } = StratumPreflightReport.NotRun();

	public static bool FineSleepGranularity { get; private set; } = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

	public static string TimerResolutionStatus { get; private set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "not requested" : "native";

	// Updated by PhysicsManager.ServerTick each tick: true if the previous tick exceeded
	// Performance.Physics.OverloadedTickThresholdMs. EventManager reads this to apply
	// adaptive throttling to non-critical entity tick listeners.
	public static volatile bool PreviousTickOverloaded;

	public static void SetTimerResolutionStatus(bool fineSleepGranularity, string status)
	{
		FineSleepGranularity = fineSleepGranularity;
		TimerResolutionStatus = status ?? "unknown";
	}

	public static void WaitForNextTick(Stopwatch tickTimer, float tickBudgetMs)
	{
		// Without a 1ms-accurate Sleep, chasing the deadline via Yield/SpinWait means spinning
		// a full core for the whole ~15.6ms scheduler tick instead of just the sleep overshoot.
		// Fall back to a single coarse sleep here rather than the precise wait below.
		if (!FineSleepGranularity)
		{
			double coarseRemainingMs = tickBudgetMs - tickTimer.Elapsed.TotalMilliseconds;
			if (coarseRemainingMs > 0)
			{
				Thread.Sleep((int)Math.Ceiling(coarseRemainingMs));
			}
			return;
		}

		while (true)
		{
			double remainingMs = tickBudgetMs - tickTimer.Elapsed.TotalMilliseconds;
			if (remainingMs <= 0)
			{
				return;
			}

			// Sleep once for most of the wait, then trim the last edge without another overshooting sleep.
			const double coarseMarginMs = 2.0;
			if (remainingMs > coarseMarginMs)
			{
				int sleepMs = Math.Max(1, (int)Math.Floor(remainingMs - coarseMarginMs));
				Thread.Sleep(sleepMs);
			}
			else if (remainingMs > 0.5)
			{
				Thread.Yield();
			}
			else
			{
				Thread.SpinWait(64);
			}
		}
	}

	public static void InitAdaptiveRadius(ServerMain server)
	{
		AdaptiveRadius = new StratumAdaptiveRadiusController(server);
	}

	public static void LogInfo(string message)
	{
		ServerMain.Logger?.Notification("{0} {1}", LogPrefix, message);
	}

	public static void LogWarning(string message)
	{
		ServerMain.Logger?.Warning("{0} {1}", LogPrefix, message);
	}

	public static void LogError(string message)
	{
		ServerMain.Logger?.Error("{0} {1}", LogPrefix, message);
	}

	public static bool AllowsUnauthenticatedLoadTestClient(Packet_ClientIdentification identification)
	{
		Config.EnsurePopulated();
		StratumLoadTestingConfig loadTesting = Config.LoadTesting;
		if (!loadTesting.AllowUnauthenticatedClients || identification == null)
		{
			return false;
		}

		return StartsWith(identification.Playername, loadTesting.RequiredPlayerNamePrefix) && StartsWith(identification.PlayerUID, loadTesting.RequiredPlayerUidPrefix);
	}

	private static bool StartsWith(string value, string prefix)
	{
		return !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(prefix) && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
	}

	public static string[] BuildClientModWhitelist(IReadOnlyList<Packet_ModId> serverUniversalMods, string[] serverConfigWhitelist)
	{
		Config.EnsurePopulated();
		StratumClientModPolicyConfig policy = Config.ClientModPolicy;
		if (!policy.Enabled || !policy.StrictWhitelist)
		{
			return null;
		}

		SortedSet<string> whitelist = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		AddAlwaysAllowedCoreMods(whitelist);
		if (policy.IncludeServerUniversalMods && serverUniversalMods != null)
		{
			for (int index = 0; index < serverUniversalMods.Count; index++)
			{
				Packet_ModId mod = serverUniversalMods[index];
				AddModWhitelistEntry(whitelist, FormatModVersionEntry(mod?.Modid, mod?.Version));
			}
		}

		AddModWhitelistEntries(whitelist, serverConfigWhitelist);
		AddModWhitelistEntries(whitelist, policy.AllowModIds);
		string[] result = new string[whitelist.Count];
		whitelist.CopyTo(result);
		return result;
	}

	private static void AddAlwaysAllowedCoreMods(SortedSet<string> whitelist)
	{
		foreach (string modId in new[] { "game", "creative", "survival" })
		{
			whitelist.Add(modId);
		}
	}

	private static void AddModWhitelistEntries(SortedSet<string> whitelist, IEnumerable<string> entries)
	{
		if (entries == null)
		{
			return;
		}

		foreach (string entry in entries)
		{
			AddModWhitelistEntry(whitelist, entry);
		}
	}

	private static void AddModWhitelistEntry(SortedSet<string> whitelist, string entry)
	{
		if (!string.IsNullOrWhiteSpace(entry))
		{
			whitelist.Add(entry.Trim());
		}
	}

	private static string FormatModVersionEntry(string modId, string version)
	{
		if (string.IsNullOrWhiteSpace(modId))
		{
			return null;
		}

		return string.IsNullOrWhiteSpace(version) ? modId.Trim() : modId.Trim() + "@" + version.Trim();
	}

	public static bool LoadOrCreateConfig(ServerMain server, out string message)
	{
		ConfigPath = Path.Combine(GamePaths.Config, "stratum.json");
		CommandsConfigPath = Path.Combine(GamePaths.Config, "stratum-commands.json");
		PerformanceConfigPath = Path.Combine(GamePaths.Config, "stratum-performance.json");

		try
		{
			GamePaths.EnsurePathExists(GamePaths.Config);

			bool mainConfigExisted = File.Exists(ConfigPath);
			Config = mainConfigExisted
				? JsonConvert.DeserializeObject<StratumConfig>(File.ReadAllText(ConfigPath)) ?? StratumConfig.CreateDefault()
				: StratumConfig.CreateDefault();

			// Stratum: load the sidecars whenever they exist, not only when stratum.json also
			// exists. A first boot on a data dir provisioned with just stratum-performance.json
			// (or stratum-commands.json) used to skip this check and overwrite it with defaults.
			if (File.Exists(CommandsConfigPath))
			{
				Config.Commands = JsonConvert.DeserializeObject<StratumCommandsConfig>(File.ReadAllText(CommandsConfigPath)) ?? new StratumCommandsConfig();
			}
			if (File.Exists(PerformanceConfigPath))
			{
				Config.Performance = JsonConvert.DeserializeObject<StratumPerformanceConfig>(File.ReadAllText(PerformanceConfigPath)) ?? new StratumPerformanceConfig();
			}
			Config.EnsurePopulated();
			SaveConfig();
			message = mainConfigExisted ? "loaded config" : "created default config";

			LastLoadedUtc = DateTime.UtcNow;
			LastLoadStatus = message;
			return true;
		}
		catch (Exception exception)
		{
			Config = StratumConfig.CreateDefault();
			LastLoadedUtc = DateTime.UtcNow;
			LastLoadStatus = "failed to load config: " + exception.Message;
			message = LastLoadStatus;
			LogError("Failed to load config at " + ConfigPath);
			ServerMain.Logger.Error(exception);
			return false;
		}
	}

	public static void SaveConfig()
	{
		Config.EnsurePopulated();
		File.WriteAllText(CommandsConfigPath, JsonConvert.SerializeObject(Config.Commands, Formatting.Indented));
		File.WriteAllText(PerformanceConfigPath, JsonConvert.SerializeObject(Config.Performance, Formatting.Indented));
		File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config, StratumConfig.MainFileSerializerSettings));
	}

	public static StratumPreflightReport RunPreflight()
	{
		StratumPreflightReport report = new StratumPreflightReport();
		string baseDirectory = AppContext.BaseDirectory;

		CheckDirectory(report, baseDirectory, "assets");
		CheckDirectory(report, baseDirectory, "Lib");
		CheckDirectory(report, baseDirectory, "Mods");
		CheckFile(report, baseDirectory, "VintagestoryLib.dll");
		CheckFile(report, baseDirectory, "VintagestoryAPI.dll");
		CheckFile(report, baseDirectory, Path.Combine("Lib", "libSkiaSharp.dll"));
		CheckSuspiciousRootNative(report, baseDirectory, "libSkiaSharp.dll", 1024 * 1024);
		CheckServerConfigShape(report);
		CheckStratumConfigSafety(report);

		LastPreflight = report;
		return report;
	}

	private static void CheckServerConfigShape(StratumPreflightReport report)
	{
		string serverConfigPath = Path.Combine(GamePaths.Config, "serverconfig.json");
		string rolesConfigPath = Path.Combine(GamePaths.Config, "serverroles.json");
		if (!File.Exists(serverConfigPath))
		{
			report.Errors.Add("Missing serverconfig.json");
			return;
		}

		if (!File.Exists(rolesConfigPath))
		{
			report.Errors.Add("Missing serverroles.json");
		}

		string serverConfig = File.ReadAllText(serverConfigPath);
		if (serverConfig.Contains("\"RolesByCode\"", StringComparison.Ordinal) || serverConfig.Contains("\"Roles\"", StringComparison.Ordinal) || serverConfig.Contains("\"DefaultRoleCode\"", StringComparison.Ordinal))
		{
			report.Warnings.Add("serverconfig.json still contains role data; run --setconfig or /serverconfig once to rewrite it into serverroles.json");
		}

		if (!serverConfig.Contains("\"WorldConfiguration\"", StringComparison.Ordinal))
		{
			report.Errors.Add("serverconfig.json WorldConfig is missing WorldConfiguration; regenerate or rewrite config before creating a new world");
		}
	}

	private static void CheckStratumConfigSafety(StratumPreflightReport report)
	{
		Config.EnsurePopulated();
		if (Config.LoadTesting.AllowUnauthenticatedClients)
		{
			report.Warnings.Add("LoadTesting.AllowUnauthenticatedClients is enabled; matching load-test clients can join without Vintage Story auth/session validation");
		}

		if (!Config.Hardening.PreserveVanillaProtocol)
		{
			report.Errors.Add("PreserveVanillaProtocol is disabled; Stratum should remain vanilla-client compatible");
		}

		StratumPacketLimitsConfig packets = Config.PacketLimits;
		if (Config.Hardening.PacketMonitoring && packets.Enabled && packets.KickViolations && packets.KickAfterViolations > 0 && packets.KickAfterViolations < 3)
		{
			report.Warnings.Add("Packet KickAfterViolations is very low; large servers should tune in monitor/drop mode before aggressive kicking");
		}

		StratumBlockBreakGuardsConfig blockBreak = Config.BlockBreakGuards;
		StratumBlockBreakProgressAnticheatConfig blockBreakKick = Config.Anticheat.BlockBreakProgress;
		if (Config.Hardening.BlockBreakGuards && blockBreak.Enabled && Config.Anticheat.Enabled && blockBreakKick.Enabled && blockBreakKick.KickConfirmedCheats && blockBreakKick.KickAfterViolations < 3)
		{
			report.Warnings.Add("Anticheat.BlockBreakProgress KickAfterViolations is very low; raise it while validating modded mining behavior");
		}

		if (blockBreak.RequiredProgressRatio > 0.95f && blockBreak.GraceSeconds <= 0.1f)
		{
			report.Warnings.Add("BlockBreakGuards has a strict progress ratio with little grace; this can false-positive under latency");
		}

		StratumPerformanceConfig performance = Config.Performance;
		StratumChunkRequestManagementConfig chunkRequests = performance.ChunkRequestManagement;
		if (chunkRequests.Enabled && chunkRequests.CancelStalePendingRequests && chunkRequests.MinimumPendingAgeSeconds < 1)
		{
			report.Warnings.Add("ChunkRequestManagement MinimumPendingAgeSeconds is below 1; stale request cancellation may become too aggressive for fast movement");
		}

		if (chunkRequests.Enabled && chunkRequests.CancelStalePendingRequests && chunkRequests.MaxDistanceBeyondViewChunks == 0)
		{
			report.Warnings.Add("ChunkRequestManagement MaxDistanceBeyondViewChunks is 0; allow a small buffer to avoid cancelling near-edge player movement");
		}

		StratumPregenConfig pregen = performance.Pregen;
		if (pregen.Enabled && pregen.MaxColumnsPerSecond > pregen.MaxPendingColumnQueue)
		{
			report.Warnings.Add("Pregen MaxColumnsPerSecond is higher than MaxPendingColumnQueue; queue pressure may pause pregen every tick");
		}

		if (pregen.Enabled && pregen.PauseBelowTps > 0 && pregen.PauseBelowTps > 19.5)
		{
			report.Warnings.Add("Pregen PauseBelowTps is very close to perfect TPS; pregen may rarely make progress");
		}

		StratumSimulationDistanceConfig simulationDistance = performance.SimulationDistance;
		if (simulationDistance.Enabled && simulationDistance.LimitRandomTicks && simulationDistance.RandomTickDistanceBlocks < MagicNum.ServerChunkSize)
		{
			report.Warnings.Add("SimulationDistance RandomTickDistanceBlocks is below one chunk; random tick behavior will be very localized");
		}

		if (simulationDistance.Enabled && simulationDistance.LimitBlockGameTickListeners && simulationDistance.BlockGameTickListenerDistanceBlocks < MagicNum.ServerChunkSize * 2)
		{
			report.Warnings.Add("SimulationDistance BlockGameTickListenerDistanceBlocks is below two chunks; modded block entities may pause close to players");
		}

		if (performance.EntityTicking.Enabled && performance.EntityTicking.FarEntityTickInterval > 20)
		{
			report.Warnings.Add("EntityTicking FarEntityTickInterval is very high; far AI may look bursty or delayed");
		}

		if (performance.BlockTicks.Enabled && performance.BlockTicks.MaxRandomTicksPerChunk == 0)
		{
			report.Warnings.Add("BlockTicks MaxRandomTicksPerChunk is 0; crop/fire/fluid style random ticks may effectively stop");
		}

		if (performance.AutoSave.Enabled && performance.AutoSave.DelayDuringChunkPressure && performance.AutoSave.MaxDelaySeconds > 300)
		{
			report.Warnings.Add("AutoSave MaxDelaySeconds is above 5 minutes; large saves should avoid very long save deferrals");
		}

		if (performance.AutoSave.Enabled && performance.AutoSave.IncrementalDirtyFlush && performance.AutoSave.IncrementalFlushIntervalSeconds < 3)
		{
			report.Warnings.Add("AutoSave IncrementalFlushIntervalSeconds is very low; avoid turning save smoothing into constant disk churn");
		}

		if (performance.AutoSave.Enabled && performance.AutoSave.IncrementalDirtyFlush && performance.AutoSave.MaxLoadedChunksPerFlush > 1000)
		{
			report.Warnings.Add("AutoSave MaxLoadedChunksPerFlush is very high; large flushes can still create disk spikes");
		}
	}

	private static void CheckDirectory(StratumPreflightReport report, string baseDirectory, string relativePath)
	{
		if (!Directory.Exists(Path.Combine(baseDirectory, relativePath)))
		{
			report.Errors.Add("Missing directory: " + relativePath);
		}
	}

	private static void CheckFile(StratumPreflightReport report, string baseDirectory, string relativePath)
	{
		if (!File.Exists(Path.Combine(baseDirectory, relativePath)))
		{
			report.Errors.Add("Missing file: " + relativePath);
		}
	}

	private static void CheckSuspiciousRootNative(StratumPreflightReport report, string baseDirectory, string fileName, long minimumExpectedBytes)
	{
		string path = Path.Combine(baseDirectory, fileName);
		if (!File.Exists(path))
		{
			return;
		}

		FileInfo fileInfo = new FileInfo(path);
		if (fileInfo.Length < minimumExpectedBytes)
		{
			report.Errors.Add($"Suspicious root native library: {fileName} is only {fileInfo.Length} bytes");
		}
	}

	public static void LogAudit(string message, bool mirrorToConsole = false)
	{
		message = NormalizeLogMessage(message);
		string prefixedMessage = PrefixLogMessage(message);

		if (mirrorToConsole)
		{
			LogInfo("audit " + message);
		}

		try
		{
			GamePaths.EnsurePathExists(GamePaths.Logs);
			string line = $"{DateTime.UtcNow:O} {prefixedMessage}{Environment.NewLine}";
			lock (AuditLock)
			{
				File.AppendAllText(Path.Combine(GamePaths.Logs, "stratum-audit.log"), line);
			}
		}
		catch (Exception exception)
		{
			LogWarning("audit log write failed: " + exception.Message);
		}
	}

	public static string PrefixLogMessage(string message)
	{
		return message.StartsWith(LogPrefix, StringComparison.Ordinal) ? message : LogPrefix + " " + message;
	}

	private static string NormalizeLogMessage(string message)
	{
		return (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
	}
}

internal class StratumPreflightReport
{
	public List<string> Errors { get; } = new List<string>();

	public List<string> Warnings { get; } = new List<string>();

	public bool WasRun { get; private set; } = true;

	public bool Passed => Errors.Count == 0;

	public string Summary => WasRun ? (Passed ? "passed" : $"failed ({Errors.Count} errors, {Warnings.Count} warnings)") : "not run";

	public static StratumPreflightReport NotRun()
	{
		return new StratumPreflightReport { WasRun = false };
	}
}
