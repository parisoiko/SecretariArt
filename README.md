# SecretariArt

[![CI](https://github.com/parisoiko/SecretariArt/actions/workflows/ci.yml/badge.svg)](https://github.com/parisoiko/SecretariArt/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

**The front office for schools of the arts.** SecretariArt helps drama schools, conservatoires, dance studios, ateliers
and rehearsal spaces with the hardest part of running them: fitting students, courses, teachers and classrooms into a
schedule, and keeping track of who owes what.

> Work in progress. Built from first-hand experience at a drama school's front office.

## Who it's for

| Role | What they do |
|---|---|
| **Admin** | sets the organisation's rules, classrooms, courses and enrolments; builds the schedule; keeps the payment books; approves who joins |
| **Teacher** | posts their availability; sees what they teach, where and when |
| **Student** | sees their classes across every organisation they belong to, with clashes flagged; sees payment deadlines |

One account works everywhere: the same person can teach at one school and study at another.

## Status

| Milestone | |
|---|---|
| 1. Accounts & organisations: registration and sign-in, organisation requests approved by the platform, invite codes, join requests approved by the organisation, role-aware navigation | ✅ |
| 2. Administration: rules, classrooms, courses, groups and bundles, enrolments | ⏳ |
| 3. Schedules: teacher availability, schedule editor with conflict checks, calendars, change notifications | |
| 4. Payments: plans, discounts, due dates, books | |
| 5. Automatic timetable generation (constraint solver) | |

## Running it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and SQL Server LocalDB (Windows).

```bash
dotnet run --project src/SecretariArt
```

Open http://localhost:5100. On first start the database is created and, in Development, filled with demo organisations
and people (password `Demo!2026` for all of them):

| Email | Who |
|---|---|
| `platform@demo.secretariart.app` | platform admin: approves new organisations |
| `eleni@demo.secretariart.app` | admin of Athens Drama School (invite code `ATHENS26`) |
| `dimitra@demo.secretariart.app` | teaches at Athens Drama School, studies at Patras Dance Studio |
| `giorgos@demo.secretariart.app` | student at Athens Drama School |

## Tests

```bash
dotnet test
```

Service tests run against an in-memory SQLite database; integration tests start the whole app in memory and go through
the real forms (sign-in, anti-forgery tokens, redirects), including the authorization boundaries between organisations.
