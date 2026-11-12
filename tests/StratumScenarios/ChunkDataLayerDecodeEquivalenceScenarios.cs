using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Verifies bit-for-bit agreement of the three decode branches within
/// ChunkDataLayer.CopyBlocksToUnsafe: the scalar fallback, AVX2 and AVX-512.
///
/// The scalar implementation is duplicated here as a reference; the two SIMD methods are invoked
/// via reflection on the private statics DecodePlanesAvx2 / DecodePlanesAvx512,
/// because the test project references only VintagestoryAPI and does not pull in VintagestoryLib
/// into signatures that xUnit reflects over during discovery.
///
/// Comparisons requiring an ISA absent from the current machine are skipped, so the test
/// stays green even on a CPU without AVX2/AVX-512. The scenario's goal is to catch divergence
/// between implementations rather than require a specific processor.
/// </summary>
public class ChunkDataLayerDecodeEquivalenceScenarios : AtlasScenarioBase
{
	private const int PlaneLength = 1024;   // 32768 / 32
	private const int OutputLength = 32768; // 32 * 32 * 32
	private const int BitsizeMin = 1;
	private const int BitsizeMax = 15;      // dataBits — int[15][], num limited to 15

	[AtlasScenario(TimeoutMs = 180_000)]
	public void CopyBlocksToUnsafe_DecodePaths_Should_Agree_BitForBit()
	{
		Type? layerType = Type.GetType("Vintagestory.Common.ChunkDataLayer, VintagestoryLib");
		Assert.True(layerType != null,
			"ChunkDataLayer not found in VintagestoryLib.");

		MethodInfo? avx2 = layerType!.GetMethod(
			"DecodePlanesAvx2", BindingFlags.NonPublic | BindingFlags.Static);
		MethodInfo? avx512 = layerType.GetMethod(
			"DecodePlanesAvx512", BindingFlags.NonPublic | BindingFlags.Static);

		Assert.True(avx2 != null,
			"DecodePlanesAvx2 is missing in ChunkDataLayer: the SIMD branch was renamed or removed.");
		Assert.True(avx512 != null,
			"DecodePlanesAvx512 is missing in ChunkDataLayer: the SIMD branch was renamed or removed.");

		// The same set of cases is run for both available ISAs. The values don't depend on what
		// the CPU actually supports — the reference always holds.
		//
		// Stratum: this run reports which ISAs it actually exercised and, when the environment
		// variable STRATUM_REQUIRE_AVX512 / STRATUM_REQUIRE_AVX2 asks for a specific ISA, fails
		// loudly if that ISA was silently skipped instead of passing "quiet green" — the previous
		// version could pass on an AVX-512-only CI runner while never actually comparing the
		// AVX-512 branch, if AVX2 happened to also be available and ran first. Both env vars are
		// opt-in so a plain dev machine without either ISA still passes with just a log line, as
		// before.
		bool avx2Checked = false;
		bool avx512Checked = false;

		foreach (DecodeCase c in BuildCases())
		{
			int[] reference = ScalarDecode(c.Planes, c.Palette, c.BitSize);

			if (Avx2.IsSupported)
			{
				int[] viaAvx2 = new int[OutputLength];
				// Signature: private static unsafe void DecodePlanesAvx2(int[] dst, int[][] planes, int[] palette, int num)
				avx2!.Invoke(null, new object[] { viaAvx2, c.Planes, c.Palette, c.BitSize });
				AssertSame(reference, viaAvx2, c.Label, "AVX2");
				avx2Checked = true;
			}

			if (Avx512F.IsSupported)
			{
				int[] viaAvx512 = new int[OutputLength];
				// Signature: private static unsafe void DecodePlanesAvx512(int[] dst, int[][] planes, int[] palette, int num)
				avx512!.Invoke(null, new object[] { viaAvx512, c.Planes, c.Palette, c.BitSize });
				AssertSame(reference, viaAvx512, c.Label, "AVX-512");
				avx512Checked = true;
			}
		}

		Console.WriteLine(
			$"[ChunkDataLayerDecodeEquivalence] ISA coverage this run: AVX2={avx2Checked}, AVX-512={avx512Checked}.");

		if (!avx2Checked && !avx512Checked)
		{
			// No SIMD ISA at all: intentionally not a failure, only the scalar reference ran.
			Console.WriteLine(
				"[ChunkDataLayerDecodeEquivalence] neither AVX2 nor AVX-512 is supported: " +
				"SIMD comparisons skipped, only the scalar reference was run.");
		}

		if (Environment.GetEnvironmentVariable("STRATUM_REQUIRE_AVX512") == "1")
		{
			Assert.True(avx512Checked,
				"STRATUM_REQUIRE_AVX512=1 but this run's CPU does not report Avx512F.IsSupported, " +
				"so the AVX-512 decode branch was never compared. This test would otherwise report " +
				"a quiet green without exercising the ISA this CI leg exists to cover.");
		}
		if (Environment.GetEnvironmentVariable("STRATUM_REQUIRE_AVX2") == "1")
		{
			Assert.True(avx2Checked,
				"STRATUM_REQUIRE_AVX2=1 but this run's CPU does not report Avx2.IsSupported, " +
				"so the AVX2 decode branch was never compared.");
		}
	}

	private static void AssertSame(int[] expected, int[] actual, string label, string variant)
	{
		Assert.True(expected.Length == actual.Length,
			$"[{label}] {variant} returned {actual.Length} values, expected {expected.Length}");

		for (int i = 0; i < expected.Length; i++)
		{
			if (expected[i] != actual[i])
			{
				Assert.Fail(
					$"decode divergence [{label}] {variant} vs scalar " +
					$"at index {i} (word {i / 32}, bit {i % 32}): " +
					$"scalar={expected[i]}, {variant}={actual[i]}");
			}
		}
	}

	// ==== Input generation ==================================================

	private readonly struct DecodeCase
	{
		public readonly string Label;
		public readonly int[][] Planes;
		public readonly int[] Palette;
		public readonly int BitSize;

		public DecodeCase(string label, int[][] planes, int[] palette, int bitsize)
		{
			Label = label;
			Planes = planes;
			Palette = palette;
			BitSize = bitsize;
		}
	}

	private static IEnumerable<DecodeCase> BuildCases()
	{
		// Two independent seeds are cheaper than running the test many times and cover more patterns
		// of mixed words, where the main transpose/gather logic actually lives.
		foreach (int seed in new[] { 0x5EED_5EED, 0x0BADCAFE })
		{
			var rng = new Random(seed);

			for (int bitsize = BitsizeMin; bitsize <= BitsizeMax; bitsize++)
			{
				int paletteLength = 1 << bitsize;
				int[] palette = new int[paletteLength];
				for (int i = 0; i < paletteLength; i++)
				{
					palette[i] = NextFullRangeInt(rng);
				}

				// Completely random planes cover every edge case of mixed words on both SIMD branches.
				yield return new DecodeCase(
					$"seed=0x{seed:X8} bitsize={bitsize} random planes",
					BuildPlanes(bitsize, _ => NextFullRangeInt(rng)),
					palette,
					bitsize);

				// All planes = 0: decoding must yield palette[0] everywhere.
				yield return new DecodeCase(
					$"seed=0x{seed:X8} bitsize={bitsize} all-zero planes",
					BuildPlanes(bitsize, _ => 0),
					palette,
					bitsize);

				// All planes = -1: decoding must yield palette[2^bitsize - 1] everywhere;
				// this also hits the uniform-word fast path on both SIMD branches.
				yield return new DecodeCase(
					$"seed=0x{seed:X8} bitsize={bitsize} all-ones planes",
					BuildPlanes(bitsize, _ => -1),
					palette,
					bitsize);

				// Per-word uniform planes: each 32-block word yields one palette index, so a single
				// pass exercises both the uniform path and the mixed path.
				yield return new DecodeCase(
					$"seed=0x{seed:X8} bitsize={bitsize} per-word uniform",
					BuildPerWordUniformPlanes(bitsize, rng),
					palette,
					bitsize);
			}

			// Stratum: non-power-of-two palette lengths. These are the shapes Decompress
			// actually produces (palette.Length is whatever the saved data carried; bitsize
			// is derived from it as floor(log2(...))), and the only shapes that actually test
			// the "1 << bitsize <= palette.Length" invariant the tiered dispatch in
			// DecodePlanesAvx512/Avx2 relies on, rather than assuming it. Every prior case built
			// palette.Length as exactly 1 << bitsize, which is the one shape where the tiers
			// cannot be wrong regardless of whether the invariant holds.
			foreach ((int paletteLength, int bitsizeForLength) in new[]
					 {
						 (6, 2),   // Perm tier (palLen<=16); palette shorter than 1<<bitsize (4).
						 (48, 5),  // Perm2x2 tier (32<palLen<=64); indices from num=5 only reach 31.
						 (20, 4),  // Perm2 tier (16<palLen<=32); num=4 keeps indices within the first 16.
					 })
			{
				int[] palette = new int[paletteLength];
				for (int i = 0; i < paletteLength; i++) palette[i] = NextFullRangeInt(rng);

				yield return new DecodeCase(
					$"seed=0x{seed:X8} bitsize={bitsizeForLength} palLen={paletteLength} (non-power-of-two)",
					BuildPlanes(bitsizeForLength, _ => NextFullRangeInt(rng)),
					palette,
					bitsizeForLength);
			}
		}
	}

	private static int[][] BuildPlanes(int bitsize, Func<int, int> wordFactory)
	{
		var planes = new int[bitsize][];
		for (int k = 0; k < bitsize; k++)
		{
			planes[k] = new int[PlaneLength];
			for (int w = 0; w < PlaneLength; w++)
			{
				planes[k][w] = wordFactory(k);
			}
		}
		return planes;
	}

	private static int[][] BuildPerWordUniformPlanes(int bitsize, Random rng)
	{
		var planes = new int[bitsize][];
		for (int k = 0; k < bitsize; k++) planes[k] = new int[PlaneLength];

		for (int w = 0; w < PlaneLength; w++)
		{
			int index = rng.Next(1 << bitsize);
			for (int k = 0; k < bitsize; k++)
			{
				planes[k][w] = ((index >> k) & 1) != 0 ? -1 : 0;
			}
		}
		return planes;
	}

	private static int NextFullRangeInt(Random rng)
	{
		// Random.Next() is non-negative; two long ranges cover the whole int range so that the
		// sign-extension paths in SIMD are exercised too.
		return unchecked((int)rng.NextInt64(int.MinValue, (long)int.MaxValue + 1));
	}

	// ==== Scalar reference =================================================
	/// <summary>
	/// Matches the scalar fallback in CopyBlocksToUnsafe exactly, including its null-plane
	/// guard (`plane != null ? plane[word] : 0`). A null plane is the one input shape where
	/// a naive `planes[k][word]` oracle and the shipped fallback could genuinely disagree —
	/// e.g. after CleanUpPalette frees a plane down to null without shrinking bitsize in the
	/// same step — so the oracle must express the same guard, not assume every plane is populated.
	/// </summary>
	private static int[] ScalarDecode(int[][] planes, int[] palette, int num)
	{
		var output = new int[OutputLength];
		int outIdx = 0;

		for (int word = 0; word < PlaneLength; word++)
		{
			for (int j = 0; j < 32; j++)
			{
				int index = 0;
				for (int k = 0; k < num; k++)
				{
					int[] plane = planes[k];
					int v = plane != null ? plane[word] : 0;
					index |= ((v >> j) & 1) << k;
				}
				output[outIdx++] = index < palette.Length ? palette[index] : 0;
			}
		}

		return output;
	}
}
