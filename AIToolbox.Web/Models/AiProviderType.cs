using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.Models;

public enum AiProviderType
{
    [Display(Name = "OpenAI-compatible Chat Completions")]
    OpenAiCompatible = 0,

    [Display(Name = "Azure OpenAI")]
    AzureOpenAi = 1,

    [Display(Name = "Anthropic Messages")]
    Anthropic = 2,

    [Display(Name = "OpenAI Responses API")]
    OpenAiResponses = 3,

    [Display(Name = "OpenAI Responses Background API")]
    OpenAiResponsesBackground = 4,

    [Display(Name = "OpenAI Batch Responses API")]
    OpenAiBatchResponses = 5,

    [Display(Name = "Anthropic Message Batches")]
    AnthropicMessageBatch = 6
}
