namespace SomeDataProvider.DataStorage.Definitions;

public sealed class ProbeRejectedException() : InvalidOperationException("Probe source budget is unavailable, expired or exhausted.");
