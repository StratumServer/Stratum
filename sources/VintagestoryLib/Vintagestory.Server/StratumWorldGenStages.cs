namespace Vintagestory.Server;

// Stratum: internal worldgen stage numbering. The public EnumWorldGenPass keeps
// vanilla values (Done = 6) because compiled mods inline enum constants at
// their compile time; changing the public numbering silently shifts every
// registration and comparison inside mod DLLs built against vanilla. The
// scheduler runs the Terrain pass as two internal sub-stages, so internal
// stage numbers run 0..7 with Done = 7. ServerMapChunk translates at the
// enum-typed property and persistence boundaries; scheduler code works in
// raw ints from this class.
internal static class StratumWorldGenStages
{
	public const int None = 0;
	public const int Terrain = 1;       // vanilla Terrain, first half (3d terrain)
	public const int TerrainLate = 2;   // vanilla Terrain, second half (rock strata, caves, block layers)
	public const int TerrainFeatures = 3;
	public const int Vegetation = 4;
	public const int NeighbourSunLightFlood = 5;
	public const int PreDone = 6;
	public const int Done = 7;

	// Vanilla numbering to internal numbering and back. Monotone, so order
	// comparisons survive translation; Terrain and TerrainLate both map to
	// vanilla Terrain, which is also the on-disk numbering.
	public static int FromVanilla(int vanillaPass)
	{
		if (vanillaPass <= Terrain)
		{
			return vanillaPass;
		}
		return vanillaPass + 1;
	}

	public static int ToVanilla(int internalPass)
	{
		if (internalPass <= Terrain)
		{
			return internalPass;
		}
		return internalPass - 1;
	}
}
