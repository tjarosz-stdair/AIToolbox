using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.Models;

public enum ExternalResourceType
{
    [Display(Name = "Azure Speech-to-Text")]
    AzureSpeech = 0
}
