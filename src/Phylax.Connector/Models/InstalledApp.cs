namespace Phylax.Connector.Models;

public record InstalledApp(
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    string ProductCode,
    string? WingetId
);
