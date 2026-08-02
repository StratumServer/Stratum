using System;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

// This is a purely anonymous server stats reporter, like a "phone home" feature, it can be opt-out via Config.ServerStats.
//
// How it works so those may feel comfortable enabling it: 
// Every ServerStats.IntervalMinutes each server POSTs a tiny JSON blob to ServerStats.ReportUrl: 
// this blob contains a random per-install id, the Stratum/game version, and the current player count.
// A central endpoint can count distinct ids (which are servers running Stratum) and sum players (= players across all Stratum servers) 
// So the project can show those two numbers for graphs and stats.
//
// Privacy: nothing identifying is sent EVER no player names, no IPs, no server address or name. The serverId is a random GUID 
// Which is generated once and stored in the config purely so the same install is counted once instead of once per restart. 
// Any owner can wipe it or turn reporting off anytime. The endpoint never even see's what server is who, or anything.
//
// The report is fired off the tick thread and is entirely best-effort, a slow, unreachable, or erroring endpoint never blocks or affects the server.
internal static class StratumServerStats
{
	private static readonly object gate = new object();
	private static readonly HttpClient http = new HttpClient();

	private static bool active;
	private static bool reportInFlight;
	private static long lastReportMs = long.MinValue;

	public static void Start(ServerMain server)
	{
		StratumServerStatsConfig config = StratumRuntime.Config.ServerStats;
		if (config == null)
		{
			return;
		}

		// Generate and persist the anonymous id on first run so restarts are not counted as new servers.
		if (string.IsNullOrWhiteSpace(config.ServerId))
		{
			config.ServerId = Guid.NewGuid().ToString("N");
			try
			{
				StratumRuntime.SaveConfig();
			}
			catch (Exception ex)
			{
				StratumRuntime.LogWarning("could not persist server stats id: " + ex.Message);
			}
		}

		if (!config.Enabled)
		{
			active = false;
			return;
		}

		if (string.IsNullOrWhiteSpace(config.ReportUrl))
		{
			active = false;
			StratumRuntime.LogInfo("server stats reporting is on but ServerStats.ReportUrl is empty; nothing will be sent.");
			return;
		}

		active = true;
		lastReportMs = long.MinValue;
		StratumRuntime.LogInfo("anonymous server stats reporting is on (player count and version only, nothing identifying). Turn it off with ServerStats.Enabled=false in " + StratumRuntime.ConfigPath);
	}

	// Called each server tick. Throttles to the configured interval, snapshots on this (main) thread, then posts off-thread.
	public static void Tick(ServerMain server)
	{
		if (!active || server == null)
		{
			return;
		}

		StratumServerStatsConfig config = StratumRuntime.Config.ServerStats;
		if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.ReportUrl))
		{
			return;
		}

		long now = server.ElapsedMilliseconds;
		long intervalMs = (long)Math.Max(1, config.IntervalMinutes) * 60_000L;
		lock (gate)
		{
			if (reportInFlight || (lastReportMs != long.MinValue && now - lastReportMs < intervalMs))
			{
				return;
			}

			lastReportMs = now;
			reportInFlight = true;
		}

		// Snapshot player state on the tick thread, posting happens on a worker.
		StatsPayload payload = BuildPayload(server, config);
		string url = config.ReportUrl;
		int timeoutSeconds = config.TimeoutSeconds;

		Task.Run(async () =>
		{
			bool ok = false;
			try
			{
				ok = await PostAsync(url, payload, timeoutSeconds);
			}
			catch
			{
				// 'Best-effort' never report or post any errors, the server should never be affected by this
			}
			finally
			{
				lock (gate)
				{
					reportInFlight = false;
				}
			}
		});
	}

	private static StatsPayload BuildPayload(ServerMain server, StratumServerStatsConfig config)
	{
		int apiOnlinePlayers = server.AllOnlinePlayers.Length;
		int admittedClients = 0;
		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (client.State.IsAdmitted())
			{
				admittedClients++;
			}
		}

		return new StatsPayload
		{
			ServerId = config.ServerId,
			StratumVersion = StratumInfo.Version,
			GameVersion = StratumInfo.BaseGameVersion,
			Players = apiOnlinePlayers,
			PlayerCounts = new PlayerCountsPayload
			{
				ApiOnline = apiOnlinePlayers,
				AdmittedClients = admittedClients
			},
			MaxPlayers = server.Config?.MaxClients ?? 0,
			Os = GetOs(),
			ReportedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
		};
	}

	private static async Task<bool> PostAsync(string url, StatsPayload payload, int timeoutSeconds)
	{
		using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
		string json = JsonConvert.SerializeObject(payload);
		using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
		request.Headers.UserAgent.ParseAdd("StratumServer/" + StratumInfo.Version);
		using HttpResponseMessage response = await http.SendAsync(request, timeout.Token);
		// Body is ignored on purpose, dont fix. reporting is fire-and-forget
		return response.IsSuccessStatusCode;
	}

	private static string GetOs()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
		return "other";
	}

	private sealed class StatsPayload
	{
		[JsonProperty("serverId")] public string ServerId { get; set; }

		[JsonProperty("stratumVersion")] public string StratumVersion { get; set; }

		[JsonProperty("gameVersion")] public string GameVersion { get; set; }

		[JsonProperty("players")] public int Players { get; set; }

		[JsonProperty("playerCounts")] public PlayerCountsPayload PlayerCounts { get; set; }

		[JsonProperty("maxPlayers")] public int MaxPlayers { get; set; }

		[JsonProperty("os")] public string Os { get; set; }

		[JsonProperty("reportedAtUtc")] public string ReportedAtUtc { get; set; }
	}

	private sealed class PlayerCountsPayload
	{
		[JsonProperty("apiOnline")] public int ApiOnline { get; set; }

		[JsonProperty("admittedClients")] public int AdmittedClients { get; set; }
	}
}
