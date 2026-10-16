namespace Trellis.ServiceLevelIndicators;

/// <summary>
/// Excludes a controller or action from automatic SLI measurement.
/// Exclusion takes precedence over automatic emission and explicit SLI inclusion metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ExcludeFromServiceLevelIndicatorAttribute : Attribute
{
}
