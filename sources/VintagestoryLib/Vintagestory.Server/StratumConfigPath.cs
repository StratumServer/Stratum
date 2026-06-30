using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Vintagestory.Server;

// Reflection-driven walker over the StratumConfig tree. Powers /stratum get and
// /stratum set so operators can tune the config from in-game without editing
// stratum.json by hand. Walks public instance properties only; lookup is
// case-insensitive on the dotted path (e.g. "performance.pregen.pausebelowtps").
// Only scalar leaf types are settable from chat: bool, integer, floating-point,
// string, enum. Lists/dicts must still be edited in the JSON.
internal static class StratumConfigPath
{
	public sealed class ResolveResult
	{
		public object? Owner;
		public PropertyInfo? Property;
		public object? Value;
		public string? Error;
		public string PathBuilt = "";
		public bool IsLeaf => Property != null && IsScalar(Property.PropertyType);
	}

	public static ResolveResult Resolve(object root, string path)
	{
		ResolveResult r = new() { Owner = root, Value = root, PathBuilt = "" };
		if (string.IsNullOrWhiteSpace(path)) return r;

		string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
		object current = root;
		StringBuilder built = new();

		for (int i = 0; i < parts.Length; i++)
		{
			string segment = parts[i];
			PropertyInfo? prop = current.GetType()
				.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.FirstOrDefault(p => string.Equals(p.Name, segment, StringComparison.OrdinalIgnoreCase));
			if (prop == null)
			{
				r.Error = "no property '" + segment + "' on " + current.GetType().Name +
					". Available: " + string.Join(", ", ListPropertyNames(current));
				return r;
			}

			if (built.Length > 0) built.Append('.');
			built.Append(prop.Name);

			object? next;
			try { next = prop.GetValue(current); }
			catch (Exception ex) { r.Error = "could not read '" + prop.Name + "': " + ex.Message; return r; }

			if (i == parts.Length - 1)
			{
				r.Owner = current;
				r.Property = prop;
				r.Value = next;
				r.PathBuilt = built.ToString();
				return r;
			}

			if (next == null)
			{
				r.Error = "'" + built + "' is null; cannot descend";
				return r;
			}

			current = next;
		}

		r.Value = current;
		r.PathBuilt = built.ToString();
		return r;
	}

	public static bool TrySet(PropertyInfo prop, object owner, string rawValue, out string message)
	{
		Type t = prop.PropertyType;
		Type underlying = Nullable.GetUnderlyingType(t) ?? t;

		try
		{
			object? converted;
			if (underlying.IsEnum)
			{
				converted = Enum.Parse(underlying, rawValue, ignoreCase: true);
			}
			else if (underlying == typeof(bool))
			{
				converted = ParseBool(rawValue);
			}
			else if (underlying == typeof(string))
			{
				converted = rawValue;
			}
			else if (IsScalar(underlying))
			{
				converted = Convert.ChangeType(rawValue, underlying, CultureInfo.InvariantCulture);
			}
			else
			{
				message = "type " + underlying.Name + " is not settable from chat; edit stratum.json";
				return false;
			}

			prop.SetValue(owner, converted);
			message = "set " + prop.Name + " = " + FormatValue(converted);
			return true;
		}
		catch (Exception ex)
		{
			message = "could not set '" + prop.Name + "' to '" + rawValue + "': " + ex.Message;
			return false;
		}
	}

	public static string DescribeObject(object value, string path)
	{
		StringBuilder sb = new();
		sb.AppendLine(string.IsNullOrEmpty(path) ? "(root)" : path);
		foreach (PropertyInfo prop in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.OrderBy(p => p.Name))
		{
			object? v;
			try { v = prop.GetValue(value); } catch { v = "(unreadable)"; }
			string typeTag = TypeTag(prop.PropertyType);
			string display = IsScalar(prop.PropertyType) || prop.PropertyType == typeof(string)
				? FormatValue(v)
				: (v == null ? "null" : "[" + prop.PropertyType.Name + "]");
			sb.Append("  ").Append(prop.Name).Append(" (").Append(typeTag).Append(") = ").AppendLine(display);
		}
		return sb.ToString().TrimEnd();
	}

	public static string FormatValue(object? value)
	{
		if (value == null) return "null";
		if (value is bool b) return b ? "true" : "false";
		if (value is string s) return "\"" + s + "\"";
		if (value is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
		if (value is IEnumerable && value is not string)
		{
			List<string> parts = new();
			foreach (object item in (IEnumerable)value) parts.Add(item?.ToString() ?? "null");
			return "[" + string.Join(", ", parts) + "]";
		}
		return value.ToString() ?? "";
	}

	public static bool IsScalar(Type t)
	{
		Type u = Nullable.GetUnderlyingType(t) ?? t;
		return u.IsEnum || u == typeof(string) || u == typeof(bool)
			|| u == typeof(byte) || u == typeof(sbyte)
			|| u == typeof(short) || u == typeof(ushort)
			|| u == typeof(int) || u == typeof(uint)
			|| u == typeof(long) || u == typeof(ulong)
			|| u == typeof(float) || u == typeof(double) || u == typeof(decimal);
	}

	private static string TypeTag(Type t)
	{
		Type u = Nullable.GetUnderlyingType(t) ?? t;
		if (u.IsEnum) return "enum " + u.Name;
		if (u == typeof(string)) return "string";
		if (u == typeof(bool)) return "bool";
		if (u == typeof(int) || u == typeof(uint) || u == typeof(short) || u == typeof(ushort) || u == typeof(byte) || u == typeof(sbyte)) return "int";
		if (u == typeof(long) || u == typeof(ulong)) return "long";
		if (u == typeof(float) || u == typeof(double) || u == typeof(decimal)) return "float";
		if (typeof(IEnumerable).IsAssignableFrom(u) && u != typeof(string)) return "list";
		return u.Name;
	}

	private static IEnumerable<string> ListPropertyNames(object value)
	{
		return value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => p.Name)
			.OrderBy(n => n);
	}

	private static bool ParseBool(string raw)
	{
		string v = raw.Trim().ToLowerInvariant();
		return v switch
		{
			"true" or "1" or "yes" or "on" or "y" => true,
			"false" or "0" or "no" or "off" or "n" => false,
			_ => throw new FormatException("expected true/false/on/off/yes/no/1/0"),
		};
	}
}
