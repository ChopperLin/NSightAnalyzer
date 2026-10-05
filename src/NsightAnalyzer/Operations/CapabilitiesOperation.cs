using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class CapabilitiesOperation
{
    public static OperationResult Execute(
        IReadOnlyList<OperationDescriptor> descriptors,
        bool detail = false)
    {
        var ordered = descriptors.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        return OperationResult.Success(new
        {
            tool = VersionOperation.Describe(),
            sourcePolicy = "unsupportedVersionPinnedViewer",
            decoder = new
            {
                adapter = "NsightViewer2026_2",
                productVersion = "2026.2.0.0",
                productBuild = "37991608",
                productSku = "public-release",
                qtVersion = "6.8.1",
                bridgeVersion = ViewerProbeRunner.ExpectedBridgeVersion,
            },
            help = "describe <operation>",
            operations = new Page<object>(
                ordered.Select(item => detail ? (object)item : new
                {
                    item.Id,
                    item.Layer,
                    item.Effect,
                    item.OpensViewer,
                    item.Description,
                }).ToArray(),
                0,
                ordered.Length,
                ordered.Length,
                ordered.Length,
                false,
                null),
        });
    }
}
