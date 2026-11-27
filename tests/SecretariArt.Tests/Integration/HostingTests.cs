using System.Net;

namespace SecretariArt.Tests.Integration;

[Collection(IntegrationTests.Name)]
public class HostingTests(SecretariArtFactory factory)
{
    [Fact]
    public async Task Health_AnswersWithoutSigningIn_WhenTheDatabaseIsReachable()
    {
        HttpResponseMessage response = await factory.CreateBrowser(followRedirects: false).GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
