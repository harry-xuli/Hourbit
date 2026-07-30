namespace Moment.Windows.Notifications;

public enum NotificationAction { Complete, Snooze10, Ignore }

public sealed record NotificationActivation(Guid OccurrenceId, NotificationAction Action);

public static class NotificationArguments
{
    public static string Format(Guid occurrenceId, NotificationAction action) =>
        $"action={ActionName(action)}&occurrenceId={occurrenceId:D}";

    public static NotificationActivation Parse(string arguments) =>
        TryParse(arguments, out var activation)
            ? activation
            : throw new FormatException("Notification activation arguments are invalid.");

    public static bool TryParse(string? arguments, out NotificationActivation activation)
    {
        activation = default!;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in arguments.Split('&', StringSplitOptions.None))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                return false;
            }

            var name = part[..separator];
            var value = part[(separator + 1)..];
            if (!values.TryAdd(name, value))
            {
                return false;
            }
        }

        if (values.Count != 2 || !values.TryGetValue("action", out var actionText) ||
            !values.TryGetValue("occurrenceId", out var occurrenceText) ||
            !Guid.TryParseExact(occurrenceText, "D", out var occurrenceId) ||
            !TryParseAction(actionText, out var action))
        {
            return false;
        }

        activation = new NotificationActivation(occurrenceId, action);
        return true;
    }

    private static bool TryParseAction(string text, out NotificationAction action)
    {
        action = text switch
        {
            "complete" => NotificationAction.Complete,
            "snooze10" => NotificationAction.Snooze10,
            "ignore" => NotificationAction.Ignore,
            _ => default
        };
        return text is "complete" or "snooze10" or "ignore";
    }

    private static string ActionName(NotificationAction action) => action switch
    {
        NotificationAction.Complete => "complete",
        NotificationAction.Snooze10 => "snooze10",
        NotificationAction.Ignore => "ignore",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
