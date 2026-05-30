using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// Server-side nametag customization that piggybacks on the vanilla client's nametag
/// renderer. Two things are pushed to the client over the normal protocol:
/// <list type="number">
///   <item>The display string in <c>WatchedAttributes["nametag"].name</c> is rewritten to
///   include the role's chat prefix (e.g. "[Admin] Alice"). All vanilla clients render
///   this verbatim.</item>
///   <item>An entitlement code from <c>GlobalConstants.playerColorByEntitlement</c> is
///   spliced into the player's entitlement list before the PlayerData packet broadcasts,
///   so vanilla clients paint the tag in that colour.</item>
/// </list>
/// Wired off <see cref="ServerEventManager.OnPlayerJoin"/>, which fires after the entity
/// is set up but before <c>BroadcastPlayerData</c> and the initial entity sync. Also
/// exposes a static <see cref="RefreshFor"/> so the role-change command can re-apply
/// without waiting for a reconnect.
/// </summary>
internal sealed class StratumNametags
{
	private static StratumNametags instance;

	private readonly ServerMain server;

	// Tracks entitlement codes we injected per UID so we can replace them cleanly on role change.
	private readonly Dictionary<string, string> injectedByUid = new Dictionary<string, string>(StringComparer.Ordinal);

	public StratumNametags(ServerMain server)
	{
		this.server = server;
		instance = this;
		server.EventManager.OnPlayerJoin += OnPlayerJoin;
		server.EventManager.OnPlayerDisconnect += OnPlayerDisconnect;
	}

	private StratumNametagsConfig Cfg => StratumRuntime.Config?.Nametags;

	/// <summary>
	/// Called by <c>CmdPlayer.ChangeRole</c> after a role swap so the prefix + colour are
	/// re-applied and re-broadcast without requiring the affected player to relog.
	/// Returns true if a refresh ran (caller should re-broadcast PlayerData).
	/// </summary>
	public static bool RefreshFor(IServerPlayer player)
	{
		return instance?.Refresh(player) == true;
	}

	private bool Refresh(IServerPlayer player)
	{
		StratumNametagsConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return false;
		if (player?.Role == null) return false;

		StratumConfig root = StratumRuntime.Config;
		StratumChatRolePrefixConfig prefix = FindRolePrefix(root?.Chat?.RolePrefixes, player.Role.Code);

		if (cfg.ApplyChatPrefix)
		{
			ApplyNametagPrefix(player, prefix, cfg.PrefixFormat);
		}

		RemoveInjectedEntitlement(player);
		MaybeInjectEntitlement(player, cfg);
		return true;
	}

	private void OnPlayerJoin(IServerPlayer player)
	{
		Refresh(player);
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		if (player == null) return;
		injectedByUid.Remove(player.PlayerUID);
	}

	private void ApplyNametagPrefix(IServerPlayer player, StratumChatRolePrefixConfig prefix, string format)
	{
		EntityPlayer entity = player.Entity;
		if (entity == null) return;

		string baseName = player.PlayerName;
		if (string.IsNullOrEmpty(baseName)) return;

		string desired;
		if (prefix != null && prefix.Enabled && !string.IsNullOrWhiteSpace(prefix.Tag))
		{
			string fmt = string.IsNullOrEmpty(format) ? "[{tag}] " : format;
			string prefixText = fmt.Replace("{tag}", prefix.Tag);
			desired = prefixText + baseName;
		}
		else
		{
			// New role has no prefix \u2014 strip back to the bare player name so a demoted
			// admin loses their "[Admin] " badge instead of keeping it stuck.
			desired = baseName;
		}

		string current = entity.WatchedAttributes.GetTreeAttribute("nametag")?.GetString("name");
		if (string.Equals(current, desired, StringComparison.Ordinal)) return;

		entity.SetName(desired);
		entity.WatchedAttributes.MarkPathDirty("nametag");
	}

	private void RemoveInjectedEntitlement(IServerPlayer player)
	{
		if (player is not ServerPlayer sp) return;
		if (!injectedByUid.TryGetValue(player.PlayerUID, out string prevCode)) return;

		List<Entitlement> list = sp.Entitlements;
		if (list != null)
		{
			list.RemoveAll(e => string.Equals(e?.Code, prevCode, StringComparison.OrdinalIgnoreCase));
		}
		injectedByUid.Remove(player.PlayerUID);
	}

	private void MaybeInjectEntitlement(IServerPlayer player, StratumNametagsConfig cfg)
	{
		if (cfg.EntitlementColorByRole == null) return;
		if (!cfg.EntitlementColorByRole.TryGetValue(player.Role.Code, out string entCode)) return;
		if (string.IsNullOrWhiteSpace(entCode)) return;

		if (player is not ServerPlayer sp) return;

		List<Entitlement> list = sp.Entitlements;
		if (list == null) return;

		if (cfg.OnlyInjectIfNoExistingEntitlement && list.Count > 0)
		{
			// Leave real entitlements alone so an actual VS supporter keeps their own colour.
			return;
		}

		if (list.Any(e => string.Equals(e?.Code, entCode, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}

		list.Add(new Entitlement { Code = entCode, Name = entCode });
		injectedByUid[player.PlayerUID] = entCode;
		StratumRuntime.LogInfo("nametag: injected entitlement '" + entCode + "' for " + player.PlayerName + " (role " + player.Role.Code + ")");
	}

	private static StratumChatRolePrefixConfig FindRolePrefix(Dictionary<string, StratumChatRolePrefixConfig> prefixes, string roleCode)
	{
		if (prefixes == null || string.IsNullOrWhiteSpace(roleCode)) return null;
		return prefixes
			.Where(kv => kv.Value != null && kv.Value.Enabled && string.Equals(kv.Key, roleCode, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(kv => kv.Value.Priority)
			.Select(kv => kv.Value)
			.FirstOrDefault();
	}
}
