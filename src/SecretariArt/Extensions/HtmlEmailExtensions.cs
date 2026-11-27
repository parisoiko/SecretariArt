using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace SecretariArt.Extensions;

public static class HtmlEmailExtensions
{
    // An email address in the pieces between which a line may break: before "@" and before each ".", rather than
    // mid-word, for narrow phone cards (QA N1). "puppets@ioannina.app" → "puppets", "@ioannina", ".app".
    public static IReadOnlyList<string> BreakPoints(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return [];
        }

        var parts = new List<string>();
        int start = 0;
        for (int i = 1; i < email.Length; i++)
        {
            if (email[i] is '@' or '.')
            {
                parts.Add(email[start..i]);
                start = i;
            }
        }
        parts.Add(email[start..]);
        return parts;
    }

    // MVC views: @Html.Email(...). Each piece is HTML-encoded; <wbr> only marks where a line may break.
    // Blazor pages use <EmailText Value="..." /> (Components/Shared/EmailText.razor) instead.
    public static IHtmlContent Email(this IHtmlHelper html, string? email)
    {
        var builder = new HtmlContentBuilder();
        IReadOnlyList<string> parts = BreakPoints(email);
        for (int i = 0; i < parts.Count; i++)
        {
            builder.AppendHtml(HtmlEncoder.Default.Encode(parts[i]));
            if (i < parts.Count - 1)
            {
                builder.AppendHtml("<wbr>");
            }
        }
        return builder;
    }
}
