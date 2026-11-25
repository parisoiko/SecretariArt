using Microsoft.AspNetCore.Identity;

namespace SecretariArt.Models;

// One account per person. What someone can do in an organisation comes from their memberships, not from the account.
public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";

    public string FullName => $"{FirstName} {LastName}";

    public List<Membership> Memberships { get; set; } = [];
}
