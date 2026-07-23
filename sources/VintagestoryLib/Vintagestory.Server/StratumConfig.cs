using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common.Entities;

namespace Vintagestory.Server;

internal class StratumConfig
{
	public int ConfigVersion { get; set; } = 2;

	public StratumDiagnosticsConfig Diagnostics { get; set; } = new StratumDiagnosticsConfig();

	public StratumUpdateCheckerConfig UpdateChecker { get; set; } = new StratumUpdateCheckerConfig();

	public StratumHardeningConfig Hardening { get; set; } = new StratumHardeningConfig();

	public StratumPacketLimitsConfig PacketLimits { get; set; } = new StratumPacketLimitsConfig();

	public StratumPacketBackPressureConfig PacketBackPressure { get; set; } = new StratumPacketBackPressureConfig();

	public StratumBlockBreakGuardsConfig BlockBreakGuards { get; set; } = new StratumBlockBreakGuardsConfig();

	public StratumAnticheatConfig Anticheat { get; set; } = new StratumAnticheatConfig();

	public StratumClientModPolicyConfig ClientModPolicy { get; set; } = new StratumClientModPolicyConfig();

	public StratumPerformanceConfig Performance { get; set; } = new StratumPerformanceConfig();

	public StratumCommandsConfig Commands { get; set; } = new StratumCommandsConfig();

	public StratumChatConfig Chat { get; set; } = new StratumChatConfig();

	public StratumThemeConfig Theme { get; set; } = new StratumThemeConfig();

	public StratumCrowdSpawnConfig CrowdSpawn { get; set; } = new StratumCrowdSpawnConfig();

	public StratumLoadTestingConfig LoadTesting { get; set; } = new StratumLoadTestingConfig();

	public StratumLoginProtectionConfig LoginProtection { get; set; } = new StratumLoginProtectionConfig();

	public StratumPlayerPrivacyConfig PlayerPrivacy { get; set; } = new StratumPlayerPrivacyConfig();

	public StratumNametagsConfig Nametags { get; set; } = new StratumNametagsConfig();

	public StratumBackupConfig Backup { get; set; } = new StratumBackupConfig();

	public StratumAnnouncementsConfig Announcements { get; set; } = new StratumAnnouncementsConfig();

	public StratumServerStatsConfig ServerStats { get; set; } = new StratumServerStatsConfig();

	public void EnsurePopulated()
	{
		Diagnostics ??= new StratumDiagnosticsConfig();
		UpdateChecker ??= new StratumUpdateCheckerConfig();
		Hardening ??= new StratumHardeningConfig();
		PacketLimits ??= new StratumPacketLimitsConfig();
		PacketBackPressure ??= new StratumPacketBackPressureConfig();
		BlockBreakGuards ??= new StratumBlockBreakGuardsConfig();
		Anticheat ??= new StratumAnticheatConfig();
		ClientModPolicy ??= new StratumClientModPolicyConfig();
		Performance ??= new StratumPerformanceConfig();
		Commands ??= new StratumCommandsConfig();
		Chat ??= new StratumChatConfig();
		Theme ??= new StratumThemeConfig();
		CrowdSpawn ??= new StratumCrowdSpawnConfig();
		LoadTesting ??= new StratumLoadTestingConfig();
		LoginProtection ??= new StratumLoginProtectionConfig();
		PlayerPrivacy ??= new StratumPlayerPrivacyConfig();
		Nametags ??= new StratumNametagsConfig();
		Backup ??= new StratumBackupConfig();
		Announcements ??= new StratumAnnouncementsConfig();
		ServerStats ??= new StratumServerStatsConfig();
		PacketLimits.EnsureSane();
		PacketBackPressure.EnsureSane();
		BlockBreakGuards.EnsureSane();
		Anticheat.EnsureSane();
		ClientModPolicy.EnsurePopulated();
		Performance.EnsurePopulated();
		Commands.EnsurePopulated();
		Chat.EnsurePopulated();
		Theme.EnsurePopulated();
		CrowdSpawn.EnsureSane();
		LoadTesting.EnsureSane();
		LoginProtection.EnsureSane();
		PlayerPrivacy.EnsurePopulated();
		Nametags.EnsurePopulated();
		Backup.EnsureSane();
		Announcements.EnsureSane();
		ServerStats.EnsureSane();
		UpdateChecker.EnsureSane();
		MigrateLegacyDefaults();
	}

	// Bumps known-bad legacy defaults to the new recommended values when an older config is
	// loaded. Only overrides values that match the *exact* old default so a server owner who
	// chose a custom value keeps it.
	private void MigrateLegacyDefaults()
	{
		if (ConfigVersion < 2)
		{
			StratumPhysicsConfig phys = Performance?.Physics;
			if (phys != null)
			{
				if (phys.ParallelThreshold == 256) phys.ParallelThreshold = 32;
				// MaxThreadsOverride==0 is now interpreted by PhysicsManager as "auto", no JSON
				// change needed for thread count.
			}
			ConfigVersion = 2;
		}
	}

	public static StratumConfig CreateDefault()
	{
		return new StratumConfig();
	}

	// Serializer settings used when writing the main stratum.json. Skips Commands and Performance
	// because those live in stratum-commands.json / stratum-performance.json sidecars.
	public static readonly Newtonsoft.Json.JsonSerializerSettings MainFileSerializerSettings = new Newtonsoft.Json.JsonSerializerSettings
	{
		Formatting = Newtonsoft.Json.Formatting.Indented,
		ContractResolver = new SidecarPropertyResolver()
	};

	private class SidecarPropertyResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
	{
		protected override Newtonsoft.Json.Serialization.JsonProperty CreateProperty(System.Reflection.MemberInfo member, Newtonsoft.Json.MemberSerialization memberSerialization)
		{
			var prop = base.CreateProperty(member, memberSerialization);
			if (member.DeclaringType == typeof(StratumConfig) && (member.Name == nameof(Commands) || member.Name == nameof(Performance)))
			{
				prop.ShouldSerialize = _ => false;
			}
			return prop;
		}
	}
}

internal class StratumUpdateCheckerConfig
{
	public bool Enabled { get; set; } = true;

	public bool CheckOnStartup { get; set; } = true;

	public string LatestReleaseUrl { get; set; } = "https://api.github.com/repos/StratumServer/Stratum/releases/latest";

	public int TimeoutSeconds { get; set; } = 5;

	public void EnsureSane()
	{
		LatestReleaseUrl = string.IsNullOrWhiteSpace(LatestReleaseUrl)
			? "https://api.github.com/repos/StratumServer/Stratum/releases/latest"
			: LatestReleaseUrl.Trim();
		TimeoutSeconds = Math.Min(30, Math.Max(1, TimeoutSeconds));
	}
}

// Anonymous server stats reporting ("phone home"). Opt-out: on by default. Each server posts a
// tiny JSON blob (a random per-install id, the version, and the current player count) to
// ReportUrl every IntervalMinutes so the project can show total players and total servers running
// Stratum. Nothing identifying is sent - no player names, no IPs, no server address or name.
internal class StratumServerStatsConfig
{
	public bool Enabled { get; set; } = true;

	// Central endpoint that receives the report (HTTP POST, application/json). Point this at your
	// aggregator. Leave empty to keep reporting off even when Enabled is true.
	public string ReportUrl { get; set; } = "https://my.stratumvs.dev/stratum-stats.php";

	public int IntervalMinutes { get; set; } = 30;

	public int TimeoutSeconds { get; set; } = 10;

	// Random, anonymous, per-install id. Generated once on first run and kept so restarts are not
	// counted as new servers. Delete it to reset this server's identity.
	public string ServerId { get; set; } = "";

	public void EnsureSane()
	{
		IntervalMinutes = Math.Clamp(IntervalMinutes, 5, 1440);
		TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 60);
		ReportUrl = string.IsNullOrWhiteSpace(ReportUrl) ? "" : ReportUrl.Trim();
		ServerId = ServerId?.Trim() ?? "";
	}
}

internal class StratumCrowdSpawnConfig
{
	public bool Enabled { get; set; } = true;

	public int MinimumNearbyPlayers { get; set; } = 24;

	public int DetectionRadiusBlocks { get; set; } = 48;

	public int SpreadRadiusBlocks { get; set; } = 64;

	public int SafePositionTries { get; set; } = 15;

	public bool IncludeReturningPlayers { get; set; }

	public void EnsureSane()
	{
		MinimumNearbyPlayers = Math.Max(1, MinimumNearbyPlayers);
		DetectionRadiusBlocks = Math.Max(1, DetectionRadiusBlocks);
		SpreadRadiusBlocks = Math.Max(1, SpreadRadiusBlocks);
		SafePositionTries = Math.Max(1, SafePositionTries);
	}
}

internal class StratumLoadTestingConfig
{
	public bool AllowUnauthenticatedClients { get; set; }

	public string RequiredPlayerNamePrefix { get; set; } = "StratumBot";

	public string RequiredPlayerUidPrefix { get; set; } = "stratum-loadtest-";

	public bool LogAcceptedClients { get; set; } = true;

	public void EnsureSane()
	{
		if (string.IsNullOrWhiteSpace(RequiredPlayerNamePrefix))
		{
			RequiredPlayerNamePrefix = "StratumBot";
		}

		if (string.IsNullOrWhiteSpace(RequiredPlayerUidPrefix))
		{
			RequiredPlayerUidPrefix = "stratum-loadtest-";
		}
	}
}

internal class StratumLoginProtectionConfig
{
	/// <summary>Enable join/spawn damage immunity.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>How long the player is invulnerable after joining the world, in seconds.</summary>
	public int ProtectionSeconds { get; set; } = 5;

	/// <summary>End protection as soon as the player moves horizontally past <see cref="MoveThresholdBlocks"/>.</summary>
	public bool CancelOnHorizontalMove { get; set; } = true;

	/// <summary>How far (in blocks) a player must move horizontally from their join position to end protection.</summary>
	public double MoveThresholdBlocks { get; set; } = 0.5;

	/// <summary>If true, protection ends immediately when the player is on fire or standing in lava.</summary>
	public bool CancelInFireOrLava { get; set; } = true;

	/// <summary>If true, send the player a chat message when protection starts.</summary>
	public bool AnnounceOnStart { get; set; } = true;

	/// <summary>If true, send the player a chat message when protection ends.</summary>
	public bool AnnounceOnEnd { get; set; } = true;

	/// <summary>Message sent when protection starts. {0} = seconds.</summary>
	public string StartMessage { get; set; } = "You are protected from damage for {0} seconds. Move to cancel.";

	/// <summary>Message sent when protection ends.</summary>
	public string EndMessage { get; set; } = "Login protection ended.";

	public void EnsureSane()
	{
		ProtectionSeconds = Math.Max(0, ProtectionSeconds);
		if (MoveThresholdBlocks < 0.05) MoveThresholdBlocks = 0.05;
		if (string.IsNullOrWhiteSpace(StartMessage)) StartMessage = "You are protected from damage for {0} seconds. Move to cancel.";
		if (string.IsNullOrWhiteSpace(EndMessage)) EndMessage = "Login protection ended.";
	}
}

internal class StratumPlayerPrivacyConfig
{
	/// <summary>Enable the player-privacy disclosure hook. Disabled by default \u2014 opt-in.</summary>
	public bool Enabled { get; set; }

	/// <summary>Hide player pins on the world map from other players (other than staff / group, per settings).</summary>
	public bool HideMapPins { get; set; } = true;

	/// <summary>Players in the same player-group can still see each other on the map.</summary>
	public bool AllowGroupMapVisibility { get; set; } = true;

	/// <summary>Players matching this access (privilege) see all map pins regardless of HideMapPins.</summary>
	public StratumCommandAccessConfig StaffOverride { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.privacy.bypass");

	/// <summary>If true, staff with the override are also visible to everyone (their own pin is shown). If false, staff are hidden like other players.</summary>
	public bool ShowStaffPinsToAll { get; set; }

	/// <summary>
	/// Snap broadcast pin coordinates to a grid of this many blocks for non-staff, non-group receivers.
	/// 0 = exact. Use e.g. 32 to round nearest 32-block bucket so a hostile client can't deduce
	/// exact location even from a sneaked-in receiver. Only applies when HideMapPins is false (i.e.,
	/// when pins are being sent at all).
	/// </summary>
	public int CoordinateSnapBlocks { get; set; }

	/// <summary>
	/// If \u22650, overrides the world's <c>mapPlayerRenderDistance</c> at startup so map pins are
	/// only sent within this many blocks. -1 = leave worldconfig alone.
	/// </summary>
	public int MaxBroadcastDistanceBlocks { get; set; } = -1;

	public void EnsurePopulated()
	{
		StaffOverride ??= StratumCommandAccessConfig.ForPrivilege("stratum.privacy.bypass");
		StaffOverride.EnsurePopulated("stratum.privacy.bypass");
		if (CoordinateSnapBlocks < 0) CoordinateSnapBlocks = 0;
	}
}

/// <summary>
/// Server-only nametag customization. Reuses <see cref="StratumChatConfig.RolePrefixes"/>
/// to decorate player nametags above their head with the same tag (e.g. "[Admin]") used in
/// chat, and optionally injects one of the stock VS entitlement codes to colour the tag via
/// the vanilla client's <c>playerColorByEntitlement</c> renderer path. No client mod required.
/// <para>
/// Limitations (vanilla client renders the tag): only a single colour, no second line / rank
/// row, no background/border tweaks. Anything beyond a coloured name with a prefix needs a
/// companion client mod.
/// </para>
/// </summary>
internal class StratumNametagsConfig
{
	public bool Enabled { get; set; }

	/// <summary>Prepend the role's chat tag (from <c>Chat.RolePrefixes</c>) to the player's nametag, e.g. "[Admin] Alice".</summary>
	public bool ApplyChatPrefix { get; set; } = true;

	/// <summary>Format applied to the tag before it is prepended. {tag} = chat-tag text. Default mirrors chat.</summary>
	public string PrefixFormat { get; set; } = "[{tag}] ";

	/// <summary>
	/// Maps Stratum role code \u2192 vanilla entitlement code (must be one of
	/// <c>playerColorByEntitlement</c>: vsteam, glintteam, vscontributor, vssupporter,
	/// staff, bughunter, chiselmaster). If a role already grants a real entitlement we leave
	/// it alone. Empty string or unknown code = no colour injection.
	/// </summary>
	public Dictionary<string, string> EntitlementColorByRole { get; set; } = CreateDefaultEntitlementMap();

	/// <summary>If true, only apply the colour injection when the player has no real entitlement of their own.</summary>
	public bool OnlyInjectIfNoExistingEntitlement { get; set; } = true;

	public void EnsurePopulated()
	{
		PrefixFormat ??= "[{tag}] ";
		if (EntitlementColorByRole == null || EntitlementColorByRole.Count == 0)
		{
			EntitlementColorByRole = CreateDefaultEntitlementMap();
		}
	}

	private static Dictionary<string, string> CreateDefaultEntitlementMap()
	{
		return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["admin"] = "vsteam",
			["sumod"] = "glintteam",
			["crmod"] = "glintteam"
		};
	}
}

internal class StratumThemeConfig
{
	public bool Enabled { get; set; } = true;

	public bool StyleDisconnectScreens { get; set; } = true;

	public bool StyleJoinLeaveMessages { get; set; } = true;

	public bool StyleWelcomeMessages { get; set; } = true;

	public string BrandName { get; set; } = "Stratum";

	public string AccentColor { get; set; } = "#8bd5ff";

	public string GoodColor { get; set; } = "#9bd77e";

	public string WarnColor { get; set; } = "#e6c15f";

	public string BadColor { get; set; } = "#e47d68";

	public string MutedColor { get; set; } = "#9aa8b5";

	public string LabelColor { get; set; } = "#c9d6e2";

	public void EnsurePopulated()
	{
		BrandName ??= "Stratum";
		AccentColor = NormalizeHexColor(AccentColor, "#8bd5ff");
		GoodColor = NormalizeHexColor(GoodColor, "#9bd77e");
		WarnColor = NormalizeHexColor(WarnColor, "#e6c15f");
		BadColor = NormalizeHexColor(BadColor, "#e47d68");
		MutedColor = NormalizeHexColor(MutedColor, "#9aa8b5");
		LabelColor = NormalizeHexColor(LabelColor, "#c9d6e2");
	}

	private static string NormalizeHexColor(string color, string fallback)
	{
		if (string.IsNullOrWhiteSpace(color))
		{
			return fallback;
		}

		string value = color.Trim();
		if (value.Length == 7 && value[0] == '#')
		{
			for (int index = 1; index < value.Length; index++)
			{
				char c = value[index];
				bool isHex = c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F';
				if (!isHex)
				{
					return fallback;
				}
			}

			return value;
		}

		return fallback;
	}
}

internal class StratumDiagnosticsConfig
{
	public bool LogStartupSummary { get; set; } = true;

	public bool RunStartupPreflight { get; set; } = true;

	public bool LogPreflightWarnings { get; set; } = true;

	// Logs every mod-owned Harmony patch at startup (grouped by mod, split by prefix/postfix/
	// transpiler/finalizer). Visibility only, does not cross-reference against Stratum's own
	// source-patched methods -- see StratumHarmonyVisibility.
	public bool LogModHarmonyPatches { get; set; } = true;
}

internal class StratumHardeningConfig
{
	public bool PreserveVanillaProtocol { get; set; } = true;

	public bool PacketMonitoring { get; set; } = true;

	public bool BlockBreakGuards { get; set; } = true;

	public bool InventoryGuards { get; set; }

	public bool EntityGuards { get; set; }
}

internal class StratumPacketLimitsConfig
{
	public bool Enabled { get; set; } = true;

	public bool DropViolations { get; set; } = true;

	public bool LogViolations { get; set; } = false;

	public bool KickViolations { get; set; } = true;

	public bool KickInvalidPackets { get; set; } = true;

	public bool KickOversizedCustomPackets { get; set; } = true;

	public bool MonitorOnlySensitivePackets { get; set; } = true;

	public int KickAfterViolations { get; set; } = 8;

	public string KickMessage { get; set; } = "Disconnected by Stratum packet protection";

	public int WindowSeconds { get; set; } = 5;

	public int DefaultMaxPackets { get; set; } = 300;

	public int MovementMaxPackets { get; set; } = 420;

	public int InventoryMaxPackets { get; set; } = 90;

	public int BlockInteractionMaxPackets { get; set; } = 140;

	public int EntityInteractionMaxPackets { get; set; } = 140;

	public int HandInteractionMaxPackets { get; set; } = 90;

	public int CustomPacketMaxPackets { get; set; } = 120;

	public int CustomPacketMaxBytes { get; set; } = 262144;

	public void EnsureSane()
	{
		KickAfterViolations = Math.Max(0, KickAfterViolations);
		WindowSeconds = Math.Max(1, WindowSeconds);
		DefaultMaxPackets = Math.Max(1, DefaultMaxPackets);
		MovementMaxPackets = Math.Max(1, MovementMaxPackets);
		InventoryMaxPackets = Math.Max(1, InventoryMaxPackets);
		BlockInteractionMaxPackets = Math.Max(1, BlockInteractionMaxPackets);
		EntityInteractionMaxPackets = Math.Max(1, EntityInteractionMaxPackets);
		HandInteractionMaxPackets = Math.Max(1, HandInteractionMaxPackets);
		CustomPacketMaxPackets = Math.Max(1, CustomPacketMaxPackets);
		CustomPacketMaxBytes = Math.Max(1024, CustomPacketMaxBytes);
		if (string.IsNullOrWhiteSpace(KickMessage))
		{
			KickMessage = "Disconnected by Stratum packet protection";
		}
	}
}

internal class StratumPacketBackPressureConfig
{
	public bool Enabled { get; set; } = false;

	public int MaxMillisecondsPerTick { get; set; } = 25;

	public int MaxPacketsPerClientPerTick { get; set; } = 32;

	public int MaxQueueDepthPerClient { get; set; } = 2000;

	public bool KickOnQueueOverflow { get; set; } = true;

	public string KickMessage { get; set; } = "Disconnected by Stratum packet back-pressure";

	public void EnsureSane()
	{
		MaxMillisecondsPerTick = Math.Max(1, MaxMillisecondsPerTick);
		MaxPacketsPerClientPerTick = Math.Max(1, MaxPacketsPerClientPerTick);
		MaxQueueDepthPerClient = Math.Max(MaxPacketsPerClientPerTick * 4, MaxQueueDepthPerClient);
		if (string.IsNullOrWhiteSpace(KickMessage))
		{
			KickMessage = "Disconnected by Stratum packet back-pressure";
		}
	}
}

internal class StratumBlockBreakGuardsConfig
{
	public bool Enabled { get; set; } = true;

	public bool RequireServerSelection { get; set; } = true;

	public bool DropViolations { get; set; } = true;

	public bool LogViolations { get; set; } = false;

	public float RequiredProgressRatio { get; set; } = 0.8f;

	public float GraceSeconds { get; set; } = 0.25f;

	public float MinimumTrackedBreakSeconds { get; set; } = 0.15f;

	public float PartialProgressRetentionSeconds { get; set; } = 8f;

	public float MaxRememberedProgressRatio { get; set; } = 0.95f;

	public int MaxRememberedPartialBreaksPerClient { get; set; } = 24;

	// Staff alerting and confirmed-cheat kicking are governed by Anticheat.BlockBreakProgress.
	public void EnsureSane()
	{
		RequiredProgressRatio = Math.Max(0.1f, Math.Min(1f, RequiredProgressRatio));
		GraceSeconds = Math.Max(0f, GraceSeconds);
		MinimumTrackedBreakSeconds = Math.Max(0f, MinimumTrackedBreakSeconds);
		PartialProgressRetentionSeconds = Math.Max(0f, PartialProgressRetentionSeconds);
		MaxRememberedProgressRatio = Math.Max(0.1f, Math.Min(0.99f, MaxRememberedProgressRatio));
		MaxRememberedPartialBreaksPerClient = Math.Max(0, MaxRememberedPartialBreaksPerClient);
	}
}

internal class StratumClientModPolicyConfig
{
	public bool Enabled { get; set; } = false;

	public bool StrictWhitelist { get; set; } = true;

	public bool IncludeServerUniversalMods { get; set; } = true;

	public List<string> AllowModIds { get; set; } = new List<string>();

	public bool LogPolicyOnStartup { get; set; } = true;

	public void EnsurePopulated()
	{
		AllowModIds ??= new List<string>();
	}
}

internal class StratumPerformanceConfig
{
	public StratumChunkSendingConfig ChunkSending { get; set; } = new StratumChunkSendingConfig();

	public StratumChunkGenerationConfig ChunkGeneration { get; set; } = new StratumChunkGenerationConfig();

	public StratumChunkRequestManagementConfig ChunkRequestManagement { get; set; } = new StratumChunkRequestManagementConfig();

	public StratumPregenConfig Pregen { get; set; } = new StratumPregenConfig();

	public StratumSimulationDistanceConfig SimulationDistance { get; set; } = new StratumSimulationDistanceConfig();

	public StratumPhysicsConfig Physics { get; set; } = new StratumPhysicsConfig();

	public StratumEntityTickingConfig EntityTicking { get; set; } = new StratumEntityTickingConfig();

	public StratumChunkIoConfig ChunkIo { get; set; } = new StratumChunkIoConfig();

	public StratumChunkPriorityConfig ChunkPriority { get; set; } = new StratumChunkPriorityConfig();

	public StratumAutoSaveConfig AutoSave { get; set; } = new StratumAutoSaveConfig();

	public StratumBlockTickConfig BlockTicks { get; set; } = new StratumBlockTickConfig();

	public StratumTimingsConfig Timings { get; set; } = new StratumTimingsConfig();

	public StratumChatConfig Chat { get; set; } = new StratumChatConfig();

	public StratumPathfindingConfig Pathfinding { get; set; } = new StratumPathfindingConfig();

	public StratumRegionTickingConfig RegionTicking { get; set; } = new StratumRegionTickingConfig();

	public StratumBodyTemperatureConfig BodyTemperature { get; set; } = new StratumBodyTemperatureConfig();

	public StratumEventTickConfig EventTick { get; set; } = new StratumEventTickConfig();

	public StratumEntityCollisionsConfig EntityCollisions { get; set; } = new StratumEntityCollisionsConfig();

	public StratumBlockEntityInitConfig BlockEntityInit { get; set; } = new StratumBlockEntityInitConfig();

	public StratumTimerResolutionConfig TimerResolution { get; set; } = new StratumTimerResolutionConfig();

	public StratumJoinConfig Join { get; set; } = new StratumJoinConfig();

	public StratumAdaptiveRadiusConfig AdaptiveRadius { get; set; } = new StratumAdaptiveRadiusConfig();

	public StratumItemCleanupConfig ItemCleanup { get; set; } = new StratumItemCleanupConfig();

	public StratumNetworkConfig Network { get; set; } = new StratumNetworkConfig();

	public void EnsurePopulated()
	{
		ChunkSending ??= new StratumChunkSendingConfig();
		ChunkGeneration ??= new StratumChunkGenerationConfig();
		ChunkRequestManagement ??= new StratumChunkRequestManagementConfig();
		Pregen ??= new StratumPregenConfig();
		SimulationDistance ??= new StratumSimulationDistanceConfig();
		Physics ??= new StratumPhysicsConfig();
		EntityTicking ??= new StratumEntityTickingConfig();
		AutoSave ??= new StratumAutoSaveConfig();
		BlockTicks ??= new StratumBlockTickConfig();
		ChunkIo ??= new StratumChunkIoConfig();
		ChunkPriority ??= new StratumChunkPriorityConfig();
		Timings ??= new StratumTimingsConfig();
		Chat ??= new StratumChatConfig();
		Pathfinding ??= new StratumPathfindingConfig();
		RegionTicking ??= new StratumRegionTickingConfig();
		BodyTemperature ??= new StratumBodyTemperatureConfig();
		EventTick ??= new StratumEventTickConfig();
		EntityCollisions ??= new StratumEntityCollisionsConfig();
		BlockEntityInit ??= new StratumBlockEntityInitConfig();
		TimerResolution ??= new StratumTimerResolutionConfig();
		Join ??= new StratumJoinConfig();
		AdaptiveRadius ??= new StratumAdaptiveRadiusConfig();
		ItemCleanup ??= new StratumItemCleanupConfig();
		Network ??= new StratumNetworkConfig();
		ChunkSending.EnsureSane();
		ChunkGeneration.EnsureSane();
		ChunkRequestManagement.EnsureSane();
		Pregen.EnsureSane();
		SimulationDistance.EnsureSane();
		Physics.EnsureSane();
		EntityTicking.EnsureSane();
		AutoSave.EnsureSane();
		BlockTicks.EnsureSane();
		ChunkIo.EnsureSane();
		ChunkPriority.EnsureSane();
		Timings.EnsureSane();
		Chat.EnsurePopulated();
		Pathfinding.EnsureSane();
		RegionTicking.EnsureSane();
		BodyTemperature.EnsureSane();
		EventTick.EnsureSane();
		EntityCollisions.EnsureSane();
		BlockEntityInit.EnsureSane();
		TimerResolution.EnsureSane();
		Join.EnsureSane();
		AdaptiveRadius.EnsureSane();
		ItemCleanup.EnsureSane();
		Network.EnsureSane();
	}
}

internal class StratumNetworkConfig
{
	// Off by default. The queue removes the packet reordering bug from the old
	// StratumNetworkFlush (disabled after client crashes, see PR #138), but it is new code
	// on the hottest path in the server. Soak on a community server before flipping the
	// shipped default.
	public bool SendQueueEnabled { get; set; } = false;

	// Packets at or above this size skip coalescing and go out alone. Matches the TCP MTU
	// assumption the old flush buffer used.
	public int LargeThresholdBytes { get; set; } = 1400;

	// Upper bound on one coalesced send. Caps the reusable drain buffer and how much data
	// a single SendAsync call carries.
	public int CoalesceLimitBytes { get; set; } = 65536;

	public void EnsureSane()
	{
		LargeThresholdBytes = Math.Max(64, LargeThresholdBytes);
		CoalesceLimitBytes = Math.Max(LargeThresholdBytes, CoalesceLimitBytes);
	}
}

internal class StratumJoinConfig
{
	public bool CacheStartupPackets { get; set; } = true;

	// Limits how many queued players are admitted in one queue pass. 0 = vanilla behavior.
	public int MaxQueueAdmissionsPerPass { get; set; } = 2;

	// Limits how many RequestJoin packets (Id 11) are processed per tick. Deferred joins
	// drain on subsequent ticks. Keeps the main thread responsive during join storms.
	// 0 = no limit (vanilla behavior).
	public int MaxJoinsPerTick { get; set; } = 3;

	public void EnsureSane()
	{
		MaxQueueAdmissionsPerPass = Math.Max(0, Math.Min(64, MaxQueueAdmissionsPerPass));
		MaxJoinsPerTick = Math.Max(0, Math.Min(64, MaxJoinsPerTick));
	}
}

internal class StratumAdaptiveRadiusConfig
{
	public bool Enabled { get; set; } = false;

	public double SmoothingAlpha { get; set; } = 0.15;

	public double DecreaseMsptThreshold { get; set; } = 45.0;

	public double IncreaseMsptThreshold { get; set; } = 35.0;

	// Instant radius drop when a single tick exceeds this value (ms).
	public double OverloadDropThresholdMs { get; set; } = 500.0;

	public int FloorChunks { get; set; } = 4;

	public float AdjustmentIntervalSeconds { get; set; } = 2.0f;

	public void EnsureSane()
	{
		SmoothingAlpha = Math.Max(0.01, Math.Min(0.5, SmoothingAlpha));
		DecreaseMsptThreshold = Math.Max(1.0, DecreaseMsptThreshold);
		IncreaseMsptThreshold = Math.Max(1.0, IncreaseMsptThreshold);
		OverloadDropThresholdMs = Math.Max(50.0, OverloadDropThresholdMs);
		FloorChunks = Math.Max(1, FloorChunks);
		AdjustmentIntervalSeconds = Math.Max(1.0f, AdjustmentIntervalSeconds);
	}
}

internal class StratumItemCleanupConfig
{
	public bool Enabled { get; set; } = false;
	public int IntervalSeconds { get; set; } = 60;
	public int MinimumEntityAgeSeconds { get; set; } = 10;
	public string CleanupWarningMessage { get; set; } = "Cleaning up dropped items in {0} seconds.";
	public int[] CleanupWarningTimeOffsets { get; set; } = [30, 10];
	public string CleanupStartingMessage { get; set; } = "Cleaning up dropped items...";
	public string CleanupDoneMessage { get; set; } = "Cleaned up {0} items.";

	public void EnsureSane()
	{
		IntervalSeconds = Math.Max(1, IntervalSeconds);
		MinimumEntityAgeSeconds = Math.Max(0, MinimumEntityAgeSeconds);
		CleanupWarningMessage ??= "";
		CleanupStartingMessage ??= "";
		CleanupDoneMessage ??= "";
		CleanupWarningTimeOffsets ??= [];

		CleanupWarningTimeOffsets = CleanupWarningTimeOffsets.Select(n => Math.Clamp(n, 1, IntervalSeconds)).Distinct().ToArray();
	}
}

// Windows multimedia-timer resolution boost. Without this, Thread.Sleep() on Windows
// rounds up to the next ~15.6ms scheduler tick, capping the server's effective tickrate
// at ~25 tps regardless of how little work each tick does. Calling timeBeginPeriod(1) at
// startup makes Sleep accurate to ~1ms so the server can actually reach its 30 tps target.
// Process-wide; no-op on non-Windows. There is a small system-wide power-usage cost.
internal class StratumTimerResolutionConfig
{
	public bool Enabled { get; set; } = true;

	// Requested timer period in ms. 1 = highest practical resolution. Range 1-15.
	public int PeriodMs { get; set; } = 1;

	public void EnsureSane()
	{
		PeriodMs = Math.Max(1, Math.Min(15, PeriodMs));
	}
}

// Paper-style max-entity-collisions cap. Once an entity has been pushed by N neighbours
// in a single tick the partition walk halts for the remainder of that tick. Prevents
// O(N^2) blowup in dense mob piles. Bridged to VSEssentials' EntityBehaviorRepulseAgents
// via reflection (see RepulseRuntimeConfig ModSystem).
internal class StratumEntityCollisionsConfig
{
	public bool Enabled { get; set; } = true;

	// 0 disables the cap. Paper default is 8; we use 12 to be more permissive of pushing.
	public int MaxCollisionsPerEntity { get; set; } = 12;

	// Distance gate for RepulseAgents (non-player creatures skip entirely beyond this range).
	public float DistanceGateBlocks { get; set; } = 24f;

	public float NearBandBlocks { get; set; } = 12f;

	public int FarBandSkipTicks { get; set; } = 4;

	// CollectEntities scan stride. Only run the spatial query every N ticks per entity,
	// phase-offset by EntityId. Reduces allocations from GetEntitiesAround. 1 = every tick.
	public int CollectStrideInterval { get; set; } = 3;

	public void EnsureSane()
	{
		MaxCollisionsPerEntity = Math.Max(0, Math.Min(256, MaxCollisionsPerEntity));
		DistanceGateBlocks = Math.Max(0f, DistanceGateBlocks);
		NearBandBlocks = Math.Max(0f, Math.Min(DistanceGateBlocks, NearBandBlocks));
		FarBandSkipTicks = Math.Max(1, FarBandSkipTicks);
		CollectStrideInterval = Math.Max(1, Math.Min(16, CollectStrideInterval));
	}
}

// Paper-style BlockEntity tick listener stagger. When a BE registers a tick listener
// with initialDelayOffsetMs=0 (the overwhelming default), we deterministically derive
// an offset from Pos.GetHashCode() so that BEs registered together (e.g. on chunk load)
// don't all fire on the exact same future tick.
internal class StratumBlockEntityInitConfig
{
	public bool Enabled { get; set; } = true;

	// Max stagger window in ms. The actual offset for a given BE is
	// (Pos.HashCode % min(MaxStaggerMs, millisecondInterval)).
	// 0 disables. 250ms = up to ~7 ticks of stagger at 30Hz.
	public int MaxStaggerMs { get; set; } = 250;

	public void EnsureSane()
	{
		MaxStaggerMs = Math.Max(0, Math.Min(10000, MaxStaggerMs));
	}
}

internal class StratumPathfindingConfig
{
	// Move pathfinding tasks off the main thread onto a dedicated worker pool.
	public bool Async { get; set; } = true;

	// Worker thread count for async pathfinding. 0 = auto (max(2, Environment.ProcessorCount / 2)).
	public int WorkerThreads { get; set; } = 0;

	// Max queued pathfind requests before stale or oldest queued work is dropped. 0 = unbounded.
	public int MaxQueued { get; set; } = 512;

	// Queued async path requests older than this are considered stale and can be discarded
	// before useful work is dropped. 0 = never stale by age.
	public int MaxTaskAgeMs { get; set; } = 2000;

	// Reorders queued work near players first once the queue is backed up.
	public bool PriorityEnabled { get; set; } = true;

	// Queue depth that enables priority sorting and pressure-only far task drops.
	public int PriorityQueueThreshold { get; set; } = 64;

	// Queued tasks whose owner is farther than this from all players can be dropped
	// under queue pressure. 0 disables distance-based dropping.
	public int FarTaskDistanceBlocks { get; set; } = 160;

	public bool DropFarTasksUnderPressure { get; set; } = true;

	// A short per-traverser cooldown after repeated no-path results. Keeps mobs from
	// burning worker time on the same impossible goal every tick.
	public int FailureCooldownAfterFailures { get; set; } = 3;

	public int FailureCooldownMs { get; set; } = 1500;

	public void EnsureSane()
	{
		WorkerThreads = Math.Max(0, WorkerThreads);
		MaxQueued = Math.Max(0, MaxQueued);
		MaxTaskAgeMs = Math.Max(0, MaxTaskAgeMs);
		PriorityQueueThreshold = Math.Max(0, PriorityQueueThreshold);
		FarTaskDistanceBlocks = Math.Max(0, FarTaskDistanceBlocks);
		FailureCooldownAfterFailures = Math.Max(0, Math.Min(100, FailureCooldownAfterFailures));
		FailureCooldownMs = Math.Max(0, Math.Min(60000, FailureCooldownMs));
	}
}

internal class StratumRegionTickingConfig
{
	// Folia-style regionized entity ticking. Splits LoadedEntities into spatial regions
	// (region size in chunks) and ticks each region on a worker thread in parallel.
	// Behaviors that throw repeatedly get runtime-blocked via FallbackAfterExceptions.
	// Disabled by default: entity behaviors from mods may not be thread-safe. Enable after
	// validating your mod set under load. Unsafe behaviors self-quarantine to serial.
	public bool Enabled { get; set; }

	// Region size in chunks (e.g. 8 = 8x8 chunk regions = 256x256 blocks).
	public int RegionSizeChunks { get; set; } = 8;

	// Worker thread count. 0 = auto (max(1, Environment.ProcessorCount - 2)).
	public int WorkerThreads { get; set; } = 0;

	// Min entities required in a region to consider parallel ticking (small regions stay on main thread).
	public int MinEntitiesPerRegion { get; set; } = 16;

	// Entity filter mode. Controls which entities tick in parallel worker threads vs main thread.
	// "all" = current behavior (all non-players go parallel). "denylist" = entities matching
	// BlockedEntityCodePrefixes or BlockedEntityClasses tick on main thread. "allowlist" = only
	// entities matching AllowedEntityCodePrefixes go parallel, everything else stays serial.
	public string EntityFilterMode { get; set; } = "all";

	// Denylist mode: entity Code.Path prefixes that force main-thread ticking.
	public List<string> BlockedEntityCodePrefixes { get; set; } = new List<string>();

	// Denylist mode: entity Properties.Class strings that force main-thread ticking.
	// Matches against the JSON "class" field operators see in entity type configs.
	public List<string> BlockedEntityClasses { get; set; } = new List<string>();

	// Allowlist mode: entity Code.Path prefixes allowed to tick in parallel.
	// Everything not matching stays on main thread.
	public List<string> AllowedEntityCodePrefixes { get; set; } = new List<string>();

	// Automatic fallback: max exceptions a single entity type (Properties.Class) can throw
	// within FallbackWindowSeconds before it gets runtime-blocked from parallel ticking.
	// 0 = disabled (no automatic fallback).
	public int FallbackAfterExceptions { get; set; } = 5;

	// Time window in seconds for counting exceptions per entity type.
	public int FallbackWindowSeconds { get; set; } = 60;

	// When true, the runtime-blocked set persists until server restart.
	// When false (default), /stratum reload clears the blocked set.
	public bool PersistFallbackUntilRestart { get; set; } = false;

	public void EnsureSane()
	{
		RegionSizeChunks = Math.Max(1, RegionSizeChunks);
		WorkerThreads = Math.Max(0, WorkerThreads);
		MinEntitiesPerRegion = Math.Max(1, MinEntitiesPerRegion);
		EntityFilterMode ??= "all";
		BlockedEntityCodePrefixes ??= new List<string>();
		BlockedEntityClasses ??= new List<string>();
		AllowedEntityCodePrefixes ??= new List<string>();
		FallbackAfterExceptions = Math.Max(0, FallbackAfterExceptions);
		FallbackWindowSeconds = Math.Max(1, FallbackWindowSeconds);
	}
}

internal class StratumBodyTemperatureConfig
{
	// When enabled, vanilla bodytemperature accumulator thresholds are replaced with the values
	// below + per-entity jitter (so 150 players don't all run the expensive RoomRegistry +
	// climate sampling on the same tick).
	public bool Enabled { get; set; } = true;

	// How often per player to run the main ambient-temp/wetness update (vanilla = 2s).
	public float UpdateIntervalSeconds { get; set; } = 3f;

	// How often to recompute heat-source strength (the RoomRegistry flood-fill, vanilla = 5s).
	public float HeatSourceIntervalSeconds { get; set; } = 15f;

	// How often to refresh the freezing visual state (vanilla = 1s).
	public float FreezingAnimIntervalSeconds { get; set; } = 2f;

	// How often to do the frost-damage check (vanilla = 10s).
	public float DamageCheckIntervalSeconds { get; set; } = 12f;

	// Per-entity random jitter applied to each interval (0..1 = +/- this fraction).
	// 0.25 means a 5s interval ends up between 3.75s and 6.25s per entity.
	public float JitterPercent { get; set; } = 0.25f;

	public void EnsureSane()
	{
		UpdateIntervalSeconds = Math.Max(0.5f, UpdateIntervalSeconds);
		HeatSourceIntervalSeconds = Math.Max(UpdateIntervalSeconds, HeatSourceIntervalSeconds);
		FreezingAnimIntervalSeconds = Math.Max(0.25f, FreezingAnimIntervalSeconds);
		DamageCheckIntervalSeconds = Math.Max(UpdateIntervalSeconds, DamageCheckIntervalSeconds);
		JitterPercent = Math.Clamp(JitterPercent, 0f, 0.9f);
	}
}

internal class StratumPregenConfig
{
	public bool Enabled { get; set; } = true;

	public int MaxColumnsPerSecond { get; set; } = 500;

	public int MaxScansPerSecond { get; set; } = 4096;

	public int MaxPendingColumnQueue { get; set; } = 500;

	public int MaxWorkerColumnQueue { get; set; } = 1000;

	public int MaxLoadedChunkColumns { get; set; } = 4096;

	public int MaxRadiusChunks { get; set; } = 1000;

	public long MaxAreaColumns { get; set; } = 1000000;

	public bool PauseWhenPlayersOnline { get; set; } = true;

	public double PauseBelowTps { get; set; } = 15.0;

	public int ProgressLogIntervalSeconds { get; set; } = 60;

	public void EnsureSane()
	{
		MaxColumnsPerSecond = Math.Min(4096, Math.Max(1, MaxColumnsPerSecond));
		MaxScansPerSecond = Math.Min(262144, Math.Max(MaxColumnsPerSecond, MaxScansPerSecond));
		MaxPendingColumnQueue = Math.Min(65536, Math.Max(1, MaxPendingColumnQueue));
		MaxWorkerColumnQueue = Math.Min(65536, Math.Max(1, MaxWorkerColumnQueue));
		MaxLoadedChunkColumns = Math.Min(1000000, Math.Max(1, MaxLoadedChunkColumns));
		MaxRadiusChunks = Math.Min(10000, Math.Max(1, MaxRadiusChunks));
		MaxAreaColumns = Math.Min(100000000, Math.Max(1, MaxAreaColumns));
		PauseBelowTps = Math.Min(20, Math.Max(0, PauseBelowTps));
		ProgressLogIntervalSeconds = Math.Min(3600, Math.Max(0, ProgressLogIntervalSeconds));

		// Automatic protection against deadlocks and throttling
		int sysQueueLimit = MagicNum.RequestChunkColumnsQueueSize;

		// MaxPendingColumnQueue <= 1/4 of RequestChunkColumnsQueueSize
		int maxPendingAllowed = Math.Max(64, sysQueueLimit / 4);
		if (MaxPendingColumnQueue > maxPendingAllowed)
		{
			StratumRuntime.LogWarning($"MaxPendingColumnQueue ({MaxPendingColumnQueue}) was automatically clamped to {maxPendingAllowed} (1/4 of engine RequestChunkColumnsQueueSize: {sysQueueLimit}).");
			MaxPendingColumnQueue = maxPendingAllowed;
		}

		// MaxWorkerColumnQueue <= 1/2 of RequestChunkColumnsQueueSize
		int maxWorkerAllowed = Math.Max(128, sysQueueLimit / 2);
		if (MaxWorkerColumnQueue > maxWorkerAllowed)
		{
			StratumRuntime.LogWarning($"MaxWorkerColumnQueue ({MaxWorkerColumnQueue}) was automatically clamped to {maxWorkerAllowed} (1/2 of engine RequestChunkColumnsQueueSize: {sysQueueLimit}).");
			MaxWorkerColumnQueue = maxWorkerAllowed;
		}

		// MaxColumnsPerSecond must not exceed MaxPendingColumnQueue
		// If the insertion rate is higher than the buffer size,
		// the queue will instantly overflow, causing constant pauses
		if (MaxColumnsPerSecond > MaxPendingColumnQueue)
		{
			StratumRuntime.LogWarning($"MaxColumnsPerSecond ({MaxColumnsPerSecond}) was automatically clamped to MaxPendingColumnQueue ({MaxPendingColumnQueue}) to prevent instant queue pressure thrashing.");
			MaxColumnsPerSecond = MaxPendingColumnQueue;
		}
	}
}

internal class StratumChunkSendingConfig
{
	public bool Enabled { get; set; } = true;

	public int MaxChunksPerServerTick { get; set; } = 384;

	public int MaxChunksPerClientTick { get; set; } = 64;

	public bool IncludeLocalClients { get; set; }

	public bool AdaptiveUnderOverload { get; set; } = true;

	public int OverloadTickMs { get; set; } = 45;

	public float OverloadScale { get; set; } = 0.5f;

	public int OverloadFloorServerTick { get; set; } = 64;

	public int OverloadFloorClientTick { get; set; } = 8;

	public bool FairSchedulingEnabled { get; set; } = true;

	public int FairWindowMilliseconds { get; set; } = 5000;

	public int NearRingRadiusChunks { get; set; } = 2;

	public bool OutboundPressureEnabled { get; set; } = true;

	public int OutboundPressurePendingSendsSoftLimit { get; set; } = 8;

	public int OutboundPressurePendingSendsHardLimit { get; set; } = 32;

	public int OutboundPressurePendingBytesSoftLimit { get; set; } = 262144;

	public int OutboundPressurePendingBytesHardLimit { get; set; } = 1048576;

	public float OutboundPressureScale { get; set; } = 0.5f;

	public int OutboundPressureMinimumClientBudget { get; set; } = 2;

	public void EnsureSane()
	{
		MaxChunksPerServerTick = Math.Max(1, MaxChunksPerServerTick);
		MaxChunksPerClientTick = Math.Max(1, MaxChunksPerClientTick);
		OverloadTickMs = Math.Max(10, Math.Min(1000, OverloadTickMs));
		OverloadScale = Math.Max(0.05f, Math.Min(1f, OverloadScale));
		OverloadFloorServerTick = Math.Max(1, OverloadFloorServerTick);
		OverloadFloorClientTick = Math.Max(1, OverloadFloorClientTick);
		FairWindowMilliseconds = Math.Max(1000, FairWindowMilliseconds);
		NearRingRadiusChunks = Math.Max(0, NearRingRadiusChunks);
		OutboundPressurePendingSendsSoftLimit = Math.Max(1, OutboundPressurePendingSendsSoftLimit);
		OutboundPressurePendingSendsHardLimit = Math.Max(OutboundPressurePendingSendsSoftLimit, OutboundPressurePendingSendsHardLimit);
		OutboundPressurePendingBytesSoftLimit = Math.Max(1024, OutboundPressurePendingBytesSoftLimit);
		OutboundPressurePendingBytesHardLimit = Math.Max(OutboundPressurePendingBytesSoftLimit, OutboundPressurePendingBytesHardLimit);
		OutboundPressureScale = Math.Max(0.05f, Math.Min(1f, OutboundPressureScale));
		OutboundPressureMinimumClientBudget = Math.Max(0, OutboundPressureMinimumClientBudget);
	}
}

internal class StratumSimulationDistanceConfig
{
	public bool Enabled { get; set; } = true;

	public bool LimitRandomTicks { get; set; } = true;

	public int RandomTickDistanceBlocks { get; set; } = 96;

	public bool LimitBlockGameTickListeners { get; set; } = true;

	public int BlockGameTickListenerDistanceBlocks { get; set; } = 128;

	public bool TickForceLoadedBlockListeners { get; set; } = true;

	public void EnsureSane()
	{
		RandomTickDistanceBlocks = Math.Max(0, RandomTickDistanceBlocks);
		BlockGameTickListenerDistanceBlocks = Math.Max(0, BlockGameTickListenerDistanceBlocks);
	}
}

internal class StratumPhysicsConfig
{
	public bool Enabled { get; set; } = true;

	// Max physics steps consolidated into a single server tick (catchup). Vanilla used 3, which
	// works fine on a CPU-bound server where every tick is already busy. With Stratum's EAR
	// freeing headroom, the server accumulates spare time during idle ticks and would otherwise
	// dump 2-3 physics steps on the next tick (visible as 15-30ms spikes in an otherwise 4ms
	// average). 2 caps spikes at ~1 step worth of overrun (~7-14ms on solo) while still
	// recovering smoothly from a single slow server tick.
	public int MaxCatchUpTicksPerServerTick { get; set; } = 2;

	public int OverloadedMaxCatchUpTicksPerServerTick { get; set; } = 1;

	public int MinimumStateUpdateIntervalMs { get; set; } = 500;

	public int OverloadedStateUpdateIntervalMs { get; set; } = 1500;

	public int OverloadedTickThresholdMs { get; set; } = 60;

	// Tickables count threshold to switch from single-thread to parallel physics (vanilla = 800).
	// Lowering this makes parallel physics kick in earlier on busy servers. 32 means: as soon as
	// there are more than ~4 tickables per available physics thread, run in parallel.
	public int ParallelThreshold { get; set; } = 32;

	// Override for the MagicNum.MaxPhysicsThreads ceiling. 0 = use vanilla MagicNum value.
	// Effective threads are still clamped to [1, 8] internally.
	public int MaxThreadsOverride { get; set; } = 0;

	// When true, evenly split tickables across all physics threads instead of vanilla's
	// "thread 1 gets the first 480 + a share" partition (which overloads thread 1).
	public bool EvenThreadPartition { get; set; } = true;

	// Stratum: Paper-style Entity Activation Range for PhysicsManager.
	// Far-tracked entities (IsTracked == 1, >50 blocks from any player) tick threadsafe
	// behaviors and OnPhysicsTick at reduced frequency, interleaved deterministically by
	// EntityId so the cost spreads evenly across physics steps.
	public bool ActivationRangeEnabled { get; set; } = true;

	// Frame stride for entities in the MID band (NearActivationRadiusBlocks .. MidActivationRadiusBlocks).
	// 2 = half-rate (~15Hz instead of 30Hz). Was the old "far-tracked" stride when EAR keyed off the
	// binary IsTracked field; now drives the configurable distance band.
	public int FarTrackedTickStride { get; set; } = 2;

	// Frame stride for OnGameTick behaviors of untracked entities (IsTracked == 0). These
	// entities are skipped for OnPhysicsTick already, but vanilla still runs all threadsafe
	// behaviors every physics step. 4 = quarter-rate. 1 disables throttling.
	public int UntrackedBehaviorTickStride { get; set; } = 4;

	// Distance bands for EAR (Paper-style activation range). Uses Entity.NearestPlayerDistance
	// directly instead of the binary IsTracked field, so EAR provides benefit even on solo/low-pop
	// servers where vanilla's hardcoded 50-block IsTracked boundary engulfs all entities.
	//
	// Bands (when EAR enabled):
	//   d <  NearActivationRadiusBlocks    -> stride 1 (full rate, smooth motion)
	//   d <  MidActivationRadiusBlocks     -> stride FarTrackedTickStride (mid)
	//   d >= MidActivationRadiusBlocks     -> stride FarBandTickStride     (far, still tracked)
	//   IsTracked == 0                     -> stride UntrackedBehaviorTickStride (behaviors only)
	//
	// Players always tick at stride 1. Set NearActivationRadiusBlocks very large to disable banding.
	public float NearActivationRadiusBlocks { get; set; } = 24f;

	public float MidActivationRadiusBlocks { get; set; } = 60f;

	// Stride for entities beyond MidActivationRadiusBlocks but still tracked (IsTracked > 0).
	public int FarBandTickStride { get; set; } = 3;

	// Max entities to dequeue from the toAdd queue per tick. Prevents spikes when multiple
	// chunks load simultaneously and dump 50-100 new physics tickables in one frame.
	// Deferred entities stay in the queue and drain on subsequent ticks. 0 = no limit.
	public int MaxActivationsPerTick { get; set; } = 50;

	public void EnsureSane()
	{
		MaxCatchUpTicksPerServerTick = Math.Min(12, Math.Max(1, MaxCatchUpTicksPerServerTick));
		OverloadedMaxCatchUpTicksPerServerTick = Math.Min(MaxCatchUpTicksPerServerTick, Math.Max(1, OverloadedMaxCatchUpTicksPerServerTick));
		MinimumStateUpdateIntervalMs = Math.Min(5000, Math.Max(0, MinimumStateUpdateIntervalMs));
		OverloadedStateUpdateIntervalMs = Math.Min(10000, Math.Max(MinimumStateUpdateIntervalMs, OverloadedStateUpdateIntervalMs));
		OverloadedTickThresholdMs = Math.Min(5000, Math.Max(0, OverloadedTickThresholdMs));
		ParallelThreshold = Math.Max(1, ParallelThreshold);
		MaxThreadsOverride = Math.Max(0, MaxThreadsOverride);
		FarTrackedTickStride = Math.Min(8, Math.Max(1, FarTrackedTickStride));
		UntrackedBehaviorTickStride = Math.Min(16, Math.Max(1, UntrackedBehaviorTickStride));
		FarBandTickStride = Math.Min(16, Math.Max(1, FarBandTickStride));
		MaxActivationsPerTick = Math.Max(0, MaxActivationsPerTick);
		NearActivationRadiusBlocks = Math.Max(0f, NearActivationRadiusBlocks);
		MidActivationRadiusBlocks = Math.Max(NearActivationRadiusBlocks, MidActivationRadiusBlocks);
	}
}

internal class StratumEventTickConfig
{
	public bool Enabled { get; set; } = true;

	// When true, records per-subsection timings for server.eventTick into PerformanceStats:
	// eventTick.gtEntity, eventTick.gtBlock, eventTick.dcEntity, eventTick.dcBlock.
	public bool RecordSubsectionTimings { get; set; } = true;

	// When a single block-listener tick exceeds this many milliseconds, record its profiler name
	// for diagnosis. 0 disables.
	public int SlowBlockListenerThresholdMs { get; set; } = 5;

	// When a single entity-listener tick exceeds this many milliseconds, record its profiler name.
	// 0 disables. Note: 'entity listeners' here = RegisterGameTickListener handlers (not entity OnGameTick).
	public int SlowEntityListenerThresholdMs { get; set; } = 5;

	// Paper-style adaptive throttle: when the previous server tick exceeded the overloaded
	// threshold, stretch the effective interval of non-critical entity listeners by this factor.
	// 1 disables. Critical listeners (interval <= AdaptiveCriticalIntervalMs) are never throttled
	// so packet, physics, and netcode listeners always fire on time.
	public bool AdaptiveThrottleWhenOverloaded { get; set; } = true;

	public int AdaptiveOverloadedMultiplier { get; set; } = 2;

	// Listeners whose configured interval is <= this many ms are exempt from adaptive throttling.
	public int AdaptiveCriticalIntervalMs { get; set; } = 50;

	public void EnsureSane()
	{
		SlowBlockListenerThresholdMs = Math.Max(0, SlowBlockListenerThresholdMs);
		SlowEntityListenerThresholdMs = Math.Max(0, SlowEntityListenerThresholdMs);
		AdaptiveOverloadedMultiplier = Math.Min(8, Math.Max(1, AdaptiveOverloadedMultiplier));
		AdaptiveCriticalIntervalMs = Math.Max(0, AdaptiveCriticalIntervalMs);
	}
}

internal class StratumChunkRequestManagementConfig
{
	public bool Enabled { get; set; } = true;

	public bool CancelStalePendingRequests { get; set; } = true;

	public int MinimumPendingAgeSeconds { get; set; } = 3;

	public int MaxDistanceBeyondViewChunks { get; set; } = 2;

	public int CleanupIntervalMilliseconds { get; set; } = 1000;

	public int MaxTrackedRequestsPerCleanup { get; set; } = 512;

	public int MaxCancelledRequestsPerCleanup { get; set; } = 128;

	public bool PrioritizeMovingDirection { get; set; } = true;

	public int ForwardPredictionBlocks { get; set; } = 48;

	public double MinimumMovementSpeed { get; set; } = 0.05;

	public void EnsureSane()
	{
		MinimumPendingAgeSeconds = Math.Max(0, MinimumPendingAgeSeconds);
		MaxDistanceBeyondViewChunks = Math.Max(0, MaxDistanceBeyondViewChunks);
		CleanupIntervalMilliseconds = Math.Max(100, CleanupIntervalMilliseconds);
		MaxTrackedRequestsPerCleanup = Math.Max(1, MaxTrackedRequestsPerCleanup);
		MaxCancelledRequestsPerCleanup = Math.Max(1, MaxCancelledRequestsPerCleanup);
		ForwardPredictionBlocks = Math.Max(0, ForwardPredictionBlocks);
		MinimumMovementSpeed = Math.Max(0, MinimumMovementSpeed);
	}
}

internal class StratumEntityTickingConfig
{
	public bool Enabled { get; set; } = true;

	// Near zone: entities within this distance of any player tick every server tick.
	public int NearDistanceBlocks { get; set; } = 32;

	// Mid zone: entities within this distance tick every Nth tick.
	public int MidDistanceBlocks { get; set; } = 64;

	public int MidTickInterval { get; set; } = 2;

	// Far zone: entities within this distance tick every Nth tick.
	public int FarEntityDistanceBlocks { get; set; } = 96;

	public int FarEntityTickInterval { get; set; } = 5;

	// Very-far zone: entities beyond Far tick every Nth tick. 1 = same as no throttle.
	public int VeryFarTickInterval { get; set; } = 10;

	public bool ThrottleCreatures { get; set; } = true;

	public bool ThrottleInanimate { get; set; } = true;

	public bool SkipMovingEntities { get; set; } = true;

	public double MovingEntitySpeedThreshold { get; set; } = 0.01;

	// Hard cap on creature entities ticked per server tick (round-robin fairness via accumulated dt).
	// 0 = unlimited. Recommended ~ (target tick ms) * (ticks-per-ms creature budget).
	public int MaxCreatureTicksPerTick { get; set; } = 0;

	// Paper-style ambient fauna tier. Entities whose code path matches one of these prefixes
	// get an extra stride multiplier on top of the distance-band tickInterval. Ambient creatures
	// (fish, butterflies, bees, hens etc.) are background mood and don't need 30Hz AI even when
	// a player is nearby. Lazy-resolved per-entity and cached.
	public bool AmbientFaunaTierEnabled { get; set; } = true;

	public int AmbientFaunaTickMultiplier { get; set; } = 2;

	public List<string> AmbientFaunaCodePrefixes { get; set; } = new List<string>
	{
		"fish-", "butterfly-", "dragonfly-", "bee-", "rat-",
		"chicken-", "hen-", "rooster-", "pig-", "sheep-"
	};

	// Asset domains whose entities never get throttled, regardless of distance/tier. Custom
	// AI/pathfinder mods can assume near-constant tick frequency; throttling them can break
	// their internal state machine (observed with VSVillage's custom pathfinder: villagers
	// froze at town edges under a distance-based throttle in a prior version of this system).
	// Resolved once per entity and cached alongside the ambient-fauna tier.
	public List<string> ExcludedModDomains { get; set; } = new List<string> { "vsvillage" };

	// Paper-style hard despawn: creatures that have been beyond HardDespawnDistanceBlocks
	// from every player for HardDespawnGracePeriodSeconds get unloaded. Off by default —
	// enable only on long-running worlds where stray mobs accumulate in old chunks.
	public bool HardDespawnEnabled { get; set; } = false;

	public int HardDespawnDistanceBlocks { get; set; } = 128;

	public int HardDespawnGracePeriodSeconds { get; set; } = 30;

	// Skip entities whose nametag attribute has been set (Name Tag item, custom-named mobs).
	public bool HardDespawnExemptIfNamed { get; set; } = true;

	// Code-path prefixes that are exempt from hard despawn even when far. Defaults cover the
	// common Vintage Story passive/farm animals so wandering livestock isn't culled. Add
	// tamed-creature codes here for your server's mod set as needed.
	public List<string> HardDespawnExemptCodePrefixes { get; set; } = new List<string>
	{
		"sheep-", "pig-", "cow-", "chicken-", "hen-", "rooster-", "goat-",
		"yak-", "bushmeat-", "rabbit-"
	};

	// Stratum per-chunk-column entity caps. Periodically scans loaded entities and culls the
	// oldest excess in any column that exceeds the cap. Creatures honor the same exempt
	// prefixes and named-entity rule as hard despawn so livestock & tames aren't culled.
	// Item entities (ground drops) ignore those exemptions; uncollected loot is fair game.
	public bool EntityCapsEnabled { get; set; } = false;

	public int EntityCapsEnforcementIntervalSeconds { get; set; } = 10;

	public int MaxCreaturesPerChunkColumn { get; set; } = 32;

	public int MaxItemEntitiesPerChunkColumn { get; set; } = 64;

	public void EnsureSane()
	{
		NearDistanceBlocks = Math.Max(0, NearDistanceBlocks);
		MidDistanceBlocks = Math.Max(NearDistanceBlocks, MidDistanceBlocks);
		MidTickInterval = Math.Max(1, MidTickInterval);
		FarEntityDistanceBlocks = Math.Max(MidDistanceBlocks, FarEntityDistanceBlocks);
		FarEntityTickInterval = Math.Max(MidTickInterval, FarEntityTickInterval);
		VeryFarTickInterval = Math.Max(FarEntityTickInterval, VeryFarTickInterval);
		MovingEntitySpeedThreshold = Math.Max(0, MovingEntitySpeedThreshold);
		MaxCreatureTicksPerTick = Math.Max(0, MaxCreatureTicksPerTick);
		AmbientFaunaTickMultiplier = Math.Max(1, Math.Min(16, AmbientFaunaTickMultiplier));
		AmbientFaunaCodePrefixes ??= new List<string>();
		ExcludedModDomains ??= new List<string>();
		HardDespawnDistanceBlocks = Math.Max(32, HardDespawnDistanceBlocks);
		HardDespawnGracePeriodSeconds = Math.Max(1, HardDespawnGracePeriodSeconds);
		HardDespawnExemptCodePrefixes ??= new List<string>();
		EntityCapsEnforcementIntervalSeconds = Math.Max(1, EntityCapsEnforcementIntervalSeconds);
		MaxCreaturesPerChunkColumn = Math.Max(0, MaxCreaturesPerChunkColumn);
		MaxItemEntitiesPerChunkColumn = Math.Max(0, MaxItemEntitiesPerChunkColumn);
	}
}

internal class StratumAutoSaveConfig
{
	public bool Enabled { get; set; } = true;

	public bool DelayDuringChunkPressure { get; set; } = true;

	public bool IncrementalDirtyFlush { get; set; } = true;

	public int MaxPendingChunkColumns { get; set; } = 256;

	public int MaxWorkerChunkColumns { get; set; } = 512;

	public int MaxDelaySeconds { get; set; } = 120;

	public int IncrementalFlushIntervalSeconds { get; set; } = 10;

	public int MaxLoadedChunksPerFlush { get; set; } = 256;

	public int MaxMapChunksPerFlush { get; set; } = 128;

	public int MaxLoadedChunkScansPerFlush { get; set; } = 4096;

	public int MaxMapChunkScansPerFlush { get; set; } = 2048;

	public int SaveScanRefreshSeconds { get; set; } = 30;

	public void EnsureSane()
	{
		MaxPendingChunkColumns = Math.Max(0, MaxPendingChunkColumns);
		MaxWorkerChunkColumns = Math.Max(0, MaxWorkerChunkColumns);
		MaxDelaySeconds = Math.Min(3600, Math.Max(0, MaxDelaySeconds));
		IncrementalFlushIntervalSeconds = Math.Min(600, Math.Max(1, IncrementalFlushIntervalSeconds));
		MaxLoadedChunksPerFlush = Math.Min(10000, Math.Max(0, MaxLoadedChunksPerFlush));
		MaxMapChunksPerFlush = Math.Min(10000, Math.Max(0, MaxMapChunksPerFlush));
		MaxLoadedChunkScansPerFlush = Math.Min(100000, Math.Max(1, MaxLoadedChunkScansPerFlush));
		MaxMapChunkScansPerFlush = Math.Min(100000, Math.Max(1, MaxMapChunkScansPerFlush));
		SaveScanRefreshSeconds = Math.Min(3600, Math.Max(1, SaveScanRefreshSeconds));
	}
}

// Stratum parallel chunk-read pool. Opens N additional read-only SqliteConnections to the
// savegame file so the chunk-load thread can read all Y-levels of a chunk column concurrently
// (and across multiple columns). SQLite's WAL mode safely allows many readers + one writer, so
// reads here are isolated from writes performed via the existing single write-connection in
// SQLiteDbConnectionv2. Disabled by default; opt in once you've measured chunk I/O time in
// /stratum perf.
internal class StratumChunkIoConfig
{
	public bool Enabled { get; set; } = false;

	// Worker connections in the pool. Each is a fully-prepared read-only SqliteConnection; cost
	// is one OS file handle + a few KB per connection. Useful range is 2-8; saturating SSDs
	// usually happens around 4.
	public int WorkerThreads { get; set; } = 4;

	// Threshold below which the parallel read path is bypassed (small columns aren't worth the
	// dispatch overhead).
	public int MinChunkYLevelsForParallel { get; set; } = 4;

	public void EnsureSane()
	{
		WorkerThreads = Math.Min(16, Math.Max(1, WorkerThreads));
		MinChunkYLevelsForParallel = Math.Max(2, MinChunkYLevelsForParallel);
	}
}

// Reorders the next slice of pending chunk-column load requests by distance to the closest
// online player (optionally forward-predicted along motion). The underlying request queue is
// still consumed via the standard requeue/dispose flow, this only changes which request the
// chunk thread picks up *first* on each tick — useful when many players are spreading load
// across the world simultaneously and FIFO would otherwise round-robin them slowly.
// sampling now correctly takes into account the entire queue via bounded max-heap, and not just its head
internal class StratumChunkPriorityConfig
{
	public bool Enabled { get; set; } = true;

	// Cap on requests pulled into the sort window each chunk-thread iteration. Keeps the sort
	// cost bounded regardless of queue size.
	public int MaxSortedPerTick { get; set; } = 64;

	// Forward-predict each player's chunk position by this many seconds based on .Motion so
	// chunks ahead of motion are loaded earlier than chunks behind. 0 disables prediction.
	public float PredictionSeconds { get; set; } = 1.5f;

	public void EnsureSane()
	{
		MaxSortedPerTick = Math.Min(1024, Math.Max(8, MaxSortedPerTick));
		PredictionSeconds = Math.Min(10f, Math.Max(0f, PredictionSeconds));
	}
}

internal class StratumBlockTickConfig
{
	public bool Enabled { get; set; } = true;

	public int MaxChunksPerPass { get; set; } = 512;

	public int MaxRandomTicksPerChunk { get; set; } = 8;

	public int MaxMainThreadBlockTicksPerPass { get; set; } = 5000;

	// Spread the random tick pass across N smaller slices instead of one burst. Each slice
	// runs at BlockTickInterval/SliceCount ms and processes 1/SliceCount of the chunk set.
	// Same aggregate rate per chunk, removes the periodic sawtooth spike. 1 = vanilla batch.
	public int RandomTickSliceCount { get; set; } = 8;

	// Self-tuning under load: when the recent average server tick exceeds OverloadTickMs the per-chunk
	// random-tick budget is scaled down (multiplied by OverloadScale, then floored at OverloadFloor).
	// Lets crops/fluids/fire tick at full rate when the server has headroom and dial back automatically
	// when it doesn't, instead of needing a manual config bump under load.
	public bool AdaptiveUnderOverload { get; set; } = true;

	public int OverloadTickMs { get; set; } = 45;

	public float OverloadScale { get; set; } = 0.5f;

	public int OverloadFloor { get; set; } = 2;

	public void EnsureSane()
	{
		MaxChunksPerPass = Math.Max(1, MaxChunksPerPass);
		MaxRandomTicksPerChunk = Math.Max(0, MaxRandomTicksPerChunk);
		MaxMainThreadBlockTicksPerPass = Math.Max(1, MaxMainThreadBlockTicksPerPass);
		RandomTickSliceCount = Math.Max(1, Math.Min(16, RandomTickSliceCount));
		OverloadTickMs = Math.Max(10, Math.Min(1000, OverloadTickMs));
		OverloadScale = Math.Max(0.05f, Math.Min(1f, OverloadScale));
		OverloadFloor = Math.Max(0, OverloadFloor);
	}
}

internal class StratumTimingsConfig
{
	public bool EnabledOnStartup { get; set; }

	public int ReportTopEntries { get; set; } = 30;

	public double SlowSampleThresholdMs { get; set; } = 50;

	public void EnsureSane()
	{
		ReportTopEntries = Math.Max(1, ReportTopEntries);
		SlowSampleThresholdMs = Math.Max(0, SlowSampleThresholdMs);
	}
}

internal class StratumChunkGenerationConfig
{
	public bool Enabled { get; set; } = true;

	public int MaxColumnRequestsPerServerTick { get; set; } = 32;

	public int MaxColumnRequestsPerClientTick { get; set; } = 4;

	public bool IncludeLocalClients { get; set; }

	public bool AdaptiveUnderOverload { get; set; } = true;

	public int OverloadTickMs { get; set; } = 45;

	public float OverloadScale { get; set; } = 0.5f;

	public int OverloadFloorServerTick { get; set; } = 4;

	public int OverloadFloorClientTick { get; set; } = 1;

	public void EnsureSane()
	{
		MaxColumnRequestsPerServerTick = Math.Max(1, MaxColumnRequestsPerServerTick);
		MaxColumnRequestsPerClientTick = Math.Max(1, MaxColumnRequestsPerClientTick);
		OverloadTickMs = Math.Max(10, Math.Min(1000, OverloadTickMs));
		OverloadScale = Math.Max(0.05f, Math.Min(1f, OverloadScale));
		OverloadFloorServerTick = Math.Max(1, OverloadFloorServerTick);
		OverloadFloorClientTick = Math.Max(1, OverloadFloorClientTick);
	}
}

internal class StratumCommandsConfig
{
	public bool Enabled { get; set; } = true;

	public StratumCommandAccessConfig Spawn { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.spawn");

	public StratumCommandAccessConfig SetSpawn { get; set; } = StratumCommandAccessConfig.ForPrivilege("setspawn");

	public StratumTeleportRequestsConfig TeleportRequests { get; set; } = new StratumTeleportRequestsConfig();

	public StratumHomesConfig Homes { get; set; } = new StratumHomesConfig();

	public StratumCommandAccessConfig Seen { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.seen");

	public StratumCommandAccessConfig Whois { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.whois");

	public StratumCommandAccessConfig Near { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.near");

	public StratumCommandAccessConfig Back { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.back");

	public StratumCommandAccessConfig Message { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.msg");

	public StratumCommandAccessConfig StaffChat { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.staffchat");

	public StratumCommandAccessConfig ChatControl { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.chatcontrol");

	public StratumCommandAccessConfig StaffBroadcast { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.staffbroadcast");

	public StratumCommandAccessConfig InfoCommands { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.info");

	public StratumCommandAccessConfig Vanish { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.vanish");

	public StratumCommandAccessConfig Pvp { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.pvp");

	public int VanishReminderIntervalSeconds { get; set; } = 4;

	public StratumCommandAccessConfig Freeze { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.freeze");

	public StratumCommandAccessConfig Revive { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.revive");

	public StratumCommandAccessConfig Jail { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.jail");

	public StratumCommandAccessConfig Warn { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.warn");

	public StratumCommandAccessConfig Mute { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.mute");

	public StratumCommandAccessConfig Notes { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.notes");

	public StratumCommandAccessConfig Report { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.report");

	public StratumCommandAccessConfig ReportManage { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.reports");

	public int NearDefaultRadiusBlocks { get; set; } = 128;

	public int NearMaxRadiusBlocks { get; set; } = 512;

	public int SlowmodeMaxSeconds { get; set; } = 3600;

	public int ClearChatLines { get; set; } = 80;

	public StratumJailConfig JailSettings { get; set; } = new StratumJailConfig();

	public void EnsurePopulated()
	{
		Spawn ??= StratumCommandAccessConfig.ForPrivilege("stratum.spawn");
		SetSpawn ??= StratumCommandAccessConfig.ForPrivilege("setspawn");
		TeleportRequests ??= new StratumTeleportRequestsConfig();
		Homes ??= new StratumHomesConfig();
		Seen ??= StratumCommandAccessConfig.ForPrivilege("stratum.seen");
		Whois ??= StratumCommandAccessConfig.ForPrivilege("stratum.whois");
		Near ??= StratumCommandAccessConfig.ForPrivilege("stratum.near");
		Back ??= StratumCommandAccessConfig.ForPrivilege("stratum.back");
		Message ??= StratumCommandAccessConfig.ForPrivilege("stratum.msg");
		StaffChat ??= StratumCommandAccessConfig.ForPrivilege("stratum.staffchat");
		ChatControl ??= StratumCommandAccessConfig.ForPrivilege("stratum.chatcontrol");
		StaffBroadcast ??= StratumCommandAccessConfig.ForPrivilege("stratum.staffbroadcast");
		InfoCommands ??= StratumCommandAccessConfig.ForPrivilege("stratum.info");
		Vanish ??= StratumCommandAccessConfig.ForPrivilege("stratum.vanish");
		Pvp ??= StratumCommandAccessConfig.ForPrivilege("stratum.pvp");
		Freeze ??= StratumCommandAccessConfig.ForPrivilege("stratum.freeze");
		Revive ??= StratumCommandAccessConfig.ForPrivilege("stratum.revive");
		Jail ??= StratumCommandAccessConfig.ForPrivilege("stratum.jail");
		Warn ??= StratumCommandAccessConfig.ForPrivilege("stratum.warn");
		Mute ??= StratumCommandAccessConfig.ForPrivilege("stratum.mute");
		Notes ??= StratumCommandAccessConfig.ForPrivilege("stratum.notes");
		Report ??= StratumCommandAccessConfig.ForPrivilege("stratum.report");
		ReportManage ??= StratumCommandAccessConfig.ForPrivilege("stratum.reports");
		JailSettings ??= new StratumJailConfig();
		Spawn.EnsurePopulated("stratum.spawn");
		SetSpawn.EnsurePopulated("setspawn");
		TeleportRequests.EnsurePopulated();
		Homes.EnsurePopulated();
		Seen.EnsurePopulated("stratum.seen");
		Whois.EnsurePopulated("stratum.whois");
		Near.EnsurePopulated("stratum.near");
		Back.EnsurePopulated("stratum.back");
		Message.EnsurePopulated("stratum.msg");
		StaffChat.EnsurePopulated("stratum.staffchat");
		ChatControl.EnsurePopulated("stratum.chatcontrol");
		StaffBroadcast.EnsurePopulated("stratum.staffbroadcast");
		InfoCommands.EnsurePopulated("stratum.info");
		Vanish.EnsurePopulated("stratum.vanish");
		Pvp.EnsurePopulated("stratum.pvp");
		Freeze.EnsurePopulated("stratum.freeze");
		Revive.EnsurePopulated("stratum.revive");
		Jail.EnsurePopulated("stratum.jail");
		Warn.EnsurePopulated("stratum.warn");
		Mute.EnsurePopulated("stratum.mute");
		Notes.EnsurePopulated("stratum.notes");
		Report.EnsurePopulated("stratum.report");
		ReportManage.EnsurePopulated("stratum.reports");
		NearDefaultRadiusBlocks = Math.Max(1, NearDefaultRadiusBlocks);
		NearMaxRadiusBlocks = Math.Max(NearDefaultRadiusBlocks, NearMaxRadiusBlocks);
		SlowmodeMaxSeconds = Math.Max(0, SlowmodeMaxSeconds);
		ClearChatLines = Math.Min(200, Math.Max(1, ClearChatLines));
		VanishReminderIntervalSeconds = Math.Min(30, Math.Max(3, VanishReminderIntervalSeconds));
		JailSettings.EnsureSane();
	}
}

internal class StratumJailConfig
{
	public StratumPositionConfig Location { get; set; } = new StratumPositionConfig();

	public double MaxDistanceBlocks { get; set; } = 6;

	public bool ReturnOnUnjail { get; set; } = true;

	public void EnsureSane()
	{
		Location ??= new StratumPositionConfig();
		MaxDistanceBlocks = Math.Min(128, Math.Max(1, MaxDistanceBlocks));
	}
}

internal class StratumPositionConfig
{
	public bool Set { get; set; }

	public double X { get; set; }

	public double Y { get; set; }

	public double Z { get; set; }

	public int Dimension { get; set; }

	public float Yaw { get; set; }

	public float Pitch { get; set; }

	public static StratumPositionConfig FromEntityPos(EntityPos pos)
	{
		return new StratumPositionConfig
		{
			Set = true,
			X = pos.X,
			Y = pos.Y,
			Z = pos.Z,
			Dimension = pos.Dimension,
			Yaw = pos.Yaw,
			Pitch = pos.Pitch
		};
	}

	public EntityPos ToEntityPos()
	{
		return new EntityPos(X, Y, Z)
		{
			Dimension = Dimension,
			Yaw = Yaw,
			Pitch = Pitch
		};
	}

	public string FormatBlockPos()
	{
		return ((int)X).ToString(CultureInfo.InvariantCulture) + ", " + ((int)Y).ToString(CultureInfo.InvariantCulture) + ", " + ((int)Z).ToString(CultureInfo.InvariantCulture) + " dim=" + Dimension;
	}
}

internal class StratumCommandAccessConfig
{
	public bool Enabled { get; set; } = true;

	public string Privilege { get; set; }

	public int CooldownSeconds { get; set; }

	public bool CooldownBypassForStaff { get; set; } = true;

	public static StratumCommandAccessConfig ForPrivilege(string privilege)
	{
		return new StratumCommandAccessConfig
		{
			Privilege = privilege
		};
	}

	public void EnsurePopulated(string defaultPrivilege)
	{
		Privilege ??= defaultPrivilege;
		CooldownSeconds = Math.Max(0, CooldownSeconds);
	}
}

internal class StratumTeleportRequestsConfig
{
	public StratumCommandAccessConfig Request { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.tpa");

	public StratumCommandAccessConfig Here { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.tpahere");

	public bool AllowTpaHere { get; set; } = true;

	public int TimeoutSeconds { get; set; } = 60;

	public int CooldownSeconds { get; set; } = 15;

	public int WarmupSeconds { get; set; } = 5;

	public int TeleportCooldownSeconds { get; set; } = 30;

	public double CancelMoveDistanceBlocks { get; set; } = 0.35;

	public bool CancelOnMove { get; set; } = true;

	public bool CancelOnDamage { get; set; } = true;

	public bool BypassWarmupForStaff { get; set; } = true;

	public void EnsurePopulated()
	{
		Request ??= StratumCommandAccessConfig.ForPrivilege("stratum.tpa");
		Here ??= StratumCommandAccessConfig.ForPrivilege("stratum.tpahere");
		Request.EnsurePopulated("stratum.tpa");
		Here.EnsurePopulated("stratum.tpahere");
		TimeoutSeconds = Math.Max(5, TimeoutSeconds);
		CooldownSeconds = Math.Max(0, CooldownSeconds);
		WarmupSeconds = Math.Max(0, WarmupSeconds);
		TeleportCooldownSeconds = Math.Max(0, TeleportCooldownSeconds);
		CancelMoveDistanceBlocks = Math.Min(8, Math.Max(0, CancelMoveDistanceBlocks));
	}
}

internal class StratumHomesConfig
{
	public StratumCommandAccessConfig Home { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.home");

	public StratumCommandAccessConfig SetHome { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.sethome");

	public StratumCommandAccessConfig DeleteHome { get; set; } = StratumCommandAccessConfig.ForPrivilege("stratum.delhome");

	public int DefaultMaxHomes { get; set; } = 3;

	public Dictionary<string, int> MaxHomesByRole { get; set; } = new Dictionary<string, int>
	{
		["suvisitor"] = 0,
		["crvisitor"] = 0,
		["limitedsuplayer"] = 1,
		["limitedcrplayer"] = 1,
		["suplayer"] = 3,
		["crplayer"] = 3,
		["sumod"] = 10,
		["crmod"] = 10,
		["admin"] = 50
	};

	public void EnsurePopulated()
	{
		Home ??= StratumCommandAccessConfig.ForPrivilege("stratum.home");
		SetHome ??= StratumCommandAccessConfig.ForPrivilege("stratum.sethome");
		DeleteHome ??= StratumCommandAccessConfig.ForPrivilege("stratum.delhome");
		MaxHomesByRole ??= new Dictionary<string, int>();
		Home.EnsurePopulated("stratum.home");
		SetHome.EnsurePopulated("stratum.sethome");
		DeleteHome.EnsurePopulated("stratum.delhome");
		DefaultMaxHomes = Math.Max(0, DefaultMaxHomes);
	}
}

internal class StratumChatConfig
{
	public bool Enabled { get; set; } = true;

	public bool RolePrefixesEnabled { get; set; } = true;

	public bool LinkifyUrls { get; set; } = true;

	// Stratum start: connection message toggles
	public bool ShowJoinMessages { get; set; } = true;

	public bool ShowLeaveMessages { get; set; } = true;

	public bool ShowDisconnectMessages { get; set; } = true;
	// Stratum end

	// Minimum milliseconds between consecutive non-command chat lines from the same client.
	// Messages sent faster than this are silently dropped server-side.
	public int MinIntervalMs { get; set; } = 750;

	// If true, drop duplicate messages from the same client within DuplicateWindowMs.
	public bool DropDuplicates { get; set; } = true;

	public int DuplicateWindowMs { get; set; } = 3000;

	// Slash-commands are never throttled (commands typically have their own gates).
	public bool ExemptCommands { get; set; } = true;

	public string PrefixFormat { get; set; } = "[{tag}]";

	public string RulesText { get; set; } = "Rules: be respectful, no griefing, no cheating, no harassment, and do not abuse exploits.";

	public string DiscordUrl { get; set; } = "";

	public string WebsiteUrl { get; set; } = "";

	public string MotdText { get; set; } = "Welcome to this Stratum server.";

	public Dictionary<string, StratumChatRolePrefixConfig> RolePrefixes { get; set; } = CreateDefaultRolePrefixes();

	public void EnsurePopulated()
	{
		PrefixFormat ??= "[{tag}]";
		RulesText ??= "Rules: be respectful, no griefing, no cheating, no harassment, and do not abuse exploits.";
		DiscordUrl ??= "";
		WebsiteUrl ??= "";
		MotdText ??= "Welcome to this Stratum server.";
		MinIntervalMs = Math.Max(0, MinIntervalMs);
		DuplicateWindowMs = Math.Max(0, DuplicateWindowMs);
		if (RolePrefixes == null || RolePrefixes.Count == 0)
		{
			RolePrefixes = CreateDefaultRolePrefixes();
		}

		foreach (StratumChatRolePrefixConfig prefix in RolePrefixes.Values)
		{
			prefix.EnsurePopulated();
		}
	}

	private static Dictionary<string, StratumChatRolePrefixConfig> CreateDefaultRolePrefixes()
	{
		return new Dictionary<string, StratumChatRolePrefixConfig>(StringComparer.OrdinalIgnoreCase)
		{
			["admin"] = new StratumChatRolePrefixConfig
			{
				Tag = "Admin",
				Color = "#ff5f57",
				Bold = true,
				Priority = 100
			},
			["sumod"] = new StratumChatRolePrefixConfig
			{
				Tag = "Mod",
				Color = "#4cc9f0",
				Bold = true,
				Priority = 50
			},
			["crmod"] = new StratumChatRolePrefixConfig
			{
				Tag = "Mod",
				Color = "#4cc9f0",
				Bold = true,
				Priority = 50
			}
		};
	}
}

internal class StratumChatRolePrefixConfig
{
	public bool Enabled { get; set; } = true;

	public string Tag { get; set; } = "Staff";

	public string Color { get; set; } = "#ffffff";

	public bool Bold { get; set; } = true;

	public int Priority { get; set; }

	public void EnsurePopulated()
	{
		Tag ??= "Staff";
		Color ??= "#ffffff";
	}
}

internal class StratumBackupConfig
{
	public bool Enabled { get; set; }

	public int IntervalMinutes { get; set; } = 360;

	public int RetainCount { get; set; } = 5;

	public bool LogBackups { get; set; } = true;

	public void EnsureSane()
	{
		if (IntervalMinutes < 1) IntervalMinutes = 1;
		if (RetainCount < 1) RetainCount = 1;
	}
}

internal class StratumAnnouncementsConfig
{
	public bool Enabled { get; set; }

	// Seconds between broadcasts.
	public int IntervalSeconds { get; set; } = 300;

	// Messages to rotate through. Supports the same HTML formatting as /announce.
	public string[] Messages { get; set; } = Array.Empty<string>();

	// Prepended to every message. Default matches /announce orange bold styling.
	public string Prefix { get; set; } = "<strong><font color=\"orange\">";

	// Appended to every message. Closes the Prefix tags.
	public string Suffix { get; set; } = "</font></strong>";

	// If true, pick a random message each time instead of sequential rotation.
	public bool RandomOrder { get; set; }

	public void EnsureSane()
	{
		if (IntervalSeconds < 10) IntervalSeconds = 10;
		Messages ??= Array.Empty<string>();
		Prefix ??= "";
		Suffix ??= "";
	}
}
