using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Vintagestory.Server.Nimbus;

/// <summary>
/// /nimbus command surface. All subcommands return a clean error when the network layer is
/// disabled, so this command is always safe to register.
/// </summary>
internal sealed class CmdNimbus
{
    private readonly ServerMain server;

    public CmdNimbus(ServerMain server)
    {
        this.server = server;
        var api = server.api.commandapi;
        api.Create("nimbus")
            .WithDesc("Stratum Nimbus network commands (status, list servers, transfer players).")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSub("status").HandleWith(_ => HandleStatus()).EndSub()
            .BeginSub("servers").HandleWith(_ => HandleServers()).EndSub()
            .BeginSub("send")
                .WithArgs(api.Parsers.Word("player"), api.Parsers.Word("serverId"), api.Parsers.OptionalAll("reason"))
                .HandleWith(HandleSend).EndSub()
            .BeginSub("reload").HandleWith(_ => HandleReload()).EndSub();

        // Player-facing self-transfer. No elevated privilege; gated by Nimbus being enabled and
        // the target appearing in the registry snapshot. Operators who want to lock this down
        // further can wrap it with a permission node in their server config.
        api.Create("server")
            .WithDesc("Move yourself to another Nimbus backend.")
            .WithArgs(api.Parsers.Word("serverId"))
            .HandleWith(HandleServerSelf);
    }

    private TextCommandResult HandleStatus()
    {
        var cfg = NimbusBackend.Config;
        var sb = new StringBuilder();
        sb.AppendLine(StratumCommandText.Title("Nimbus"));
        sb.Append(StratumCommandText.Row("Mode", cfg.StatusSummary()));
        sb.Append(StratumCommandText.Row("Last status", NimbusBackend.LastStatus));
        if (NimbusBackend.LastSnapshotUtc != default)
        {
            var age = (int)(DateTime.UtcNow - NimbusBackend.LastSnapshotUtc).TotalSeconds;
            sb.Append(StratumCommandText.Row("Snapshot age", age + "s"));
            sb.Append(StratumCommandText.Row("Network", $"{NimbusBackend.LastSnapshot.Backends?.Count ?? 0} backends, {NimbusBackend.LastSnapshot.TotalPlayers}/{NimbusBackend.LastSnapshot.TotalCapacity} players"));
        }
        return TextCommandResult.Success(sb.ToString());
    }

    private TextCommandResult HandleServers()
    {
        if (!NimbusBackend.Enabled) return TextCommandResult.Error("Nimbus is disabled (set Network.Enabled in stratum.json).");
        var snap = NimbusBackend.LastSnapshot;
        if (snap?.Backends == null || snap.Backends.Count == 0)
            return TextCommandResult.Success("No backends in registry snapshot yet.");
        var sb = new StringBuilder();
        sb.AppendLine(StratumCommandText.Title($"Nimbus network ({snap.Backends.Count} backends, {snap.TotalPlayers}/{snap.TotalCapacity} players)"));
        foreach (var b in snap.Backends.OrderBy(x => x.ServerId))
        {
            string flags = (b.Stale ? "stale " : "") + (b.Maintenance ? "maint " : "") + (b.ReservationRequired ? "reserve " : "");
            sb.Append(StratumCommandText.Row(
                b.ServerId,
                $"{b.DisplayName} {b.PublicHost}:{b.PublicPort} {b.Players}/{b.MaxPlayers} {flags.Trim()}"));
        }
        return TextCommandResult.Success(sb.ToString());
    }

    private TextCommandResult HandleSend(TextCommandCallingArgs args)
    {
        if (!NimbusBackend.Enabled) return TextCommandResult.Error("Nimbus is disabled.");
        string playerName = args[0] as string;
        string targetServerId = args[1] as string;
        string reason = args.Parsers[2].IsMissing ? null : (args[2] as string);

        var client = server.GetClientByPlayername(playerName);
        var player = client?.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error($"Player '{playerName}' not online.");

        string callerLabel = args.Caller?.GetName() ?? "console";
        return RequestTransfer(player, targetServerId, reason, "admin:" + callerLabel);
    }

    private TextCommandResult HandleServerSelf(TextCommandCallingArgs args)
    {
        if (!NimbusBackend.Enabled) return TextCommandResult.Error("Nimbus is disabled on this server.");
        if (args.Caller?.Player is not IServerPlayer player)
            return TextCommandResult.Error("Run this command in-game.");
        string targetServerId = args[0] as string;
        return RequestTransfer(player, targetServerId, null, "player:" + player.PlayerUID);
    }

    private TextCommandResult RequestTransfer(IServerPlayer player, string targetServerId, string reason, string requestedBy)
    {
        var cfg = NimbusBackend.Config;
        if (string.Equals(targetServerId, cfg?.ServerId, StringComparison.OrdinalIgnoreCase))
            return TextCommandResult.Error($"You are already on '{targetServerId}'.");

        var snap = NimbusBackend.LastSnapshot;
        var target = snap?.Backends?.FirstOrDefault(b => string.Equals(b.ServerId, targetServerId, StringComparison.OrdinalIgnoreCase));
        if (target == null) return TextCommandResult.Error($"Target '{targetServerId}' not in current registry snapshot. Try /nimbus servers.");
        if (target.Stale) return TextCommandResult.Error($"Target '{targetServerId}' is stale (offline).");
        if (target.Maintenance) return TextCommandResult.Error($"Target '{targetServerId}' is in maintenance.");

        try
        {
            var task = NimbusBackend.RequestTransferAsync(player.PlayerUID, player.PlayerName, target.ServerId, "redirect", reason, requestedBy);
            task.Wait(TimeSpan.FromSeconds(3));
            if (!task.IsCompletedSuccessfully)
                return TextCommandResult.Error("Transfer request timed out talking to the registry.");
            var resp = task.Result;
            if (resp == null || !resp.Ok)
                return TextCommandResult.Error($"Registry rejected transfer: {resp?.Error ?? "unknown"}");
        }
        catch (Exception ex)
        {
            StratumRuntime.LogWarning($"Nimbus transfer-intent post failed for {player.PlayerName}: {ex.Message}");
            return TextCommandResult.Error("Failed to post transfer intent: " + ex.Message);
        }

        return TextCommandResult.Success($"Queued transfer of {player.PlayerName} to {target.DisplayName}. The proxy will swap within ~1s.");
    }

    private TextCommandResult HandleReload()
    {
        // Network config is part of stratum.json. Full reload handled by /stratum reload.
        // This subcommand just bounces the backend so credential / URL changes take effect.
        NimbusBackend.MaybeStop();
        NimbusBackend.MaybeStart(server);
        return TextCommandResult.Success("Nimbus backend bounced. Current status: " + NimbusBackend.LastStatus);
    }
}
