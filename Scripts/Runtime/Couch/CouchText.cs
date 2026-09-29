using System.Globalization;
using System.Text.RegularExpressions;

namespace CouchSpire.Scripts.Runtime.Couch;

internal static partial class CouchText
{
    /// <summary>
    /// Strips the game's rich-text tags ([gold]...[/gold], [energy:1]...) for plain labels. Inline icons
    /// ([img]res://.../ironclad_energy_icon.png[/img]) become a word ("Energy"); a run of identical icons
    /// becomes a count ("2 Energy") so the path never leaks into the label.
    /// </summary>
    public static string Plain(string richText)
    {
        string withIcons = IconRun().Replace(richText, match =>
        {
            string word = IconWord(match.Groups["name"].Value);
            int count = match.Groups["img"].Captures.Count;
            return count > 1 ? $"{count} {word}" : word;
        });
        return RichTextTag().Replace(withIcons, "").Trim();
    }

    // "ironclad_energy" -> "Energy", "gold" -> "Gold".
    private static string IconWord(string iconName)
    {
        string last = iconName[(iconName.LastIndexOf('_') + 1)..];
        return last.Length == 0 ? "" : char.ToUpper(last[0], CultureInfo.InvariantCulture) + last[1..];
    }

    [GeneratedRegex(@"\[/?[A-Za-z_][^\]]*\]")]
    private static partial Regex RichTextTag();

    // One or more consecutive identical [img]...[/img] icons (optionally space-separated).
    [GeneratedRegex(@"(?<img>\[img[^\]]*\][^\[]*?/(?<name>[A-Za-z0-9_]+?)(?:_icon)?\.[A-Za-z]+\[/img\])(?:\s*(?<img>\[img[^\]]*\][^\[]*?/\k<name>(?:_icon)?\.[A-Za-z]+\[/img\]))*")]
    private static partial Regex IconRun();
}
