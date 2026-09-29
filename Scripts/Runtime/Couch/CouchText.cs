using System.Text.RegularExpressions;

namespace CouchSpire.Scripts.Runtime.Couch;

internal static partial class CouchText
{
    /// <summary>Strips the game's rich-text tags ([gold]...[/gold], [energy:1]...) for plain labels.</summary>
    public static string Plain(string richText)
    {
        return RichTextTag().Replace(richText, "").Trim();
    }

    [GeneratedRegex(@"\[/?[A-Za-z_][^\]]*\]")]
    private static partial Regex RichTextTag();
}
