using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.Models;

public enum ExternalResourceType
{
    [Display(Name = "Azure Speech-to-Text")]
    AzureSpeech = 0,

    /// <summary>
    /// The Call Data API/Caller Tray boundary (owned by a separate team). ConfigJson carries
    /// {"baseUrl":"https://..."}; SecretCipherText carries the bearer credential they issue us
    /// for calling their entity-detected endpoint. See AIToolbox/TODO_Lists/call-data-api-contract.md.
    /// </summary>
    [Display(Name = "Call Data API")]
    CallDataApi = 1
}
