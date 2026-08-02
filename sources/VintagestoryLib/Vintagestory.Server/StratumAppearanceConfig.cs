using System;
using System.Collections.Generic;

namespace Vintagestory.Server;

internal class StratumAppearanceConfig
{
	public StratumThemeConfig Theme { get; set; } = new StratumThemeConfig();

	public StratumRolePrefixesConfig RolePrefixes { get; set; } = new StratumRolePrefixesConfig();

	public StratumNametagsConfig Nametags { get; set; } = new StratumNametagsConfig();

	public void EnsurePopulated()
	{
		Theme ??= new StratumThemeConfig();
		RolePrefixes ??= new StratumRolePrefixesConfig();
		Nametags ??= new StratumNametagsConfig();
		Theme.EnsurePopulated();
		RolePrefixes.EnsurePopulated();
		Nametags.EnsurePopulated();
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

internal class StratumRolePrefixesConfig
{
	public bool Enabled { get; set; } = true;

	public string Format { get; set; } = "[{tag}]";

	public Dictionary<string, StratumRolePrefixConfig> Roles { get; set; } = CreateDefaults();

	public void EnsurePopulated()
	{
		Format ??= "[{tag}]";
		if (Roles == null || Roles.Count == 0)
		{
			Roles = CreateDefaults();
		}

		foreach (StratumRolePrefixConfig prefix in Roles.Values)
		{
			prefix?.EnsurePopulated();
		}
	}

	private static Dictionary<string, StratumRolePrefixConfig> CreateDefaults()
	{
		return new Dictionary<string, StratumRolePrefixConfig>(StringComparer.OrdinalIgnoreCase)
		{
			["admin"] = new StratumRolePrefixConfig
			{
				Tag = "Admin",
				Color = "#ff5f57",
				Bold = true,
				Priority = 100
			},
			["sumod"] = new StratumRolePrefixConfig
			{
				Tag = "Mod",
				Color = "#4cc9f0",
				Bold = true,
				Priority = 50
			},
			["crmod"] = new StratumRolePrefixConfig
			{
				Tag = "Mod",
				Color = "#4cc9f0",
				Bold = true,
				Priority = 50
			}
		};
	}
}

internal class StratumRolePrefixConfig
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

internal class StratumNametagsConfig
{
	public bool Enabled { get; set; }

	public bool ApplyRolePrefix { get; set; } = true;

	public string PrefixFormat { get; set; } = "[{tag}] ";

	public Dictionary<string, string> EntitlementColorByRole { get; set; } = CreateDefaultEntitlementMap();

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
