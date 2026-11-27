using System.Diagnostics;
using Google.OrTools.Sat;
using Google.OrTools.Util;
using SecretariArt.Models;

namespace SecretariArt.Services;

// A class the generator placed, before it's a ClassSession.
public record PlannedSession(int GroupId, int ClassroomId, DayOfWeek Day, TimeOnly Starts, TimeOnly Ends);

// Sessions it couldn't place for a group, and why.
public record PlanGap(int GroupId, int Missing, string Reason);

public record TimetablePlan(IReadOnlyList<PlannedSession> Sessions, IReadOnlyList<PlanGap> Gaps, bool Optimal, int Milliseconds);

// Works out a weekly timetable with Google OR-Tools CP-SAT: every session a time and a room, keeping all the rules the
// editor refuses to break (ScheduleChecker's problems) and avoiding, as far as it can, what it only warns about.
//
// Time is one axis for the whole week, in minutes from Monday 00:00, so "two classes at once" is two intervals that
// overlap, whatever the day. Each session is an optional interval (placing as many as possible is the first aim), with
// a start chosen from the times the Rules allow and one of the rooms that suit the course. No-overlap constraints keep
// apart the classes of each room (with its changeover time), each teacher (also against what they teach elsewhere in
// the same weeks), each group and each student.
public static class TimetableGenerator
{
    private const int MinutesPerDay = 24 * 60;

    // What each kind of compromise costs, in the order the product owner set (27 Nov 2026), most serious first:
    //   1. a class outside the teacher's posted availability;
    //   2. a teacher over the daily teaching-hours limit (per minute: 15 minutes over outweighs a small room);
    //   3. a room smaller than the group's places;
    //   4. a group meeting twice on one day (per class beyond the first);
    //   5. a start off the hour;
    //   6. a student with a class at another organisation then (per student);
    //   7. gently, an uneven week (for a teacher and for the organisation; per minute, so an hour of it stays below one
    //      student's clash) and the last hour of the day.
    // Placing a session outweighs everything else together.
    private const int UnplacedCost = 10_000_000;
    private const int OutsideAvailabilityCost = 30_000;
    private const int OverDailyHoursCostPerMinute = 200;
    private const int SmallRoomCost = 2500;
    private const int SameDayCost = 1500;
    private const int OffTheHourCost = 600;
    private const int StudentElsewhereCostPerStudent = 300;
    private const int UnevenWeekCostPerMinute = 1;
    private const int LastHourCost = 50;

    public static TimetablePlan Plan(ScheduleContext context, GenerationMode mode, TimeSpan timeLimit)
    {
        var watch = Stopwatch.StartNew();
        OrganizationRules rules = context.Rules;
        var kept = mode == GenerationMode.FillGaps ? context.Sessions.ToList() : [];
        int changeover = rules.BreakBetweenSessionsMinutes;

        var model = new CpModel();
        var objective = LinearExpr.NewBuilder();
        var sessions = new List<SessionVars>();
        var gaps = new List<PlanGap>();
        var reasons = new Dictionary<int, string>();

        // ---- The sessions to place, with their possible starts and rooms ----
        foreach (CourseGroup group in context.Groups.OrderBy(g => g.Course!.Name).ThenBy(g => g.Name))
        {
            Course course = group.Course!;
            int needed = course.SessionsPerWeek - kept.Count(s => s.GroupId == group.Id);
            if (needed <= 0)
            {
                continue;
            }

            int length = course.SessionMinutes;
            long[] starts = AllowedStarts(rules, length);
            var rooms = SuitableRooms(context, group);
            string? reason = starts.Length == 0
                ? $"Sessions of {DisplayNames.Minutes(length)} don't fit in the teaching day ({rules.DayStarts:HH:mm}–{rules.DayEnds:HH:mm}, see Rules)."
                : rooms.Count == 0 ? NoRoomReason(context, group) : null;
            if (reason != null)
            {
                gaps.Add(new PlanGap(group.Id, needed, reason));
                continue;
            }

            long[] available = AvailableStarts(context, group, starts, length);
            for (int k = 0; k < needed; k++)
            {
                string name = $"g{group.Id}s{k}";
                BoolVar present = model.NewBoolVar($"{name}_present");
                IntVar start = model.NewIntVarFromDomain(Domain.FromValues(starts), $"{name}_start");
                // One yes/no per teaching day, exactly one of them when placed: days become plain sums for the costs
                // below, which keeps the model fast (QA, G8).
                var onDay = new Dictionary<int, BoolVar>();
                foreach (var dayStarts in starts.GroupBy(t => (int)(t / MinutesPerDay)))
                {
                    BoolVar on = model.NewBoolVar($"{name}_on{dayStarts.Key}");
                    model.AddLinearExpressionInDomain(start, Domain.FromValues(dayStarts.ToArray())).OnlyEnforceIf(on);
                    onDay[dayStarts.Key] = on;
                }
                model.Add(LinearExpr.Sum(onDay.Values) == present);
                IntervalVar interval = model.NewOptionalFixedSizeIntervalVar(start, length, present, $"{name}_time");

                // Exactly one room if placed; each room's interval includes the changeover after the class.
                var roomChoices = new List<(Classroom Room, BoolVar Chosen, IntervalVar Busy)>();
                foreach (Classroom room in rooms)
                {
                    BoolVar chosen = model.NewBoolVar($"{name}_r{room.Id}");
                    roomChoices.Add((room, chosen, model.NewOptionalFixedSizeIntervalVar(start, length + changeover, chosen, $"{name}_r{room.Id}_busy")));
                    if (room.Capacity < group.Capacity)
                    {
                        objective.AddTerm(chosen, SmallRoomCost);
                    }
                }
                model.Add(LinearExpr.Sum(roomChoices.Select(c => c.Chosen)) == present);

                objective.Add(UnplacedCost).AddTerm(present, -UnplacedCost);
                Prefer(model, objective, start, starts, available, OutsideAvailabilityCost, $"{name}_avail");
                Prefer(model, objective, start, starts, starts.Where(t => t % 60 == 0).ToArray(), OffTheHourCost, $"{name}_hour");

                sessions.Add(new SessionVars(group, k, length, present, start, onDay, interval, roomChoices));
            }
        }

        // ---- Fixed busy times: the classes kept, and what teachers teach elsewhere in the same weeks ----
        var keptIntervals = kept.Select(s => (Session: s, Interval: Fixed(model, s.Day, s.Starts, s.Ends, 0, $"kept{s.Id}"),
                                              RoomBusy: Fixed(model, s.Day, s.Starts, s.Ends, changeover, $"kept{s.Id}_room")))
            .ToList();

        // Rooms.
        foreach (Classroom room in context.Rooms)
        {
            var busy = sessions.SelectMany(s => s.Rooms.Where(r => r.Room.Id == room.Id).Select(r => r.Busy))
                .Concat(keptIntervals.Where(k => k.Session.ClassroomId == room.Id).Select(k => k.RoomBusy)).ToList();
            if (busy.Count > 1)
            {
                model.AddNoOverlap(busy);
            }
        }

        // Teachers.
        foreach (var teacher in context.Groups.Where(g => g.TeacherId != null).GroupBy(g => g.TeacherId!))
        {
            var groupIds = teacher.Select(g => g.Id).ToHashSet();
            var busy = sessions.Where(s => groupIds.Contains(s.Group.Id)).Select(s => s.Interval)
                .Concat(keptIntervals.Where(k => groupIds.Contains(k.Session.GroupId)).Select(k => k.Interval))
                .Concat(context.TaughtElsewhere
                    .Where(s => s.Group!.TeacherId == teacher.Key && context.SameWeeks(s.OrganizationId))
                    .Select(s => Fixed(model, s.Day, s.Starts, s.Ends, 0, $"elsewhere{s.Id}")))
                .ToList();
            if (busy.Count > 1)
            {
                model.AddNoOverlap(busy);
            }
        }

        // Groups, and students (one constraint per distinct set of groups, since many students share theirs).
        var groupSets = context.Groups.Select(g => new[] { g.Id })
            .Concat(context.Groups.SelectMany(g => g.Enrolments.Select(e => (e.StudentId, g.Id)))
                .GroupBy(x => x.StudentId).Select(x => x.Select(y => y.Id).Order().ToArray()))
            .Where(set => set.Length > 0)
            .DistinctBy(set => string.Join(",", set));
        foreach (int[] set in groupSets)
        {
            var busy = sessions.Where(s => set.Contains(s.Group.Id)).Select(s => s.Interval)
                .Concat(keptIntervals.Where(k => set.Contains(k.Session.GroupId)).Select(k => k.Interval)).ToList();
            if (busy.Count > 1 && sessions.Any(s => set.Contains(s.Group.Id)))
            {
                model.AddNoOverlap(busy);
            }
        }

        // ---- Preferences between sessions ----
        foreach (var group in sessions.GroupBy(s => s.Group.Id))
        {
            var list = group.OrderBy(s => s.Index).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                // The same session numbered differently is the same timetable: fix an order to spare the solver.
                if (i > 0)
                {
                    model.AddImplication(list[i].Present, list[i - 1].Present);
                    model.Add(list[i].Start > list[i - 1].Start).OnlyEnforceIf([list[i].Present, list[i - 1].Present]);
                }
            }
            // A group's classes on one day beyond the first cost something (kept classes count too).
            foreach (int dayIndex in list.SelectMany(s => s.OnDay.Keys).Distinct())
            {
                int keptThatDay = kept.Count(s => s.GroupId == group.Key && Array.IndexOf(DisplayNames.Week, s.Day) == dayIndex);
                var onThatDay = list.Where(s => s.OnDay.ContainsKey(dayIndex)).Select(s => s.OnDay[dayIndex]).ToList();
                if (onThatDay.Count + keptThatDay < 2)
                {
                    continue;
                }
                IntVar extra = model.NewIntVar(0, onThatDay.Count + keptThatDay, $"g{group.Key}_d{dayIndex}_extra");
                model.Add(LinearExpr.Sum(onThatDay) + keptThatDay - 1 <= extra);
                objective.AddTerm(extra, SameDayCost);
            }
        }

        // Students with a class at another organisation then (in the same weeks): allowed, as in the editor, but a cost
        // for each such student, so the generator finds another time when it can.
        foreach (SessionVars session in sessions)
        {
            var students = session.Group.Enrolments.Select(e => e.StudentId).ToHashSet();
            var busyElsewhere = context.AttendedElsewhere
                .Where(a => students.Contains(a.StudentId) && context.SameWeeks(a.Session.OrganizationId))
                .Select(a => (a.StudentId, a.Session))
                .Concat(context.TaughtElsewhere.Where(s => s.Group?.TeacherId != null && students.Contains(s.Group.TeacherId) && context.SameWeeks(s.OrganizationId))
                    .Select(s => (StudentId: s.Group!.TeacherId!, Session: s)))
                .GroupBy(x => (x.Session.Day, x.Session.Starts, x.Session.Ends))
                .Select(g => (g.Key.Day, g.Key.Starts, g.Key.Ends, Students: g.Select(x => x.StudentId).Distinct().Count()));
            foreach (var (day, from, to, count) in busyElsewhere)
            {
                long busyStart = Array.IndexOf(DisplayNames.Week, day) * (long)MinutesPerDay + (long)from.ToTimeSpan().TotalMinutes;
                long busyEnd = busyStart + (long)(to - from).TotalMinutes;
                string name = $"{session.Start.Name()}_elsewhere{busyStart}";
                BoolVar before = model.NewBoolVar($"{name}_before"), after = model.NewBoolVar($"{name}_after"), clash = model.NewBoolVar($"{name}_clash");
                model.Add(session.Start + session.Length <= busyStart).OnlyEnforceIf(before);
                model.Add(session.Start >= busyEnd).OnlyEnforceIf(after);
                model.AddBoolOr([before, after, clash]);
                objective.AddTerm(clash, StudentElsewhereCostPerStudent * count);
            }
        }

        // Each teacher's day: the daily teaching-hours limit (a cost for each minute over it), and a spread week (a cost
        // for each minute over their fair share of a day, so classes don't pile onto Tuesday while Friday is empty;
        // QA, G4). The fair share is their week's teaching over the days they can teach, posted availability permitting.
        foreach (var teacher in sessions.Where(s => s.Group.TeacherId != null).GroupBy(s => s.Group.TeacherId!))
        {
            var keptOfTeacher = kept.Where(s => context.Group(s.GroupId)?.TeacherId == teacher.Key).ToList();
            var posted = context.Availability.Where(a => a.TeacherId == teacher.Key).Select(a => a.Day).ToHashSet();
            var days = rules.TeachingDays.Days().Where(d => posted.Count == 0 || posted.Contains(d)).ToList();
            int weekMinutes = teacher.Sum(s => s.Length) + keptOfTeacher.Sum(s => (int)(s.Ends - s.Starts).TotalMinutes);
            int fairShare = days.Count == 0 ? weekMinutes : (int)Math.Ceiling(weekMinutes / (double)days.Count / 60) * 60;

            foreach (DayOfWeek weekday in rules.TeachingDays.Days())
            {
                int dayIndex = Array.IndexOf(DisplayNames.Week, weekday);
                LinearExpr load = DayLoad(teacher, dayIndex);
                int keptMinutes = keptOfTeacher.Where(s => s.Day == weekday).Sum(s => (int)(s.Ends - s.Starts).TotalMinutes);

                if (rules.MaxTeachingHoursPerDay is int maxHours)
                {
                    IntVar over = model.NewIntVar(0, MinutesPerDay, $"t{teacher.Key}_d{dayIndex}_over");
                    model.Add(load + keptMinutes - over <= maxHours * 60);
                    objective.AddTerm(over, OverDailyHoursCostPerMinute);
                }
                IntVar heavy = model.NewIntVar(0, MinutesPerDay, $"t{teacher.Key}_d{dayIndex}_heavy");
                model.Add(load + keptMinutes - heavy <= fairShare);
                objective.AddTerm(heavy, UnevenWeekCostPerMinute);
            }
        }

        // The organisation's week as a whole, gently: a cost for each minute of teaching on a day beyond its fair share,
        // so classes without a teacher (or with many teachers) don't all land on Monday (QA, G9).
        var teachingDays = rules.TeachingDays.Days().Select(d => Array.IndexOf(DisplayNames.Week, d)).ToList();
        int organizationMinutes = sessions.Sum(s => s.Length) + kept.Sum(s => (int)(s.Ends - s.Starts).TotalMinutes);
        int organizationShare = teachingDays.Count == 0 ? organizationMinutes
            : (int)Math.Ceiling(organizationMinutes / (double)teachingDays.Count / 60) * 60;
        foreach (int dayIndex in teachingDays)
        {
            int keptMinutes = kept.Where(s => Array.IndexOf(DisplayNames.Week, s.Day) == dayIndex).Sum(s => (int)(s.Ends - s.Starts).TotalMinutes);
            IntVar heavy = model.NewIntVar(0, MinutesPerDay * 50, $"org_d{dayIndex}_heavy");
            model.Add(DayLoad(sessions, dayIndex) + keptMinutes - heavy <= organizationShare);
            objective.AddTerm(heavy, UnevenWeekCostPerMinute);
        }

        // Ties go to ordinary times: not the last hour of the teaching day (a lone class in an empty week doesn't land at
        // 20:00; QA, G4). No pull towards early days: with many classes it crowded Monday and slowed the solver (QA, G8, G9).
        int lastHour = (int)rules.DayEnds.ToTimeSpan().TotalMinutes - 60;
        foreach (SessionVars session in sessions)
        {
            long[] starts = AllowedStarts(rules, session.Length);
            Prefer(model, objective, session.Start, starts, starts.Where(t => t % MinutesPerDay + session.Length <= lastHour).ToArray(),
                LastHourCost, $"{session.Start.Name()}_notlate");
        }

        model.Minimize(objective);

        // ---- Solve ----
        var solver = new CpSolver
        {
            // Within 1% of the best possible is as good as best for a timetable, and proving the last 1% can take most of
            // the time limit on a big school (QA, G8). CP-SAT then reports the answer as optimal.
            StringParameters = $"max_time_in_seconds:{timeLimit.TotalSeconds:0.#} num_search_workers:8 random_seed:1 relative_gap_limit:0.01",
        };
        CpSolverStatus status = sessions.Count == 0 ? CpSolverStatus.Optimal : solver.Solve(model);
        bool solved = status is CpSolverStatus.Optimal or CpSolverStatus.Feasible;

        var placed = new List<PlannedSession>();
        foreach (SessionVars session in sessions.Where(s => solved && solver.BooleanValue(s.Present)))
        {
            long start = solver.Value(session.Start);
            var starts = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(start % MinutesPerDay));
            Classroom room = session.Rooms.First(r => solver.BooleanValue(r.Chosen)).Room;
            placed.Add(new PlannedSession(session.Group.Id, room.Id, DisplayNames.Week[start / MinutesPerDay], starts, starts.AddMinutes(session.Length)));
        }

        // What blocks each class that couldn't be placed, in the editor's own words: every time and room the rules
        // allow, checked against the timetable the plan gives, and the most common reason (QA, G1).
        ScheduleContext proposed = context.WithSessions(kept.Concat(placed.Select((p, i) => new ClassSession
        {
            Id = -(i + 1), OrganizationId = context.Organization.Id, GroupId = p.GroupId, ClassroomId = p.ClassroomId,
            Day = p.Day, Starts = p.Starts, Ends = p.Ends,
        })).ToList());
        foreach (var group in sessions.GroupBy(s => s.Group))
        {
            int missing = group.Count() - placed.Count(p => p.GroupId == group.Key.Id);
            if (missing > 0)
            {
                gaps.Add(new PlanGap(group.Key.Id, missing, solved
                    ? Blocked(proposed, group.Key)
                    : "The generator ran out of time before finding a timetable. Try again, or place a few classes by hand first."));
            }
        }

        return new TimetablePlan(
            placed.OrderBy(p => Array.IndexOf(DisplayNames.Week, p.Day)).ThenBy(p => p.Starts).ToList(),
            gaps, status == CpSolverStatus.Optimal, (int)watch.ElapsedMilliseconds);
    }

    // Why no time works for a group: the editor's refusals for every time and room the rules allow, grouped by kind,
    // and the commonest kind with an example. A hand placement would be refused for the same reasons.
    private static string Blocked(ScheduleContext proposed, CourseGroup group)
    {
        int length = group.Course!.SessionMinutes;
        var problems = new List<string>();
        foreach (long start in AllowedStarts(proposed.Rules, length))
        {
            DayOfWeek day = DisplayNames.Week[start / MinutesPerDay];
            var from = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(start % MinutesPerDay));
            foreach (Classroom room in SuitableRooms(proposed, group))
            {
                problems.AddRange(ScheduleChecker.Check(proposed, group, room, day, from, from.AddMinutes(length), null).Problems);
            }
        }

        static string Kind(string problem) =>
            problem.Contains("teaches at another organisation") ? "teacher-elsewhere"
            : problem.Contains(" is taken by ") ? "room"
            : problem.Contains(" also take") ? "students"
            : problem.Contains(" teaches ") ? "teacher"
            : problem.Contains("already meets then") ? "group"
            : "other";
        var commonest = problems.GroupBy(Kind).OrderByDescending(g => g.Count()).FirstOrDefault();
        string because = commonest?.Key switch
        {
            "teacher-elsewhere" => $"{group.Teacher?.FullName} teaches at another organisation at the times left",
            "teacher" => $"{group.Teacher?.FullName} teaches other classes here at the times left",
            "room" => "the suitable rooms are taken at the times left",
            "students" => "its students have other classes at the times left",
            "group" => "the group's other classes take the times left",
            _ => "every time left breaks a rule",
        };
        return $"No time works: {because}. For example: {commonest?.First() ?? "no time fits the Rules."} " +
               "Free up the teacher, a room or the students, or change the Rules, then generate again.";
    }

    // ---- Helpers ----

    // Minutes of these sessions on one day (the per-day flags are 0 for a session not placed).
    private static LinearExpr DayLoad(IEnumerable<SessionVars> sessions, int dayIndex)
    {
        var load = LinearExpr.NewBuilder();
        foreach (SessionVars session in sessions.Where(s => s.OnDay.ContainsKey(dayIndex)))
        {
            load.AddTerm(session.OnDay[dayIndex], session.Length);
        }
        return load;
    }

    private record SessionVars(
        CourseGroup Group, int Index, int Length, BoolVar Present, IntVar Start, IReadOnlyDictionary<int, BoolVar> OnDay, IntervalVar Interval,
        IReadOnlyList<(Classroom Room, BoolVar Chosen, IntervalVar Busy)> Rooms);

    // Every start time the Rules allow for a class this long, as minutes from Monday 00:00.
    internal static long[] AllowedStarts(OrganizationRules rules, int length)
    {
        var starts = new List<long>();
        foreach (DayOfWeek day in rules.TeachingDays.Days())
        {
            long dayStart = Array.IndexOf(DisplayNames.Week, day) * (long)MinutesPerDay;
            int first = (int)rules.DayStarts.ToTimeSpan().TotalMinutes, last = (int)rules.DayEnds.ToTimeSpan().TotalMinutes;
            for (int minute = first; minute + length <= last; minute += rules.SlotMinutes)
            {
                starts.Add(dayStart + minute);
            }
        }
        return starts.ToArray();
    }

    // Rooms with what the course needs and space for the students already enrolled (per-class courses check in up
    // to the room's size, so any size will do for them).
    private static List<Classroom> SuitableRooms(ScheduleContext context, CourseGroup group) =>
        context.Rooms.Where(r => (group.Course!.RequiredFeatures & ~r.Features) == RoomFeatures.None &&
                                 (group.Course.Pricing == CoursePricing.PerClass || r.Capacity >= group.Enrolments.Count))
            .ToList();

    private static string NoRoomReason(ScheduleContext context, CourseGroup group)
    {
        RoomFeatures needs = group.Course!.RequiredFeatures;
        bool anyWithFeatures = context.Rooms.Any(r => (needs & ~r.Features) == RoomFeatures.None);
        return context.Rooms.Count == 0 ? "There are no classrooms yet (see Classrooms)."
            : !anyWithFeatures ? $"No classroom has {string.Join(", ", needs.Split().Select(f => f.Display().ToLowerInvariant()))}, which {group.Course.Name} needs."
            : $"No classroom with what {group.Course.Name} needs holds its {DisplayNames.Plural(group.Enrolments.Count, "student")}.";
    }

    // Starts inside the teacher's posted availability. A teacher who hasn't posted any isn't held to it.
    private static long[] AvailableStarts(ScheduleContext context, CourseGroup group, long[] starts, int length)
    {
        var posted = context.Availability.Where(a => a.TeacherId == group.TeacherId).ToList();
        if (group.TeacherId == null || posted.Count == 0)
        {
            return starts;
        }
        return starts.Where(t =>
        {
            DayOfWeek day = DisplayNames.Week[t / MinutesPerDay];
            var from = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(t % MinutesPerDay));
            return posted.Any(a => a.Covers(day, from, from.AddMinutes(length)));
        }).ToArray();
    }

    // A cost unless the start is one of the preferred ones.
    private static void Prefer(CpModel model, LinearExprBuilder objective, IntVar start, long[] all, long[] preferred, int cost, string name)
    {
        if (preferred.Length == all.Length)
        {
            return;
        }
        if (preferred.Length == 0)
        {
            objective.Add(cost);
            return;
        }
        BoolVar inside = model.NewBoolVar(name);
        model.AddLinearExpressionInDomain(start, Domain.FromValues(preferred)).OnlyEnforceIf(inside);
        model.AddLinearExpressionInDomain(start, Domain.FromValues(all.Except(preferred).ToArray())).OnlyEnforceIf(inside.Not());
        objective.Add(cost).AddTerm(inside, -cost);
    }

    private static IntervalVar Fixed(CpModel model, DayOfWeek day, TimeOnly starts, TimeOnly ends, int extra, string name) =>
        model.NewFixedSizeIntervalVar(
            Array.IndexOf(DisplayNames.Week, day) * (long)MinutesPerDay + (long)starts.ToTimeSpan().TotalMinutes,
            (long)(ends - starts).TotalMinutes + extra, name);
}
