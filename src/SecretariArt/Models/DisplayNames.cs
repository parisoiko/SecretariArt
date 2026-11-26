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

    public static string Display(this FloorType floor) => floor switch
    {
        FloorType.SprungDanceFloor => "Sprung dance floor",
        FloorType.Stage => "Stage",
        FloorType.Carpet => "Carpet",
        FloorType.Concrete => "Concrete",
        _ => "Standard",
    };

    public static string Display(this RoomFeatures feature) => feature switch
    {
        RoomFeatures.SoundSystem => "Sound system",
        RoomFeatures.StageLighting => "Stage lighting",
        _ => feature.ToString(),
    };

    // Every single feature (no None, no combinations), in declaration order: for checkbox lists and badges.
    public static readonly RoomFeatures[] AllFeatures =
        Enum.GetValues<RoomFeatures>().Where(f => f != RoomFeatures.None).ToArray();

    public static IEnumerable<RoomFeatures> Split(this RoomFeatures features) =>
        AllFeatures.Where(f => features.HasFlag(f));

    public static readonly TeachingDays[] AllDays =
        Enum.GetValues<TeachingDays>().Where(d => d != TeachingDays.None).ToArray();

    // "Mon–Sat", "Mon, Wed, Fri": short enough for a summary line.
    public static string Display(this TeachingDays days)
    {
        var chosen = AllDays.Where(d => days.HasFlag(d)).Select(d => Array.IndexOf(AllDays, d)).ToList();
        if (chosen.Count == 0)
        {
            return "No days";
        }

        static string Short(int index) => AllDays[index].ToString()[..3];
        bool consecutive = chosen.Count > 2 && chosen[^1] - chosen[0] == chosen.Count - 1;
        return consecutive ? $"{Short(chosen[0])}–{Short(chosen[^1])}" : string.Join(", ", chosen.Select(Short));
    }

    // The week as people read a timetable: Monday first.
    public static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static TeachingDays ToTeachingDay(this DayOfWeek day) => (TeachingDays)(1 << (((int)day + 6) % 7));

    public static IEnumerable<DayOfWeek> Days(this TeachingDays days) => Week.Where(d => days.HasFlag(d.ToTeachingDay()));

    public static string Short(this DayOfWeek day) => day.ToString()[..3];

    // "1 course", "3 courses".
    public static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public static string Plural(int count, string noun, string plural) => $"{count} {(count == 1 ? noun : plural)}";

    // "1 h 30 min", "45 min", "2 h".
    public static string Minutes(int minutes) => (minutes / 60, minutes % 60) switch
    {
        (0, var m) => $"{m} min",
        (var h, 0) => $"{h} h",
        (var h, var m) => $"{h} h {m} min",
    };

    // The pages are lang="en", so CSS upper-casing would keep Greek accents ("ΘΕΣΣΑΛΟΝΊΚΗ"). Marking Greek text as
    // lang="el" lets the browser apply Greek rules ("ΘΕΣΣΑΛΟΝΙΚΗ"). Returns null (no attribute) for anything else.
    public static string? Language(this string? text) =>
        text != null && text.Any(c => c is >= 'Ͱ' and <= 'Ͽ' or >= 'ἀ' and <= '῿') ? "el" : null;
}
