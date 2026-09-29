using System.Globalization;
using System.Text;

namespace VctHub.Web.Data;

public static class Slug
{
    /// <summary>"VCT 2026: Americas Stage 2" -> "vct-2026-americas-stage-2", "LEVIATÁN" -> "leviatan"</summary>
    public static string Make(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
