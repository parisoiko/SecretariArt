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

    // The pages are lang="en", so CSS upper-casing would keep Greek accents ("ΘΕΣΣΑΛΟΝΊΚΗ"). Marking Greek text as
    // lang="el" lets the browser apply Greek rules ("ΘΕΣΣΑΛΟΝΙΚΗ"). Returns null (no attribute) for anything else.
    public static string? Language(this string? text) =>
        text != null && text.Any(c => c is >= 'Ͱ' and <= 'Ͽ' or >= 'ἀ' and <= '῿') ? "el" : null;
}
