# Ductwork worker

The central worker for Ductwork Takeoff (plan sections 6.5 and 6.9). It is a Windows service (or a console app) that pulls
tasks from one AIToolbox instance, does the heavy drawing work, and hands results back. **It has no database access, no
files of its own and no provider keys.** Everything goes through the instance's worker API (`api/v1/ductwork/worker`),
and the web app validates and stores every result.

## What it does

1. **Handshake.** Introduces itself and states its contract version. If the server's major version is incompatible it logs
   a critical error and stops without claiming anything. An unreachable server is retried with backoff.
2. **Claim.** Asks for the oldest runnable task of the types it has handlers for. At most `MaxConcurrency` run at once.
3. **Run.** Each task gets its own scratch folder. The heavy part runs in a child process that is given one PDF and one
   output path, an empty environment (no API key), the scratch folder as its working and temp folder, a timeout, and is
   killed with its whole process tree on timeout or cancellation.
4. **Heartbeat.** While a task runs, a heartbeat keeps its lease and reports progress. If the server answers that the task
   was cancelled or taken back, the handler is cancelled and no result is sent.
5. **Report.** A manifest goes back as the task's result. A failure is reported as retryable or not; a result the server
   refuses is reported as a non-retryable failure with the server's reasons. A worker that is stopped hands its tasks back.

Downloaded drawings are checked against the SHA-256 the server holds before any work is done on them.

## Handlers

| Task type | What it does |
|---|---|
| `detect-residential-labels` | Runs the Python sidecar once per in-scope plan sheet and merges the results. Needs `Sidecar:Directory` (the folder holding the `ductwork_sidecar` package) and Python with PyMuPDF. The sidecar's producer must match the binding for the task type, or the worker says so before sending anything. |
| `fake-detect` | Invents six detections so the whole path can be exercised without drawing logic. Only when `EnableFakeHandler` is on. Never in production. |

New detector families are new `ITaskHandler` classes registered in `Program.cs`.

## Hosted vision

A handler that needs a hosted model to read a page asks the server (`TaskContext.SendVisionAsync`); the worker never calls a
provider itself and holds no provider keys. The server sends only if the administrator has switched hosted vision on, a person
has approved those sheets for this run, and the approval is unexpired, for this model and within its image budget. A refusal
(`VisionRefusedApiException`) carries a reason code and ends the task rather than trying another way to read the page. A
vision send is never retried automatically: each one spends the approved budget and discloses content.

## Setting it up

1. In AIToolbox, create an API consumer for the worker with the scopes `ductwork:worker` and `tool:DuctworkTakeoff`.
2. Put `ServerUrl` (including the site's path prefix) and `ApiKey` in `appsettings.Production.json`, an environment
   variable (`Worker__ApiKey`) or user secrets. Do not commit them.
3. A task type is only handed out once an administrator has bound it to an evaluated producer in Ductwork settings.
4. Install as a service: `sc create "AIToolbox Ductwork Worker" binPath= "<path>\AIToolbox.Ductwork.Worker.exe"`.

One worker process serves one instance. Several instances need several processes, each with its own key.

## Draining for an upgrade

Create an empty file named `drain` (`DrainFileName`) in the folder that holds the scratch folder. The worker takes no new
tasks and lets the running ones finish; with `ExitWhenDrained` it then stops. Delete the file to resume. An administrator can also ask this worker to drain from the Workers card on the Ductwork settings page; the worker checks that request each time it would ask for work, and it takes effect the same way. When the
contract version changes, the handshake refuses a worker the server cannot accept, so upgrade the worker first (plan decision:
the worker is upgraded before the server).

## Not built yet

Scratch-folder size limits, per-task memory caps (a Windows job object around the child), the symbol and geometry
detector handlers, hosted-vision calls (those go through the server's gateway, never from here), and the central worker's
instance registry and per-instance key provisioning screen.
