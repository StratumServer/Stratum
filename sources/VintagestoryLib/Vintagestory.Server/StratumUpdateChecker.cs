using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal static class StratumUpdateChecker
{
	private static readonly object StateLock = new object();
	private static readonly HttpClient Http = new HttpClient();
	private static readonly Dictionary<string, string> NotifiedVersionByPlayerUid = new Dictionary<string, string>(StringComparer.Ordinal);

	private static StratumUpdateCheckResult lastResult = StratumUpdateCheckResult.NotChecked();
	private static bool checkInFlight;
	private static long nextCheckMs = long.MaxValue;

	public static StratumUpdateCheckResult LastResult
	{
		get
		{
			lock (StateLock)
			{
				return lastResult;
			}
		}
	}

	public static void Start(ServerMain server)
	{
		StratumUpdateCheckerConfig config = StratumRuntime.Config.UpdateChecker;
		server.EventManager.OnPlayerJoin += OnPlayerJoin;
		server.EventManager.OnPlayerDisconnect += OnPlayerDisconnect;
		lock (StateLock)
		{
			NotifiedVersionByPlayerUid.Clear();
			checkInFlight = false;
			nextCheckMs = server.ElapsedMilliseconds + GetCheckIntervalMs(config);
		}

		if (config == null || !config.Enabled)
		{
			SetLast(StratumUpdateCheckResult.Disabled());
			return;
		}

		if (config.CheckOnStartup)
		{
			BeginCheck(server);
		}
	}

	public static void Tick(ServerMain server)
	{
		StratumUpdateCheckerConfig config = StratumRuntime.Config.UpdateChecker;
		if (config == null || !config.Enabled || server == null)
		{
			return;
		}

		lock (StateLock)
		{
			if (checkInFlight || server.ElapsedMilliseconds < nextCheckMs)
			{
				return;
			}
		}

		BeginCheck(server);
	}

	public static async Task<StratumUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
	{
		StratumUpdateCheckerConfig config = StratumRuntime.Config.UpdateChecker ?? new StratumUpdateCheckerConfig();
		config.EnsureSane();

		if (!config.Enabled)
		{
			StratumUpdateCheckResult disabled = StratumUpdateCheckResult.Disabled();
			SetLast(disabled);
			return disabled;
		}

		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, config.LatestReleaseUrl);
			request.Headers.UserAgent.ParseAdd("StratumServer/" + StratumInfo.Version);
			using HttpResponseMessage response = await Http.SendAsync(request, timeout.Token);
			response.EnsureSuccessStatusCode();

			string body = await response.Content.ReadAsStringAsync(timeout.Token);
			GitHubRelease release = JsonConvert.DeserializeObject<GitHubRelease>(body);
			string latestVersion = CleanVersion(release?.TagName);
			if (string.IsNullOrWhiteSpace(latestVersion))
			{
				throw new InvalidOperationException("release response did not include a tag_name");
			}

			string currentVersion = CleanVersion(StratumInfo.Version);
			ParsedStratumVersion currentParsed = ParsedStratumVersion.Parse(currentVersion);
			ParsedStratumVersion latestParsed = ParsedStratumVersion.Parse(latestVersion);
			if (!currentParsed.IsValid || !latestParsed.IsValid)
			{
				throw new InvalidOperationException("release version did not match <game version>-stratum.<revision>");
			}

			if (release.Draft || release.Prerelease || latestParsed.IsPrerelease)
			{
				StratumUpdateCheckResult ignored = StratumUpdateCheckResult.UnstableReleaseIgnored(currentVersion, latestVersion, release.HtmlUrl);
				SetLast(ignored);
				return ignored;
			}

			int comparison = currentParsed.CompareTo(latestParsed);
			StratumUpdateCheckResult result = comparison < 0
				? StratumUpdateCheckResult.NewerAvailable(currentVersion, latestVersion, release.HtmlUrl)
				: StratumUpdateCheckResult.UpToDate(currentVersion, latestVersion, release.HtmlUrl);

			SetLast(result);
			return result;
		}
		catch (Exception ex)
		{
			StratumUpdateCheckResult failed = StratumUpdateCheckResult.Failed(StratumInfo.Version, ex.Message);
			SetLast(failed);
			return failed;
		}
	}

	public static string BuildReport()
	{
		StratumUpdateCheckResult result = LastResult;
		return result.State switch
		{
			StratumUpdateCheckState.NewerAvailable => "Update available: " + result.LatestVersion + " (running " + result.CurrentVersion + "). " + result.ReleaseUrl,
			StratumUpdateCheckState.UpToDate => "Stratum is up to date: " + result.CurrentVersion,
			StratumUpdateCheckState.UnstableReleaseIgnored => "Latest release is a draft or prerelease and was ignored: " + result.LatestVersion,
			StratumUpdateCheckState.Disabled => "Update checker is disabled.",
			StratumUpdateCheckState.Failed => "Update check failed: " + result.Message,
			_ => "Update check has not run yet."
		};
	}

	private static void BeginCheck(ServerMain server)
	{
		StratumUpdateCheckerConfig config = StratumRuntime.Config.UpdateChecker;
		lock (StateLock)
		{
			if (checkInFlight)
			{
				return;
			}

			checkInFlight = true;
			nextCheckMs = server.ElapsedMilliseconds + GetCheckIntervalMs(config);
		}

		Task.Run(async () =>
		{
			try
			{
				StratumUpdateCheckResult result = await CheckAsync(CancellationToken.None);
				if (result.State == StratumUpdateCheckState.NewerAvailable)
				{
					LogUpdateAvailable(result, config.CheckIntervalHours);
					server.EnqueueMainThreadTask(() => NotifyOnlineStaff(server, result));
				}
				else if (result.State == StratumUpdateCheckState.Failed)
				{
					StratumRuntime.LogWarning("update check failed: " + result.Message);
				}
			}
			finally
			{
				lock (StateLock)
				{
					checkInFlight = false;
				}
			}
		});
	}

	private static void LogUpdateAvailable(StratumUpdateCheckResult result, int intervalHours)
	{
		StratumRuntime.LogWarning("UPDATE AVAILABLE: running " + result.CurrentVersion + ", latest " + result.LatestVersion + ".");
		StratumRuntime.LogWarning("Download: " + result.ReleaseUrl);
		StratumRuntime.LogWarning("Run /stratum version for details. This reminder repeats every " + intervalHours + " hours.");
	}

	private static void NotifyOnlineStaff(ServerMain server, StratumUpdateCheckResult result)
	{
		foreach (IPlayer player in server.AllOnlinePlayers)
		{
			if (player is IServerPlayer serverPlayer)
			{
				NotifyStaff(serverPlayer, result);
			}
		}
	}

	private static void OnPlayerJoin(IServerPlayer player)
	{
		NotifyStaff(player, LastResult);
	}

	private static void OnPlayerDisconnect(IServerPlayer player)
	{
		string playerKey = GetPlayerKey(player);
		if (playerKey == null)
		{
			return;
		}

		lock (StateLock)
		{
			NotifiedVersionByPlayerUid.Remove(playerKey);
		}
	}

	private static void NotifyStaff(IServerPlayer player, StratumUpdateCheckResult result)
	{
		StratumUpdateCheckerConfig config = StratumRuntime.Config.UpdateChecker;
		if (player == null || result.State != StratumUpdateCheckState.NewerAvailable)
		{
			return;
		}

		if (config == null || !config.Enabled || !config.NotifyStaffInGame || !player.HasPrivilege(Privilege.controlserver))
		{
			return;
		}

		string playerKey = GetPlayerKey(player);
		if (playerKey == null)
		{
			return;
		}

		lock (StateLock)
		{
			if (NotifiedVersionByPlayerUid.TryGetValue(playerKey, out string notifiedVersion) && notifiedVersion == result.LatestVersion)
			{
				return;
			}
			NotifiedVersionByPlayerUid[playerKey] = result.LatestVersion;
		}

		string message = StratumCommandText.Warning("Stratum update available")
			+ StratumCommandText.Row("Running", result.CurrentVersion)
			+ StratumCommandText.Row("Latest", result.LatestVersion)
			+ StratumCommandText.Row("Download", result.ReleaseUrl)
			+ "\n" + StratumCommandText.Empty("Run /stratum version for details.");
		player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
	}

	private static string GetPlayerKey(IServerPlayer player)
	{
		if (!string.IsNullOrWhiteSpace(player?.PlayerUID))
		{
			return player.PlayerUID;
		}

		return string.IsNullOrWhiteSpace(player?.PlayerName) ? null : player.PlayerName;
	}

	private static long GetCheckIntervalMs(StratumUpdateCheckerConfig config)
	{
		return (long)Math.Clamp(config?.CheckIntervalHours ?? 12, 1, 168) * 60L * 60L * 1000L;
	}

	private static void SetLast(StratumUpdateCheckResult result)
	{
		lock (StateLock)
		{
			lastResult = result;
		}
	}

	private static string CleanVersion(string version)
	{
		if (string.IsNullOrWhiteSpace(version))
		{
			return "";
		}

		string clean = version.Trim();
		if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
		{
			clean = clean.Substring(1);
		}
		return clean;
	}

	private sealed class GitHubRelease
	{
		[JsonProperty("tag_name")]
		public string TagName { get; set; }

		[JsonProperty("html_url")]
		public string HtmlUrl { get; set; }

		[JsonProperty("draft")]
		public bool Draft { get; set; }

		[JsonProperty("prerelease")]
		public bool Prerelease { get; set; }
	}
}

internal enum StratumUpdateCheckState
{
	NotChecked,
	Disabled,
	Failed,
	UpToDate,
	UnstableReleaseIgnored,
	NewerAvailable
}

internal sealed class StratumUpdateCheckResult
{
	public StratumUpdateCheckState State { get; private set; }

	public string CurrentVersion { get; private set; }

	public string LatestVersion { get; private set; }

	public string ReleaseUrl { get; private set; }

	public string Message { get; private set; }

	public static StratumUpdateCheckResult NotChecked()
	{
		return new StratumUpdateCheckResult { State = StratumUpdateCheckState.NotChecked, CurrentVersion = StratumInfo.Version };
	}

	public static StratumUpdateCheckResult Disabled()
	{
		return new StratumUpdateCheckResult { State = StratumUpdateCheckState.Disabled, CurrentVersion = StratumInfo.Version };
	}

	public static StratumUpdateCheckResult Failed(string currentVersion, string message)
	{
		return new StratumUpdateCheckResult { State = StratumUpdateCheckState.Failed, CurrentVersion = currentVersion, Message = message };
	}

	public static StratumUpdateCheckResult UpToDate(string currentVersion, string latestVersion, string releaseUrl)
	{
		return new StratumUpdateCheckResult { State = StratumUpdateCheckState.UpToDate, CurrentVersion = currentVersion, LatestVersion = latestVersion, ReleaseUrl = releaseUrl };
	}

	public static StratumUpdateCheckResult UnstableReleaseIgnored(string currentVersion, string latestVersion, string releaseUrl)
	{
		return new StratumUpdateCheckResult { State = StratumUpdateCheckState.UnstableReleaseIgnored, CurrentVersion = currentVersion, LatestVersion = latestVersion, ReleaseUrl = releaseUrl };
	}

	public static StratumUpdateCheckResult NewerAvailable(string currentVersion, string latestVersion, string releaseUrl)
	{
		return new StratumUpdateCheckResult { State = StratumUpdateCheckState.NewerAvailable, CurrentVersion = currentVersion, LatestVersion = latestVersion, ReleaseUrl = releaseUrl };
	}
}

internal readonly struct ParsedStratumVersion : IComparable<ParsedStratumVersion>
{
	private readonly Version gameVersion;
	private readonly int[] revision;
	private readonly string suffix;

	public bool IsValid => gameVersion != null && gameVersion.CompareTo(new Version(0, 0)) > 0 && revision != null && revision.Length > 0;

	public bool IsPrerelease => !string.IsNullOrEmpty(suffix);

	private ParsedStratumVersion(Version gameVersion, int[] revision, string suffix)
	{
		this.gameVersion = gameVersion ?? new Version(0, 0);
		this.revision = revision ?? Array.Empty<int>();
		this.suffix = suffix ?? "";
	}

	public static ParsedStratumVersion Parse(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return new ParsedStratumVersion(new Version(0, 0), Array.Empty<int>(), "");
		}

		string clean = value.Trim();
		string[] parts = clean.Split(new[] { "-stratum." }, StringSplitOptions.None);
		if (parts.Length != 2)
		{
			return new ParsedStratumVersion(ParseVersion(clean), Array.Empty<int>(), "");
		}

		string suffix = "";
		string revisionPart = parts[1];
		int dash = revisionPart.IndexOf('-');
		if (dash >= 0)
		{
			suffix = revisionPart.Substring(dash + 1);
			revisionPart = revisionPart.Substring(0, dash);
		}
		return new ParsedStratumVersion(ParseVersion(parts[0]), ParseRevision(revisionPart), suffix);
	}

	public int CompareTo(ParsedStratumVersion other)
	{
		int result = gameVersion.CompareTo(other.gameVersion);
		if (result != 0) return result;

		result = CompareRevision(revision, other.revision);
		if (result != 0) return result;

		if (string.IsNullOrEmpty(suffix) && !string.IsNullOrEmpty(other.suffix)) return 1;
		if (!string.IsNullOrEmpty(suffix) && string.IsNullOrEmpty(other.suffix)) return -1;
		return string.Compare(suffix, other.suffix, StringComparison.OrdinalIgnoreCase);
	}

	private static Version ParseVersion(string value)
	{
		if (Version.TryParse(value, out Version version))
		{
			return version;
		}

		return new Version(0, 0);
	}

	private static int[] ParseRevision(string value)
	{
		string[] parts = value.Split('.');
		int[] result = new int[parts.Length];
		for (int i = 0; i < parts.Length; i++)
		{
			if (!int.TryParse(parts[i], out result[i]) || result[i] < 0)
			{
				return Array.Empty<int>();
			}
		}

		return result;
	}

	private static int CompareRevision(int[] left, int[] right)
	{
		int length = Math.Max(left.Length, right.Length);
		for (int i = 0; i < length; i++)
		{
			int leftPart = i < left.Length ? left[i] : 0;
			int rightPart = i < right.Length ? right[i] : 0;
			int result = leftPart.CompareTo(rightPart);
			if (result != 0)
			{
				return result;
			}
		}

		return 0;
	}
}
