# Security hardening - Authorization + CSRF

- [x] Apply `RequireRole(...)` checks to every admin/treasurer/protected endpoint.
- [x] Ensure mismatches return `Forbid()` (and missing role redirects to Login).
- [x] Add `[ValidateAntiForgeryToken]` to every `[HttpPost]` action.

- [x] Run/build and fix compile errors.
- [ ] Smoke test: student cannot access treasurer/admin endpoints; verify POST endpoints reject missing/invalid tokens.


