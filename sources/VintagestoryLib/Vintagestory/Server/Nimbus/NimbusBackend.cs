using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server.Nimbus;

/// <summary>
/// Process-wide Nimbus runtime state. Lifecycle:
///   - <see cref="MaybeStart"/> called once during server startup. No-op if Nimbus disabled.
///   - Background heartbeat task posts BackendHeartbeat to the registry every N seconds.
///   - Snapshot of registered servers cached + refreshed on each successful heartbeat.
///   - <see cref="MaybeStop"/> called on server shutdown.
/// All public methods are safe to call when Nimbus is disabled. They return false/null/empty.
/// </summary>
internal static class NimbusBackend
{
    private static ServerMain _server;
    private static NimbusRegistryClient _client;
    private static CancellationTokenSource _cts;
    private static Task _heartbeatTask;
    private static long _startUnix;

    public static NimbusBackendConfig Config => StratumRuntime.Config.Network;
    public static bool Enabled => Config != null && Config.Enabled;
    public static NetworkSnapshot LastSnapshot { get; private set; } = new();
    public static DateTime LastSnapshotUtc { get; private set; }
    public static string LastStatus { get; private set; } = "not started";

    /// <summary>
    /// Reservations held locally (already consumed against the registry) keyed by PlayerUID.
    /// Cleared when consumed by <see cref="TryConsumeLocalReservation"/> during identification.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TransferReservation> _pendingByUid =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call once during server startup. No-op if Nimbus is disabled.</summary>
    public static void MaybeStart(ServerMain server)
    {
        if (_server != null) return; // idempotent
        _server = server;
        StratumRuntime.Config.EnsurePopulated();
        var cfg = Config;
        if (cfg == null || !cfg.Enabled)
        {
            LastStatus = "disabled";
            return;
        }
        if (string.IsNullOrWhiteSpace(cfg.ServerId)
            || string.IsNullOrWhiteSpace(cfg.RegistryUrl)
            || string.IsNullOrWhiteSpace(cfg.SharedSecret))
        {
            StratumRuntime.LogWarning("Nimbus: Network.Enabled=true but ServerId / RegistryUrl / SharedSecret is missing. Staying offline.");
            LastStatus = "misconfigured";
            return;
        }
        try
        {
            _client = new NimbusRegistryClient(cfg);
            _cts = new CancellationTokenSource();
            _startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _heartbeatTask = Task.Run(() => HeartbeatLoop(_cts.Token));
            LastStatus = "starting";
            StratumRuntime.LogInfo($"Nimbus backend started: id={cfg.ServerId} registry={cfg.RegistryUrl}");

            if (server?.Config != null && server.Config.AdvertiseServer)
            {
                StratumRuntime.LogWarning("Nimbus: backend has AdvertiseServer=true. Set it false so only the registry advertises the network.");
            }
        }
        catch (Exception ex)
        {
            StratumRuntime.LogWarning($"Nimbus start failed: {ex.Message}");
            LastStatus = "error: " + ex.Message;
        }
    }

    public static void MaybeStop()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { /* ignore */ }
        _cts = null;
        _heartbeatTask = null;
        _client = null;
        LastStatus = "stopped";
    }

    private static async Task HeartbeatLoop(CancellationToken ct)
    {
        int interval = Math.Max(1, Config.HeartbeatIntervalSeconds);
        int consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hb = BuildHeartbeat();
                var resp = await _client.HeartbeatAsync(hb, ct);
                if (resp.Ok)
                {
                    LastStatus = "ok";
                    consecutiveFailures = 0;
                    if (resp.NextHeartbeatSeconds > 0) interval = resp.NextHeartbeatSeconds;
                }
                else
                {
                    consecutiveFailures++;
                    LastStatus = "heartbeat rejected: " + (resp.Message ?? "unknown");
                }

                // Refresh server snapshot (best-effort).
                var snap = await _client.GetServersAsync(ct);
                if (snap != null)
                {
                    LastSnapshot = snap;
                    LastSnapshotUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                consecutiveFailures++;
                LastStatus = "error: " + ex.Message;
                if (consecutiveFailures == 1 || consecutiveFailures % 12 == 0)
                    StratumRuntime.LogWarning($"Nimbus heartbeat failed ({consecutiveFailures}x): {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static BackendHeartbeat BuildHeartbeat()
    {
        var cfg = Config;
        int players = 0;
        int maxPlayers = _server?.Config?.MaxClients ?? 0;
        try
        {
            if (_server != null)
            {
                foreach (var c in _server.Clients.Values)
                    if (c.State.IsAdmitted()) players++;
            }
        }
        catch { /* ignore */ }

        // TPS is not currently exposed on StratumPerformanceStats; leave 0 for phase 1.
        double tps = 0;

        return new BackendHeartbeat
        {
            ServerId = cfg.ServerId,
            DisplayName = string.IsNullOrEmpty(cfg.DisplayName) ? cfg.ServerId : cfg.DisplayName,
            PublicHost = cfg.PublicHost,
            PublicPort = cfg.PublicPort,
            Tags = cfg.Tags?.ToArray() ?? Array.Empty<string>(),
            Players = players,
            MaxPlayers = maxPlayers,
            Tps = tps,
            UptimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _startUnix,
            Maintenance = false,
            ReservationRequired = cfg.RequireReservationForJoin,
            StratumVersion = StratumInfo.Version,
            GameVersion = StratumInfo.BaseGameVersion,
            RequiredClientMods = BuildRequiredClientMods()
        };
    }

    private static BackendModInfo[] BuildRequiredClientMods()
    {
        try
        {
            var loader = _server?.api?.ModLoader;
            if (loader == null) return Array.Empty<BackendModInfo>();
            var list = new List<BackendModInfo>();
            foreach (var mod in loader.Mods)
            {
                var info = mod.Info;
                if (info == null) continue;
                if (!info.Side.IsUniversal()) continue;
                if (!info.RequiredOnClient) continue;
                list.Add(new BackendModInfo { Id = info.ModID ?? "", Version = info.Version ?? "" });
            }
            return list.ToArray();
        }
        catch
        {
            return Array.Empty<BackendModInfo>();
        }
    }

    // ---- Reservation helpers ----

    /// <summary>
    /// Mint a transfer reservation on the registry for the given player + target backend.
    /// Returns null on failure (registry unreachable, target unknown, etc.).
    /// </summary>
    public static async Task<TransferReservation> MintReservationAsync(string playerUid, string playerName, string targetServerId, string reason = null)
    {
        if (!Enabled || _client == null) return null;
        var req = new ReservationRequest
        {
            PlayerUid = playerUid,
            PlayerName = playerName ?? "",
            SourceServerId = Config.ServerId,
            TargetServerId = targetServerId,
            Reason = reason
        };
        var resp = await _client.MintReservationAsync(req, CancellationToken.None);
        return resp?.Reservation;
    }

    /// <summary>
    /// Backend-side reservation check during identification. Returns true if either:
    ///   - reservation not required for this backend, OR
    ///   - a valid registry-side reservation exists for this PlayerUID + this ServerId.
    /// The registry call is best-effort; on registry outage we fall back to <see cref="NimbusBackendConfig.RequireReservationForJoin"/>.
    /// </summary>
    public static bool AllowPlayerJoin(string playerUid, bool isStaff, out string denyReason)
    {
        return AllowPlayerJoin(playerUid, isStaff, out denyReason, out _, out _);
    }

    /// <summary>
    /// Same as <see cref="AllowPlayerJoin(string, bool, out string)"/> but additionally returns the
    /// player's real remote endpoint as forwarded by the Nimbus proxy in the consumed reservation.
    /// <paramref name="forwardedIp"/> is empty when no reservation was consumed (e.g. direct
    /// backend connect, or a pre-IP-forwarding proxy build).
    /// </summary>
    public static bool AllowPlayerJoin(string playerUid, bool isStaff, out string denyReason,
        out string forwardedIp, out int forwardedPort)
    {
        denyReason = null;
        forwardedIp = "";
        forwardedPort = 0;
        if (!Enabled) return true;
        var cfg = Config;
        if (!cfg.RequireReservationForJoin) return true;
        if (isStaff && cfg.StaffBypassReservation) return true;

        if (_client == null) { denyReason = "Nimbus client unavailable"; return false; }

        // Fast path: a /nimbus send on this same node primed the local pending dict.
        if (_pendingByUid.TryRemove(playerUid, out var r))
        {
            if (r.ExpiresAtUnix >= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && string.Equals(r.TargetServerId, cfg.ServerId, StringComparison.OrdinalIgnoreCase))
            {
                forwardedIp = r.RealRemoteIp ?? "";
                forwardedPort = r.RealRemotePort;
                return true;
            }
        }

        // Cross-backend path: ask the registry to consume a reservation for (uid, this server).
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.RegistryHttpTimeoutSeconds + 1));
            var task = _client.ConsumeReservationByUidAsync(playerUid, cfg.ServerId, cts.Token);
            task.Wait(cts.Token);
            var resp = task.Result;
            if (resp != null && resp.Ok && resp.Reservation != null)
            {
                forwardedIp = resp.Reservation.RealRemoteIp ?? "";
                forwardedPort = resp.Reservation.RealRemotePort;
                return true;
            }
            denyReason = resp?.Error ?? "no valid reservation";
            return false;
        }
        catch (Exception ex)
        {
            denyReason = "reservation check error: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Called by the SOURCE backend right after successfully minting a reservation, so the
    /// TARGET backend can validate the join without an extra round-trip when the player arrives.
    /// In phase 1 this is co-located on the same registry; the source pushes the reservation
    /// into its own pending table only if it happens to BE the target (loopback test path).
    /// Future phase 1b: registry-side "by-uid" reservation query so any backend can validate
    /// without needing a sticky pending entry.
    /// </summary>
    public static void RememberPendingReservation(TransferReservation r)
    {
        if (r == null || string.IsNullOrEmpty(r.PlayerUid)) return;
        _pendingByUid[r.PlayerUid] = r;
    }

    /// <summary>Trigger a vanilla VS server redirect. Caller is responsible for minting any reservation first.</summary>
    public static void RedirectPlayer(IServerPlayer player, BackendSnapshot target)
    {
        if (_server == null || player == null || target == null) return;
        string host = target.PublicHost;
        if (target.PublicPort > 0 && target.PublicPort != 42420)
            host = $"{target.PublicHost}:{target.PublicPort}";
        _server.SendServerRedirect(player, host, target.DisplayName);
    }

    /// <summary>
    /// Post a transfer intent to the registry. The Nimbus proxy drains the intent queue and
    /// runs the actual swap against the live session. Use this in preference to
    /// <see cref="RedirectPlayer"/> when the deployment is behind the proxy, because a vanilla
    /// redirect tells the client to reconnect directly to the target backend (bypassing the
    /// proxy and breaking IP forwarding, sticky routing, and the single-address contract).
    /// </summary>
    public static async Task<TransferIntentResponse> RequestTransferAsync(
        string playerUid, string playerName, string targetServerId,
        string mode = "redirect", string reason = null, string requestedBy = null)
    {
        if (!Enabled || _client == null)
            return new TransferIntentResponse { Ok = false, Error = "Nimbus disabled" };
        var req = new TransferIntentRequest
        {
            PlayerUid = playerUid,
            PlayerName = playerName ?? "",
            SourceServerId = Config.ServerId,
            TargetServerId = targetServerId,
            Mode = string.IsNullOrEmpty(mode) ? "redirect" : mode,
            Reason = reason,
            RequestedBy = requestedBy ?? "",
        };
        var resp = await _client.PostTransferIntentAsync(req, CancellationToken.None);
        return resp ?? new TransferIntentResponse { Ok = false, Error = "no response from registry" };
    }
}
