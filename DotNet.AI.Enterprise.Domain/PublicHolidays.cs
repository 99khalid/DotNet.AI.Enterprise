namespace DotNet.AI.Enterprise.Domain;

/// <summary>Egypt's official public holidays for 2026 (demo data — a real system would load them from a database or API).</summary>
public static class PublicHolidays
{
    public static IReadOnlyList<(DateOnly Date, string Name)> All { get; } =
    [
        (new DateOnly(2026, 1, 7), "Coptic Christmas Day"),
        (new DateOnly(2026, 1, 25), "January 25th Revolution / Police Day"),
        (new DateOnly(2026, 3, 21), "Eid Al-Fitr"),
        (new DateOnly(2026, 4, 13), "Sham El-Nessim"),
        (new DateOnly(2026, 4, 25), "Sinai Liberation Day"),
        (new DateOnly(2026, 5, 1), "Labor Day"),
        (new DateOnly(2026, 5, 27), "Eid Al-Adha"),
        (new DateOnly(2026, 6, 18), "Islamic New Year"),
        (new DateOnly(2026, 6, 30), "June 30 Revolution"),
        (new DateOnly(2026, 7, 23), "Revolution Day (July 23)"),
        (new DateOnly(2026, 8, 27), "Prophet Muhammad's Birthday"),
        (new DateOnly(2026, 10, 6), "Armed Forces Day"),
    ];

    public static IReadOnlyList<(DateOnly Date, string Name)> Between(DateOnly start, DateOnly end) =>
        All.Where(h => h.Date >= start && h.Date <= end).ToList();
}
