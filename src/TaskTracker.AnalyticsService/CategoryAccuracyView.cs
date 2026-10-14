namespace TaskTracker.AnalyticsService;

/// <summary>
/// Проекція точності оцінювання за категорією завдань (ФВ-5.1 зі звіту ЛР №1).
///
/// Коефіцієнт = середні фактичні витрати / середні планові.
/// Більше за 1 — студент систематично недооцінює роботи цього типу,
/// менше за 1 — переоцінює. Саме це розходження і є Потребою 1,
/// заради якої система проєктувалася.
/// </summary>
public sealed class CategoryAccuracyView
{
    public string Category { get; set; } = string.Empty;
    public int CompletedCount { get; set; }
    public int TotalEstimatedMinutes { get; set; }
    public int TotalActualMinutes { get; set; }

    public double AverageEstimated =>
        CompletedCount == 0 ? 0 : Math.Round((double)TotalEstimatedMinutes / CompletedCount, 1);

    public double AverageActual =>
        CompletedCount == 0 ? 0 : Math.Round((double)TotalActualMinutes / CompletedCount, 1);

    public double AccuracyCoefficient =>
        TotalEstimatedMinutes == 0 ? 0 : Math.Round((double)TotalActualMinutes / TotalEstimatedMinutes, 2);
}
