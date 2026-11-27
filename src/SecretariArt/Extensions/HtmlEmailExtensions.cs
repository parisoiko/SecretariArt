using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace SecretariArt.Extensions;

public static class HtmlEmailExtensions
{
    // An email address that may wrap at its natural points (before "@" and each ".") rather than mid-word, for
    // narrow phone cards (QA N1). Each part is HTML-encoded; <wbr> only marks where a line may break.
    public static IHtmlContent Email(this IHtmlHelper html, string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return HtmlString.Empty;
        }

        var builder = new HtmlContentBuilder();
        int start = 0;
        for (int i = 1; i < email.Length; i++)
        {
            if (email[i] is '@' or '.')
            {
                builder.AppendHtml(HtmlEncoder.Default.Encode(email[start..i])).AppendHtml("<wbr>");
                start = i;
            }
        }
        return builder.AppendHtml(HtmlEncoder.Default.Encode(email[start..]));
    }
}
