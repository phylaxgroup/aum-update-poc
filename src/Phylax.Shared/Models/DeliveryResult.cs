using System;

namespace Phylax.Shared.Models
{
    public class DeliveryResult
    {
        public bool Success { get; set; }
        public string AdapterName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTimeOffset CompletedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public static DeliveryResult Ok(string adapter, string message = "") =>
            new() { Success = true, AdapterName = adapter, Message = message };

        public static DeliveryResult Fail(string adapter, string message) =>
            new() { Success = false, AdapterName = adapter, Message = message };
    }
}
