using System.Collections;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// revive, restart, and clearitems are registered with the other command privileges.
/// </summary>
public class CommandPrivilegeScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task ReviveRestartAndClearItems_Should_BeRegistered()
	{
		await World.Ticks(1);
		object server = World.Api.World;
		var privileges = (IEnumerable)server.GetType().GetField("AllPrivileges")!.GetValue(server)!;
		var codes = new System.Collections.Generic.HashSet<string>();
		foreach (object privilege in privileges)
		{
			codes.Add(privilege.ToString());
		}

		Assert.Contains("stratum.revive", codes);
		Assert.Contains("stratum.restart", codes);
		Assert.Contains("stratum.clearitems", codes);
	}
}
