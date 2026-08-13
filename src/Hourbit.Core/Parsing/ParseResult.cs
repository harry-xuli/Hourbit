using System.Globalization;
using Hourbit.Core.Domain;

namespace Hourbit.Core.Parsing;

public abstract record ParseResult
{
    public sealed record Success(ItemDraft Draft) : ParseResult;

    public sealed record Ambiguous(string OriginalText, IReadOnlyList<ParseChoice> Choices) : ParseResult;

    public sealed record Invalid(string OriginalText, string Message) : ParseResult;
}

public abstract record ItemDraft(string Title, ReminderImportance Importance);

public sealed record ReminderDraft(
    string Title,
    DateTimeOffset DueAt,
    ReminderKind Kind,
    ReminderImportance Importance,
    RecurrenceRule? Recurrence) : ItemDraft(Title, Importance);

public sealed record TodoDraft(
    string Title,
    DateOnly? DueDate,
    ReminderImportance Importance) : ItemDraft(Title, Importance);

public sealed record ParseChoice(string Label, ItemDraft Draft);

public interface IChineseTimeParser
{
    ParseResult Parse(string text, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture);
}
