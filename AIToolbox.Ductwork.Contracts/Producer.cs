namespace AIToolbox.Ductwork.Contracts;

/// <summary>
/// Who or what produced a result (plan section 6.1). Exactly one of three shapes. An LLM producer is
/// not valid without both snapshots, an algorithm without a name, version and params hash, and a
/// manual producer without the user. The same rules are repeated as CHECK constraints in the schema.
/// </summary>
public sealed record ProducerRef
{
    public required ProducerKind Kind { get; init; }

    // Llm
    public int? DirectiveSnapshotId { get; init; }
    public int? ProviderSnapshotId { get; init; }

    // Algorithm
    public string? AlgorithmName { get; init; }
    public string? AlgorithmVersion { get; init; }
    public string? ParamsHash { get; init; }

    // Manual
    public string? UserEmail { get; init; }

    public static ProducerRef Llm(int directiveSnapshotId, int providerSnapshotId) => new()
    {
        Kind = ProducerKind.Llm,
        DirectiveSnapshotId = directiveSnapshotId,
        ProviderSnapshotId = providerSnapshotId
    };

    public static ProducerRef Algorithm(string name, string version, string paramsHash) => new()
    {
        Kind = ProducerKind.Algorithm,
        AlgorithmName = name,
        AlgorithmVersion = version,
        ParamsHash = paramsHash
    };

    public static ProducerRef Manual(string userEmail) => new()
    {
        Kind = ProducerKind.Manual,
        UserEmail = userEmail
    };

    /// <summary>Returns the reasons this producer is invalid; empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        switch (Kind)
        {
            case ProducerKind.Llm:
                if (DirectiveSnapshotId is null or <= 0) errors.Add("An LLM producer requires a directive snapshot.");
                if (ProviderSnapshotId is null or <= 0) errors.Add("An LLM producer requires a provider snapshot.");
                RequireAbsent(errors, AlgorithmName, "algorithm name", "LLM");
                RequireAbsent(errors, UserEmail, "user", "LLM");
                break;
            case ProducerKind.Algorithm:
                if (string.IsNullOrWhiteSpace(AlgorithmName)) errors.Add("An algorithm producer requires a name.");
                if (string.IsNullOrWhiteSpace(AlgorithmVersion)) errors.Add("An algorithm producer requires a version.");
                if (string.IsNullOrWhiteSpace(ParamsHash)) errors.Add("An algorithm producer requires a params hash.");
                if (DirectiveSnapshotId is not null || ProviderSnapshotId is not null)
                    errors.Add("An algorithm producer must not carry LLM snapshots.");
                RequireAbsent(errors, UserEmail, "user", "algorithm");
                break;
            case ProducerKind.Manual:
                if (string.IsNullOrWhiteSpace(UserEmail)) errors.Add("A manual producer requires the user.");
                if (DirectiveSnapshotId is not null || ProviderSnapshotId is not null)
                    errors.Add("A manual producer must not carry LLM snapshots.");
                RequireAbsent(errors, AlgorithmName, "algorithm name", "manual");
                break;
            default:
                errors.Add($"Unknown producer kind {Kind}.");
                break;
        }

        return errors;
    }

    /// <summary>
    /// Stable identity used to reuse one Producer row for identical producers. Never contains
    /// anything secret.
    /// </summary>
    public string Key()
    {
        EnsureValid();
        return Kind switch
        {
            ProducerKind.Llm => $"llm|{DirectiveSnapshotId}|{ProviderSnapshotId}",
            ProducerKind.Algorithm => $"alg|{AlgorithmName!.Trim().ToLowerInvariant()}|{AlgorithmVersion!.Trim()}|{ParamsHash!.Trim().ToLowerInvariant()}",
            _ => $"man|{UserEmail!.Trim().ToLowerInvariant()}"
        };
    }

    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid producer: " + string.Join(" ", errors));
    }

    private static void RequireAbsent(List<string> errors, string? value, string what, string kind)
    {
        if (!string.IsNullOrWhiteSpace(value))
            errors.Add($"A {kind} producer must not carry a {what}.");
    }
}

/// <summary>
/// The only value that lets an entity be written. It is issued by the task context for a running
/// task, so no code path can create an entity without a producer and a task (plan section 7).
/// </summary>
public readonly record struct Provenance
{
    public long ProducerId { get; }
    public long ProcessingTaskId { get; }

    public Provenance(long producerId, long processingTaskId)
    {
        if (producerId <= 0) throw new ArgumentOutOfRangeException(nameof(producerId), "A producer id is required.");
        if (processingTaskId <= 0) throw new ArgumentOutOfRangeException(nameof(processingTaskId), "A processing task id is required.");
        ProducerId = producerId;
        ProcessingTaskId = processingTaskId;
    }

    public bool IsValid => ProducerId > 0 && ProcessingTaskId > 0;

    /// <summary>A default(Provenance) is detectable and must be rejected by every write path.</summary>
    public void EnsureValid()
    {
        if (!IsValid) throw new InvalidOperationException("Provenance is required: no producer or processing task.");
    }
}
