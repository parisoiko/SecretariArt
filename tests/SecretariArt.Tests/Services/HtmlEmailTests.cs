using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using SecretariArt.Extensions;

namespace SecretariArt.Tests.Services;

public class HtmlEmailTests
{
    [Theory]
    [InlineData("puppets@ioannina.test.app", "puppets<wbr>@ioannina<wbr>.test<wbr>.app")]
    [InlineData("<b>@x", "&lt;b&gt;<wbr>@x")]
    [InlineData("", "")]
    public void Email_MayWrapBeforeTheAtAndEachDot_AndIsEncoded(string email, string expected)
    {
        IHtmlContent content = HtmlEmailExtensions.Email(null!, email);
        using var writer = new StringWriter();
        content.WriteTo(writer, HtmlEncoder.Default);

        Assert.Equal(expected, writer.ToString());
    }
}
