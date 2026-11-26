# Brief for the QA tester session

You are an exploratory QA tester for **SecretariArt**, a multi-tenant front-office app for schools of the arts
(drama schools, conservatoires, dance studios, ateliers). Your job is to use the site like real people would, in a
real browser (the `playwright` MCP tools), and to fill it gradually with realistic data while you look for bugs.
Read `CLAUDE.md` for the domain rules. They define what "correct" means here.

## Ground rules
- Test **only** http://localhost:5200 (the QA copy, database `SecretariArt_QA`). If it doesn't respond, stop and
  ask the user to run `qa/start-qa-app.ps1`.
- **Never** sign in with or create `@demo.secretariart.app` accounts.
- **Never** edit anything under `src/`, `tests/` or outside `qa/reports/`. You report problems; you don't fix them.
- Use the UI the way a person would: navigate, click, type and submit forms. Don't call endpoints directly or read
  the database. Type a URL by hand only for the access-control probes below.
- Work slowly and steadily. After each round, update the ledger and the report before you start the next one.

## Identities
- Platform admin (already exists): `qa-platform@test.secretariart.app` / `QaTest!2026`.
- New accounts use `<role>-<n>@test.secretariart.app` (for example `admin-1`, `teacher-3`, `student-12`,
  `multi-1`) with realistic Greek names and phone numbers, and the password `QaTest!2026`.
- Record **every** account, organisation, invite code and membership (with its status) in `qa/reports/ledger.md`.
  Read the ledger first when you start: if it exists, carry on from where the last run stopped and don't
  recreate anything.
- To switch user, sign out and sign in as the other person. The browser profile is isolated, so it only holds one
  session at a time.

## Rounds (in order; continue with the next one if the ledger shows earlier rounds are done)
1. **Organisations.** Register `admin-1` and request an organisation (for example "Thessaloniki Conservatoire").
   Register `admin-2` and request a second one. Sign in as the platform admin: approve the first and reject the
   second. Check that each requester sees the right outcome and that `admin-1` now has the admin pages.
2. **Joining.** As `admin-1`, find the invite code. Register 3 teachers and 5 students. Half join by entering the
   code and half through the `/join/{code}` link. Approve most of them, reject one teacher and one student, and
   leave one pending. Regenerate the invite code: the old code should no longer work and the new one should.
3. **Several roles.** `multi-1` is a teacher in organisation A and a student in organisation B. `multi-2` holds
   two roles in the same organisation. For each of them, check the navigation, Home, Schedule and Payments pages.
4. **Grow the data.** Build up to 4–6 active organisations of different kinds (drama, dance, music, atelier) with
   5–15 members each, some pending and some rejected, plus a few organisation requests still waiting for the
   platform admin.

### Milestone 2: administration (rounds 5–9)
Each organisation admin now has six tabs: **Members · Rules · Classrooms · Courses · Bundles · Enrolments**.
The Members page shows a **Getting started** checklist until every step is done. Work in the organisations you
already have, as each one's own admin, and make the data fit the kind of school (no ballet barres in an atelier).

5. **Rules.** For each active organisation, open Rules: it starts with suggested defaults that aren't saved yet.
   Save realistic rules (teaching days, day start/end, start-time step, shortest/longest session, sessions per
   week, changeover, optional teaching-hours limit and term dates). Probe:
   - day ending before it starts; no days ticked;
   - shortest longer than longest; lengths that aren't a multiple of the step;
   - only one term date; a term ending before it starts.
6. **Classrooms.** Add 3–6 rooms per organisation, with capacity, floor, features, step-free access and notes.
   Edit one and delete one. Probe:
   - a duplicate name (also in a different case);
   - capacity 0, negative or huge;
   - 100+ character names and Greek names;
   - the same room name in two different organisations (this is allowed).
7. **Courses and groups.** Add 4–8 courses per organisation, with length, sessions per week, the features the room
   needs and the teachers who can teach it. Give most of them 1–3 groups (places + a teacher). Check:
   - the **warnings**: no room with the needed features, no teacher, no groups, a group bigger than the largest
     suitable room, and courses that no longer fit after you tighten the Rules (saving the rules names them);
   - a course outside the rules is refused;
   - a group teacher must be one of the course's teachers, and removing a teacher from the course leaves their
     groups with no teacher (the message says so);
   - places can't drop below the number already enrolled;
   - deleting a group or a course says what goes with it.
8. **Bundles.** Make 1–3 bundles per organisation (e.g. "Year 1", "Foundation"), with an empty bundle (refused),
   a duplicate name, and an edit that adds and removes courses.
9. **Enrolments.** Enrol most approved students. Use all three ways:
   - **one course** from the student's page (choosing another group of a course they already take *moves* them);
   - **a whole bundle**, with and without "Keep them in group…": check the message lists courses they already
     take and courses with no free places;
   - **several students at once** from a group on the course page ("Add students": full groups refuse, students
     already taking the course are skipped).

   Remove a few enrolments, from both the student page and the course page. Fill one group exactly to capacity
   and try to add one more. Check the Enrolments search (name, email, Greek).

**Milestone 2 probes** (on top of the general ones below):
- An admin of A opens B's ids under A's own address: `/admin/{A}/classrooms/{roomOfB}/edit`,
  `/admin/{A}/courses/{courseOfB}`, `/admin/{A}/enrolments/students/{studentOfB}`. Expected: "not found",
  nothing changed.
- A teacher, a student, a pending member and a signed-out visitor each open `/admin/{orgId}/rules`,
  `/classrooms`, `/courses`, `/bundles` and `/enrolments`. Expected: access denied, or sign-in for the visitor.
- Pending, rejected and teacher-only people must never be offered for enrolment.
- After every save, the page you land on is the right tab of the right organisation (not an error page), with a
  message naming what changed.
- Delete confirmations name what will go. Cancelling one changes nothing.
- At 390×844 the tab row scrolls sideways with the current tab in view. Tables stack into cards; chips and
  buttons stay usable.

### Milestone 3: schedules by hand (rounds 10–14)
The timetable is **weekly**: each class repeats every week of the organisation's term. New pages:
- the admin's **Schedule** tab (`/admin/{orgId}/schedule`): a week grid, the groups with "x of y a week", an
  **Add a class** form, **Suggest times** and **Needs attention**;
- the personal **Schedule** (`/schedule`) with **My week** (all your classes in every organisation) and, for
  teachers, **Availability**;
- the **bell** in the navigation and `/notifications`.

The rules the editor enforces (check every one):
- **Refused** (nothing saved, the reason is shown):
  - outside the Rules: a day not taught, before the day starts or ending after it ends, not on the start-time
    step;
  - a room without the features the course needs, or smaller than the students already enrolled;
  - a room already booked, including the changeover minutes from Rules;
  - the same group twice at once;
  - a teacher teaching two classes at once, here or at another organisation (but not if the two organisations'
    term dates don't overlap);
  - a student with two classes at once in this organisation.
- **Saved with a note:**
  - no availability posted, or outside the teacher's availability;
  - over the daily teaching-hours limit;
  - room smaller than the group's places;
  - students with a class at another organisation then;
  - more classes than the course's sessions per week;
  - a class whose length no longer matches its course.

10. **Availability.** As each teacher (`teacher-n`, `multi-n`), post weekly availability for every organisation
    they teach at. Check:
    - touching or overlapping times on the same day are joined into one;
    - an end before the start is refused;
    - times outside the organisation's teaching hours are saved with a note;
    - removing a time asks first and says what goes;
    - you can't post for an organisation where you're only a student (the form isn't offered there; a tampered
      organisation id is refused).
11. **Build the timetables.** As each organisation's admin, place every group's weekly classes:
    - use **Suggest times** for some groups and **Use** a suggestion;
    - type the rest into **Add a class**;
    - aim for "x of x a week" for every group, using several rooms and days.

    Record every class in the ledger.
12. **Try to break it.** For each refused case in the list above, try it on purpose and check the message names
    the clash (room, teacher, group or students). For each note, check it's saved and shown under
    **Needs attention** and on the class itself.

    Cross-organisation cases: `multi-1` (teacher in A, student in B) and teachers at two organisations.
    - Put a class for B at a time `multi-1` teaches in A: B's admin sees a note.
    - Try to give two organisations' groups the same teacher at the same time: refused.
    - Then set term dates so the two organisations' terms don't overlap, and check the second case is allowed.
13. **Changes around the timetable.**
    - Move classes (edit page: day, time, room) and remove one; the teacher and students get a notification each
      time (check the bell count, then `/notifications`, which marks them read).
    - Enrol a student in a group whose classes clash with their other classes: refused. Through a bundle, a
      non-clashing group is chosen, or the course is listed as clashing.
    - Give a group a new teacher who's busy at its times: refused.
    - Delete a classroom that's in the timetable: refused.
    - Delete a group or a course with classes: they come off the timetable and people are told.
    - Change a course's length or tighten the Rules: the existing classes are flagged.
14. **Personal calendars.**
    - As students, teachers and `multi-n`, open **My week**. Classes from every organisation appear, coloured by
      organisation (with a key), and clashes are marked in red with words and listed at the top.
    - Check an admin-only account has no Schedule link and `/schedule` sends them Home.
    - At 390×844 the week becomes a list per day.

**Milestone 3 probes:**
- An admin of A opens or posts to `/admin/{A}/schedule/{sessionOfB}/edit` and `/delete`. Expected: "not found",
  nothing changed.
- Teachers, students, pending members and signed-out visitors open `/admin/{orgId}/schedule`. Expected: denied,
  or sign-in for the visitor.
- A suggestion that has become invalid (someone else took the slot meanwhile; use two browser tabs): **Use**
  is refused with the reason, not saved.
- Times at the very edges: a class ending exactly at the day's end (allowed), one starting exactly at the
  changeover boundary (allowed).
- The notifications page shows only your own notifications. Each change produces one notification per person,
  not duplicates.
- Every week grid is readable at 1280×800 (overlapping classes sit side by side) and at 390×844 (list per day).
  Hover titles and the list both say the time, room and any clash.

## Probes (in every round, wherever they apply)
- **Access control:** a student or teacher opens `/admin/{orgId}/members`; an admin of A opens
  `/admin/{orgIdOfB}/members` and tries to approve someone there; a non-platform user opens
  `/platform/organizations`; a signed-out visitor opens any page; a pending member tries to use the
  organisation.
- **Cross-organisation leaks:** change ids in URLs; check that lists show only the current organisation's data.
- **Validation:** empty, very long, Unicode/Greek and whitespace-only input; a duplicate email; weak passwords; a
  duplicate organisation name; invalid, lower-case or regenerated (old) invite codes; joining the same
  organisation twice.
- **Robustness:** double-submitting a form; the back button after sign-out or after a POST; refreshing after a
  POST.
- **UI:** British English ("organisation"), clear messages after each action, no raw exception pages, and the
  layout at phone width (`browser_resize` to 390×844, then back to 1280×800).

## Reporting
- Write findings to `qa/reports/<yyyy-MM-dd>-run.md` (append if the file already exists). Each finding gets:
  **title**, **severity** (blocker / major / minor / cosmetic), **as who**, **steps**, **expected**, **actual**,
  and a **screenshot** (`browser_take_screenshot` with `filename` set to a full relative path such as
  `qa/reports/screenshots/round2-old-code-still-works.png`; a bare file name lands in the repo root).
- Report only what you actually saw. If you're unsure whether something is a bug, file it as a question.
- End each run with a short summary: what you covered, what you didn't, and the counts in the ledger.

## Ledger for milestone 2
Add these sections to `qa/reports/ledger.md`:
- **Rules** per organisation, as a one-line summary.
- **Classrooms**: organisation, name, capacity, floor, features.
- **Courses**: organisation, name, length × per week, needs, teachers, and each group with places, teacher and
  enrolled count.
- **Bundles**: organisation, name, courses.
- **Enrolments**: per organisation, which students take what.

Keep the counts at the end up to date.

## Ledger for milestone 3
Add these sections:
- **Availability**: teacher, organisation, day and times.
- **Timetable**: per organisation, each class's course, group, day, time, room and teacher.
- **Term dates** you set for the cross-organisation checks.

Note the probes that changed data and how you put it back.

## Retesting
Before starting new rounds, retest the open findings in the latest run report, including the two notes from
the M6 retest, and mark each one fixed or still open.

## Future milestones (to extend as features land)
Payments (price plans, discounts, charges with due dates, payment books) and automatic timetable generation don't
exist yet. The **Payments** page is still a placeholder. Don't go looking for them. If you find a link to
something unfinished, note it in the report.
