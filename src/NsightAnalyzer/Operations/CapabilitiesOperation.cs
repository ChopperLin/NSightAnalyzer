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
        var preferred = ViewerHostTargets.Preferred;
        return OperationResult.Success(new
        {
            tool = VersionOperation.Describe(),
            sourcePolicy = "unsupportedVersionPinnedViewer",
            decoder = new
            {
                adapter = preferred.AdapterName,
                productVersion = preferred.ProductVersion,
                productBuild = preferred.ProductBuild,
                productSku = preferred.ProductSku,
                qtVersion = preferred.QtRuntimeVersion,
                bridgeVersion = ViewerProbeRunner.ExpectedBridgeVersion,
                selectionPolicy = "firstInstalledTargetInTableOrder",
                targets = ViewerHostTargets.All.Select(target => new
                {
                    adapter = target.AdapterName,
                    compatibilityProfile = target.CompatibilityProfile,
                    productVersion = target.ProductVersion,
                    productBuild = target.ProductBuild,
                    productSku = target.ProductSku,
                    qtRuntimeVersion = target.QtRuntimeVersion,
                    qtCompileVersion = target.QtCompileVersion,
                }).ToArray(),
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
