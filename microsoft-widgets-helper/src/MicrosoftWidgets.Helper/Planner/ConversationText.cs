using System.Text;
using HtmlAgilityPack;

namespace PlannerEdge.Helper.Planner;

public static class ConversationText
{
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "div", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "li", "main", "nav", "ol", "p", "pre", "section", "table", "tr", "ul"
    };

    public static string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(html);
        var text = new StringBuilder();
        Append(document.DocumentNode, text);

        var lines = HtmlEntity.DeEntitize(text.ToString())
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00a0', ' ')
            .Split('\n')
            .Select(line => CollapseSpaces(line).Trim())
            .ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var normalized = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Length > 0 || normalized.Count == 0 || normalized[^1].Length > 0)
                normalized.Add(line);
        }
        return string.Join('\n', normalized);
    }

    private static void Append(HtmlNode node, StringBuilder text)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            text.Append(node.InnerText);
            return;
        }
        if (node.Name.Equals("script", StringComparison.OrdinalIgnoreCase)
            || node.Name.Equals("style", StringComparison.OrdinalIgnoreCase))
            return;
        if (node.Name.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            AppendLineBreak(text);
            return;
        }

        var block = BlockElements.Contains(node.Name);
        if (block) AppendLineBreak(text);
        foreach (var child in node.ChildNodes) Append(child, text);
        if (block) AppendLineBreak(text);
    }

    private static void AppendLineBreak(StringBuilder text)
    {
        if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
    }

    private static string CollapseSpaces(string value)
    {
        var text = new StringBuilder(value.Length);
        var inWhitespace = false;
        foreach (var character in value)
        {
            if (character is ' ' or '\t' or '\f' or '\v')
            {
                if (!inWhitespace) text.Append(' ');
                inWhitespace = true;
            }
            else
            {
                text.Append(character);
                inWhitespace = false;
            }
        }
        return text.ToString();
    }
}
