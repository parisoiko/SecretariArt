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

    public static TimetablePlan Plan(ScheduleContext context, GenerationMode mode, TimeSpan timeLimit, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var week = new WeekModel(context, mode == GenerationMode.FillGaps ? context.Sessions.ToList() : []);

        // The order below is the order variables are created in, which steers the (seeded) search: keep it.
        week.AddSessions();
        week.KeepApart();
        week.AddSameDayCosts();
        week.AddElsewhereCosts();
        week.AddTeacherDayCosts();
        week.AddOrganisationSpreadCosts();
        week.AddLateHourCosts();
        week.Model.Minimize(week.Objective);

        var solver = new CpSolver
        {
            // Four workers, not one per core: several schools can generate at once without starving the rest of the site.
            // Within 1% of the best possible is as good as best for a timetable, and proving the last 1% can take most of
            // the time limit on a big school (QA, G8). CP-SAT then reports the answer as optimal.
            StringParameters = $"max_time_in_seconds:{timeLimit.TotalSeconds:0.#} num_search_workers:4 random_seed:1 relative_gap_limit:0.01",
        };
        // The admin left the page (or the server is stopping): stop searching rather than finish for nobody.
        using CancellationTokenRegistration stop = cancellationToken.Register(solver.StopSearch);
        CpSolverStatus status = week.Sessions.Count == 0 ? CpSolverStatus.Optimal : solver.Solve(week.Model);
        cancellationToken.ThrowIfCancellationRequested();
        bool solved = status is CpSolverStatus.Optimal or CpSolverStatus.Feasible;

        List<PlannedSession> placed = solved ? ReadPlacements(solver, week.Sessions) : [];
        week.Gaps.AddRange(ExplainGaps(context, week, placed, solved));

        return new TimetablePlan(
            placed.OrderBy(p => p.Day.WeekIndex()).ThenBy(p => p.Starts).ToList(),
            week.Gaps, status == CpSolverStatus.Optimal, (int)watch.ElapsedMilliseconds);
    }

    private static List<PlannedSession> ReadPlacements(CpSolver solver, IEnumerable<SessionVars> sessions)
    {
        var placed = new List<PlannedSession>();
        foreach (SessionVars session in sessions.Where(s => solver.BooleanValue(s.Present)))
        {
            (DayOfWeek day, TimeOnly starts) = FromWeekMinute(solver.Value(session.Start));
            Classroom room = session.Rooms.First(r => solver.BooleanValue(r.Chosen)).Room;
            placed.Add(new PlannedSession(session.Group.Id, room.Id, day, starts, starts.AddMinutes(session.Length)));
        }
        return placed;
    }

    // What blocks each class that couldn't be placed, in the editor's own words: every time and room the rules
    // allow, checked against the timetable the plan gives, and the most common reason (QA, G1).
    private static IEnumerable<PlanGap> ExplainGaps(ScheduleContext context, WeekModel week, List<PlannedSession> placed, bool solved)
    {
        ScheduleContext proposed = context.WithSessions(week.Kept.Concat(placed.Select((p, i) => new ClassSession
        {
            Id = -(i + 1), OrganizationId = context.Organization.Id, GroupId = p.GroupId, ClassroomId = p.ClassroomId,
            Day = p.Day, Starts = p.Starts, Ends = p.Ends,
        })).ToList());
        foreach (var group in week.Sessions.GroupBy(s => s.Group))
        {
            int missing = group.Count() - placed.Count(p => p.GroupId == group.Key.Id);
            if (missing > 0)
            {
                yield return new PlanGap(group.Key.Id, missing, solved
                    ? Blocked(proposed, group.Key)
                    : "The generator ran out of time before finding a timetable. Try again, or place a few classes by hand first.");
            }
        }
    }

    // The CP-SAT model of one week, built step by step: the sessions to place, the rules that keep them apart, and
    // the costs of each compromise (see the constants above).
    private sealed class WeekModel(ScheduleContext context, List<ClassSession> kept)
    {
        // An upper bound for one day's teaching across the whole organisation (all rooms at once).
        private const int MaxOrganisationDayMinutes = MinutesPerDay * 50;

        private readonly OrganizationRules _rules = context.Rules;
        private readonly int _changeover = context.Rules.BreakBetweenSessionsMinutes;

        public CpModel Model { get; } = new();
        public LinearExprBuilder Objective { get; } = LinearExpr.NewBuilder();
        public List<SessionVars> Sessions { get; } = [];
        public List<PlanGap> Gaps { get; } = [];

        // In "Fill the gaps", the classes already in the timetable: fixed, and counted in every cost.
        public List<ClassSession> Kept => kept;

        // The sessions to place, each with its possible starts and rooms. Groups that can't have any (no room suits
        // them, or the class is longer than the teaching day) go straight to the gaps.
        public void AddSessions()
        {
            foreach (CourseGroup group in context.Groups.OrderBy(g => g.Course!.Name).ThenBy(g => g.Name))
            {
                Course course = group.Course!;
                int needed = course.SessionsPerWeek - kept.Count(s => s.GroupId == group.Id);
                if (needed <= 0)
                {
                    continue;
                }

                int length = course.SessionMinutes;
                long[] starts = AllowedStarts(_rules, length);
                var rooms = SuitableRooms(context, group);
                string? reason = starts.Length == 0
                    ? $"Sessions of {DisplayNames.Minutes(length)} don't fit in the teaching day ({_rules.DayStarts:HH:mm}–{_rules.DayEnds:HH:mm}, see Rules)."
                    : rooms.Count == 0 ? NoRoomReason(context, group) : null;
                if (reason != null)
                {
                    Gaps.Add(new PlanGap(group.Id, needed, reason));
                    continue;
                }

                long[] available = AvailableStarts(context, group, starts, length);
                for (int k = 0; k < needed; k++)
                {
                    Sessions.Add(AddSession(group, k, length, starts, available, rooms));
                }
            }
        }

        private SessionVars AddSession(CourseGroup group, int index, int length, long[] starts, long[] available, List<Classroom> rooms)
        {
            string name = $"g{group.Id}s{index}";
            BoolVar present = Model.NewBoolVar($"{name}_present");
            IntVar start = Model.NewIntVarFromDomain(Domain.FromValues(starts), $"{name}_start");
            // One yes/no per teaching day, exactly one of them when placed: days become plain sums for the costs
            // below, which keeps the model fast (QA, G8).
            var onDay = new Dictionary<int, BoolVar>();
            foreach (var dayStarts in starts.GroupBy(t => (int)(t / MinutesPerDay)))
            {
                BoolVar on = Model.NewBoolVar($"{name}_on{dayStarts.Key}");
                Model.AddLinearExpressionInDomain(start, Domain.FromValues(dayStarts.ToArray())).OnlyEnforceIf(on);
                onDay[dayStarts.Key] = on;
            }
            Model.Add(LinearExpr.Sum(onDay.Values) == present);
            IntervalVar interval = Model.NewOptionalFixedSizeIntervalVar(start, length, present, $"{name}_time");

            // Exactly one room if placed; each room's interval includes the changeover after the class.
            var roomChoices = new List<(Classroom Room, BoolVar Chosen, IntervalVar Busy)>();
            foreach (Classroom room in rooms)
            {
                BoolVar chosen = Model.NewBoolVar($"{name}_r{room.Id}");
                IntervalVar busy = Model.NewOptionalFixedSizeIntervalVar(start, length + _changeover, chosen, $"{name}_r{room.Id}_busy");
                roomChoices.Add((room, chosen, busy));
                if (room.Capacity < group.Capacity)
                {
                    Objective.AddTerm(chosen, SmallRoomCost);
                }
            }
            Model.Add(LinearExpr.Sum(roomChoices.Select(c => c.Chosen)) == present);

            Objective.Add(UnplacedCost).AddTerm(present, -UnplacedCost);
            Prefer(Model, Objective, start, starts, available, OutsideAvailabilityCost, $"{name}_avail");
            Prefer(Model, Objective, start, starts, starts.Where(t => t % 60 == 0).ToArray(), OffTheHourCost, $"{name}_hour");

            return new SessionVars(group, index, length, present, start, onDay, interval, roomChoices);
        }

        // No room, teacher, group or student in two places at once. Fixed busy times count too: the classes kept, and
        // what teachers teach at other organisations in the same weeks.
        public void KeepApart()
        {
            var keptIntervals = kept.Select(s => (Session: s, Interval: Fixed(s.Day, s.Starts, s.Ends, 0, $"kept{s.Id}"),
                                                  RoomBusy: Fixed(s.Day, s.Starts, s.Ends, _changeover, $"kept{s.Id}_room")))
                .ToList();

            foreach (Classroom room in context.Rooms)
            {
                NoOverlap(Sessions.SelectMany(s => s.Rooms.Where(r => r.Room.Id == room.Id).Select(r => r.Busy))
                    .Concat(keptIntervals.Where(k => k.Session.ClassroomId == room.Id).Select(k => k.RoomBusy)));
            }

            foreach (var teacher in context.Groups.Where(g => g.TeacherId != null).GroupBy(g => g.TeacherId!))
            {
                var groupIds = teacher.Select(g => g.Id).ToHashSet();
                NoOverlap(Sessions.Where(s => groupIds.Contains(s.Group.Id)).Select(s => s.Interval)
                    .Concat(keptIntervals.Where(k => groupIds.Contains(k.Session.GroupId)).Select(k => k.Interval))
                    .Concat(context.TaughtElsewhere
                        .Where(s => s.Group!.TeacherId == teacher.Key && context.SameWeeks(s.OrganizationId))
                        .Select(s => Fixed(s.Day, s.Starts, s.Ends, 0, $"elsewhere{s.Id}"))));
            }

            // Groups, and students: one constraint per distinct set of groups, since many students share theirs.
            var groupSets = context.Groups.Select(g => new[] { g.Id })
                .Concat(context.Groups.SelectMany(g => g.Enrolments.Select(e => (e.StudentId, g.Id)))
                    .GroupBy(x => x.StudentId).Select(x => x.Select(y => y.Id).Order().ToArray()))
                .Where(set => set.Length > 0)
                .DistinctBy(set => string.Join(",", set));
            foreach (int[] set in groupSets.Where(set => Sessions.Any(s => set.Contains(s.Group.Id))))
            {
                NoOverlap(Sessions.Where(s => set.Contains(s.Group.Id)).Select(s => s.Interval)
                    .Concat(keptIntervals.Where(k => set.Contains(k.Session.GroupId)).Select(k => k.Interval)));
            }
        }

        // A group's classes on one day beyond the first cost something (kept classes count too).
        public void AddSameDayCosts()
        {
            foreach (var group in Sessions.GroupBy(s => s.Group.Id))
            {
                var list = group.OrderBy(s => s.Index).ToList();
                // The same sessions numbered differently are the same timetable: fix an order to spare the solver.
                for (int i = 1; i < list.Count; i++)
                {
                    Model.AddImplication(list[i].Present, list[i - 1].Present);
                    Model.Add(list[i].Start > list[i - 1].Start).OnlyEnforceIf([list[i].Present, list[i - 1].Present]);
                }

                foreach (int dayIndex in list.SelectMany(s => s.OnDay.Keys).Distinct())
                {
                    int keptThatDay = kept.Count(s => s.GroupId == group.Key && s.Day.WeekIndex() == dayIndex);
                    var onThatDay = list.Where(s => s.OnDay.ContainsKey(dayIndex)).Select(s => s.OnDay[dayIndex]).ToList();
                    if (onThatDay.Count + keptThatDay < 2)
                    {
                        continue;
                    }
                    IntVar extra = Model.NewIntVar(0, onThatDay.Count + keptThatDay, $"g{group.Key}_d{dayIndex}_extra");
                    Model.Add(LinearExpr.Sum(onThatDay) + keptThatDay - 1 <= extra);
                    Objective.AddTerm(extra, SameDayCost);
                }
            }
        }

        // Students with a class at another organisation then (in the same weeks): allowed, as in the editor, but a cost
        // for each such student, so the generator finds another time when it can.
        public void AddElsewhereCosts()
        {
            foreach (SessionVars session in Sessions)
            {
                foreach (var (day, from, to, count) in StudentsBusyElsewhere(session))
                {
                    long busyStart = WeekMinute(day, from);
                    long busyEnd = busyStart + (long)(to - from).TotalMinutes;
                    string name = $"{session.Start.Name()}_elsewhere{busyStart}";
                    BoolVar before = Model.NewBoolVar($"{name}_before"), after = Model.NewBoolVar($"{name}_after"),
                            clash = Model.NewBoolVar($"{name}_clash");
                    Model.Add(session.Start + session.Length <= busyStart).OnlyEnforceIf(before);
                    Model.Add(session.Start >= busyEnd).OnlyEnforceIf(after);
                    Model.AddBoolOr([before, after, clash]);
                    Objective.AddTerm(clash, StudentElsewhereCostPerStudent * count);
                }
            }
        }

        // The times the session's students (as students, or teaching) are busy at other organisations, and how many.
        private IEnumerable<(DayOfWeek Day, TimeOnly From, TimeOnly To, int Students)> StudentsBusyElsewhere(SessionVars session)
        {
            var students = session.Group.Enrolments.Select(e => e.StudentId).ToHashSet();
            var attending = context.AttendedElsewhere
                .Where(a => students.Contains(a.StudentId) && context.SameWeeks(a.Session.OrganizationId))
                .Select(a => (a.StudentId, a.Session));
            var teaching = context.TaughtElsewhere
                .Where(s => s.Group?.TeacherId != null && students.Contains(s.Group.TeacherId) && context.SameWeeks(s.OrganizationId))
                .Select(s => (StudentId: s.Group!.TeacherId!, Session: s));
            return attending.Concat(teaching)
                .GroupBy(x => (x.Session.Day, x.Session.Starts, x.Session.Ends))
                .Select(g => (g.Key.Day, g.Key.Starts, g.Key.Ends, g.Select(x => x.StudentId).Distinct().Count()));
        }

        // Each teacher's day: the daily teaching-hours limit (a cost for each minute over it), and a spread week (a cost
        // for each minute over their fair share of a day, so classes don't pile onto Tuesday while Friday is empty;
        // QA, G4). The fair share is their week's teaching over the days they can teach, posted availability permitting.
        public void AddTeacherDayCosts()
        {
            foreach (var teacher in Sessions.Where(s => s.Group.TeacherId != null).GroupBy(s => s.Group.TeacherId!))
            {
                var keptOfTeacher = kept.Where(s => context.Group(s.GroupId)?.TeacherId == teacher.Key).ToList();
                var posted = context.Availability.Where(a => a.TeacherId == teacher.Key).Select(a => a.Day).ToHashSet();
                int days = _rules.TeachingDays.Days().Count(d => posted.Count == 0 || posted.Contains(d));
                int fairShare = FairShare(teacher.Sum(s => s.Length) + keptOfTeacher.Sum(s => s.Minutes), days);

                foreach (DayOfWeek weekday in _rules.TeachingDays.Days())
                {
                    int dayIndex = weekday.WeekIndex();
                    LinearExpr load = DayLoad(teacher, dayIndex);
                    int keptMinutes = keptOfTeacher.Where(s => s.Day == weekday).Sum(s => s.Minutes);

                    if (_rules.MaxTeachingHoursPerDay is int maxHours)
                    {
                        IntVar over = Model.NewIntVar(0, MinutesPerDay, $"t{teacher.Key}_d{dayIndex}_over");
                        Model.Add(load + keptMinutes - over <= maxHours * 60);
                        Objective.AddTerm(over, OverDailyHoursCostPerMinute);
                    }
                    IntVar heavy = Model.NewIntVar(0, MinutesPerDay, $"t{teacher.Key}_d{dayIndex}_heavy");
                    Model.Add(load + keptMinutes - heavy <= fairShare);
                    Objective.AddTerm(heavy, UnevenWeekCostPerMinute);
                }
            }
        }

        // The organisation's week as a whole, gently: a cost for each minute of teaching on a day beyond its fair share,
        // so classes without a teacher (or with many teachers) don't all land on Monday (QA, G9).
        public void AddOrganisationSpreadCosts()
        {
            var teachingDays = _rules.TeachingDays.Days().Select(d => d.WeekIndex()).ToList();
            int share = FairShare(Sessions.Sum(s => s.Length) + kept.Sum(s => s.Minutes), teachingDays.Count);
            foreach (int dayIndex in teachingDays)
            {
                int keptMinutes = kept.Where(s => s.Day.WeekIndex() == dayIndex).Sum(s => s.Minutes);
                IntVar heavy = Model.NewIntVar(0, MaxOrganisationDayMinutes, $"org_d{dayIndex}_heavy");
                Model.Add(DayLoad(Sessions, dayIndex) + keptMinutes - heavy <= share);
                Objective.AddTerm(heavy, UnevenWeekCostPerMinute);
            }
        }

        // Ties go to ordinary times: not the last hour of the teaching day (a lone class in an empty week doesn't land at
        // 20:00; QA, G4). No pull towards early days: with many classes it crowded Monday and slowed the solver (QA, G8, G9).
        public void AddLateHourCosts()
        {
            int lastHour = (int)_rules.DayEnds.ToTimeSpan().TotalMinutes - 60;
            foreach (SessionVars session in Sessions)
            {
                long[] starts = AllowedStarts(_rules, session.Length);
                long[] notLate = starts.Where(t => t % MinutesPerDay + session.Length <= lastHour).ToArray();
                Prefer(Model, Objective, session.Start, starts, notLate, LastHourCost, $"{session.Start.Name()}_notlate");
            }
        }

        private void NoOverlap(IEnumerable<IntervalVar> intervals)
        {
            var list = intervals.ToList();
            if (list.Count > 1)
            {
                Model.AddNoOverlap(list);
            }
        }

        private IntervalVar Fixed(DayOfWeek day, TimeOnly starts, TimeOnly ends, int extra, string name) =>
            Model.NewFixedSizeIntervalVar(WeekMinute(day, starts), (long)(ends - starts).TotalMinutes + extra, name);
    }


    // Why no time works for a group: the editor's refusals for every time and room the rules allow, grouped by kind,
    // and the commonest kind with an example. A hand placement would be refused for the same reasons.
    private static string Blocked(ScheduleContext proposed, CourseGroup group)
    {
        int length = group.Course!.SessionMinutes;
        var problems = new List<string>();
        foreach (long start in AllowedStarts(proposed.Rules, length))
        {
            (DayOfWeek day, TimeOnly from) = FromWeekMinute(start);
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
            long dayStart = day.WeekIndex() * (long)MinutesPerDay;
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
            (DayOfWeek day, TimeOnly from) = FromWeekMinute(t);
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

    // The model's time axis: minutes from Monday 00:00.
    private static long WeekMinute(DayOfWeek day, TimeOnly time) =>
        day.WeekIndex() * (long)MinutesPerDay + (long)time.ToTimeSpan().TotalMinutes;

    private static (DayOfWeek Day, TimeOnly Time) FromWeekMinute(long minute) =>
        (DisplayNames.Week[minute / MinutesPerDay], TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute % MinutesPerDay)));

    // A day's fair share of a week's teaching, rounded up to whole hours (all of it when there are no days).
    private static int FairShare(int weekMinutes, int days) =>
        days == 0 ? weekMinutes : (int)Math.Ceiling(weekMinutes / (double)days / 60) * 60;
}
