using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Nested byType is resolved after the outer byType merge, so the surgeon hood
/// keeps the nested warmth.
/// </summary>
public class ByTypeWarmthScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task SurgeonHood_Should_KeepTheNestedWarmth()
	{
		await World.Ticks(1);
		Item hood = World.Api.World.GetItem(new AssetLocation("game:clothes-face-surgeonhood"));
		Assert.NotNull(hood);
		Assert.Equal(0.25f, hood.Attributes["warmth"].AsFloat(-1f));

		Type registry = AppDomain.CurrentDomain.GetAssemblies()
			.Select(assembly => assembly.GetType("Vintagestory.ServerMods.NoObf.RegistryObjectType"))
			.First(type => type != null)!;
		MethodInfo resolve = registry.GetMethod("Resolve")!;
		JObject token = JObject.Parse("""
			{
			  "attributes": { "warmth": 0.25 },
			  "attributesbyType": { "game:clothes-face-surgeonhood": 2.2 }
			}
			""");
		JToken resolved = (JToken)resolve.Invoke(null, new object[] { token, "game:clothes-face-surgeonhood", null! })!;
		Assert.Equal(JTokenType.Object, resolved["attributes"]!.Type);
		Assert.Equal(0.25d, resolved["attributes"]!["warmth"]!.Value<double>());
	}
}
