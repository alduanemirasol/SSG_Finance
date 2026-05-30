# TODO: Security Level 1–10 (implemented scoring)

## Goal
Create a numeric **security level (1–10)** score for the application based on implemented hardening tasks.

## Plan
- Define a scoring rubric (0/1 points per control, map to 1–10).
- Implement a server-side endpoint that returns the current score.
- Display it in dashboards (Admin / Treasurer / Professor / Student) as a badge or stat.

## Scoring rubric (current working draft)
Total points: 10

1. HTTPS + HSTS configured (`UseHsts`, `UseHttpsRedirection`) ✅ currently present in Program.cs
2. Security headers: `X-Content-Type-Options`, `X-Frame-Options`, `Server` cleared ✅ partially present in Program.cs
3. Session cookie hardened (`HttpOnly`, `SameSite=Lax`) ✅ present; SecurePolicy set to None for dev/HTTP
4. CSRF protection on all sensitive `[HttpPost]` ✅ appears mostly present via `[ValidateAntiForgeryToken]`
5. Authorization checks on protected endpoints ✅ `RequireRole` helpers exist; some endpoints enforce session role
6. Login brute-force protection ✅ in-memory lockout present in HomeController
7. Password hashing via BCrypt ✅ in AuthService
8. Sensitive data exposure reduced (avoid returning `ex.Message`) ❌ not consistently applied in HomeController
9. Receipt/payment access control prevents IDOR ✅ in GetOrgFeeReceipt; other receipt/data endpoints need review
10. OTP/reset tokens not leaked in responses ❌ reset code/OTP flows still appear to store verification codes and some responses leak details

## Current computed level (based on rubric)
Estimated points: **6 / 10** => **Level 7/10** (rounding rule: >=6 => 7)

## Next implementation steps (when user approves)
1. Add `SecurityLevelService` (computes score from rubric + runtime config).
2. Add controller endpoint `GET /Home/SecurityLevel` returning `{ level, score, maxScore, details }`.
3. Update dashboard views:
   - Views/Dashboard/admin_dashboard.cshtml
   - Views/Dashboard/student_dashboard.cshtml
   - Views/Dashboard/treasurer_dashboard.cshtml
   - Views/Dashboard/professor_dashboard.cshtml
4. Remove or neutralize debug output that logs sensitive info.
5. Replace exception `ex.Message` returns with generic messages.

