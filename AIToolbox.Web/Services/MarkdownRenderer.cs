using System.Globalization;
using Markdig;
using Markdig.Renderers;
using Microsoft.AspNetCore.Html;

namespace AIToolbox.Web.Services;

public sealed class MarkdownRenderer : IMarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseGridTables()
            .UseTaskLists()
            .UseAutoLinks()
            .DisableHtml()
            .Build();
    }

    public IHtmlContent Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return HtmlString.Empty;
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer)
        {
            LinkRewriter = RewriteLink
        };

        _pipeline.Setup(renderer);
        var document = Markdown.Parse(markdown, _pipeline);
        renderer.Render(document);
        writer.Flush();

        return new HtmlString(writer.ToString());
    }

    private static string RewriteLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return "#";
        }

        if (Uri.TryCreate(link, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.Scheme is "http" or "https" or "mailto"
                ? link
                : "#";
        }

        return link.StartsWith('#') || link.StartsWith('/')
            ? link
            : "#";
    }
}
