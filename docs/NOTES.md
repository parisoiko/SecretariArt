# Design notes

Why SecretariArt is built the way it is: the decisions that aren't obvious from the code, and the alternatives I turned
down. Paths are relative to `src/SecretariArt/`.

## Contents

1. [One account, many organisations](#1-one-account-many-organisations)
2. [Keeping organisations apart](#2-keeping-organisations-apart)
3. [All clash rules in one place](#3-all-clash-rules-in-one-place)
4. [Generating a timetable with CP-SAT](#4-generating-a-timetable-with-cp-sat)
5. [Billing that can run any time](#5-billing-that-can-run-any-time)
6. [Check-ins that outlive their class](#6-check-ins-that-outlive-their-class)
7. [Security](#7-security)
8. [Testing](#8-testing)

---

## 1. One account, many organisations

The pain this app comes from: at a drama school's front office, the same teacher also taught at a dance studio across
town, and some students took classes at both. Each school planned its week alone, and the clashes surfaced on the day.

So a **person has one account**, and what they can do lives in **`Membership`** rows (user + organisation + role +
status). Dimitra can teach at Athens Drama School and study ballet at Patras Dance Studio with the same sign-in. Her
**My schedule** merges both, and an admin placing her Tuesday class at Athens is warned that she's busy elsewhere,
without being told where. Other schools' names stay private.

- Roles are per organisation (Admin / Teacher / Student), and a person can hold several in one organisation.
- Nobody grants themselves anything. A new organisation is a request that a **platform admin** approves; joining is an
  invite code that leads to a **pending** membership, which the organisation's admin approves or rejects (with a reason the
  person sees).
- Identity roles are used for one thing only: `PlatformAdmin`. Everything per-organisation is data, not roles, so it can be
  queried and shown ("pending since 3 Nov") instead of being buried in claims.

## 2. Keeping organisations apart

Every organisation-owned row carries an `OrganizationId`, and every query filters by it. That's a convention, so it is
backed by structure that makes it hard to forget:

- **One policy, read from the route.** Admin pages live under `admin/{organizationId}/...`, and the `OrganizationAdmin`
  policy (`Authorization/OrganizationAdminRequirement.cs`) reads that id from the route and checks for an active admin
  membership in an active organisation. Admin tabs derive from `OrganizationAdminController`, which applies the policy
  and exposes `OrganizationId`, read from the route as well, so the check and the action can't disagree.
- **Services take the organisation id** and use it in every lookup: `FindAsync(organizationId, courseId)`, never
  `FindAsync(courseId)`. Ids that come from forms (a teacher, a room, a student) are checked against the organisation
  before use.
- **A lesson from the security review (milestone 6).** One older controller took `organizationId` as an action parameter.
  MVC binds a posted form field *before* the route value, so an admin of one school could post `organizationId=<another>`
  to their own URL: the policy approved the route's school, and the action changed the other one. It's fixed with
  `[FromRoute]`. As a safety net, the policy now refuses any request whose form or query names a different organisation
  from its route, and an integration test keeps it that way.

## 3. All clash rules in one place

`Services/ScheduleChecker.cs` decides whether a class can go at a time and in a room. It sorts what it finds into two kinds:

| Kind | Effect | Examples |
|---|---|---|
| **Problem** | refused | a room double-booked (with changeover time); a teacher, group or student in two places (a teacher's classes at other organisations count); outside the school's days, hours or start times; a room without what the course needs (piano, mirrors) or too small for the students enrolled |
| **Warning** | saved, with a note | outside the teacher's availability or over their daily hours; a room smaller than the group's places; students (or the teacher, as a student) busy at another organisation; no teacher yet |

Everything that changes the timetable goes through it: the editor, enrolment and teacher changes (`TimetableClashes`),
the generator (problems become hard constraints and warnings become costs) and drafts (re-checked before use). With one
rulebook, the editor and the generator can't disagree about what is allowed. The checker is a pure function over a
`ScheduleContext` (the week's rooms, groups, sessions, availability and the people's other commitments), so it's tested
without a database.

## 4. Generating a timetable with CP-SAT

`Services/TimetableGenerator.cs` uses Google OR-Tools' **CP-SAT** solver.

- **One time axis for the week**, in minutes from Monday 00:00. "Two classes at once" is two intervals that overlap,
  whatever the day, so a single no-overlap constraint per room, teacher, group and student covers everything.
- Each session is an **optional interval** with a start from the times the Rules allow and one of the rooms that suit
  the course. Placing a session outweighs every other cost put together, so the solver places as many as it can and
  explains the rest, group by group (for example, the teacher teaches at another organisation at the only times left).
- **Soft rules are costs, ordered by the product owner**: teacher availability > daily hours > room size > a group once
  a day > starting on the hour > students busy elsewhere > an even week. The constants at the top of the file say this
  in code.
- **Two modes:** *Fill the gaps* keeps the current timetable and adds what's missing, and *Start over* re-plans the
  whole week. Either way the result is a **draft**: nothing changes until the admin looks at it and presses *Use this
  draft*. Before use it's re-checked with `ScheduleChecker`, because the timetable may have changed in the meantime.
- **Bounded:** a time limit, a 1% optimality gap (good enough for a timetable, and much faster to prove), four search
  workers, at most two generations at once across the server, and cancellation when the admin leaves the page.

I considered a hand-written greedy placer. It's simpler, but it can't trade one soft rule against another, and its first
bad choice can block every class after it. CP-SAT finds the best week the rules allow in a few seconds, and it reports
when it has proved nothing better exists.

## 5. Billing that can run any time

Payments are either **monthly** (billed automatically) or **per class attended** (students check in; last month's
classes are billed at the start of the next). `Services/BillingService.cs`:

- **Idempotent.** Every charge has a `Key` such as `M:{course}:{student}:{2026-11}`, unique per organisation. Billing
  can run from many places: when a payments page opens, and hourly in `BillingWorker`. Running it twice makes nothing new.
- **Charges keep their amounts.** A price or discount change only updates *this month's unpaid* fees (and says what it
  changed). Anything paid, part paid, waived or from an earlier month stays as it was billed.
- **Bundles:** the bundle price replaces its courses' prices only for students who take every course of the bundle,
  and never both in one month.
- **Discounts** stack multiplicatively (25% then 10% is 32.5% off) and can apply to everything, one course or one bundle.
- **Time is the school's** (`SchoolTime`, Europe/Athens), so "this month" and due days don't shift with the server's time zone.
- **Money maths happens in memory.** SQLite, used by the tests, can't sum or compare `decimal` on the server, and
  the amounts per organisation are small.

## 6. Check-ins that outlive their class

For per-class courses, places are **per class**, not per enrolment: a group of 30 can share a studio of 12 places a
night. A check-in (`Booking`) records the course and the date as well as the class, so it stays valid history when the
class is moved or removed. Upcoming check-ins to a removed or moved class are cancelled (and the students told) through
one method, `BookingService.CancelUpcomingAsync`, called wherever classes go away, including when a draft is used.

## 7. Security

- **Sign-in:** Identity with lockout (5 tries, 10 minutes) and one message for "unknown email" and "wrong password".
  Registration does say an email is taken, which is a deliberate trade-off until emails are confirmed. A password reset
  also clears a lockout, so a locked-out person has a way back in.
- **Password reset:** the same answer whether or not the email has an account, links valid for two hours and usable
  once, and links built from a configured `Email:PublicUrl`, so a forged `Host` header can't point a reset link at someone
  else's site.
- **Rate limits** (`Extensions/RateLimiting.cs`): a global flood guard, plus sliding windows for sign-in, registration and
  password recovery (per IP), and for invite codes, organisation requests and timetable generation (per person).
- **Browser headers** (`Extensions/SecurityHeaders.cs`): a Content Security Policy that allows scripts only from the site,
  the CDN and inline blocks carrying a per-request nonce (a tag helper adds it, so views don't need to). Also no framing,
  `nosniff` and a same-origin referrer policy, so invite links don't leak.
- **CSRF:** a global anti-forgery filter, so every POST needs a token. A test walks every form in the app to check that
  each one carries its own token.

## 8. Testing

- **Service tests** run against SQLite in memory, a fresh database for each test, with a fake `TimeProvider`, so
  "the 5th of next month" is a fixed date.
- **Integration tests** start the whole app in memory (`WebApplicationFactory`) and go through the real forms: get the
  page, take its anti-forgery token, post the fields, follow the redirect. They cover the authorization boundaries
  between organisations, rate limits (with low limits), password reset (emails are captured in memory) and the security
  headers.
- **A QA agent:** a separate Claude session drives a copy of the app (`qa/`, port 5200, its own database) through
  Playwright, following `qa/TESTER.md`, a script of rounds and probes per milestone. It keeps a ledger of the data it
  created and files findings that I fix and it retests. Several real bugs were found this way, including an unclear
  error on a refused draft.
- **Accessibility:** every page reachable from the navigation was checked with axe-core at desktop width, and for
  horizontal overflow at phone width, for four kinds of user.
