using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

	// One role, one or more prefixes. See StratumRolePrefixList: the value reads and writes as
	// a bare object for a single prefix and as an array for several, so a config that predates
	// this feature is not reshaped by the boot rewrite.
	public Dictionary<string, StratumRolePrefixList> Roles { get; set; } = CreateDefaults();

	public void EnsurePopulated()
	{
		Format ??= "[{tag}]";
		if (Roles == null || Roles.Count == 0)
		{
			Roles = CreateDefaults();
		}

		foreach (StratumRolePrefixList list in Roles.Values)
		{
			if (list?.Prefixes == null)
			{
				continue;
			}

			foreach (StratumRolePrefixConfig prefix in list.Prefixes)
			{
				prefix?.EnsurePopulated();
			}
		}
	}

	// The prefixes that apply to a role, highest Priority first. Returns null when the role
	// has none configured or enabled, so a player with no prefix allocates nothing.
	public List<StratumRolePrefixConfig> ResolveFor(string roleCode)
	{
		if (Roles == null || string.IsNullOrWhiteSpace(roleCode))
		{
			return null;
		}

		List<StratumRolePrefixConfig> matches = null;
		foreach (KeyValuePair<string, StratumRolePrefixList> entry in Roles)
		{
			// A linear scan with an explicit case-insensitive compare. Newtonsoft rebuilds the
			// dictionary on load and drops the OrdinalIgnoreCase comparer, so indexing by key
			// here would be case sensitive. Do not replace this with Roles.TryGetValue.
			if (entry.Value?.Prefixes == null || !string.Equals(entry.Key, roleCode, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			foreach (StratumRolePrefixConfig prefix in entry.Value.Prefixes)
			{
				if (prefix == null || !prefix.Enabled)
				{
					continue;
				}

				(matches ??= new List<StratumRolePrefixConfig>()).Add(prefix);
			}
		}

		// OrderByDescending is a stable sort, so equal Priority keeps config file order.
		// Do not swap this for List.Sort, which is unstable and would scramble ties.
		return matches?.OrderByDescending(prefix => prefix.Priority).ToList();
	}

	private static Dictionary<string, StratumRolePrefixList> CreateDefaults()
	{
		return new Dictionary<string, StratumRolePrefixList>(StringComparer.OrdinalIgnoreCase)
		{
			["admin"] = new StratumRolePrefixList(new StratumRolePrefixConfig { Tag = "Admin", Color = "#ff5f57", Bold = true, Priority = 100 }),
			["sumod"] = new StratumRolePrefixList(new StratumRolePrefixConfig { Tag = "Mod", Color = "#4cc9f0", Bold = true, Priority = 50 }),
			["crmod"] = new StratumRolePrefixList(new StratumRolePrefixConfig { Tag = "Mod", Color = "#4cc9f0", Bold = true, Priority = 50 })
		};
	}
}

// A role's prefixes. Serialized as a bare object when there is exactly one and as an array
// otherwise, so an operator who wants two tags writes `[ {...}, {...} ]` and everyone else
// keeps the pre-#274 `{ ... }` shape untouched through the boot config rewrite.
[JsonConverter(typeof(StratumRolePrefixListConverter))]
internal sealed class StratumRolePrefixList
{
	public List<StratumRolePrefixConfig> Prefixes { get; } = new List<StratumRolePrefixConfig>();

	public StratumRolePrefixList()
	{
	}

	public StratumRolePrefixList(params StratumRolePrefixConfig[] prefixes)
	{
		if (prefixes != null)
		{
			Prefixes.AddRange(prefixes);
		}
	}
}

internal sealed class StratumRolePrefixListConverter : JsonConverter<StratumRolePrefixList>
{
	public override StratumRolePrefixList ReadJson(JsonReader reader, Type objectType, StratumRolePrefixList existingValue, bool hasExistingValue, JsonSerializer serializer)
	{
		StratumRolePrefixList result = new StratumRolePrefixList();

		if (reader.TokenType == JsonToken.Null)
		{
			return result;
		}

		if (reader.TokenType == JsonToken.StartArray)
		{
			foreach (JToken item in JArray.Load(reader))
			{
				StratumRolePrefixConfig prefix = item.ToObject<StratumRolePrefixConfig>(serializer);
				if (prefix != null)
				{
					result.Prefixes.Add(prefix);
				}
			}

			return result;
		}

		StratumRolePrefixConfig single = JObject.Load(reader).ToObject<StratumRolePrefixConfig>(serializer);
		if (single != null)
		{
			result.Prefixes.Add(single);
		}

		return result;
	}

	public override void WriteJson(JsonWriter writer, StratumRolePrefixList value, JsonSerializer serializer)
	{
		if (value == null)
		{
			writer.WriteNull();
			return;
		}

		if (value.Prefixes.Count == 1)
		{
			serializer.Serialize(writer, value.Prefixes[0]);
			return;
		}

		writer.WriteStartArray();
		foreach (StratumRolePrefixConfig prefix in value.Prefixes)
		{
			serializer.Serialize(writer, prefix);
		}
		writer.WriteEndArray();
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
