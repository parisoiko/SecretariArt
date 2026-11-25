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
- `.github/workflows/ci.yml`: build + test on every push to `main`

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
  `ReportAndRedirect`, which keeps the organisation id. A student takes a course in one group (unique index).

## Conventions
- Registrations live in `Extensions/ServiceCollectionExtensions.cs`; `Program.cs` stays short.
- Business rules live in services returning `ServiceResult`; controllers stay thin.
- Primary constructors for injection; `TimeProvider` for "now" (testable).
- Enums are stored as strings. Read every migration before applying it.
- `[Required]` is always explicit on view models. State-changing actions are POST + anti-forgery (global filter).
- Comments explain *why*. UI text is British English ("organisation").
- Package versions: Microsoft.AspNetCore.* 10.0.13, EF Core 10.0.15.

## Demo data (Development)
Password for every demo account: `Demo!2026`. Platform admin `platform@demo.secretariart.app`; Eleni (admin, Athens Drama
School, code ATHENS26), Nikos (admin, Patras Dance Studio, PATRAS26), Dimitra (teacher Athens + student Patras),
Giorgos (student Athens), Katerina (pending student Athens), Sofia (pending organisation request). Milestone 2 adds
rules, classrooms, courses with groups, the "Year 1 – Acting" bundle and enrolments for Athens (plus teacher Petros and
students Maria, Yannis, Eleftheria, Christos, Ioanna, Stavros) and Patras (teacher Elena). See `Data/DbInitializer.cs`.

## Roadmap
Plan with milestones: 1 accounts & organisations (done) · 2 administration (rules, classrooms, courses/groups/bundles,
enrolments; done) · 3 schedules by hand (availability, editor with conflict checks, calendars, notifications) ·
4 payments · 5 automatic generation with Google OR-Tools CP-SAT · 6 polish.
