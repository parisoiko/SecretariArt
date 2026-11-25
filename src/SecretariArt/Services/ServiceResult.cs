namespace SecretariArt.Services;

// The outcome of a user action that can fail for a reason worth showing (e.g. "That invite code doesn't exist.").
public record ServiceResult(bool Succeeded, string? Error = null)
{
    public static ServiceResult Ok() => new(true);

    public static ServiceResult Fail(string error) => new(false, error);
}
