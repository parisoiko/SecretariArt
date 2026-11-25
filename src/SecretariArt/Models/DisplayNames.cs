namespace SecretariArt.Models;

// Human-readable names for enums shown on pages.
public static class DisplayNames
{
    public static string Display(this OrganizationType type) => type switch
    {
        OrganizationType.DramaSchool => "Drama school",
        OrganizationType.Conservatoire => "Conservatoire",
        OrganizationType.DanceSchool => "Dance school",
        OrganizationType.MusicSchool => "Music school",
        OrganizationType.Atelier => "Atelier",
        OrganizationType.RehearsalSpace => "Rehearsal space",
        _ => "Other",
    };
}
