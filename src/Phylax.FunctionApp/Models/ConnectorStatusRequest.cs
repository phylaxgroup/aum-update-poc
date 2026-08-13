namespace Phylax.FunctionApp.Models;

public class ConnectorStatusRequest
{
    public string MachineName { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
