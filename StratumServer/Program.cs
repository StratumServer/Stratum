using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Vintagestory.Server;

namespace StratumServer;

internal static class Program
{
	private const string ServerAssemblyName = "VintagestoryLib";
	private const string ServerProgramTypeName = "Vintagestory.Server.ServerProgram";
	private static readonly HashSet<Assembly> NativeResolverAssemblies = new();
	private static string[] nativeSearchPaths = Array.Empty<string>();

	private static int Main(string[] args)
	{
		if (HasOption(args, "--stratum-version"))
		{
			PrintVersion();
			return 0;
		}

		if (HasOption(args, "--stratum-help"))
		{
			PrintUsage();
			return 0;
		}

		bool refresh = HasOption(args, "--stratum-refresh");
		bool skipBootstrap = HasOption(args, "--stratum-skip-bootstrap");
		string[] serverArgs = RemoveOption(args, "--stratum-no-banner", "--stratum-refresh", "--stratum-skip-bootstrap");
		bool printBanner = !HasOption(args, "--stratum-no-banner");
		serverArgs = AddDefaultDataPath(serverArgs, out string defaultDataPathAdded);
		if (printBanner)
		{
			PrintBanner(defaultDataPathAdded);
		}

		if (!skipBootstrap)
		{
			try
			{
				VanillaBootstrap.EnsureVanillaAssets(StratumInfo.BaseGameVersion, refresh);
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine($"Stratum: vanilla asset bootstrap failed: {exception.Message}");
				Console.Error.WriteLine("Pass --stratum-skip-bootstrap to launch anyway if the assets are already in place.");
				return 1;
			}
		}

		return LaunchServer(serverArgs);
	}

	private static int LaunchServer(string[] args)
	{
		try
		{
			ConfigureNativeDependencyResolution();
			string serverAssemblyPath = Path.Combine(AppContext.BaseDirectory, ServerAssemblyName + ".dll");
			Assembly serverAssembly = Assembly.LoadFrom(serverAssemblyPath);
			Type serverProgramType = serverAssembly.GetType(ServerProgramTypeName, throwOnError: true)!;
			MethodInfo mainMethod = serverProgramType.GetMethod("Main", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string[]) }, null)!;
			mainMethod.Invoke(null, new object[] { args });
			return 0;
		}
		catch (TargetInvocationException exception) when (exception.InnerException != null)
		{
			Console.Error.WriteLine(exception.InnerException);
			return 1;
		}
		catch (FileNotFoundException exception)
		{
			Console.Error.WriteLine($"Unable to find {ServerAssemblyName}.dll in {AppContext.BaseDirectory}.");
			Console.Error.WriteLine(exception.Message);
			return 1;
		}
	}

	private static void ConfigureNativeDependencyResolution()
	{
		nativeSearchPaths = GetNativeSearchPaths();
		PrependProcessPath(nativeSearchPaths);
		AppDomain.CurrentDomain.AssemblyLoad += (_, args) => TrySetSkiaSharpNativeResolver(args.LoadedAssembly);

		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			TrySetSkiaSharpNativeResolver(assembly);
		}
	}

	private static string[] GetNativeSearchPaths()
	{
		string baseDirectory = AppContext.BaseDirectory;
		string runtimeNativeDirectory = Path.Combine(baseDirectory, "runtimes", GetRuntimeIdentifier(), "native");

		return new[]
		{
			baseDirectory,
			Path.Combine(baseDirectory, "Lib"),
			runtimeNativeDirectory
		}
		.Where(Directory.Exists)
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.ToArray();
	}

	private static string GetRuntimeIdentifier()
	{
		string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
			RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";

		string architecture = RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.X86 => "x86",
			Architecture.Arm64 => "arm64",
			Architecture.Arm => "arm",
			_ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
		};

		return $"{os}-{architecture}";
	}

	private static void PrependProcessPath(string[] paths)
	{
		string pathVariable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "PATH" :
			RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";
		string separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";
		string currentValue = Environment.GetEnvironmentVariable(pathVariable) ?? string.Empty;
		HashSet<string> existingPaths = currentValue.Split(separator, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
		string[] missingPaths = paths.Where(path => !existingPaths.Contains(path)).ToArray();

		if (missingPaths.Length > 0)
		{
			Environment.SetEnvironmentVariable(pathVariable, string.Join(separator, missingPaths.Concat(new[] { currentValue }).Where(value => value.Length > 0)));
		}
	}

	private static void TrySetSkiaSharpNativeResolver(Assembly assembly)
	{
		if (assembly.IsDynamic || !string.Equals(assembly.GetName().Name, "SkiaSharp", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		lock (NativeResolverAssemblies)
		{
			if (!NativeResolverAssemblies.Add(assembly))
			{
				return;
			}
		}

		try
		{
			NativeLibrary.SetDllImportResolver(assembly, ResolveNativeLibrary);
		}
		catch (InvalidOperationException)
		{
		}
	}

	private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
	{
		foreach (string directory in nativeSearchPaths)
		{
			foreach (string candidateName in GetNativeLibraryCandidateNames(libraryName))
			{
				string candidatePath = Path.Combine(directory, candidateName);
				if (File.Exists(candidatePath) && NativeLibrary.TryLoad(candidatePath, out IntPtr handle))
				{
					return handle;
				}
			}
		}

		return IntPtr.Zero;
	}

	private static IEnumerable<string> GetNativeLibraryCandidateNames(string libraryName)
	{
		yield return libraryName;

		if (Path.HasExtension(libraryName))
		{
			yield break;
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			yield return libraryName + ".dll";
			if (!libraryName.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
			{
				yield return "lib" + libraryName + ".dll";
			}
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			yield return "lib" + libraryName + ".dylib";
		}
		else
		{
			yield return "lib" + libraryName + ".so";
		}
	}

	private static bool HasOption(string[] args, string option)
	{
		return args.Any(arg => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase));
	}

	private static string[] RemoveOption(string[] args, params string[] options)
	{
		HashSet<string> drop = new(options, StringComparer.OrdinalIgnoreCase);
		return args.Where(arg => !drop.Contains(arg)).ToArray();
	}

	private static string[] AddDefaultDataPath(string[] args, out string defaultDataPath)
	{
		defaultDataPath = string.Empty;
		if (HasDataPathOption(args))
		{
			return args;
		}

		defaultDataPath = Path.Combine(AppContext.BaseDirectory, "Data");
		Directory.CreateDirectory(defaultDataPath);
		return args.Concat(new[] { "--dataPath", defaultDataPath }).ToArray();
	}

	private static bool HasDataPathOption(string[] args)
	{
		return args.Any(arg =>
			string.Equals(arg, "--dataPath", StringComparison.OrdinalIgnoreCase) ||
			arg.StartsWith("--dataPath=", StringComparison.OrdinalIgnoreCase));
	}

	private static void PrintBanner(string defaultDataPath)
	{
		Console.WriteLine($"Starting {StratumInfo.FullName} ({StratumInfo.ProtocolMode})");
		if (defaultDataPath.Length > 0)
		{
			Console.WriteLine($"Data path: {defaultDataPath}");
		}
	}

	private static void PrintVersion()
	{
		Console.WriteLine(StratumInfo.FullName);
		Console.WriteLine($"Base game: Vintage Story {StratumInfo.BaseGameVersion}");
		Console.WriteLine($"Protocol mode: {StratumInfo.ProtocolMode}");
	}

	private static void PrintUsage()
	{
		PrintVersion();
		Console.WriteLine();
		Console.WriteLine("Stratum options:");
		Console.WriteLine("  --stratum-version          Print Stratum version information and exit");
		Console.WriteLine("  --stratum-help             Print Stratum launcher options and exit");
		Console.WriteLine("  --stratum-no-banner        Start without printing the Stratum banner");
		Console.WriteLine("  --stratum-refresh          Re-download and re-extract vanilla assets");
		Console.WriteLine("  --stratum-skip-bootstrap   Skip the first-run vanilla asset bootstrap");
		Console.WriteLine();
		Console.WriteLine("If --dataPath is omitted, Stratum uses the local Data folder next to StratumServer.exe.");
		Console.WriteLine("All other arguments are passed through to the Stratum server core.");
	}
}
