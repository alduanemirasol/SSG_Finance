using MyMvcApp.Models;

namespace MyMvcApp.Services;

/// <summary>
/// Pure business rules for deciding which organizational fees apply to a student.
/// Extracted from HomeController so the logic lives in one named, testable place.
/// Behavior is identical to the original controller methods.
/// </summary>
public static class FeeRules
{
    public static int GetSemesterOrder(Semester semester)
    {
        return semester == Semester.First ? 1 : 2;
    }

    public static bool IsFeeApplicableToStudent(
        AcademicProfile? academicProfile,
        FullAmount fee,
        HashSet<(int schoolYearId, Semester semester)>? exemptions = null)
    {
        // Students who are no longer active (dropped) or who have reached the
        // completion year level (5) no longer owe fees.
        if (academicProfile != null
            && (academicProfile.AcademicStatus == AcademicStatus.Dropped
                || academicProfile.YearLevel == 5))
            return false;

        // Semesters explicitly exempted (e.g. gap year during a drop/re-enrollment).
        if (exemptions != null && exemptions.Contains((fee.SchoolYearId, fee.Semester)))
            return false;

        if (academicProfile?.SchoolYearId == null || academicProfile.SemesterEntered == null)
            return false;

        var entrySchoolYearId = academicProfile.SchoolYearId.Value;

        if (fee.SchoolYearId == entrySchoolYearId)
        {
            return GetSemesterOrder(fee.Semester) >= GetSemesterOrder(academicProfile.SemesterEntered.Value);
        }

        if (academicProfile.SchoolYear != null && fee.SchoolYear != null)
        {
            if (fee.SchoolYear.YearStart != academicProfile.SchoolYear.YearStart)
                return fee.SchoolYear.YearStart > academicProfile.SchoolYear.YearStart;

            return fee.SchoolYear.YearEnd >= academicProfile.SchoolYear.YearEnd;
        }

        return fee.SchoolYearId > entrySchoolYearId;
    }

    public static bool IsFeeApplicableToStudent(
        int? schoolYearId,
        Semester? semesterEntered,
        FullAmount fee,
        HashSet<(int schoolYearId, Semester semester)>? exemptions = null)
    {
        if (exemptions != null && exemptions.Contains((fee.SchoolYearId, fee.Semester)))
            return false;

        if (schoolYearId == null || semesterEntered == null)
            return false;

        if (fee.SchoolYearId == schoolYearId.Value)
            return GetSemesterOrder(fee.Semester) >= GetSemesterOrder(semesterEntered.Value);

        return fee.SchoolYearId > schoolYearId.Value;
    }
}
