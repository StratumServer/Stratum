using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

	private StratumNametagsConfig Cfg => StratumRuntime.Config?.Appearance?.Nametags;

	/// <summary>
	/// Called by <c>CmdPlayer.ChangeRole</c> after a role swap so the prefix + colour are
	/// re-applied and re-broadcast without requiring the affected player to relog.
	/// Returns true if a refresh ran (caller should re-broadcast PlayerData).
	/// </summary>
	public static bool RefreshFor(IServerPlayer player)
	{
		return instance?.Refresh(player, entitlements: true) == true;
	}

	/// <summary>
	/// Stratum #335: re-applies the name after a group membership, kind or tag change. Called
	/// from the vanilla group packet senders, which every join, leave, kick, disband and staff
	/// move already goes through, so no membership path can leave a stale tag behind. Leaves
	/// the role entitlement alone: that only changes with the role, and swapping it here would
	/// churn the entitlement list on every group packet.
	/// </summary>
	public static void RefreshGroupTagFor(IServerPlayer player)
	{
		instance?.Refresh(player, entitlements: false);
	}

	private bool Refresh(IServerPlayer player, bool entitlements)
	{
		if (player == null) return false;

		StratumNametagsConfig cfg = Cfg;
		StratumConfig root = StratumRuntime.Config;

		// Stratum #335: group tags ride the same nametag rewrite as role prefixes, but they are
		// their own feature with their own switch. A server can run group tags with
		// Appearance.Nametags off, so the role half below is gated separately.
		StratumGroupsConfig groups = root?.Groups;
		bool groupTags = groups != null && groups.Enabled && groups.ShowTagInNametag;
		bool roleTags = cfg != null && cfg.Enabled && player.Role != null;
		if (!groupTags && !roleTags) return false;

		// Null when the player holds no tagged group, which also has to reach
		// ApplyNametagPrefix: that is how a cleared tag gets stripped off the name again.
		string groupTag = groupTags ? StratumGroupPolicy.FormatTag(StratumGroupPolicy.ResolveTag(server, player)) : null;
		bool rolePrefixes = roleTags && cfg.ApplyRolePrefix;
		List<StratumRolePrefixConfig> prefixes = rolePrefixes ? root?.Appearance?.RolePrefixes?.ResolveFor(player.Role.Code) : null;

		if (rolePrefixes || groupTags)
		{
			ApplyNametagPrefix(player, prefixes, cfg?.PrefixFormat, groupTag);
		}

		if (roleTags && entitlements)
		{
			RemoveInjectedEntitlement(player);
			MaybeInjectEntitlement(player, cfg);
		}

		return true;
	}

	private void OnPlayerJoin(IServerPlayer player)
	{
		Refresh(player, entitlements: true);
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		if (player == null) return;
		injectedByUid.Remove(player.PlayerUID);
	}

	private void ApplyNametagPrefix(IServerPlayer player, List<StratumRolePrefixConfig> prefixes, string format, string groupTag)
	{
		EntityPlayer entity = player.Entity;
		if (entity == null) return;

		string baseName = player.PlayerName;
		if (string.IsNullOrEmpty(baseName)) return;

		string fmt = string.IsNullOrEmpty(format) ? "[{tag}] " : format;
		StringBuilder prefixText = new StringBuilder();
		if (groupTag != null)
		{
			prefixText.Append(groupTag).Append(' ');
		}

		if (prefixes != null)
		{
			foreach (StratumRolePrefixConfig prefix in prefixes)
			{
				if (string.IsNullOrWhiteSpace(prefix.Tag)) continue;
				prefixText.Append(fmt.Replace("{tag}", prefix.Tag));
			}
		}

		// Nothing left for the new role, so strip back to the bare player name and a demoted
		// admin loses their "[Admin] " badge instead of keeping it stuck.
		string desired = prefixText.Length == 0 ? baseName : prefixText + baseName;

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

}
