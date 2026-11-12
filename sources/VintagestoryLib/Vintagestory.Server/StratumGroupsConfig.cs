using System;
using System.Collections.Generic;

namespace Vintagestory.Server;

/// <summary>
/// One group kind. A kind is a label put on a player group by staff (/group admin kind) that
/// decides how that group behaves: whether a player may hold only one of them at a time, how
/// many members it takes, whether members can be locked into it, and whether sharing it turns
/// friendly fire off.
/// </summary>
internal sealed class StratumGroupKindConfig
{
	// The unclassified kind: every group that predates this feature, and every group made while
	// Groups.DefaultKind is null. Its values are exactly vanilla behaviour, so a server that
	// never defines a kind sees no change.
	public static readonly StratumGroupKindConfig Unclassified = new StratumGroupKindConfig
	{
		Code = null,
		Exclusive = false,
		MaxMembers = 0,
		Lockable = true,
		CountsAsSameSide = true,
		PlayerCreatable = true,
		TagPriority = 0
	};

	public string Code { get; set; }

	// At most one live membership of this kind per player. Faction-style. Two different
	// exclusive kinds do not conflict with each other, so a player can hold one faction and
	// one arena team at the same time.
	public bool Exclusive { get; set; }

	// 0 means unlimited. Staff adds are allowed past the cap and audited; player joins are not.
	public int MaxMembers { get; set; }

	// Whether /group admin lock may pin a member into a group of this kind.
	public bool Lockable { get; set; } = true;

	// Whether sharing a group of this kind counts as being on the same side, which is what
	// FriendlyFire.AllowGroupDamage=false reads. Turn it off for administrative groups
	// ("newcomers", "event signups") that should not switch PvP off between their members.
	public bool CountsAsSameSide { get; set; } = true;

	// Whether /group create may produce this kind. Only meaningful for Groups.DefaultKind.
	public bool PlayerCreatable { get; set; } = true;

	// Tag used for groups of this kind that have no tag of their own. Null for none.
	public string Tag { get; set; }

	// Highest priority wins when a player holds several tagged groups. Ties break on group Uid.
	public int TagPriority { get; set; }
}

/// <summary>
/// Stratum #335: staff authority over player groups. Kinds, roster freeze, membership locks,
/// group relations and group tags. Every default here reproduces vanilla behaviour: nothing
/// changes until a server defines a kind or a staff member sets something.
/// </summary>
internal sealed class StratumGroupsConfig
{
	public bool Enabled { get; set; } = true;

	// Kind assigned to groups made with /group create. Null (or "none", which /stratum set can
	// write where it cannot write null) leaves them unclassified, the vanilla-equivalent
	// default. A kind named here must exist in Kinds and be PlayerCreatable, otherwise new
	// groups stay unclassified and each /group create logs a warning.
	public string DefaultKind { get; set; } = null;

	// Shipped as an example, and referenced by nothing until staff run /group admin kind. The
	// entry exists so the shape of a kind is visible in a freshly written config file.
	public List<StratumGroupKindConfig> Kinds { get; set; } = new List<StratumGroupKindConfig>
	{
		new StratumGroupKindConfig
		{
			Code = "faction",
			Exclusive = true,
			MaxMembers = 0,
			Lockable = true,
			CountsAsSameSide = true,
			PlayerCreatable = false,
			Tag = null,
			TagPriority = 100
		}
	};

	// Whether /group admin relation works at all.
	public bool RelationsEnabled { get; set; } = true;

	// Whether an "ally" relation between two groups also turns friendly fire off between their
	// members. False keeps allies purely informational.
	public bool AlliesCountAsSameSide { get; set; } = true;

	// Whether /group info shows a group's kind, tag, frozen roster and relations to ordinary
	// players. Allies already decide whether a player's weapon lands, and tags are on nametags,
	// so hiding this mostly hides it from the people it costs. False keeps it to staff.
	public bool ShowStateToPlayers { get; set; } = true;

	// Group tag rendering. The tag itself is per group (/group admin tag), with a per-kind
	// fallback. These only decide where a tag that exists gets shown.
	public bool ShowTagInChat { get; set; } = true;

	public bool ShowTagInNametag { get; set; } = true;

	public string TagFormat { get; set; } = "[{tag}]";

	public string TagColor { get; set; } = null;

	public int MaxTagLength { get; set; } = 8;

	public void EnsurePopulated()
	{
		Kinds ??= new List<StratumGroupKindConfig>();
		Kinds.RemoveAll(kind => kind == null || string.IsNullOrWhiteSpace(kind.Code));
		foreach (StratumGroupKindConfig kind in Kinds)
		{
			kind.Code = kind.Code.Trim();
			kind.MaxMembers = Math.Max(0, kind.MaxMembers);
			if (string.IsNullOrWhiteSpace(kind.Tag))
			{
				kind.Tag = null;
			}
		}

		if (string.IsNullOrWhiteSpace(DefaultKind)
			|| string.Equals(DefaultKind.Trim(), "none", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(DefaultKind.Trim(), "unclassified", StringComparison.OrdinalIgnoreCase))
		{
			DefaultKind = null;
		}

		if (string.IsNullOrWhiteSpace(TagFormat) || !TagFormat.Contains("{tag}"))
		{
			TagFormat = "[{tag}]";
		}

		if (string.IsNullOrWhiteSpace(TagColor))
		{
			TagColor = null;
		}

		MaxTagLength = Math.Clamp(MaxTagLength, 1, 32);
	}
}
