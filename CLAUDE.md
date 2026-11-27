# SecretariArt

A multi-tenant "front office" web app for schools of the arts (drama schools, conservatoires, dance studios, ateliers,
rehearsal spaces): admins manage rules, classrooms, courses, enrolments, schedules and payment books; teachers post
availability and see their schedule; students see their schedule (with cross-organisation conflict warnings) and payment
deadlines. ASP.NET Core 10 MVC, EF Core 10 (SQL Server LocalDB), ASP.NET Core Identity, Bootstrap 5.

## Layout
- `src/SecretariArt/`: Authorization, Controllers, Data (DbContext, DbInitializer, Migrations), Extensions (registrations),
  Models, Services, ViewComponents, ViewModels, Views, wwwroot
- `tests/SecretariArt.Tests/`: service tests on in-memory SQLite (`Services/`) and `WebApplicationFactory` integration
  tests that drive the real forms (`Integration/`, shared `SecretariArtFactory`)
- `.github/workflows/ci.yml`: build + test, and build the Docker image, on every push to `main`
- `docs/`: NOTES.md (design decisions) and README screenshots; `Dockerfile` + `docker-compose.yml` at the root

## Commands (from the repo root)
- Run: `dotnet run --project src/SecretariArt` (http://localhost:5100) or F5 in VS Code. Start-up migrates the database and
  seeds the platform admin, plus demo data in Development (`SeedDemoData`).
- Test: `dotnet test`
- Migrations (from `src/SecretariArt/`): `dotnet ef migrations add <Name> -o Data/Migrations`. `dotnet-ef` is a local tool.
- If F5 is running, `bin/` is locked: build/test with `-o <tmp>`.
- Manual QA agent: `qa/README.md` (separate app on :5200 with database `SecretariArt_QA`, Playwright tester session).

## Domain rules
- One account per person; what they can do comes from `Membership` (user + organisation + role + status). Roles are
  Admin / Teacher / Student; a person can hold different roles in different organisations (or several in one).
- New organisations: requested by any user → approved by a platform admin (Identity role `PlatformAdmin`) → requester becomes Admin.
- Joining: invite code / link (`/join/{code}`) → pending membership → an admin of that organisation approves.
- Everything requires sign-in (fallback policy). Organisation admin pages use the `OrganizationAdmin` policy, which reads
  `{organizationId}` from the route: every admin route must contain it.
- Any organisation-owned data must carry `OrganizationId`, and queries must filter by it.
- Admin tabs derive from `OrganizationAdminController` (routes `admin/{organizationId}/...`); redirect with
  `ReportAndRedirect`, which keeps the organisation id. Their views use `_AdminLayout` (set `Layout = "_AdminLayout"` and
  `ViewData["AdminTab"]`; optional `Heading`, `Intro` and a `HeadingAside` section): sidebar, organisation switcher, page title. A student takes a course in one group (unique index).
- The timetable is weekly (`ClassSession`: group, room, day, start–end, every week of the term). All clash rules live in
  `ScheduleChecker` (problems refuse, warnings save with a note); enrolment and group-teacher changes use
  `TimetableClashes`. Session changes notify the group's teacher and students (`INotificationService`).
- Payments: a course is paid monthly (billed automatically) or per class attended (students check in to dated classes,
  `BookingService`; places are per class, enrolment isn't capped). `BillingService.EnsureChargesAsync` makes what's due
  (idempotent via `Charge.Key`) when pages open and hourly in `BillingWorker` (`Billing:RunInBackground=false` in tests).
  Charges keep their amounts; only this month's unpaid fees follow price or discount changes. Times and dates are the
  school's (`SchoolTime`, Europe/Athens). SQLite can't sum or compare decimals: do money maths in memory.
- Timetable generation: `TimetableGenerator` (pure, CP-SAT over one week axis in minutes) plans from a `ScheduleContext`;
  hard constraints = ScheduleChecker's problems, soft ones = its warnings. `TimetableDraftService` stores one draft per
  organisation and re-checks it with ScheduleChecker before using it. Check-ins keep their `CourseId` and outlive their
  class: removing or moving classes cancels upcoming check-ins via `BookingService.CancelUpcomingAsync` (call it
  wherever classes go away).

## Conventions
- Registrations live in `Extensions/ServiceCollectionExtensions.cs`; `Program.cs` stays short.
- Business rules live in services returning `ServiceResult`; controllers stay thin.
- Shared helpers, use them rather than writing the logic again: `MembershipQueries` (`Live()`, `Approved(role)`,
  `HasApprovedAsync`) and `Membership.IsLive` for "who counts here"; `PaymentQueries` (settings or defaults, per-class
  courses); `DisplayNames` (`group.Title()`, `session.When()`, `TimeRange`, `WeekIndex`, `ToneClass`, `Truncate`);
  `User.UserId()`; `ChargeKeys` (stored formats, never change them); `PaymentDefaults` for payment constants.
- Primary constructors for injection; `TimeProvider` for "now" (testable).
- Enums are stored as strings. Read every migration before applying it.
- Rate limits (`Extensions/RateLimiting.cs`, section `RateLimits`): a global flood guard per person or IP, and stricter
  sliding windows on sign-in and registration (per IP), invite codes and organisation requests (per person). Add
  `[EnableRateLimiting(...)]` to any new action someone could script. Behind a reverse proxy, set `ForwardedHeaders:*`
  (`Extensions/HostingExtensions.cs`), or every visitor shares the proxy's address. Tests raise the limits
  (`SecretariArtFactory.Limits`).
- Admin actions read the organisation from the route only (`OrganizationId`, or `[FromRoute]`): MVC binds a posted
  field first. The policy refuses a request whose form or query names another organisation.
- Content Security Policy (`Extensions/SecurityHeaders.cs`): scripts only from the site, jsdelivr and inline blocks with
  the request's nonce (added by `ScriptNonceTagHelper`); no inline event handlers (`onclick=`), no `javascript:` URLs.
- Emails go through `IEmailSender`; the only implementation saves `.eml` files (`Email:PickupDirectory`). Links in
  emails use `Email:PublicUrl`. Show emails in views with `@Html.Email(...)` (wraps at "@" and ".").
- `[Required]` is always explicit on view models. State-changing actions are POST + anti-forgery (global filter).
  A `<form>` with a hand-written `action` gets no token unless it has `asp-antiforgery="true"`.
- Comments explain *why*. UI text is British English ("organisation").
- Package versions: Microsoft.AspNetCore.* 10.0.13, EF Core 10.0.15.

## Demo data (Development)
Password for every demo account: `Demo!2026`. Platform admin `platform@demo.secretariart.app`; Eleni (admin, Athens Drama
School, code ATHENS26), Nikos (admin, Patras Dance Studio, PATRAS26), Dimitra (teacher Athens + student Patras),
Giorgos (student Athens), Katerina (pending student Athens), Sofia (pending organisation request). Milestone 2 adds
rules, classrooms, courses with groups, the "Year 1 – Acting" bundle and enrolments for Athens (plus teacher Petros and
students Maria, Yannis, Eleftheria, Christos, Ioanna, Stavros) and Patras (teacher Elena). Milestone 3 adds a weekly
timetable for both, Dimitra's and Petros' availability in Athens, and notifications; Dimitra's Tuesday 18:00 classes
clash on purpose (Athens teaching vs Patras ballet). Milestone 4 adds prices (Athens monthly, Year 1 bundle €180;
Patras ballet monthly, Contemporary per class, billing no-shows), Siblings/Scholarship discounts, last month's Athens
bundle fees in every state (Eleftheria overdue) and Dimitra's Contemporary check-ins. See `Data/DbInitializer.cs`.

## Roadmap
Plan with milestones: 1 accounts & organisations (done) · 2 administration (rules, classrooms, courses/groups/bundles,
enrolments; done) · 3 schedules by hand (availability, editor with conflict checks, calendars, notifications; done) ·
4 payments (prices, check-ins, discounts, charges, books; done) · 5 automatic generation with Google OR-Tools CP-SAT
(drafts: fill the gaps or start over; done) · 6 polish (account self-service, security review, accessibility,
deployment: Dockerfile, /health; docs/NOTES.md and README; done).
