using Microsoft.AspNetCore.Html;

namespace AIToolbox.Web.Services;

public interface IMarkdownRenderer
{
    IHtmlContent Render(string? markdown);
}
