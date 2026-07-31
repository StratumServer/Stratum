using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace Vintagestory.Server;

// Shared answer to "is third-party code involved in this path".
// Stratum reshapes enough that mods can observe it in a lot of places, and each one needs the
// same question settled before it turns an optimization on. Ask here, then fall back to the
// vanilla shape and say so through ReportFallback so every subsystem reports it the same way.
internal static class StratumModCompat
{
	// Assemblies this build ships. Anything else is code Stratum has no way to verify.
	public static readonly HashSet<string> ShippedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"VintagestoryLib", "VSEssentials", "VSSurvivalMod", "VSCreativeMod", "VintagestoryAPI"
	};

	// Name of the assembly behind the first foreign handler, or null when they are all ours.
	// Unknown counts as foreign, since a handler with no resolvable assembly is not ours either.
	public static string FindForeignHandler(IEnumerable<Delegate> handlers)
	{
		if (handlers == null) return null;

		foreach (Delegate handler in handlers)
		{
			string assemblyName = handler?.Method?.DeclaringType?.Assembly?.GetName()?.Name;
			if (assemblyName == null || !ShippedAssemblies.Contains(assemblyName))
			{
				return assemblyName ?? "<unknown>";
			}
		}

		return null;
	}

	// First mod-patched method under this namespace, described for a log, or null when clean.
	// Catches mods that register nothing but patch code a Stratum fast path runs through.
	public static string FindPatchedMethodUnder(string namespacePrefix)
	{
		foreach (MethodBase method in Harmony.GetAllPatchedMethods())
		{
			Type declaringType = method?.DeclaringType;
			if (declaringType?.Namespace == null || !declaringType.Namespace.StartsWith(namespacePrefix, StringComparison.Ordinal))
			{
				continue;
			}

			string assemblyName = declaringType.Assembly?.GetName()?.Name;
			if (assemblyName == null || !ShippedAssemblies.Contains(assemblyName)) continue;

			string patches = DescribeModPatches(method);
			if (patches != null)
			{
				return declaringType.Name + "." + method.Name + " (" + patches + ")";
			}
		}

		return null;
	}

	// Every mod-owned Harmony patch on one method, or null when there are none. Stratum compiles
	// its own changes in and applies no Harmony patches, so anything here is third-party.
	public static string DescribeModPatches(MethodBase method)
	{
		if (method == null) return null;

		Patches info = Harmony.GetPatchInfo(method);
		if (info == null) return null;

		StringBuilder sb = null;
		AppendPatchOwners(ref sb, info.Transpilers, "transpiler");
		AppendPatchOwners(ref sb, info.Prefixes, "prefix");
		AppendPatchOwners(ref sb, info.Postfixes, "postfix");
		AppendPatchOwners(ref sb, info.Finalizers, "finalizer");
		return sb?.ToString();
	}

	// One message shape for every subsystem that gives up a speedup to stay compatible.
	public static void ReportFallback(string subsystem, string feature, string reason, string overrideSetting)
	{
		StratumRuntime.LogWarning(
			subsystem + ": " + feature + " is off because " + reason + ". "
			+ "Behavior stays correct and vanilla-compatible, only the speedup is given up. "
			+ "Set " + overrideSetting + " to false in stratum.json to force it on anyway.");
	}

	private static void AppendPatchOwners(ref StringBuilder sb, IReadOnlyCollection<Patch> patches, string kind)
	{
		if (patches == null) return;

		foreach (Patch patch in patches)
		{
			if (sb == null) sb = new StringBuilder();
			else sb.Append(", ");

			sb.Append(kind).Append(" from '")
				.Append(string.IsNullOrWhiteSpace(patch.owner) ? "(unknown mod)" : patch.owner)
				.Append('\'');
		}
	}
}
