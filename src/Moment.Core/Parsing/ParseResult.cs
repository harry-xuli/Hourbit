using Moment.Core.Domain;

namespace Moment.Core.Parsing;

public abstract record ParseResult
{
    public sealed record Success(ReminderDraft Draft) : ParseResult;

    public sealed record Ambiguous(string OriginalText, IReadOnlyList<ParseChoice> Choices) : ParseResult;

    public sealed record Invalid(string OriginalText, string Message) : ParseResult;
}

public sealed record ReminderDraft(
    string Title,
    DateTimeOffset DueAt,
    ReminderKind Kind,
    ReminderImportance Importance,
    RecurrenceRule? Recurrence);

public sealed record ParseChoice(string Label, ReminderDraft Draft);

public interface IChineseTimeParser
{
    ParseResult Parse(string text, DateTimeOffset now, TimeZoneInfo zone);
}
