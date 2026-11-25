# QA tester kit

A second Claude session tests the site in a headless browser (Playwright, Edge) against its own copy of the app.
It uses its own accounts and fills the site with data as it goes. Its brief is `TESTER.md`.

**Terminal 1: the QA app** (http://localhost:5200, database `SecretariArt_QA`, no demo data):
```powershell
powershell -File qa/start-qa-app.ps1          # add -Reset to start again from an empty database
```

**Terminal 2: the tester** (from the repo root):
```powershell
claude --mcp-config qa/playwright.mcp.json --allowedTools "mcp__playwright__*" "Read" "Edit(qa/reports/**)" -- "Read qa/TESTER.md and follow it."
```

The QA platform admin is `qa-platform@test.secretariart.app` / `QaTest!2026`. Results go to `qa/reports/` (not in
git): `ledger.md` (everything the tester has created), dated run reports, and screenshots. Run it again later
and it carries on from the ledger.
