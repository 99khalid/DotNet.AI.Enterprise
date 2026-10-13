using System.Text.RegularExpressions;
using DotNet.AI.Enterprise.Domain;

namespace DotNet.AI.Enterprise.Application.Routing;

/// <summary>
/// Guard before the Profile agent: if the question names ANOTHER employee ("هات رصيد سارة"), the answer is
/// a fixed reply from code. The tools only return the current employee's data, but a small model may still
/// present it as the other person's — so this case never reaches the model.
/// </summary>
public sealed class PrivacyGuard(IEmployeeRepository employees, ICurrentUser currentUser)
{
    /// <summary>The other employee named in the question, or null.</summary>
    public Employee? FindOtherEmployee(string question)
    {
        var text = Normalize(question);

        return employees.GetAll()
            .Where(e => e.Id != currentUser.EmployeeId)
            .FirstOrDefault(e => Regex.IsMatch(text, $@"\b{Regex.Escape(Normalize(FirstName(e)))}\b"));
    }

    public static string Reply(Employee other) =>
        $"أقدر أعرض بياناتك أنت بس 🔒 — بيانات {other.FullName} تقدر تطلبها منها/منه أو من الـ HR على hr@company.example.";

    private static string FirstName(Employee e) => e.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    // Common Arabic spelling variants: سارة/ساره، أحمد/احمد، علي/على.
    private static string Normalize(string s) => s
        .Replace('ة', 'ه').Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ى', 'ي');
}
