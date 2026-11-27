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

### Milestone 4: payments (rounds 15–19)
Each course is paid one of two ways, set on the admin's **Payments → Prices** page:
- a **monthly fee**: billed automatically for every enrolled student, each month of the term. Nobody records
  anything;
- **per class attended**: students **check in** to each dated class beforehand (Schedule → **Check in to
  classes**, up to 4 weeks ahead). Check-ins are limited by the group's places (or the room, if smaller), but
  enrolment in such a course isn't. After each month ends, the classes are billed as one charge.

A **bundle** can have its own monthly price, which replaces its courses' prices for students who take the bundle.
Discounts are **named** (Settings & discounts, e.g. "Siblings 25%") or **one-off** (on the student's page, with a
reason). Either applies to everything or to one course or bundle, and several apply one after the other.

**Payments → Settings** has the due day (fees due on that day of their month; per-class charges on that day of the
next month; a charge made after its due day gets a week), reminder days, and the per-class rule **bill students who
check in but don't come**, with a free-cancellation window in hours.

New pages:
- the admin's **Payments** tab (`/admin/{orgId}/payments`): **Books** (totals, charges filtered To pay / Overdue /
  Paid or waived / All, or one line per student), a **student's page** (charges with payments, Record payment,
  Change, Waive, discounts, one-off charges), **Check-ins**, **Prices**, **Settings & discounts**;
- the student's **Payments** page (`/payments`): what they owe in every organisation, overdue in red, history;
- **Check in to classes** (students) and **Who's coming** (teachers) on the personal Schedule, plus a class's
  check-in list, where the teacher or an admin marks anyone who didn't come (after the class has started).

Time: billing runs when the books or a Payments page open, and every hour in the background (which also sends
reminders). You can't move the clock, so per-class charges for this month only show as "so far this month". The
due-day and reminder rules can be checked on charges made today. Term dates matter: months outside an
organisation's term aren't billed, so look at the term dates you set in round 12 first.

15. **Prices and settings.** As each admin, price every course (mix monthly and per class across organisations;
    an atelier is a good per-class case), give at least one bundle a monthly price, set the due day and reminders,
    and turn on billing no-shows (with a window) in one organisation only. Check:
    - a priced course with no amount is refused and what you typed is kept;
    - opening the Books makes this month's fees for every enrolled student, with the right amount and due date;
    - changing a price updates this month's unpaid fees (the message says how many) but not paid ones;
    - a bundle price replaces its courses' fees for bundle students, and clearing it brings them back.
16. **Discounts.** Create named discounts; give students a named one, a one-off one on one course, and both on the
    same student. Check the amounts (25% then 10% is 32.5% off), the note on each charge, and that removing a
    discount updates this month's unpaid fee.
17. **The desk.** As admins, record payments: full, part (twice to settle), by each method, with a reference.
    Check:
    - paying more than is owed, or a future date, is refused;
    - a payment can be removed and the charge is owed again;
    - waiving needs a reason, which the student sees;
    - changing a charge's amount can't go below what's paid;
    - a one-off charge (e.g. registration fee) reaches the student.

    Each action notifies the student (bell). Students see it on `/payments`, overdue in red with words.
18. **Checking in.** As students of per-class courses:
    - check in to several classes and cancel some, on both sides of the free-cancellation window (in the
      organisation that bills no-shows the message says it will still be billed);
    - fill a class to its places: the next student sees "Full" and is refused;
    - check the "so far this month" line on `/payments`.

    As the teacher (Who's coming) and the admin (Check-ins), open a class that has started and mark someone as
    not there, then back. A class that hasn't started yet can't be marked.
19. **Cross-checks.** `multi-n` students with charges in two organisations see both on `/payments`, each with its
    own totals. A student who leaves a course keeps the charges already made. A per-class group can take more
    students than its places, and the course page says "places per class".

**Milestone 4 probes:**
- An admin of A opens `/admin/{A}/payments/students/{studentOfB}` and posts a payment to a charge of B: "not
  found", nothing changed.
- Students, teachers and pending members open `/admin/{orgId}/payments`: denied. An admin-only account opening
  `/payments` is sent Home.
- A student checks in to a class of a course they're not enrolled in, or to a date that isn't a class day
  (tampered form): refused.
- Amounts: 0, negative, 0.001, 100000.01, commas ("12,50") and Greek input in money fields.
- Double-submitting Record payment doesn't record it twice past the balance.
- Every new page at 390×844: the record-payment and change forms open in place and are usable.

### Milestone 5: automatic timetable generation (rounds 20–23)
Also new since milestone 4: a **redesign** (new header and logo, admin sections in a sidebar, new Home, Members and
Rules pages) and **rate limits** (the QA app runs with them raised, so they won't get in your way).

The admin's Schedule tab has **Generate a timetable** (`/admin/{orgId}/schedule/generate`):
- **Fill the gaps** keeps every class already in the timetable and places only what each group is missing;
- **Start over** plans the whole week again.

The generator (Google OR-Tools) makes a **draft** (`/admin/{orgId}/schedule/draft`): the week grid with new classes
marked "New:", what it couldn't place and why, and notes on new classes. Nothing changes until the admin chooses
**Use this timetable** (or "Add these N classes") or **Discard**. Using it checks the draft again against the timetable as
it is then. A draft that no longer fits is refused. One notification per person lists their classes.

What it must always keep to (the editor's refusals): the Rules (days, hours, start-time step), room features and size
(for enrolled students), the room changeover time, and no teacher, group or student in two places at once (also a
teacher teaching at another organisation in overlapping terms). What it should avoid when it can (the editor's notes),
most important first: times outside a teacher's availability, the daily teaching-hours limit, a room smaller than the
group's places, a group twice on one day, starts that aren't on the hour, students with a class at another organisation
then, and an uneven week. A lower one gives way to a higher one.

Also changed: removing or moving a class (by hand, by deleting its group or course, or by a "Start over" draft) now
cancels **upcoming check-ins** to it and tells each student. Past check-ins stay and are still billed. Unenrolling a
student, or moving them to another group, cancels their upcoming check-ins in the old group.

20. **Fill the gaps.** In an organisation with some groups short of classes (add a group or a course if none is), generate
    with Fill the gaps. Check:
    - kept classes don't move, and only the missing ones are added;
    - every new class keeps every rule above: open each one's details on the Schedule tab after using the draft, and
      try to add the same class by hand somewhere it clashes;
    - a group's classes land on different days when possible, inside teachers' availability when posted;
    - what couldn't be placed is listed with a reason that makes sense (e.g. a course needing a feature no room has);
    - using it adds the classes, and the teachers and students get one notification each.
21. **Start over.** In another organisation, generate with Start over, look at the draft, then:
    - discard it: the timetable is unchanged;
    - generate again and use it: the old classes are replaced, people are told, and the Schedule tab shows no clashes
      (notes are fine);
    - in an organisation with per-class courses and upcoming check-ins, check the students get the cancellation
      notification and their check-ins are gone from "Check in to classes", while last month's still show on Payments.
22. **Stale drafts and edges.**
    - Generate a draft, then (in another tab, or before using it) add a class by hand where the draft puts one, or delete
      a classroom or group the draft uses: using the draft is refused with the reason.
    - An organisation with no classrooms, or no groups: generating says what's missing.
    - Fill the gaps when every group is complete: says so.
    - Double-click Generate: one draft, not two; generating again replaces the draft.
    - Cross-organisation: a teacher (`multi-n` or one teaching at two organisations) is never given a time they teach
      elsewhere in overlapping terms.
23. **The redesign.** Walk the new Home (with and without organisations, as an admin, a teacher and a student), the
    organisation switcher on admin pages, the sidebar (and its scrolling row on a phone), Members (invite bar, copy
    link, role filters, search, turned down recently) and Rules (day toggles, the "In short" summary). Check
    the logo and the browser-tab icon at desktop and phone width.

**Milestone 5 probes:**
- An admin of A opens or posts to `/admin/{B}/schedule/generate`, `/draft`, `/draft/use`, `/draft/discard`: denied.
- Teachers and students open the generate and draft pages: denied.
- Generating with a large organisation (many groups) finishes within about 20 seconds and says how long it took.
- Every draft page at 390×844.

### Milestone 6: polish (rounds 24–27)
New: **My account** (account menu → My account, `/account/profile`) to change your name, phone and password; **Forgot your
password?** on the sign-in page. The QA app doesn't send email: each message is saved as an `.eml` text file in
`qa/reports/mail/` (newest last by name). Read it with the Read tool to get the reset link. That's the one file you may read
outside the browser. Also new: security headers (a Content Security Policy), accessibility fixes, emails that wrap at "@"
and "." on narrow screens, and a refused draft that names the deleted group.

24. **My account.** As an existing teacher and a student:
    - change first name, last name and phone: the menu greets the new name at once; Members and rosters (as their admin)
      show the new details; invalid phone, empty names and a 101-character name are refused with clear messages;
    - change the password: a wrong current password, a short new one and a mismatched confirmation are refused; then
      change it for real, sign out and in with the new one; the old one fails ("other devices are signed out" can't
      be tested with one browser profile: skip it).
    Put every changed password and name in the ledger (Accounts table).
25. **Forgot password.** For a QA account:
    - ask for a link from the sign-in page; the confirmation page reads the same for an unknown email (and no `.eml` file
      appears for it);
    - open the link from the newest `.eml` file: set a new password, then sign in with it;
    - the same link a second time: refused, with a way to ask for a new one;
    - an edited link (change a few characters of `code=`) is refused; an expired one can't be tested (2 hours);
    - lock an account with 5 wrong passwords, then reset its password: it can sign in straight away;
    - asking many times in a row from the forgot page eventually says "too many requests" only with `-RealRateLimits`
      (skip otherwise).
26. **Accessibility and phone width.** Keyboard only (Tab, Shift+Tab, Enter, Space) through: sign-in, Home, the account
    menu, Members (approve), Schedule (add a class), Payments (record a payment) and My account. Every control can be
    reached, the focused one is visibly outlined, and nothing traps focus. On the Schedule week grid, Tab reaches the grid
    and the arrow keys scroll it on a phone. At 390×844, check that long emails in Members, the platform queue and
    Enrolments wrap at "@" or "." rather than mid-word.
27. **Retest G1.** Generate a Fill the gaps draft that places a class for a group, delete that group in another tab, then
    use the draft: the refusal names it ("<Course> (group X) has been deleted since").

**Milestone 6 probes:**
- As the admin of A, use the browser's developer tools only if you have them; otherwise skip: posting a form on
  `/admin/{A}/members` with an extra field `organizationId={B}` must not touch B (this was a real bug, fixed). If you can't
  add fields to a form, note it as not tested.
- `/account/profile` signed out: goes to sign-in. `/account/reset-password` without a link: goes to Forgot password.
- No page shows a browser console error about a Content Security Policy (`browser_console_messages`), especially pages
  with scripts: Rules, Members (copy link), Payments, Schedule, Generate.

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

## Ledger for milestone 4
Add these sections:
- **Prices** per organisation: each course's way of paying and amount, and bundle prices.
- **Payment settings** and **named discounts** per organisation; **discounts given** to students.
- **Charges and payments** you recorded (student, what, amount, paid, method), waived charges, one-off charges.
- **Check-ins**: student, class, date, cancelled (in time or late), marked as not there.

## Ledger for milestone 5
Add a **Generated drafts** section: organisation, mode, when, classes placed and not placed, used or discarded; and the
classes each used draft put in the timetable (replace that organisation's Timetable section). Note cancelled check-ins.

## Retesting
Before starting new rounds, retest the open findings in the latest run report and mark each one fixed or still
open. N1 (long emails breaking mid-word on a phone) and the G1 deletion wording were fixed in milestone 6 (round 26, 27).

## Ledger for milestone 6
In the Accounts table, keep every account's current password and name (mark changed ones). Note the reset emails you used.

## Future milestones (to extend as features land)
Milestone 6 is the last one planned. If you find a link to something unfinished, note it in the report.
