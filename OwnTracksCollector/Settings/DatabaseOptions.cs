using Microsoft.Extensions.Options;

namespace OwnTracksCollector.Settings;

/// <summary>Strongly typed view of the <c>Database</c> configuration section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Path { get; set; } = "owntracks.db";

    /// <summary>Delete existing duplicate rows (oldest kept) so the unique indexes can be built.</summary>
    public bool RemoveDuplicates { get; set; }

    /// <summary>Maximum rows written per transaction.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Messages held in memory waiting to be written. When full, the MQTT handler waits,
    /// which slows the receive loop instead of dropping data.
    /// </summary>
    public int QueueCapacity { get; set; } = 100_000;
}

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions o)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(o.Path))
            errors.Add("Database:Path must not be empty.");
        if (o.BatchSize < 1)
            errors.Add($"Database:BatchSize must be at least 1 (was {o.BatchSize}).");
        if (o.QueueCapacity < 1)
            errors.Add($"Database:QueueCapacity must be at least 1 (was {o.QueueCapacity}).");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
