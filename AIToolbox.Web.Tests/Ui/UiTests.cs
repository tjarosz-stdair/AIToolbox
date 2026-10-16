using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIToolbox.Web.Controllers.Ductwork;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.StaticFiles;

namespace AIToolbox.Web.Tests.Ui;

/// <summary>
/// The Ductwork screens are plain JavaScript modules. These tests keep them honest: their pure logic is tested with Node, the real
/// screens are run in a real browser against a mock of the API, every URL they call must be a route the controllers really have,
/// and the pages that load them must point at files that exist. Tests that need Node or a browser do nothing where those are missing.
/// </summary>
public class UiTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "AIToolbox.Web", "wwwroot"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }
    }

    private static string JsDir => Path.Combine(Root, "AIToolbox.Web", "wwwroot", "js", "ductwork");

    private static (int Exit, string Output) Run(string file, string arguments, string workingDirectory, int timeoutSeconds)
    {
        var info = new ProcessStartInfo(file, arguments) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeoutSeconds * 1000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return (-1, output + "\nTimed out."); }
        process.WaitForExit();
        return (process.ExitCode, output.ToString());
    }

    private static bool HasNode()
    {
        try { return Run("node", "--version", Root, 10).Exit == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    [Fact]
    public void The_screens_pure_logic_passes_its_node_tests()
    {
        if (!HasNode()) return;

        var (exit, output) = Run("node", "--test pure.test.mjs", Path.Combine(Root, "AIToolbox.Web.Tests", "Ui"), 120);

        Assert.True(exit == 0, output);                                       // node exits non-zero if any test fails
    }

    [Fact]
    public void The_real_screens_work_in_a_real_browser_against_the_api_mock()
    {
        if (!HasNode()) return;

        var (exit, output) = Run("node", "run.mjs", Path.Combine(Root, "AIToolbox.Web.Tests", "Ui", "smoke"), 300);

        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.TrimStart().StartsWith('{')) ?? "";
        Assert.True(line.Length > 0, "The browser run printed no result:\n" + output);
        using var result = JsonDocument.Parse(line);
        var failures = result.RootElement.GetProperty("failures").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.True(failures.Count == 0 && exit == 0, string.Join("\n", failures));
        if (result.RootElement.TryGetProperty("skipped", out _)) return;               // no browser on this machine
        Assert.True(result.RootElement.GetProperty("steps").GetArrayLength() >= 15);
    }

    // ---- every URL the screens call is a route that exists

    private sealed record Route(string Method, string[] Segments, string Template);

    private static IEnumerable<Route> ControllerRoutes()
    {
        var types = typeof(DuctworkJobsController).Assembly.GetTypes()
            .Where(t => t.Namespace == "AIToolbox.Web.Controllers.Ductwork" && typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(DuctworkWorkerApiController) && t != typeof(DuctworkAppController));
        foreach (var type in types)
        {
            var prefix = type.GetCustomAttributes<RouteAttribute>(true).Select(r => r.Template).FirstOrDefault() ?? "";
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                foreach (var verb in method.GetCustomAttributes().OfType<HttpMethodAttribute>())
                {
                    var template = verb.Template is { } t && t.StartsWith('~') ? t.TrimStart('~', '/') : (prefix + "/" + verb.Template).Trim('/');
                    foreach (var httpMethod in verb.HttpMethods)
                        yield return new Route(httpMethod, Normalize(template), template);
                }
        }
    }

    /// <summary>Splits a route into segments, turning every {parameter} into a wildcard and dropping the API prefix.</summary>
    private static string[] Normalize(string template)
    {
        var path = template.Trim('/');
        if (path.StartsWith("ductwork/api", StringComparison.OrdinalIgnoreCase)) path = path["ductwork/api".Length..].Trim('/');
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.StartsWith('{') ? "{}" : s.ToLowerInvariant()).ToArray();
    }

    private static readonly Regex VerbCall = new(@"\b(?:api|ctx\.api|this\.api)\.(get|post|put|patch|del)\(\s*(?<q>[`""'])(?<path>.+?)\k<q>", RegexOptions.Compiled);
    private static readonly Regex RequestCall = new(@"request\(\s*""(?<m>GET|POST|PUT|PATCH|DELETE)""\s*,\s*(?<q>[`""'])(?<path>.+?)\k<q>", RegexOptions.Compiled);

    private static IEnumerable<(string File, string Method, string Path)> JsCalls()
    {
        foreach (var file in Directory.GetFiles(JsDir, "*.js"))
        {
            var text = File.ReadAllText(file)
                // Two modules keep a path in a property and build calls from it.
                .Replace("${this.base}", "/jobs/${j}/uploads").Replace("${this.path}", "/jobs/${j}/lock");
            foreach (Match m in VerbCall.Matches(text))
                yield return (Path.GetFileName(file), m.Groups[1].Value == "del" ? "DELETE" : m.Groups[1].Value.ToUpperInvariant(), m.Groups["path"].Value);
            foreach (Match m in RequestCall.Matches(text))
                yield return (Path.GetFileName(file), m.Groups["m"].Value, m.Groups["path"].Value);
        }
    }

    private static string[] JsSegments(string path)
    {
        var clean = Regex.Replace(path, @"\$\{qs\(.*\)\}", "");           // a query string built by qs()
        clean = clean.Split('?')[0];
        clean = Regex.Replace(clean, @"\$\{[^}]*\}", "{}");
        return clean.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Contains("{}") ? "{}" : s.ToLowerInvariant()).ToArray();
    }

    [Fact]
    public void Every_api_call_the_screens_make_matches_a_route_a_controller_really_has()
    {
        var routes = ControllerRoutes().ToList();
        var calls = JsCalls().ToList();
        Assert.True(calls.Count > 60, $"Only {calls.Count} calls were found, so the pattern is probably wrong.");

        var unmatched = new List<string>();
        foreach (var (file, method, path) in calls)
        {
            var segments = JsSegments(path);
            var ok = routes.Any(r => r.Method == method && r.Segments.Length == segments.Length
                && r.Segments.Zip(segments).All(p => p.First == "{}" || p.Second == "{}" || p.First == p.Second));
            if (!ok) unmatched.Add($"{file}: {method} {path}");
        }

        Assert.True(unmatched.Count == 0, "These calls match no controller route:\n" + string.Join("\n", unmatched.Distinct()));
    }

    [Fact]
    public void The_screens_use_every_kind_of_call_so_the_check_above_is_not_vacuous()
    {
        var methods = JsCalls().Select(c => c.Method).Distinct().Order().ToList();
        Assert.Equal(["DELETE", "GET", "PATCH", "POST", "PUT"], methods);
    }

    // ---- the pages and the files they load

    [Fact]
    public void Every_script_a_page_loads_and_every_module_a_script_imports_exists()
    {
        var views = Directory.GetFiles(Path.Combine(Root, "AIToolbox.Web", "Views", "DuctworkApp"), "*.cshtml");
        Assert.NotEmpty(views);
        foreach (var view in views)
            foreach (Match m in Regex.Matches(File.ReadAllText(view), @"~/(?<p>(?:js|css|lib)/[^""']+?)""?(?=[\s>""'])"))
                Assert.True(File.Exists(Path.Combine(Root, "AIToolbox.Web", "wwwroot", m.Groups["p"].Value.Replace('/', Path.DirectorySeparatorChar))), $"{Path.GetFileName(view)} loads {m.Groups["p"].Value}, which does not exist.");

        foreach (var file in Directory.GetFiles(JsDir, "*.js"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"(?:from|import\()\s*[""']\./(?<f>[\w.-]+\.js)[""']"))
                Assert.True(File.Exists(Path.Combine(JsDir, m.Groups["f"].Value)), $"{Path.GetFileName(file)} imports ./{m.Groups["f"].Value}, which does not exist.");
    }

    [Fact]
    public void The_boot_attributes_the_scripts_read_are_the_ones_the_page_writes()
    {
        var boot = File.ReadAllText(Path.Combine(Root, "AIToolbox.Web", "Views", "DuctworkApp", "_Boot.cshtml"));
        foreach (var attribute in new[] { "data-api", "data-base", "data-job-id", "data-email", "data-admin", "data-pdfjs", "data-pdfjs-worker" })
            Assert.Contains(attribute + "=", boot);
        Assert.Contains("id=\"dw-boot\"", boot);
        Assert.Contains("id=\"dw-token-form\"", boot);
        Assert.Contains("AntiForgeryToken", boot);

        var reader = File.ReadAllText(Path.Combine(JsDir, "boot.js"));
        foreach (var key in new[] { "d.api", "d.base", "d.jobId", "d.email", "d.admin", "d.pdfjs", "d.pdfjsWorker" }) Assert.Contains(key, reader);
    }

    [Fact]
    public void The_vendored_pdf_viewer_is_present_with_its_license_and_its_module_type_is_served()
    {
        var lib = Path.Combine(Root, "AIToolbox.Web", "wwwroot", "lib", "pdfjs");
        Assert.True(new FileInfo(Path.Combine(lib, "pdf.min.mjs")).Length > 100_000);
        Assert.True(new FileInfo(Path.Combine(lib, "pdf.worker.min.mjs")).Length > 100_000);
        Assert.Contains("Apache License", File.ReadAllText(Path.Combine(lib, "LICENSE")));

        Assert.True(new FileExtensionContentTypeProvider().TryGetContentType("pdf.min.mjs", out var type));
        Assert.Contains("javascript", type);
    }

    [Fact]
    public void No_screen_builds_markup_from_text_so_server_text_can_never_become_markup()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(JsDir, "*.js"))
        {
            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"\.innerHTML\s*=|\binsertAdjacentHTML\b|\bdocument\.write\b|\beval\(")) offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0, "These files assemble HTML from strings: " + string.Join(", ", offenders));
    }
}
