using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.Common;

namespace Vintagestory.Server;

public class ServerRolesConfig
{
	[JsonProperty]
	public string FileEditWarning { get; set; }

	[JsonProperty]
	public string ConfigVersion { get; set; } = "1.0";

	[JsonProperty]
	public string DefaultRoleCode { get; set; }

	[JsonProperty]
	public List<PlayerRole> Roles { get; set; } = new List<PlayerRole>();
}