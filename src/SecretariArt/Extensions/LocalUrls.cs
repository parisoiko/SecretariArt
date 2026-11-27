namespace SecretariArt.Extensions;

// returnUrl comes from the query string and could point anywhere: only ever follow it inside the site. The same rule
// as MVC's Url.IsLocalUrl: "/path" or "~/path", but not "//host" or "/\host", which browsers treat as another site.
public static class LocalUrls
{
    public static bool IsLocal(string? url) => url switch
    {
        null or "" => false,
        "/" or "~/" => true,
        ['/', '/' or '\\', ..] => false,
        ['/', ..] => true,
        ['~', '/', '/' or '\\', ..] => false,
        ['~', '/', ..] => true,
        _ => false,
    };

    // Where to go after signing in or registering: the local returnUrl, or Home.
    public static string OrHome(string? url) => !IsLocal(url) ? "/" : url!.StartsWith('~') ? url[1..] : url;
}
