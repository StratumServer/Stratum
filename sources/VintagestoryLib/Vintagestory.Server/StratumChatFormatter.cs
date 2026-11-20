using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal static class StratumChatFormatter
{
	private static readonly Regex PlainUrlRegex = new Regex(@"\b(?:https?://|www\.)[^\s<>'""\]]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public static string FormatMessageBody(string message)
	{
		StratumConfig config = StratumRuntime.Config;
		config.EnsurePopulated();

		if (!config.Chat.Enabled || !config.Chat.LinkifyUrls)
		{
			return message;
		}

		return LinkifyPlainUrls(message);
	}

	public static bool TryFormat(IServerPlayer player, string messageBody, out string formattedMessage)
	{
		formattedMessage = messageBody;
		StratumConfig config = StratumRuntime.Config;
		config.EnsurePopulated();
		StratumRolePrefixesConfig rolePrefixes = config.Appearance.RolePrefixes;

		if (!config.Chat.Enabled || player == null)
		{
			return false;
		}

		// Stratum #335: a group tag renders on its own, so a server can run group tags with role
		// prefixes switched off. Null unless the player holds a tagged group.
		string groupTag = null;
		StratumGroupsConfig groups = config.Groups;
		if (groups.Enabled && groups.ShowTagInChat)
		{
			string tag = StratumGroupPolicy.ResolveTag(player);
			if (tag != null)
			{
				groupTag = ApplyColorAndWeight(FormatTag(groups.TagFormat, tag), groups.TagColor, bold: false);
			}
		}

		List<StratumRolePrefixConfig> prefixes = rolePrefixes.Enabled && player.Role != null
			? rolePrefixes.ResolveFor(player.Role.Code)
			: null;
		if (prefixes == null && groupTag == null)
		{
			return false;
		}

		prefixes ??= new List<StratumRolePrefixConfig>();
		StringBuilder renderedTags = new StringBuilder();
		if (groupTag != null)
		{
			renderedTags.Append(prefixes.Count == 0 ? EnsureTrailingSpace(groupTag) : groupTag + " ");
		}

		for (int index = 0; index < prefixes.Count; index++)
		{
			StratumRolePrefixConfig prefix = prefixes[index];
			string tag = FormatTag(rolePrefixes.Format, prefix.Tag);
			// One space separates the whole stack from the name, so only the last tag carries it.
			if (index == prefixes.Count - 1)
			{
				tag = EnsureTrailingSpace(tag);
			}

			renderedTags.Append(ApplyColorAndWeight(tag, prefix.Color, prefix.Bold));
		}

		formattedMessage = renderedTags + "<strong>" + EscapeVtml(player.PlayerName) + ":</strong> " + messageBody;
		return true;
	}

	private static string LinkifyPlainUrls(string message)
	{
		if (string.IsNullOrEmpty(message))
		{
			return message;
		}

		StringBuilder builder = new StringBuilder(message.Length + 32);
		int position = 0;
		int anchorDepth = 0;

		while (position < message.Length)
		{
			if (message[position] == '<')
			{
				int tagEnd = message.IndexOf('>', position);
				if (tagEnd < 0)
				{
					builder.Append(LinkifyTextSegment(message.Substring(position)));
					break;
				}

				string tag = message.Substring(position, tagEnd - position + 1);
				builder.Append(tag);
				TrackAnchorTag(tag, ref anchorDepth);
				position = tagEnd + 1;
				continue;
			}

			int nextTag = message.IndexOf('<', position);
			if (nextTag < 0)
			{
				nextTag = message.Length;
			}

			string segment = message.Substring(position, nextTag - position);
			builder.Append(anchorDepth == 0 ? LinkifyTextSegment(segment) : segment);
			position = nextTag;
		}

		return builder.ToString();
	}

	private static void TrackAnchorTag(string tag, ref int anchorDepth)
	{
		string trimmed = tag.Trim();
		if (trimmed.StartsWith("<a ", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<a>", StringComparison.OrdinalIgnoreCase))
		{
			anchorDepth++;
			return;
		}

		if (trimmed.StartsWith("</a", StringComparison.OrdinalIgnoreCase) && anchorDepth > 0)
		{
			anchorDepth--;
		}
	}

	private static string LinkifyTextSegment(string text)
	{
		return PlainUrlRegex.Replace(text, match => BuildLink(match.Value));
	}

	private static string BuildLink(string rawUrl)
	{
		string url = rawUrl;
		string suffix = string.Empty;

		while (url.Length > 0 && IsTrailingUrlPunctuation(url[url.Length - 1]))
		{
			suffix = url[url.Length - 1] + suffix;
			url = url.Substring(0, url.Length - 1);
		}

		if (url.Length == 0)
		{
			return rawUrl;
		}

		string href = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
		return "<a href=\"" + EscapeVtmlAttribute(href) + "\">" + EscapeVtmlText(url) + "</a>" + suffix;
	}

	private static bool IsTrailingUrlPunctuation(char character)
	{
		return character == '.' || character == ',' || character == ';' || character == ':' || character == '!' || character == '?' || character == ')' || character == '}';
	}

	private static string EscapeVtmlAttribute(string value)
	{
		return value.Replace("\"", "%22").Replace("<", "%3C").Replace(">", "%3E");
	}

	private static string EscapeVtmlText(string value)
	{
		return value.Replace(">", "&gt;").Replace("<", "&lt;");
	}

	private static string EnsureTrailingSpace(string value)
	{
		return string.IsNullOrEmpty(value) || value.EndsWith(" ", StringComparison.Ordinal) ? value : value + " ";
	}

	private static string FormatTag(string format, string tag)
	{
		string safeFormat = string.IsNullOrWhiteSpace(format) ? "[{tag}]" : format;
		return safeFormat.Replace("{tag}", EscapeVtml(tag ?? "Staff"));
	}

	private static string ApplyColorAndWeight(string text, string color, bool bold)
	{
		string output = bold ? "<strong>" + text + "</strong>" : text;
		string safeColor = NormalizeColor(color);
		return safeColor == null ? output : "<font color=\"" + safeColor + "\">" + output + "</font>";
	}

	private static string NormalizeColor(string color)
	{
		if (string.IsNullOrWhiteSpace(color))
		{
			return null;
		}

		string value = color.Trim();
		if (value.Length == 7 && value[0] == '#' && value.Skip(1).All(IsHexDigit))
		{
			return value;
		}

		return value.All(character => char.IsLetter(character)) ? value : null;
	}

	private static bool IsHexDigit(char character)
	{
		return (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F');
	}

	private static string EscapeVtml(string value)
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

	public static string ColorizeVtml(string message, string color)
	{
		if (string.IsNullOrWhiteSpace(color))
		{
			return message;
		}
		return $"<font color='{color}'>{message}</font>";
	}
}
