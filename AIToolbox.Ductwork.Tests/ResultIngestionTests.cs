using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Counting;
using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Ductwork.Core.Evaluation;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Review;
using AIToolbox.Ductwork.Core.Tasks;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Tests;

public class ResultIngestionTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private const string Owner = "owner@example.com";
    private const string Editor = "editor@example.com";

    private static readonly ProducerRef Algorithm = ProducerRef.Algorithm("residential-label-counter", "0.1.0", "params-1");

    private JobRepository Jobs() => new(db.ConnectionString);
    private ProcessingTaskRepository Tasks() => new(db.ConnectionString);
    private TaskResultIngestor Ingestor(Func<CancellationToken, Task<CalibrationSet>>? calibration = null) => new(db.ConnectionString, calibration: calibration);

    private static AuditContext Ctx(string email) =>
        AuditContext.ForUser(email, displayName: email.Split('@')[0], sessionId: "s-" + email, userAgent: "tests");

    private static AuditContext Worker(string id = "worker-1") => AuditContext.ForWorker(id);

    private sealed record Setup(JobRecord Job, EditToken Token, ProcessingTaskRecord Task, long ProducerId);

    /// <summary>A job with one task claimed by worker-1 and ready to report.</summary>
    private async Task<Setup> RunningTaskAsync(ReviewLevel level = ReviewLevel.Sensitive, string taskType = "detect-residential-labels", ProducerRef? producer = null)
    {
        var jobs = Jobs();
        var job = await jobs.CreateAsync(new NewJob { Name = "Ingest " + Guid.NewGuid().ToString("N")[..8], ReviewLevel = level }, Ctx(Owner));
        await jobs.AddMemberAsync(job.JobId, Editor, JobRole.Editor, Ctx(Owner));
        var token = (await new EditLockService(db.ConnectionString).AcquireAsync(job.JobId, Ctx(Editor))).Token!;

        await using var connection = await db.OpenAsync();
        var producerId = await ProducerStore.EnsureProducerAsync(connection, producer ?? Algorithm);
        var type = taskType + "-" + Guid.NewGuid().ToString("N")[..8];
        var queued = await Tasks().EnqueueAsync(new NewProcessingTask { JobId = job.JobId, TaskType = type, ProducerId = producerId }, token, Ctx(Editor));
        var claimed = await Tasks().ClaimNextAsync(Worker(), [type]);
        Assert.Equal(queued.ProcessingTaskId, claimed!.ProcessingTaskId);
        return new Setup(job, token, claimed, producerId);
    }

    private static DetectionProposal Detection(string id, string size = "4x10x7", string? building = "Type 4", double confidence = 0.95, Disposition? disposition = null,
        string type = "Boot", string? tag = null) => new()
    {
        DetectionId = id, Type = type, Size = size, Tag = tag, BuildingType = building, Quantity = 1, Confidence = confidence,
        Method = DetectionMethod.Text, Family = DetectorFamily.LabelFlagCount, Disposition = disposition,
        Box = new BoundingBox(10, 20, 40, 30), SheetViewRef = "M1.2", Evidence = "label"
    };

    private static TaskResultManifest Manifest(Setup s, IReadOnlyList<DetectionProposal> detections, IReadOnlyList<FindingProposal>? findings = null,
        ProducerRef? producer = null) => new()
    {
        ContractVersion = ContractInfo.Version,
        TaskType = s.Task.TaskType,
        TaskPublicId = s.Task.PublicId.ToString("D"),
        Producer = producer ?? Algorithm,
        Detections = detections,
        Findings = findings ?? []
    };

    private async Task<int> CountAsync(string table, long jobId) =>
        await db.ScalarAsync<int>($"SELECT COUNT(*) FROM ductwork.{table} WHERE JobId = @j;", ("@j", jobId));

    // ---- what a schedule row carries

    [Fact]
    public async Task A_proposal_from_a_schedule_row_is_stored_with_its_manufacturer_and_model_and_one_that_is_too_long_is_refused()
    {
        var s = await RunningTaskAsync();
        var ok = Detection("sch-EF-1-M1.2", type: "EXHAUST FAN", tag: "EF-1") with { Manufacturer = "PANASONIC", Model = "FV-0510VS1", Method = DetectionMethod.Schedule, Family = DetectorFamily.ScheduleNote };

        var result = await Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), Manifest(s, [ok]), null);

        Assert.True(result.Accepted);
        var entity = Assert.Single(await new EntityRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId));
        Assert.Equal(("PANASONIC", "FV-0510VS1", "EF-1", DetectionMethod.Schedule), (entity.Manufacturer, entity.Model, entity.Tag, entity.Method));

        var t = await RunningTaskAsync();
        var tooLong = await Assert.ThrowsAsync<ResultRejectedException>(() =>
            Ingestor().SubmitAsync(t.Task.ProcessingTaskId, Worker(), Manifest(t, [Detection("d1") with { Model = new string('x', 101) }]), null));
        Assert.Contains(tooLong.Reasons, r => r.Contains("model"));
    }

    // ---- the happy path

    [Fact]
    public async Task A_valid_result_is_stored_with_the_provenance_of_its_task_and_the_task_is_finished_in_the_same_step()
    {
        var s = await RunningTaskAsync();
        var result = await Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(),
            Manifest(s, [Detection("d1"), Detection("d2", "4x12x7"), Detection("d3", building: "Type 5")]), "results/m.json");

        Assert.Equal((true, false, 3, 0, 2), (result.Accepted, result.Cancelled, result.Entities, result.Findings, result.BuildingTypesCreated));

        var entities = await new EntityRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId);
        Assert.Equal(3, entities.Count);
        Assert.All(entities, e =>
        {
            Assert.Equal((s.ProducerId, s.Task.ProcessingTaskId), (e.ProducerId, e.ProcessingTaskId));
            Assert.Equal((ReviewStatus.Proposed, ResolutionSource.Detector, ReviewLevel.Sensitive), (e.ReviewStatus, e.ResolutionSource, e.ReviewLevelAtCreation));
        });
        Assert.Equal(new BoundingBox(10, 20, 40, 30), entities[0].Box);
        Assert.Equal("M1.2", entities[0].SheetViewRef);

        var task = (await Tasks().GetAsync(s.Task.ProcessingTaskId))!;
        Assert.Equal((ProcessingTaskStatus.Succeeded, "results/m.json"), (task.Status, task.ResultManifestRef));
        var finished = (await Jobs().ListAuditAsync(s.Job.JobId)).Single(e => e.Action == "task.finished");
        Assert.Contains("\"entities\":3", finished.AfterJson);
        Assert.Equal((ActorType.Worker, s.Task.ProcessingTaskId, s.ProducerId), (finished.ActorType, finished.ProcessingTaskId, finished.ProducerId));
        Assert.True((await Jobs().VerifyAuditChainAsync(s.Job.JobId)).IsValid);
    }

    [Fact]
    public async Task Building_types_named_by_the_detector_are_created_unconfirmed_with_no_multiplier_and_reused()
    {
        var s = await RunningTaskAsync();
        await Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), Manifest(s, [Detection("a", building: "UNIT  A"), Detection("b", building: "unit a"), Detection("c", building: "Unit B")]));

        var types = await new BuildingTypeRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId);
        Assert.Equal(2, types.Count);
        Assert.All(types, t => Assert.Equal((null, false, BuildingKind.Other), (t.Multiplier, t.Confirmed, t.Kind)));
        var entities = await new EntityRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId);
        Assert.Equal(2, entities.Count(e => e.BuildingTypeId == types.Single(t => t.Name == "UNIT A").BuildingTypeId));   // "UNIT  A" and "unit a" are one type
        Assert.Equal(2, (await Jobs().ListAuditAsync(s.Job.JobId)).Count(e => e.Action == "building-type.created"));
    }

    [Fact]
    public async Task An_existing_building_types_multiplier_is_untouched_by_a_later_result()
    {
        var s = await RunningTaskAsync();
        var types = new BuildingTypeRepository(db.ConnectionString);
        var existing = await types.CreateAsync(s.Job.JobId, new NewBuildingType { Name = "Type 4" }, s.Token, Ctx(Editor));
        await types.SetMultiplierAsync(existing.BuildingTypeId, 26, MultiplierSource.Manual, "site plan", s.Token, Ctx(Editor));

        await Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), Manifest(s, [Detection("d1")]));

        var after = (await types.GetAsync(existing.BuildingTypeId))!;
        Assert.Equal((26, true), (after.Multiplier, after.Confirmed));
        Assert.Single(await types.ListByJobAsync(s.Job.JobId));
    }

    [Fact]
    public async Task Findings_are_stored_open_and_linked_to_their_entity()
    {
        var s = await RunningTaskAsync();
        var finding = new FindingProposal { FindingId = "f1", Kind = FindingKind.GrammarVariant, Question = "Label '4x12' has no depth.", Candidates = ["4x12x7", "4x12"], DetectionId = "d1", Evidence = "label '4x12'" };

        var result = await Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), Manifest(s, [Detection("d1", "4x12")], [finding]));

        Assert.Equal(1, result.Findings);
        var linked = await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM ductwork.Finding f JOIN ductwork.Entity e ON e.EntityId = f.EntityId WHERE f.JobId = @j AND f.State = 'Open' AND f.Kind = 'GrammarVariant' AND e.DetectionId = 'd1';",
            ("@j", s.Job.JobId));
        Assert.Equal(1, linked);
    }

    // ---- what is raised to the user

    [Fact]
    public async Task Without_calibration_every_detection_is_raised_as_a_finding_whatever_the_review_level()
    {
        var s = await RunningTaskAsync(ReviewLevel.Insensitive);
        var result = await Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), Manifest(s, [Detection("d1", confidence: 0.99), Detection("d2", confidence: 0.99)]));

        Assert.Equal((2, 0), (result.RaisedAsFindings, result.Batched));
    }

    [Fact]
    public async Task With_calibration_an_insensitive_job_batches_confident_detections_but_never_the_undetermined_or_questioned_ones()
    {
        var s = await RunningTaskAsync(ReviewLevel.Insensitive);
        var scored = Enumerable.Range(0, 200).Select(_ => new ScoredDetection(0.95, 1)).ToList();
        var calibration = new CalibrationSet(new Dictionary<DetectorFamily, CalibrationReport> { [DetectorFamily.LabelFlagCount] = new(scored) });
        var finding = new FindingProposal { FindingId = "f1", Kind = FindingKind.AmbiguousLabel, Question = "Which size?", DetectionId = "questioned" };

        var result = await Ingestor(_ => Task.FromResult(calibration)).SubmitAsync(s.Task.ProcessingTaskId, Worker(), Manifest(s,
        [
            Detection("confident", confidence: 0.97),
            Detection("low", confidence: 0.30),
            Detection("tbd", confidence: 0.99, disposition: Disposition.TBD, type: "CfmOnlyTerminal", size: "x", tag: "100 CFM"),
            Detection("questioned", confidence: 0.99)
        ], [finding]));

        Assert.Equal((1, 3), (result.Batched, result.RaisedAsFindings));
        var surfaced = (await new EntityRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId)).ToDictionary(e => e.DetectionId, e => e.SurfacedAs);
        Assert.Equal(SurfacedAs.Batch, surfaced["confident"]);
        Assert.Equal(SurfacedAs.Finding, surfaced["low"]);
        Assert.Equal(SurfacedAs.Finding, surfaced["tbd"]);          // a hard stop at every level
        Assert.Equal(SurfacedAs.Finding, surfaced["questioned"]);   // a detection with a question attached
    }

    [Fact]
    public async Task A_detection_that_says_it_is_undetermined_is_stored_as_TBD_and_stays_off_the_totals()
    {
        var s = await RunningTaskAsync();
        await Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), Manifest(s,
            [Detection("ok"), Detection("tbd", disposition: Disposition.TBD, type: "CfmOnlyTerminal", size: "x", tag: "100 CFM")]));

        var types = new BuildingTypeRepository(db.ConnectionString);
        var type = (await types.ListByJobAsync(s.Job.JobId)).Single();
        await types.SetMultiplierAsync(type.BuildingTypeId, 10, MultiplierSource.Manual, "plan", s.Token, Ctx(Editor));
        var sheet = await new CountSheetRepository(db.ConnectionString).BuildAsync(s.Job.JobId);

        Assert.Equal(10m, sheet.Totals.Single().Total);
        Assert.Equal("100 CFM", Assert.Single(sheet.OpenItems).Tag);
    }

    // ---- refusals: nothing is stored

    private async Task AssertRefusedAsync(Setup s, TaskResultManifest manifest, string expectedFragment, AuditContext? worker = null)
    {
        var ex = await Assert.ThrowsAsync<ResultRejectedException>(() =>
            Ingestor().SubmitAsync(s.Task.ProcessingTaskId, worker ?? Worker(), manifest));
        Assert.Contains(expectedFragment, string.Join(" ", ex.Reasons), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, await CountAsync("Entity", s.Job.JobId));
        Assert.Equal(0, await CountAsync("Finding", s.Job.JobId));
        Assert.Empty(await new BuildingTypeRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId));
        Assert.Equal(ProcessingTaskStatus.Running, (await Tasks().GetAsync(s.Task.ProcessingTaskId))!.Status);   // still the worker's to retry or fail
    }

    [Fact]
    public async Task A_result_for_a_different_task_or_type_or_producer_is_refused()
    {
        var s = await RunningTaskAsync();
        var good = Manifest(s, [Detection("d1")]);

        await AssertRefusedAsync(s, good with { TaskPublicId = Guid.NewGuid().ToString("D") }, "different task");
        await AssertRefusedAsync(s, good with { TaskType = "something-else" }, "task");
        // The worker cannot present itself as a different algorithm or model than the one the task was queued for.
        await AssertRefusedAsync(s, good with { Producer = ProducerRef.Algorithm("other-detector", "9.9", "x") }, "different producer");
        await AssertRefusedAsync(s, good with { Producer = ProducerRef.Llm(1, 2) }, "different producer");
    }

    [Fact]
    public async Task A_result_that_breaks_the_contract_rules_is_refused_whole()
    {
        var s = await RunningTaskAsync();

        await AssertRefusedAsync(s, Manifest(s, [Detection("d1", confidence: 1.5)]), "outside 0 to 1");
        await AssertRefusedAsync(s, Manifest(s, [Detection("d1"), Detection("d1")]), "more than once");
        await AssertRefusedAsync(s, Manifest(s, [Detection("d1")]) with { ContractVersion = "9.0.0" }, "not compatible");
        await AssertRefusedAsync(s, Manifest(s, [Detection("d1", size: new string('x', 101))]), "over the 100 limit");
        await AssertRefusedAsync(s, Manifest(s, [Detection("d1")], [new FindingProposal { FindingId = "f", Kind = FindingKind.Other, Question = "?", DetectionId = "nope" }]), "unknown detection");
    }

    [Fact]
    public async Task One_bad_detection_among_many_stores_nothing_from_the_result()
    {
        var s = await RunningTaskAsync();
        var many = Enumerable.Range(0, 50).Select(i => Detection("ok" + i)).Append(Detection("bad", confidence: -0.1)).ToList();

        await AssertRefusedAsync(s, Manifest(s, many), "outside 0 to 1");
    }

    [Fact]
    public async Task A_failure_inside_the_database_rolls_back_everything_including_new_building_types_and_the_task_stays_running()
    {
        var s = await RunningTaskAsync();
        // Passes every rule the code checks, but a quantity this large does not fit the column, so the bulk insert fails midway.
        var huge = Detection("huge") with { Quantity = 1e20 };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), Manifest(s, [Detection("fine", building: "New Type"), huge])));

        Assert.Equal(0, await CountAsync("Entity", s.Job.JobId));
        Assert.Empty(await new BuildingTypeRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId));
        Assert.Equal(ProcessingTaskStatus.Running, (await Tasks().GetAsync(s.Task.ProcessingTaskId))!.Status);
        Assert.True((await Jobs().VerifyAuditChainAsync(s.Job.JobId)).IsValid);
    }

    [Fact]
    public async Task Only_the_worker_holding_the_task_can_submit_its_result_and_only_once()
    {
        var s = await RunningTaskAsync();
        var manifest = Manifest(s, [Detection("d1")]);

        await AssertRefusedAsync(s, manifest, "not running under this worker", Worker("worker-2"));
        await Assert.ThrowsAsync<ArgumentException>(() => Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Ctx(Editor), manifest));   // a person is not a worker

        await Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), manifest);
        var again = await Assert.ThrowsAsync<ResultRejectedException>(() => Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), manifest));
        Assert.Contains("not running", again.Message);
        Assert.Equal(1, await CountAsync("Entity", s.Job.JobId));      // the second attempt added nothing
    }

    [Fact]
    public async Task A_task_that_does_not_exist_is_refused()
    {
        var s = await RunningTaskAsync();
        await Assert.ThrowsAsync<ResultRejectedException>(() => Ingestor().SubmitAsync(long.MaxValue, Worker(), Manifest(s, [Detection("d1")])));
    }

    [Fact]
    public async Task A_result_for_a_task_that_was_cancelled_is_not_stored_and_the_task_ends_cancelled()
    {
        var s = await RunningTaskAsync();
        await Tasks().CancelAsync(s.Task.ProcessingTaskId, Ctx(Editor));

        var result = await Ingestor().SubmitAsync(s.Task.ProcessingTaskId, Worker(), Manifest(s, [Detection("d1")]));

        Assert.Equal((false, true, 0), (result.Accepted, result.Cancelled, result.Entities));
        Assert.Equal(0, await CountAsync("Entity", s.Job.JobId));
        Assert.Equal(ProcessingTaskStatus.Cancelled, (await Tasks().GetAsync(s.Task.ProcessingTaskId))!.Status);
    }

    // ---- JSON from the wire

    [Fact]
    public async Task A_result_arriving_as_json_is_checked_against_the_schema_before_anything_else()
    {
        var s = await RunningTaskAsync();
        var json = JsonSerializer.Serialize(Manifest(s, [Detection("d1"), Detection("d2")]), ContractJson.Options);

        var result = await Ingestor().SubmitJsonAsync(s.Task.ProcessingTaskId, Worker(), json);
        Assert.Equal(2, result.Entities);
    }

    [Fact]
    public async Task Json_that_does_not_match_the_schema_is_refused_before_it_touches_the_database()
    {
        var s = await RunningTaskAsync();
        var good = JsonSerializer.Serialize(Manifest(s, [Detection("d1")]), ContractJson.Options);

        foreach (var broken in new[]
        {
            good.Replace("\"confidence\":0.95", "\"confidence\":\"high\""),
            good.Replace("\"method\":\"Text\"", "\"method\":\"Telepathy\""),
            good.Replace("{\"contractVersion\"", "{\"surprise\":1,\"contractVersion\""),
            "not json at all",
            "{}"
        })
        {
            await Assert.ThrowsAsync<ResultRejectedException>(() => Ingestor().SubmitJsonAsync(s.Task.ProcessingTaskId, Worker(), broken));
        }

        Assert.Equal(0, await CountAsync("Entity", s.Job.JobId));
    }

    [Fact]
    public async Task What_the_python_detector_emits_is_accepted_end_to_end()
    {
        var s = await RunningTaskAsync();
        // The shape the sidecar writes: camelCase, a CFM-only terminal marked TBD, a finding with candidates.
        var json = $$"""
        {
          "contractVersion": "0.1.0",
          "taskType": "{{s.Task.TaskType}}",
          "taskPublicId": "{{s.Task.PublicId}}",
          "producer": { "kind": "Algorithm", "algorithmName": "residential-label-counter", "algorithmVersion": "0.1.0", "paramsHash": "params-1" },
          "detections": [
            { "detectionId": "p1-d1", "type": "Boot", "size": "4x10x7", "quantity": 1, "unit": "EA", "method": "Text", "family": "LabelFlagCount",
              "confidence": 0.97, "box": { "x0": 400.1, "y0": 299.0, "x1": 429.0, "y1": 312.0 }, "evidence": "boot-3d: label '4x10x7' with 100 CFM", "buildingType": "Forestwood" },
            { "detectionId": "p1-d2", "type": "CfmOnlyTerminal", "quantity": 1, "unit": "EA", "method": "Text", "family": "LabelFlagCount",
              "confidence": 0.3, "tag": "100 CFM", "disposition": "TBD", "buildingType": "Forestwood" }
          ],
          "findings": [
            { "findingId": "p1-f1", "kind": "AmbiguousLabel", "question": "A terminal is labeled only '100 CFM', with no size.", "candidates": ["4x10x7"], "detectionId": "p1-d2" }
          ]
        }
        """;

        var result = await Ingestor().SubmitJsonAsync(s.Task.ProcessingTaskId, Worker(), json);

        Assert.Equal((2, 1, 1), (result.Entities, result.Findings, result.BuildingTypesCreated));
        var tbd = (await new EntityRepository(db.ConnectionString).ListByJobAsync(s.Job.JobId)).Single(e => e.Type == "CfmOnlyTerminal");
        Assert.Equal((Disposition.TBD, "100 CFM"), (tbd.Disposition, tbd.Tag));
    }

    // ---- the structural guarantee

    [Fact]
    public async Task The_database_refuses_an_entity_without_a_producer_or_a_task()
    {
        var s = await RunningTaskAsync();
        const string columns = "JobId, ProducerId, ProcessingTaskId, DetectionId, Type, Quantity, Method, Family, Confidence, SurfacedAs, ReviewLevelAtCreation";

        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            $"INSERT INTO ductwork.Entity ({columns}) VALUES (@j, NULL, @t, 'x', 'Boot', 1, 'Text', 'LabelFlagCount', 0.5, 'Finding', 'Sensitive');",
            ("@j", s.Job.JobId), ("@t", s.Task.ProcessingTaskId)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            $"INSERT INTO ductwork.Entity ({columns}) VALUES (@j, @p, NULL, 'x', 'Boot', 1, 'Text', 'LabelFlagCount', 0.5, 'Finding', 'Sensitive');",
            ("@j", s.Job.JobId), ("@p", s.ProducerId)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            $"INSERT INTO ductwork.Entity ({columns}) VALUES (@j, 999999999, @t, 'x', 'Boot', 1, 'Text', 'LabelFlagCount', 0.5, 'Finding', 'Sensitive');",
            ("@j", s.Job.JobId), ("@t", s.Task.ProcessingTaskId)));
        await Assert.ThrowsAsync<SqlException>(() => db.ExecAsync(
            $"INSERT INTO ductwork.Entity ({columns}) VALUES (@j, @p, @t, 'x', 'Boot', 1, 'Text', 'LabelFlagCount', 1.5, 'Finding', 'Sensitive');",
            ("@j", s.Job.JobId), ("@p", s.ProducerId), ("@t", s.Task.ProcessingTaskId)));      // confidence outside 0 to 1
    }

    [Fact]
    public async Task No_public_method_creates_entities_except_the_result_ingestor()
    {
        // The structural claim: apart from the ingestor, nothing in the data layer offers a way to insert an entity.
        var creators = typeof(TaskResultIngestor).Assembly.GetTypes()
            .Where(t => t.IsPublic && t != typeof(TaskResultIngestor))
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("Entity", StringComparison.Ordinal) || m.Name.Contains("Entities", StringComparison.Ordinal))
            .Where(m => m.Name.StartsWith("Add") || m.Name.StartsWith("Create") || m.Name.StartsWith("Insert") || m.Name.StartsWith("Store"))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.Empty(creators);
        await Task.CompletedTask;
    }
}
