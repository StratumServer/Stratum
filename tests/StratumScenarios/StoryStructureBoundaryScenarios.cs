using System;
using System.Linq;
using System.Reflection;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Validates the exact-height boundary behavior on real world heights
/// (128 from the original bug report, 256 vanilla default, 448 tall-world option).
///
/// - The arithmetic edge case (structure as tall as the world starting at Y=0) is
///   closed by the guard: IsStructureTooTall(sizeY, maxY) rejects anything that cannot
///   fit in [1, maxY), so the clamp never sees it and Y2 can never overflow.
/// - The underground placement path (placement="underground" never reaches ClampStructureY,
///   CenterPos.Y pinned to 1) is closed by the same guard line.
/// - The upper branch is pinned by cases that pull the base down when the top would
///   exceed maxValidY, at 128, 256 and 448.
/// - ClampStructureY and IsStructureTooTall are fork-only; this scenario runs against
///   the patched server that `make scenarios` materializes.
/// </summary>
public class StoryStructureBoundaryScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public void StoryStructureHeightBoundaries_Should_EnforceUsableRange()
	{
		MethodInfo clampMethod = ResolveMethod("ClampStructureY");
		MethodInfo guardMethod = ResolveMethod("IsStructureTooTall");

		// Row layout: sizeY, maxY, initialY1, expectedY1, expectedY2, expectedShift, label
		var clampCases = new (int SizeY, int MaxY, int InitialY1, int ExpectedY1, int ExpectedY2, bool ExpectedShift, string Label)[]
		{
			// Exact-height-minus-one starting at the mantle: lower clamp pulls Y1 up to 1,
			// top lands on 128 (last block at 127). Lower edge of Zaldaryon's case.
			(127, 128, 0,   1, 128, true,  "128 world: mantle start, lower clamp pulls Y1 up to 1"),
			// A placement that already fits cleanly: no shift at all.
			(60,  128, 50,  50, 110, false, "128 world: well-fitting placement, no shift"),
			// Upper branch at work: top would exceed maxValidY, base is pulled down.
			(100, 128, 50,  28, 128, true,  "128 world: top exceeds maxValidY, base pulled down"),
			// No-op lower boundary: startY already equals minValidY.
			(60,  128, 1,   1,  61,  false, "128 world: lower boundary no-op"),
			// No-op upper boundary: top already lands exactly on maxValidY + 1.
			(127, 128, 1,   1,  128, false, "128 world: upper boundary no-op"),
			// Vanilla default height: a 200-tall structure based at Y=100 would top out at
			// 300; maxAllowedMinY = 255 - 200 + 1 = 56 pulls the base down.
			(200, 256, 100, 56, 256, true,  "256 world: upper branch pulls base down to 56"),
			// Tall-world option: exactly the usable height (447) starting at the mantle;
			// lower clamp lifts it to Y1=1 and the top lands exactly on maxY.
			(447, 448, 0,   1,  448, true,  "448 world: usable-height structure from mantle fits exactly"),
		};

		foreach (var c in clampCases)
		{
			var strucloc = new Cuboidi(0, c.InitialY1, 0, 10, c.InitialY1 + c.SizeY, 10);
			var startPos = new BlockPos(0, c.InitialY1, 0, 0);

			object[] args = { strucloc, startPos, c.SizeY, c.MaxY };
			bool wasShifted = (bool)clampMethod.Invoke(null, args)!;

			Assert.True(
				wasShifted == c.ExpectedShift
					&& strucloc.Y1 == c.ExpectedY1
					&& strucloc.Y2 == c.ExpectedY2
					&& startPos.Y == c.ExpectedY1,
				$"clamp case '{c.Label}' failed: " +
				$"sizeY={c.SizeY}, maxY={c.MaxY}, initialY1={c.InitialY1} -> " +
				$"got Y1={strucloc.Y1}, Y2={strucloc.Y2}, startPos.Y={startPos.Y}, shift={wasShifted}");
		}

		// Row layout: sizeY, maxY, expectedRejected, label
		var guardCases = new (int SizeY, int MaxY, bool ExpectedRejected, string Label)[]
		{
			// Zaldaryon's exact case and Pixnop's underground path in one row: a structure
			// exactly as tall as the world has nowhere to go in [1, maxY), whether it came
			// through a surface branch or the underground path pinned to Y=1.
			(128, 128, true,  "128 world: structure exactly as tall as the world rejected"),
			// One block shorter: exactly the usable height, must be accepted.
			(127, 128, false, "128 world: one block shorter fits exactly, accepted"),
			// Shipped underground structure (101 tall): vanilla content stays safe.
			(101, 128, false, "128 world: shipped underground structure (101) accepted"),
			// The content that motivated the PR: BetterRuins university, 150 tall.
			(150, 128, true,  "128 world: 150-tall mod structure rejected"),
			(150, 256, false, "256 world: same 150-tall mod structure accepted"),
			// Off-by-one around the 128 boundary.
			(129, 128, true,  "128 world: one block taller than the world rejected"),
			(126, 128, false, "128 world: two blocks shorter accepted"),
			// Vanilla default height: same boundary rule, no magic numbers.
			(256, 256, true,  "256 world: structure exactly as tall as the world rejected"),
			(255, 256, false, "256 world: one block shorter accepted"),
			// Tall-world option: the rule scales, nothing is hardcoded to 128 or 256.
			(448, 448, true,  "448 world: structure exactly as tall as the world rejected"),
			(447, 448, false, "448 world: one block shorter accepted"),
		};

		foreach (var g in guardCases)
		{
			// Invokes the real IsStructureTooTall from the patched server: the test verifies
			// the shipped formula, not a copy of it.
			bool rejected = (bool)guardMethod.Invoke(null, new object[] { g.SizeY, g.MaxY })!;

			Assert.True(
				rejected == g.ExpectedRejected,
				$"guard case '{g.Label}' failed: " +
				$"sizeY={g.SizeY}, maxY={g.MaxY} -> rejected={rejected}, expected {g.ExpectedRejected}");
		}
	}

	/// <summary>
	/// Resolves a fork-only private/internal static method. A missing method here means the
	/// test is running against vanilla or the method was renamed: both are regressions this
	/// suite must catch, so the asserts are unconditional and descriptive.
	/// (BindingFlags.NonPublic resolves both private and internal methods.)
	/// </summary>
	private static MethodInfo ResolveMethod(string methodName)
	{
		Assembly? survival = AppDomain.CurrentDomain.GetAssemblies()
			.FirstOrDefault(a => a.GetName().Name == "VSSurvivalMod");
		Assert.NotNull(survival);

		Type? type = survival!.GetType("Vintagestory.GameContent.GenStoryStructures");
		Assert.True(
			type != null,
			"GenStoryStructures not found in VSSurvivalMod: the patched server is not loaded.");

		MethodInfo? method = type!.GetMethod(
			methodName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.True(
			method != null,
			$"{methodName} is missing: running against vanilla, or the fork method was renamed/removed.");

		return method!;
	}
}
