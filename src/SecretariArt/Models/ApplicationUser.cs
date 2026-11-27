using Microsoft.AspNetCore.Identity;

namespace SecretariArt.Models;

// One account per person. What someone can do in an organisation comes from their memberships, not from the account.
public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";

    public string FullName => $"{FirstName} {LastName}";

    // "ΑΠ" for Ανδρέας Παπαδόπουλος: the round avatar in lists and the header.
    public string Initials => string.Concat(FirstName.Trim().Take(1).Concat(LastName.Trim().Take(1))).ToUpperInvariant();

    public List<Membership> Memberships { get; set; } = [];
}
