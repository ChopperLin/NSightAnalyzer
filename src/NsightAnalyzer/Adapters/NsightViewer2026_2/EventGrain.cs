namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

/// <summary>
/// The one place that knows how a Viewer range name maps to a grain.
/// <para>
/// A range is either named after a D3D12 API object or call the capture
/// structures itself around ("container"), or after the caller's own
/// instrumentation ("marker"). This is a naming fact, not a judgement about
/// which rows matter.
/// </para>
/// <para>
/// The same vocabulary is used two ways: to classify a row that came back, and
/// to push the equivalent test into the bridge so a large trace does not have
/// to travel row by row. Both directions must agree, so both read these
/// patterns rather than restating them. The bridge is told only "these
/// patterns, this column" and never learns what a container is.
/// </para>
/// </summary>
internal static class EventGrain
{
    public const string Container = "container";
    public const string Marker = "marker";
    public const string All = "all";

    /// <summary>
    /// Name prefixes that identify a D3D12 call or object. Case-sensitive:
    /// the Viewer prints API names in their declared casing, and a
    /// case-insensitive test would capture caller markers that merely start
    /// with the same word.
    /// </summary>
    public static readonly string[] ContainerPrefixes =
    [
        "ID3D12", "ExecuteCommandLists",
        "Draw", "Dispatch", "Clear", "Copy", "Resolve", "Set", "Begin", "End",
        "ResourceBarrier", "Present", "IASet", "OMSet", "RSSet", "ExecuteIndirect",
    ];

    /// <summary>
    /// Queue rows carry the queue name before "CommandQueue", so they are the
    /// one container kind that cannot be recognized by a prefix.
    /// </summary>
    public static readonly string[] ContainerSubstrings = ["CommandQueue"];

    public static bool IsContainer(string description) =>
        ContainerPrefixes.Any(prefix =>
            description.StartsWith(prefix, StringComparison.Ordinal)) ||
        ContainerSubstrings.Any(substring =>
            description.Contains(substring, StringComparison.Ordinal));

    public static string Classify(string description) =>
        IsContainer(description) ? Container : Marker;

    public static bool IsSelected(string grain, string description) =>
        grain == All || Classify(description) == grain;
}
