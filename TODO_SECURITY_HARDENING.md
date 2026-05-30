# TODO SECURITY HARDENING

## Completed / Applying auth guards for sensitive read/list endpoints
- [x] Add `RequireAnyRole("Treasurer", "Admin", "Professor")` guard to:
  - [x] GetOrgFeePayments
  - [x] GetRecentPayments
  - [x] GetProfessorStudentPayments
  - [x] GetTreasurerStudentsWithFees
  - [x] GetStudentOrgFeeDetails
  - [x] GetStudentsForPayment
  - [x] SearchAllStudentsWithPaymentStatus
  - [x] GetCollectableOrgFee
  - [x] GetTreasurerDashboardStats



