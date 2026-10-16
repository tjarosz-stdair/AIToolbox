namespace AIToolbox.Ductwork.Worker;

/// <summary>
/// Everything the worker needs to know, from configuration. The API key is the only secret and it belongs to one
/// AIToolbox instance: a worker serving several instances runs one process per instance.
/// </summary>
public sealed class WorkerOptions
{
    public const string Section = "Worker";

    /// <summary>The instance the worker serves, including any path prefix, for example https://tools.example.com/aicompare/.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>An API consumer key holding the <c>ductwork:worker</c> and <c>tool:DuctworkTakeoff</c> scopes.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>1 to 60 letters, digits, dashes, underscores or dots. Together with the key it identifies this worker to the server.</summary>
    public string WorkerName { get; set; } = Environment.MachineName.ToLowerInvariant().Replace(' ', '-');

    /// <summary>The task types this worker will take. Empty means every type it has a handler for.</summary>
    public string[] TaskTypes { get; set; } = [];

    /// <summary>How many tasks run at once. Each runs in its own scratch folder, and the heavy work in its own process.</summary>
    public int MaxConcurrency { get; set; } = 2;

    public int PollSeconds { get; set; } = 5;

    public string ScratchRoot { get; set; } = "scratch";

    /// <summary>
    /// While a file with this name exists in the scratch root's parent folder the worker takes no new tasks and lets the
    /// ones it has finish, so it can be stopped for an upgrade without losing work.
    /// </summary>
    public string DrainFileName { get; set; } = "drain";

    /// <summary>Stop the process once it is draining and idle.</summary>
    public bool ExitWhenDrained { get; set; }

    public int TaskTimeoutMinutes { get; set; } = 30;

    public int HttpTimeoutSeconds { get; set; } = 120;

    /// <summary>Registers the test handler that invents a few detections. Never on in production.</summary>
    public bool EnableFakeHandler { get; set; }

    public SidecarOptions Sidecar { get; set; } = new();
}

public sealed class SidecarOptions
{
    public string PythonPath { get; set; } = "python";

    /// <summary>The folder that holds the <c>ductwork_sidecar</c> package. Put on the child's module path.</summary>
    public string Directory { get; set; } = "";

    public int PageTimeoutMinutes { get; set; } = 10;
}
