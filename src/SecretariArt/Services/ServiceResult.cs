namespace SecretariArt.Services;

// The outcome of a user action that can fail for a reason worth showing (e.g. "That invite code doesn't exist.").
// A success can carry a message too, when the service knows details the controller doesn't (e.g. whose request it was),
// and the id of what it created, so the controller can go straight to it.
public record ServiceResult(bool Succeeded, string? Error = null, string? Message = null, int? CreatedId = null)
{
    public static ServiceResult Ok(string? message = null, int? createdId = null) => new(true, Message: message, CreatedId: createdId);

    public static ServiceResult Fail(string error) => new(false, error);
}
