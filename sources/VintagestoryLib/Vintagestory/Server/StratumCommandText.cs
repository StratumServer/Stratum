using System;
using System.Globalization;
using System.Text;

namespace Vintagestory.Server;

internal static class StratumCommandText
{
	public const string Accent = "#8bd5ff";
	public const string Good = "#9bd77e";
	public const string Warn = "#e6c15f";
	public const string Bad = "#e47d68";
	public const string Muted = "#9aa8b5";
	public const string Label = "#c9d6e2";

	public static string Title(string text)
	{
		return "<font color=\"" + Accent + "\"><strong>" + Escape(text) + "</strong></font>";
	}

	public static string Success(string text)
	{
		return "<font color=\"" + Good + "\"><strong>" + Escape(text) + "</strong></font>";
	}

	public static string Warning(string text)
	{
		return "<font color=\"" + Warn + "\"><strong>" + Escape(text) + "</strong></font>";
	}

	public static string Danger(string text)
	{
		return "<font color=\"" + Bad + "\"><strong>" + Escape(text) + "</strong></font>";
	}

	public static string Empty(string text)
	{
		return "<font color=\"" + Muted + "\">" + Escape(text) + "</font>";
	}

	public static string Info(string text)
	{
		return "<font color=\"" + Accent + "\">" + Escape(text) + "</font>";
	}

	public static string Confirm(string action, string detail = null)
	{
		return Success(action) + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + Escape(detail));
	}

	public static string Pill(string text, string color)
	{
		return "<font color=\"" + color + "\"><strong>[" + Escape(text) + "]</strong></font>";
	}

	public static string Row(string label, string value)
	{
		return "\n<font color=\"" + Label + "\">" + Escape(label) + ":</font> " + Escape(value);
	}

	public static string RawRow(string label, string value)
	{
		return "\n<font color=\"" + Label + "\">" + Escape(label) + ":</font> " + (value ?? string.Empty);
	}

	public static string Bullet(string title, string value)
	{
		return "\n<font color=\"" + Accent + "\">- " + Escape(title) + "</font> " + Escape(value);
	}

	public static string AuditField(string key, object value)
	{
		return key + "=" + AuditValue(Convert.ToString(value, CultureInfo.InvariantCulture));
	}

	public static string AuditValue(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "none";
		}

		string normalized = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Replace("{", "{{").Replace("}", "}}").Trim();
		return normalized.IndexOf(' ') >= 0 ? "\"" + normalized.Replace("\"", "'") + "\"" : normalized;
	}

	public static string Escape(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}

		StringBuilder builder = new StringBuilder(value.Length);
		foreach (char character in value)
		{
			switch (character)
			{
				case '&':
					builder.Append("&amp;");
					break;
				case '<':
					builder.Append("&lt;");
					break;
				case '>':
					builder.Append("&gt;");
					break;
				default:
					builder.Append(character);
					break;
			}
		}

		return builder.ToString();
	}
}