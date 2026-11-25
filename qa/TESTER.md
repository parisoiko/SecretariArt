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

## Future milestones (to extend as features land)
Classrooms, courses, groups, bundles, enrolments, availability, schedules and payment books don't exist yet. When
these pages appear, rounds for them will be added here. Until then, don't go looking for them. If you find a
link to something unfinished, note it in the report.
