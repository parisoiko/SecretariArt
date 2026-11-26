namespace SecretariArt.Services;

// Class times are wall-clock times at the school ("Thursday 18:00"), and due dates are school dates, so "now" for
// check-ins and billing is the school's local time, whatever the server's own time zone is. One zone for the whole
// platform for now (the organisations are in Greece); a per-organisation zone can come with the first school abroad.
public static class SchoolTime
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

    public static DateTime SchoolNow(this TimeProvider clock) => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime;

    public static DateOnly SchoolToday(this TimeProvider clock) => DateOnly.FromDateTime(clock.SchoolNow());

    public static DateOnly FirstOfMonth(this DateOnly date) => new(date.Year, date.Month, 1);

    // The given day of the month, or its last day in a shorter month.
    public static DateOnly Day(this DateOnly month, int day) =>
        new(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)));
}
