using System.Security.Cryptography;

namespace SecretariArt.Services;

public interface IInviteCodeGenerator
{
    string NewCode();
}

// Short codes people can read out or type: no 0/O or 1/I/L to confuse, cryptographically random so they can't be guessed.
public class InviteCodeGenerator : IInviteCodeGenerator
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 8;

    public string NewCode() => RandomNumberGenerator.GetString(Alphabet, Length);

    // Codes are shown in capitals, but people may type them in any case or with spaces.
    public static string Normalize(string code) => code.Trim().Replace(" ", "").Replace("-", "").ToUpperInvariant();
}
