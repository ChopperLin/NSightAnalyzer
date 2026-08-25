using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

internal sealed class BridgeSchemaException : Exception
{
    public BridgeSchemaException(string message) : base(message) { }
}

internal sealed class BridgeFactNotFoundException : Exception
{
    public BridgeFactNotFoundException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed class BridgeFactUnavailableException : Exception
{
    public BridgeFactUnavailableException(
        string code,
        string message,
        string? detail = null) : base(message)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }

    public string? Detail { get; }
}

/// <summary>
/// The selected event is a single command, not a pass/marker range. Range-grain PC sampling
/// facts have no reliable denominator at that scope (capability matrix SCP-002), so the
/// projection refuses instead of returning a plausible value.
/// </summary>
internal sealed class BridgeScopeUnsupportedException : Exception
{
    public BridgeScopeUnsupportedException(
        string code,
        string message,
        string? detail = null) : base(message)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }

    public string? Detail { get; }
}

internal static class BridgeProjection
{
    public static TraceEventsValue ProjectEvents(
        JsonElement root,
        int requestedCursor,
        int requestedLimit)
    {
        var views = RequiredArray(root, "eventViews");
        if (views.GetArrayLength() != 1)
        {
            throw new BridgeSchemaException(
                $"Expected one Event List view, observed {views.GetArrayLength()}.");
        }

        var export = RequiredObject(views[0], "export");
        if (!RequiredBoolean(export, "totalCountExact"))
        {
            throw new BridgeSchemaException("The Event List total count is not exact.");
        }
        var offset = RequiredInt32(export, "offset");
        var rawLimit = RequiredInt32(export, "limit");
        if (offset != requestedCursor || rawLimit != requestedLimit)
        {
            throw new BridgeSchemaException(
                $"Event page mismatch: requested {requestedCursor}/{requestedLimit}, " +
                $"observed {offset}/{rawLimit}.");
        }

        var nodes = RequiredArray(export, "nodes");
        var items = new List<EventFact>(nodes.GetArrayLength());
        foreach (var node in nodes.EnumerateArray())
        {
            var description = CellText(node, 0) ?? string.Empty;
            var key = new EventKey(
                RequiredInt32(node, "ordinal"),
                RequiredInt32Array(node, "path"),
                CellText(node, 1),
                description);
            items.Add(new(
                key,
                RequiredInt32(node, "depth"),
                RequiredInt32(node, "childCount"),
                EmptyToNull(CellText(node, 4)),
                EmptyToNull(CellText(node, 5)),
                EmptyToNull(CellText(node, 6)),
                EmptyToNull(CellText(node, 7)),
                EmptyToNull(CellText(node, 8)),
                EmptyToNull(CellText(node, 9)),
                EmptyToNull(CellText(node, 10))));
        }

        var total = RequiredInt32(export, "totalCount");
        var returned = RequiredInt32(export, "returnedCount");
        if (returned != items.Count)
        {
            throw new BridgeSchemaException(
                $"Event returnedCount {returned} does not match {items.Count} projected nodes.");
        }
        var hasMore = RequiredBoolean(export, "hasMore");
        int? next = hasMore ? checked(offset + returned) : null;
        return new(new(
            items,
            offset,
            rawLimit,
            total,
            returned,
            hasMore,
            next));
    }

    /// <summary>
    /// Projects the range-grain skeleton. The bridge filters rows; this keeps
    /// each row's true preorder ordinal, so an outline ordinal addresses the
    /// same event as trace.events without a second lookup.
    /// </summary>
    public static TraceOutlineValue ProjectOutline(
        JsonElement root,
        int requestedCursor,
        int requestedLimit)
    {
        var views = RequiredArray(root, "eventViews");
        if (views.GetArrayLength() != 1)
        {
            throw new BridgeSchemaException(
                $"Expected one Event List view, observed {views.GetArrayLength()}.");
        }

        var export = RequiredObject(views[0], "export");
        if (!RequiredBoolean(export, "totalCountExact"))
        {
            throw new BridgeSchemaException("The Event List total count is not exact.");
        }
        var offset = RequiredInt32(export, "offset");
        var rawLimit = RequiredInt32(export, "limit");
        if (offset != requestedCursor || rawLimit != requestedLimit)
        {
            throw new BridgeSchemaException(
                $"Outline page mismatch: requested {requestedCursor}/{requestedLimit}, " +
                $"observed {offset}/{rawLimit}.");
        }
        if (RequiredInt32(export, "filterColumn") != 1)
        {
            throw new BridgeSchemaException(
                "The outline projection requires the bridge event-range filter.");
        }

        var nodes = RequiredArray(export, "nodes");
        var items = new List<OutlineFact>(nodes.GetArrayLength());
        foreach (var node in nodes.EnumerateArray())
        {
            var description = CellText(node, 0) ?? string.Empty;
            var eventRange = CellText(node, 1);
            if (eventRange is null || !eventRange.Contains('-', StringComparison.Ordinal))
            {
                throw new BridgeSchemaException(
                    "The outline filter returned a row that is not a range.");
            }
            var key = new EventKey(
                RequiredInt32(node, "ordinal"),
                RequiredInt32Array(node, "path"),
                eventRange,
                description);
            items.Add(new(
                key,
                RequiredInt32(node, "depth"),
                RequiredInt32(node, "childCount"),
                OutlineGrain(description),
                EmptyToNull(CellText(node, 8)),
                EmptyToNull(CellText(node, 9)),
                EmptyToNull(CellText(node, 10))));
        }

        var total = RequiredInt32(export, "totalCount");
        var returned = RequiredInt32(export, "returnedCount");
        if (returned != items.Count)
        {
            throw new BridgeSchemaException(
                $"Outline returnedCount {returned} does not match {items.Count} projected nodes.");
        }
        var hasMore = RequiredBoolean(export, "hasMore");
        int? next = hasMore ? checked(offset + returned) : null;
        return new(
            RequiredInt32(export, "visitedCount"),
            new(items, offset, rawLimit, total, returned, hasMore, next));
    }

    public static EventParametersValue ProjectEventParameters(
        JsonElement root,
        int? requestedOrdinal,
        string? requestedPath)
    {
        var scope = ProjectVerifiedScope(root, requestedOrdinal, requestedPath);
        var models = RequiredArray(root, "models");
        if (models.GetArrayLength() != 1)
        {
            throw new BridgeSchemaException(
                $"Expected one Event Parameters model, observed {models.GetArrayLength()}.");
        }

        var export = RequiredObject(models[0], "export");
        ValidateCompleteModel(export, "Event Parameters");
        var nodes = RequiredArray(export, "nodes");
        if (nodes.GetArrayLength() == 0)
        {
            throw new BridgeSchemaException("The Event Parameters model is unexpectedly empty.");
        }

        var namesByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes.EnumerateArray())
        {
            namesByPath[PathKey(RequiredInt32Array(node, "path"))] =
                CellText(node, 0) ?? string.Empty;
        }

        var command = string.Empty;
        var parameters = new List<EventParameterFact>();
        foreach (var node in nodes.EnumerateArray())
        {
            var depth = RequiredInt32(node, "depth");
            var name = CellText(node, 0) ?? string.Empty;
            if (depth == 0 && command.Length == 0)
            {
                command = name;
                continue;
            }
            if (depth == 0)
            {
                throw new BridgeSchemaException(
                    "The Event Parameters model contains multiple root commands.");
            }

            var numericPath = RequiredInt32Array(node, "path");
            var semanticPath = new List<string>();
            for (var length = 2; length <= numericPath.Count; length++)
            {
                var prefix = PathKey(numericPath.Take(length));
                if (namesByPath.TryGetValue(prefix, out var segment))
                {
                    semanticPath.Add(segment);
                }
            }
            var valueElement = CellValue(node, 1, "display");
            parameters.Add(new(
                semanticPath,
                name,
                ToSafeValue(valueElement),
                SafeValueKind(valueElement),
                EmptyToNull(CellText(node, 2)),
                depth));
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new BridgeSchemaException("The Event Parameters command name is missing.");
        }
        ValidateEventParameterDomains(command, parameters);
        return new(scope, command, parameters);
    }

    private static void ValidateEventParameterDomains(
        string command,
        IReadOnlyList<EventParameterFact> parameters)
    {
        const long d3d12DispatchMaximumThreadGroupsPerDimension = 65_535;
        if (!command.Equals("Dispatch", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var parameter in parameters)
        {
            if (parameter.Name is not (
                    "ThreadGroupCountX" or
                    "ThreadGroupCountY" or
                    "ThreadGroupCountZ") ||
                parameter.Value is not long observed ||
                observed <= d3d12DispatchMaximumThreadGroupsPerDimension)
            {
                continue;
            }

            throw new BridgeFactUnavailableException(
                "trace.event_parameter_out_of_domain",
                "The Viewer-decoded Dispatch parameter is outside the D3D12 semantic domain.",
                $"parameter={parameter.Name}; observed={observed}; " +
                $"maximum={d3d12DispatchMaximumThreadGroupsPerDimension}");
        }
    }

    public static RangeMetricsValue ProjectRangeMetrics(
        JsonElement root,
        int? requestedOrdinal,
        string? requestedPath,
        IReadOnlyList<string> requestedTables,
        int cursor,
        int limit)
    {
        var scope = ProjectVerifiedScope(root, requestedOrdinal, requestedPath);
        RequireRangeGrainScope(scope, "Range metrics");
        var views = RequiredArray(root, "metricViews");
        var rawTables = new List<RawMetricTable>(views.GetArrayLength());
        var tableOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var rawIndex = 0;
        foreach (var view in views.EnumerateArray())
        {
            var export = RequiredObject(view, "export");
            ValidateCompleteModel(export, "Warp Metrics");
            var headers = HeadersByColumn(export);
            if (!headers.TryGetValue(0, out var tableName) ||
                string.IsNullOrWhiteSpace(tableName))
            {
                throw new BridgeSchemaException("A Warp Metrics table has no semantic name.");
            }
            var occurrence = tableOccurrences.GetValueOrDefault(tableName);
            tableOccurrences[tableName] = occurrence + 1;
            rawTables.Add(new(tableName, occurrence, rawIndex++, export, headers));
        }

        rawTables.Sort((left, right) =>
        {
            var name = string.CompareOrdinal(left.Name, right.Name);
            return name != 0 ? name : left.Occurrence.CompareTo(right.Occurrence);
        });

        var selectedTables = requestedTables.Count == 0
            ? rawTables
            : rawTables.Where(table => requestedTables.Any(requested =>
                    requested.Equals(table.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        if (requestedTables.Count > 0 && selectedTables.Count == 0)
        {
            throw new BridgeFactNotFoundException(
                "trace.metric_table_not_found",
                "None of the exact metric table filters exist in the selected range.");
        }

        var metrics = new List<RangeMetricFact>();
        var selectedRowCount = 0;
        var sourceOrdinal = 0;
        foreach (var table in rawTables)
        {
            var includeTable = selectedTables.Contains(table);
            var headerOccurrences = OccurrencesByColumn(table.Headers);
            var rowOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var nodes = RequiredArray(table.Export, "nodes");
            if (includeTable)
            {
                selectedRowCount += RequiredInt32(table.Export, "totalCount");
            }

            foreach (var node in nodes.EnumerateArray())
            {
                var rowName = CellText(node, 0) ?? string.Empty;
                var rowOccurrence = rowOccurrences.GetValueOrDefault(rowName);
                rowOccurrences[rowName] = rowOccurrence + 1;
                var description = EmptyToNull(CellTooltipText(node, 0));
                var cells = RequiredArray(node, "cells");
                foreach (var cell in cells.EnumerateArray())
                {
                    var column = RequiredInt32(cell, "column");
                    if (column == 0)
                    {
                        continue;
                    }
                    var currentSourceOrdinal = sourceOrdinal++;
                    if (!includeTable || !table.Headers.TryGetValue(column, out var columnName))
                    {
                        continue;
                    }

                    var display = OptionalProperty(cell, "display");
                    var precise = OptionalProperty(cell, "tooltip");
                    var displayValue = ToSafeValue(display);
                    metrics.Add(new(
                        table.Name,
                        table.Occurrence,
                        rowName,
                        rowOccurrence,
                        columnName,
                        headerOccurrences[column],
                        currentSourceOrdinal,
                        displayValue,
                        ToSafeValue(precise),
                        SafeValueKind(display),
                        UnitFromHeader(columnName),
                        description,
                        Availability(displayValue)));
                }
            }
        }

        var total = metrics.Count;
        RangeMetricFact[] pageItems = cursor >= total
            ? []
            : metrics.Skip(cursor).Take(limit).ToArray();
        int? nextCursor = cursor + pageItems.Length < total
            ? cursor + pageItems.Length
            : null;
        return new(
            scope,
            selectedTables.Count,
            selectedRowCount,
            new(
                pageItems,
                cursor,
                limit,
                total,
                pageItems.Length,
                nextCursor is not null,
                nextCursor));
    }

    public static RangeShadersValue ProjectRangeShaders(
        JsonElement root,
        int? requestedOrdinal,
        string? requestedPath,
        string? requestedHash,
        int? requestedHashOccurrence,
        int cursor,
        int limit)
    {
        var scope = ProjectVerifiedScope(root, requestedOrdinal, requestedPath);
        RequireRangeGrainScope(scope, "Range shader inventory");
        var models = RequiredArray(root, "models");
        if (models.GetArrayLength() != 1)
        {
            throw new BridgeSchemaException(
                $"Expected one shader inventory model, observed {models.GetArrayLength()}.");
        }

        var export = RequiredObject(models[0], "export");
        ValidateCompleteModel(export, "Shader inventory");
        var nodes = RequiredArray(export, "nodes");
        var nodesByPath = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var node in nodes.EnumerateArray())
        {
            nodesByPath[PathKey(RequiredInt32Array(node, "path"))] = node;
        }

        var hashOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shaders = new List<RangeShaderFact>();
        foreach (var node in nodes.EnumerateArray())
        {
            var childCount = RequiredInt32(node, "childCount");
            var stage = CellText(node, 0) ?? string.Empty;
            var name = CellText(node, 2) ?? string.Empty;
            var hash = NormalizeShaderHash(CellText(node, 3));
            if (childCount > 0 && hash is null)
            {
                continue;
            }

            var path = RequiredInt32Array(node, "path");
            string? pipeline = null;
            if (path.Count > 1)
            {
                var parentKey = PathKey(path.Take(path.Count - 1));
                if (nodesByPath.TryGetValue(parentKey, out var parent))
                {
                    pipeline = EmptyToNull(CellText(parent, 2));
                }
            }

            var occurrenceKey = hash is null ? string.Empty : $"{stage}|{hash}";
            var hashOccurrence = hash is null
                ? 0
                : hashOccurrences.GetValueOrDefault(occurrenceKey);
            if (hash is not null)
            {
                hashOccurrences[occurrenceKey] = hashOccurrence + 1;
            }
            if (requestedHash is not null &&
                (!requestedHash.Equals(hash, StringComparison.OrdinalIgnoreCase) ||
                 requestedHashOccurrence is not null &&
                 requestedHashOccurrence.Value != hashOccurrence))
            {
                continue;
            }

            var correlationTooltip = CellTooltipText(node, 4) ?? string.Empty;
            var correlationName = EmptyToNull(CellText(node, 4));
            var correlation = new ShaderCorrelationInfo(
                correlationName is null or "--" or "None" or "N/A"
                    ? "unavailable"
                    : "available",
                correlationName,
                EmptyToNull(CellText(node, 5)),
                MissingPdb(correlationTooltip),
                correlationTooltip.Contains(
                    "Successfully got SASS debug info", StringComparison.Ordinal),
                correlationTooltip.Contains(
                    "Successfully got SASS to IL line table", StringComparison.Ordinal));

            var instructionTooltip = CellTooltipText(node, 12) ?? string.Empty;
            var stalls = new List<ShaderStallFact>();
            for (var rank = 1; rank <= 3; rank++)
            {
                var column = 13 + rank;
                var count = ToInt64(CellValue(node, column, "display"));
                if (count is null)
                {
                    continue;
                }
                var reason = FirstStallReason(CellTooltipText(node, column));
                stalls.Add(new(rank, reason ?? $"rank-{rank}", count.Value));
            }

            shaders.Add(new(
                new(
                    RequiredInt32(node, "ordinal"),
                    path,
                    stage,
                    name,
                    hash,
                    hashOccurrence,
                    pipeline),
                SumIntegerArray(CellValue(node, 6, "display")),
                ToInt32(CellValue(node, 7, "display")),
                ToInt32(CellValue(node, 8, "display")),
                EmptyToNull(CellText(node, 9)),
                EmptyToNull(CellText(node, 10)),
                ToInt32(CellValue(node, 11, "display")),
                StaticInstructionCount(instructionTooltip),
                SumIntegerArray(CellValue(node, 13, "display")),
                AverageWarpLatency(CellTooltipText(node, 38)),
                correlation,
                ParseInstructionMix(instructionTooltip),
                stalls));
        }

        if (requestedHash is not null && shaders.Count == 0)
        {
            throw new BridgeFactNotFoundException(
                "trace.shader_not_found",
                "The exact shader hash/occurrence was not found in the selected range.");
        }

        var total = shaders.Count;
        RangeShaderFact[] pageItems = cursor >= total
            ? []
            : shaders.Skip(cursor).Take(limit).ToArray();
        int? next = cursor + pageItems.Length < total
            ? cursor + pageItems.Length
            : null;
        return new(
            scope,
            new(
                pageItems,
                cursor,
                limit,
                total,
                pageItems.Length,
                next is not null,
                next));
    }

    public static RangeInstructionMixValue ProjectRangeInstructionMix(
        JsonElement root,
        int? requestedOrdinal,
        string? requestedPath,
        int cursor,
        int limit)
    {
        var scope = ProjectVerifiedScope(root, requestedOrdinal, requestedPath);
        RequireRangeGrainScope(scope, "Range instruction mix");
        var models = RequiredArray(root, "models");
        if (models.GetArrayLength() != 1 ||
            RequiredString(models[0], "class") !=
                "NV::ShaderProfiler::UI::InstructionMixModel")
        {
            throw new BridgeSchemaException(
                $"Expected one range InstructionMixModel, observed {models.GetArrayLength()}.");
        }
        var export = RequiredObject(models[0], "export");
        ValidateCompleteModel(export, "Range instruction mix");
        var headers = HeadersByColumn(export);
        var expectedHeaders = new[]
        {
            "Pipe", "Family", "Operation", "Samples", "Instructions",
        };
        for (var column = 0; column < expectedHeaders.Length; column++)
        {
            if (!headers.TryGetValue(column, out var observed) ||
                observed != expectedHeaders[column])
            {
                throw new BridgeSchemaException(
                    "The range Instruction Mix columns changed.");
            }
        }

        var facts = new List<RangeInstructionMixFact>();
        foreach (var node in RequiredArray(export, "nodes").EnumerateArray())
        {
            var pipe = CellText(node, 0) ?? string.Empty;
            var family = CellText(node, 1) ?? string.Empty;
            var instructionCount = ToInt64(CellValue(node, 4, "display"));
            if (string.IsNullOrWhiteSpace(pipe) ||
                string.IsNullOrWhiteSpace(family) ||
                instructionCount is null)
            {
                throw new BridgeSchemaException(
                    "A range Instruction Mix row has an incomplete identity/value.");
            }
            var stalls = ProjectStalls(CellValue(node, 3, "display"));
            facts.Add(new(
                RequiredInt32(node, "ordinal"),
                pipe,
                family,
                EmptyToNull(CellText(node, 2)),
                stalls.Sum(stall => stall.SampleCount),
                instructionCount.Value,
                stalls));
        }

        var total = facts.Count;
        RangeInstructionMixFact[] page = cursor >= total
            ? []
            : facts.Skip(cursor).Take(limit).ToArray();
        int? nextCursor = cursor + page.Length < total
            ? cursor + page.Length
            : null;
        return new(
            scope,
            new(
                page,
                cursor,
                limit,
                total,
                page.Length,
                nextCursor is not null,
                nextCursor));
    }

    public static ShaderSourceValue ProjectShaderSource(
        JsonElement root,
        int? requestedOrdinal,
        string? requestedPath,
        string requestedHash,
        int requestedHashOccurrence,
        int cursor,
        int limit)
    {
        var scope = ProjectVerifiedScope(root, requestedOrdinal, requestedPath);
        if (!RequiredBoolean(root, "modelSelectionMatchesTarget") ||
            !RequiredBoolean(root, "comboSelectionMatchesTarget") ||
            !RequiredBoolean(root, "modelSelectionTargetProviderReady"))
        {
            throw new BridgeSchemaException(
                "The final shader/source provider does not match the requested shader.");
        }

        var target = RequiredObject(root, "targetModelSelection");
        var targetParent = RequiredObject(root, "targetModelSelectionParent");
        var targetCells = RequiredArray(target, "cells");
        var parentCells = RequiredArray(targetParent, "cells");
        var targetHash = NormalizeShaderHash(ArrayText(targetCells, 3));
        var stage = ArrayText(targetCells, 0) ?? string.Empty;
        var shaderName = ArrayText(targetCells, 2) ?? string.Empty;
        var pipeline = ArrayText(parentCells, 2) ?? string.Empty;
        if (!requestedHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(stage) ||
            string.IsNullOrWhiteSpace(shaderName) ||
            string.IsNullOrWhiteSpace(pipeline))
        {
            throw new BridgeSchemaException(
                "The selected shader identity is incomplete or differs from the request.");
        }

        var selector = RequiredObject(root, "modelSelector");
        if (RequiredString(selector, "kind") != "match" ||
            RequiredInt32(selector, "column") != 3 ||
            RequiredInt32(selector, "occurrence") != requestedHashOccurrence ||
            !requestedHash.Equals(
                RequiredString(selector, "value"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeSchemaException(
                "The bridge shader selector differs from the request.");
        }

        var derivedShaderName = RequiredString(
            root, "comboSelectionDerivedShaderName");
        var derivedPipelineName = RequiredString(
            root, "comboSelectionDerivedPipelineName");
        var currentCombo = RequiredObject(root, "currentComboSelection");
        if (!shaderName.Equals(derivedShaderName, StringComparison.Ordinal) ||
            !pipeline.Equals(derivedPipelineName, StringComparison.Ordinal) ||
            !RequiredString(currentCombo, "text").Equals(
                $"{pipeline} - {shaderName}",
                StringComparison.Ordinal))
        {
            throw new BridgeSchemaException(
                "The Source panel shader identity differs from the selected shader.");
        }

        var sourceModels = new List<RawSourceView>();
        var rawIndex = 0;
        foreach (var model in RequiredArray(root, "models").EnumerateArray())
        {
            if (!RequiredString(model, "class").Equals(
                    "NV::SourceCorrelation::SourceModel",
                    StringComparison.Ordinal))
            {
                continue;
            }
            var export = RequiredObject(model, "export");
            ValidateCompleteModel(export, "Shader source");
            var nodes = RequiredArray(export, "nodes");
            var label = nodes.GetArrayLength() == 0
                ? string.Empty
                : CellText(nodes[0], 3) ?? string.Empty;
            sourceModels.Add(new(
                rawIndex++,
                RequiredString(model, "parentId"),
                SourceKind(label),
                0,
                label,
                export,
                HeadersByColumn(export)));
        }
        if (sourceModels.Count == 0)
        {
            throw new BridgeFactNotFoundException(
                "trace.shader_source_unavailable",
                "The selected shader did not expose a SourceModel.");
        }

        var matchingParents = sourceModels
            .Where(view => SourceModelContainsHash(view.Export, requestedHash))
            .Select(view => view.ParentId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matchingParents.Length == 0)
        {
            throw new BridgeFactNotFoundException(
                "trace.shader_source_unavailable",
                "No source provider contains the selected shader hash.");
        }
        if (matchingParents.Length != 1)
        {
            throw new BridgeSchemaException(
                "Multiple source providers contain the selected shader hash.");
        }

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var targetViews = sourceModels
            .Where(view => view.ParentId == matchingParents[0])
            .OrderBy(view => SourceKindOrder(view.Kind))
            .ThenBy(view => view.RawIndex)
            .Select(view =>
            {
                var occurrence = occurrences.GetValueOrDefault(view.Kind);
                occurrences[view.Kind] = occurrence + 1;
                return view with { Occurrence = occurrence };
            })
            .ToArray();

        var viewFacts = new List<ShaderSourceViewFact>(targetViews.Length);
        foreach (var view in targetViews)
        {
            long attributedSamples = 0;
            long instructionCount = 0;
            var hasInstructionCount = false;
            var addressRows = 0;
            foreach (var node in RequiredArray(view.Export, "nodes").EnumerateArray())
            {
                attributedSamples = checked(
                    attributedSamples + SumIntegerArray(CellValue(node, 4, "display")));
                if (view.Headers.TryGetValue(26, out var instructionHeader) &&
                    instructionHeader == "Instruction Mix" &&
                    TrySumIntegerArray(
                        CellValue(node, 26, "display"),
                        out var rowInstructionCount))
                {
                    instructionCount = checked(instructionCount + rowInstructionCount);
                    hasInstructionCount = true;
                }
                if (SourceAddress(view.Kind, CellText(node, 2), CellText(node, 3))
                    is not null)
                {
                    addressRows++;
                }
            }

            var opcodeAvailable = view.Kind != "sass"
                ? "notApplicable"
                : SourceModelHasOpcodeText(view)
                    ? "available"
                    : "unavailableInViewerSku";
            viewFacts.Add(new(
                view.Kind,
                view.Occurrence,
                view.Label,
                RequiredInt32(view.Export, "totalCount"),
                attributedSamples,
                hasInstructionCount ? instructionCount : null,
                addressRows,
                opcodeAvailable));
        }

        var total = targetViews.Sum(view => RequiredInt32(view.Export, "totalCount"));
        var page = new List<ShaderSourceRowFact>(Math.Min(limit, total));
        var sourceOrdinal = 0;
        foreach (var view in targetViews)
        {
            foreach (var node in RequiredArray(view.Export, "nodes").EnumerateArray())
            {
                if (sourceOrdinal >= cursor && page.Count < limit)
                {
                    page.Add(ProjectSourceRow(
                        node, view, sourceOrdinal));
                }
                sourceOrdinal++;
            }
        }
        int? nextCursor = cursor + page.Count < total
            ? cursor + page.Count
            : null;

        return new(
            scope,
            new(
                RequiredInt32Array(target, "path"),
                stage,
                shaderName,
                targetHash!,
                requestedHashOccurrence,
                pipeline),
            viewFacts,
            targetViews.Any(view => view.Kind == "hlsl")
                ? "available"
                : "unavailable",
            new(
                page,
                cursor,
                limit,
                total,
                page.Count,
                nextCursor is not null,
                nextCursor));
    }

    public static TraceAnalysisValue ProjectTraceAnalysis(
        JsonElement root,
        int? requestedOrdinal,
        string? requestedPath,
        int cursor,
        int limit)
    {
        var seedScope = ProjectVerifiedScope(
            root, requestedOrdinal, requestedPath);
        var targetAction = RequiredObject(root, "targetAction");
        if (RequiredString(targetAction, "text") != "Trace Analysis...")
        {
            throw new BridgeSchemaException(
                "The Viewer Trace Analysis action identity changed.");
        }

        var ranges = new List<TraceAnalysisRangeFact>();
        var annotations = new List<TraceAnalysisAnnotationFact>();
        var annotationOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var annotationSourceOrdinal = 0;
        var foundAnalysisModel = false;
        foreach (var model in RequiredArray(root, "models").EnumerateArray())
        {
            var modelClass = RequiredString(model, "class");
            var export = RequiredObject(model, "export");
            if (modelClass == "NV::WarpViz::MarkerTreeModel")
            {
                foundAnalysisModel = true;
                ValidateCompleteModel(export, "Trace Analysis ranges");
                var headers = HeadersByColumn(export);
                if (!headers.TryGetValue(0, out var rangeHeader) ||
                    rangeHeader != "Range" ||
                    !headers.TryGetValue(1, out var durationHeader) ||
                    durationHeader != "ms")
                {
                    throw new BridgeSchemaException(
                        "The Trace Analysis range columns changed.");
                }
                foreach (var node in RequiredArray(export, "nodes").EnumerateArray())
                {
                    var rangeName = CellText(node, 0) ?? string.Empty;
                    var issues = new List<TraceAnalysisIssueFact>(3);
                    for (var rank = 1; rank <= 3; rank++)
                    {
                        var column = rank + 3;
                        var name = EmptyToNull(CellText(node, column));
                        if (name is null)
                        {
                            continue;
                        }
                        issues.Add(new(
                            rank,
                            name,
                            IssueScorePercent(CellTooltipText(node, column))));
                    }
                    ranges.Add(new(
                        RequiredInt32(node, "ordinal"),
                        RequiredInt32Array(node, "path"),
                        FrameIndex(rangeName),
                        rangeName,
                        DecimalValue(CellText(node, 1)),
                        DecimalValue(CellText(node, 2), trimPercent: true),
                        EmptyToNull(CellText(node, 3)),
                        issues));
                }
            }
            else if (modelClass == "NV::WarpViz::AnnotationsTreeModel")
            {
                foundAnalysisModel = true;
                ValidateCompleteModel(export, "Trace Analysis annotations");
                foreach (var node in RequiredArray(export, "nodes").EnumerateArray())
                {
                    var valueElement = CellValue(node, 1, "display");
                    var value = ToSafeValue(valueElement);
                    var name = CellText(node, 0) ?? string.Empty;
                    var occurrence = annotationOccurrences.GetValueOrDefault(name);
                    annotationOccurrences[name] = occurrence + 1;
                    annotations.Add(new(
                        annotationSourceOrdinal++,
                        name,
                        occurrence,
                        value,
                        EmptyToNull(CellText(node, 2)),
                        EmptyToNull(CellText(node, 3)),
                        Availability(value)));
                }
            }
        }
        if (!foundAnalysisModel)
        {
            throw new BridgeFactNotFoundException(
                "trace.analysis_unavailable",
                "The Viewer did not expose a Trace Analysis result model.");
        }

        var total = ranges.Count;
        TraceAnalysisRangeFact[] page = cursor >= total
            ? []
            : ranges.Skip(cursor).Take(limit).ToArray();
        int? nextCursor = cursor + page.Length < total
            ? cursor + page.Length
            : null;
        var annotationAvailability = annotations.Any(annotation =>
            annotation.Availability == "available")
            ? "available"
            : "unavailable";
        return new(
            seedScope,
            new(
                page,
                cursor,
                limit,
                total,
                page.Length,
                nextCursor is not null,
                nextCursor),
            annotationAvailability,
            annotationAvailability == "available" ? annotations : []);
    }

    private static ShaderSourceRowFact ProjectSourceRow(
        JsonElement node,
        RawSourceView view,
        int sourceOrdinal)
    {
        var rowOrdinal = RequiredInt32(node, "ordinal");
        var sourceText = EmptyToNull(CellText(node, 3));
        var address = SourceAddress(view.Kind, CellText(node, 2), sourceText);
        var lineNumber = SourceLineNumber(
            view.Kind, CellText(node, 0), CellText(node, 2), address);
        var rowKind = SourceRowKind(
            view.Kind, rowOrdinal, sourceText, address, lineNumber);
        var stalls = ProjectStalls(CellValue(node, 4, "display"));

        long? instructionCount = null;
        if (view.Headers.TryGetValue(26, out var column26) &&
            column26 == "Instruction Mix" &&
            TrySumIntegerArray(
                CellValue(node, 26, "display"), out var instructionTotal))
        {
            instructionCount = instructionTotal;
        }

        long? dependencySamples = null;
        if (view.Headers.TryGetValue(27, out var column27) &&
            column27 == "Dependency-Attributed Samples" &&
            TrySumIntegerArray(
                CellValue(node, 27, "display"), out var dependencyTotal))
        {
            dependencySamples = dependencyTotal;
        }

        var liveRegistersColumn = view.Headers
            .Where(pair => pair.Value == "Live Registers")
            .Select(pair => (int?)pair.Key)
            .FirstOrDefault();
        return new(
            sourceOrdinal,
            view.Kind,
            view.Occurrence,
            rowOrdinal,
            RequiredInt32Array(node, "path"),
            rowKind,
            lineNumber,
            address,
            sourceText,
            stalls.Sum(stall => stall.SampleCount),
            instructionCount,
            dependencySamples,
            liveRegistersColumn is null
                ? null
                : ToInt32(CellValue(node, liveRegistersColumn.Value, "display")),
            HeaderValue(view, node, "Pipe"),
            HeaderValue(view, node, "Family"),
            HeaderValue(view, node, "Operation"),
            stalls);
    }

    private static readonly string[] StallReasons =
    [
        "Selected",
        "Not Selected",
        "Thread Barrier",
        "Sleep",
        "Branch Resolving",
        "Wait",
        "No Instructions",
        "Dispatch Stall",
        "Math Pipe Throttle",
        "Short Scoreboard",
        "MIO Throttle",
        "Drain",
        "Memory Barrier",
        "Long Scoreboard",
        "Texture Throttle",
        "LG Throttle",
        "Miscellaneous",
    ];

    private static IReadOnlyList<ShaderStallFact> ProjectStalls(
        JsonElement element)
    {
        var values = new List<(int Index, long Count)>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var value in element.EnumerateArray())
            {
                if (index >= StallReasons.Length)
                {
                    throw new BridgeSchemaException(
                        "A shader-source sample vector has an unknown stall layout.");
                }
                var count = ToInt64(value);
                if (count is > 0)
                {
                    values.Add((index, count.Value));
                }
                index++;
            }
        }
        else
        {
            var count = ToInt64(element);
            if (count is > 0)
            {
                values.Add((0, count.Value));
            }
        }

        return values
            .OrderByDescending(value => value.Count)
            .ThenBy(value => value.Index)
            .Select((value, index) => new ShaderStallFact(
                index + 1,
                StallReasons[value.Index],
                value.Count))
            .ToArray();
    }

    private static bool TrySumIntegerArray(JsonElement element, out long total)
    {
        total = 0;
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() == 0)
            {
                return false;
            }
            foreach (var value in element.EnumerateArray())
            {
                var number = ToInt64(value);
                if (number is null)
                {
                    return false;
                }
                total = checked(total + number.Value);
            }
            return true;
        }

        var scalar = ToInt64(element);
        if (scalar is null)
        {
            return false;
        }
        total = scalar.Value;
        return true;
    }

    private static bool SourceModelContainsHash(
        JsonElement export,
        string shaderHash)
    {
        var sourceToken = shaderHash.StartsWith(
            "0x", StringComparison.OrdinalIgnoreCase)
            ? shaderHash[2..]
            : shaderHash;
        foreach (var node in RequiredArray(export, "nodes").EnumerateArray())
        {
            if (CellText(node, 3)?.Contains(
                    sourceToken, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }
        return false;
    }

    private static string SourceKind(string label)
    {
        if (label.StartsWith("dxil (", StringComparison.OrdinalIgnoreCase))
        {
            return "dxil";
        }
        if (label.StartsWith("SASS", StringComparison.OrdinalIgnoreCase))
        {
            return "sass";
        }
        if (label.StartsWith("HLSL", StringComparison.OrdinalIgnoreCase) ||
            label.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase))
        {
            return "hlsl";
        }
        return "source";
    }

    private static int SourceKindOrder(string kind) => kind switch
    {
        "hlsl" => 0,
        "dxil" => 1,
        "sass" => 2,
        _ => 3,
    };

    private static string? SourceAddress(
        string kind,
        string? column2,
        string? sourceText)
    {
        if (kind != "sass")
        {
            return null;
        }
        var candidate = column2?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Regex.IsMatch(
                candidate,
                "^[0-9a-fA-F]{8,16}$",
                RegexOptions.CultureInvariant))
        {
            return null;
        }
        return $"0x{candidate.ToLowerInvariant()}";
    }

    private static int? SourceLineNumber(
        string kind,
        string? column0,
        string? column2,
        string? address)
    {
        var candidate = kind == "sass" && address is null
            ? column2
            : column0;
        return int.TryParse(
            candidate?.Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var line) && line >= 0
            ? line
            : null;
    }

    private static string SourceRowKind(
        string kind,
        int rowOrdinal,
        string? sourceText,
        string? address,
        int? lineNumber)
    {
        if (rowOrdinal == 0)
        {
            return "viewHeader";
        }
        if (sourceText?.TrimStart().StartsWith("//", StringComparison.Ordinal) == true)
        {
            return "correlationHeader";
        }
        if (address is not null)
        {
            return "instructionAddress";
        }
        if (lineNumber is not null)
        {
            return kind == "sass" ? "correlatedSource" : "sourceLine";
        }
        return sourceText is null ? "empty" : "annotation";
    }

    private static string? HeaderValue(
        RawSourceView view,
        JsonElement node,
        string header)
    {
        var column = view.Headers
            .Where(pair => pair.Value == header)
            .Select(pair => (int?)pair.Key)
            .FirstOrDefault();
        return column is null
            ? null
            : EmptyToNull(CellText(node, column.Value));
    }

    private static bool SourceModelHasOpcodeText(RawSourceView view)
    {
        foreach (var node in RequiredArray(view.Export, "nodes").EnumerateArray())
        {
            if (HeaderValue(view, node, "Pipe") is not null ||
                HeaderValue(view, node, "Family") is not null ||
                HeaderValue(view, node, "Operation") is not null)
            {
                return true;
            }
        }
        return false;
    }

    private static int? FrameIndex(string rangeName)
    {
        var match = Regex.Match(
            rangeName,
            @"^Frame\s+(?<index>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success &&
            int.TryParse(
                match.Groups["index"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index)
            ? index
            : null;
    }

    private static decimal? DecimalValue(
        string? text,
        bool trimPercent = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var normalized = trimPercent
            ? text.Trim().TrimEnd('%')
            : text.Trim();
        return decimal.TryParse(
            normalized,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static decimal? IssueScorePercent(string? tooltip)
    {
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return null;
        }
        var match = Regex.Match(
            tooltip,
            @"^\s*\[(?<score>\d+(?:\.\d+)?)%\]",
            RegexOptions.CultureInvariant);
        return match.Success
            ? DecimalValue(match.Groups["score"].Value)
            : null;
    }

    private static EventKey ProjectVerifiedScope(
        JsonElement root,
        int? requestedOrdinal,
        string? requestedPath)
    {
        if (!RequiredBoolean(root, "selectionMatchesTarget"))
        {
            throw new BridgeSchemaException(
                "The final Viewer selection does not match the requested event.");
        }
        if (!RequiredBoolean(root, "metricsStable"))
        {
            throw new BridgeSchemaException(
                "The selected range metrics did not stabilize.");
        }
        if (root.TryGetProperty("settleTimedOut", out var settleTimedOut) &&
            settleTimedOut.ValueKind == JsonValueKind.True)
        {
            throw new BridgeSchemaException(
                "The selected range reached the settle timeout.");
        }

        var selector = RequiredObject(root, "eventSelector");
        var selectorKind = RequiredString(selector, "kind");
        if (requestedOrdinal is not null)
        {
            if (selectorKind != "ordinal" ||
                RequiredInt32(selector, "value") != requestedOrdinal.Value)
            {
                throw new BridgeSchemaException(
                    "The bridge event ordinal differs from the request.");
            }
        }
        else
        {
            var bridgePath = requestedPath!.Replace('.', '/');
            if (selectorKind != "path" ||
                RequiredString(selector, "value") != bridgePath)
            {
                throw new BridgeSchemaException(
                    "The bridge event path differs from the request.");
            }
        }

        var target = RequiredObject(root, "targetSelection");
        var current = RequiredObject(root, "currentSelection");
        var targetPath = RequiredInt32Array(target, "path");
        var currentPath = RequiredInt32Array(current, "path");
        if (!targetPath.SequenceEqual(currentPath))
        {
            throw new BridgeSchemaException(
                "The observed selection path differs from the target path.");
        }
        if (requestedPath is not null &&
            !targetPath.SequenceEqual(requestedPath.Split('.').Select(int.Parse)))
        {
            throw new BridgeSchemaException(
                "The observed selection path differs from the requested path.");
        }

        var cells = RequiredArray(target, "cells");
        return new(
            requestedOrdinal,
            targetPath,
            ArrayText(cells, 1),
            ArrayText(cells, 0) ?? string.Empty);
    }

    /// <summary>
    /// The Viewer Event List reports an event range as either a single command index ("1606")
    /// or an inclusive span ("1606 - 2474"). Only a span is a pass/marker range whose
    /// PC-sampling denominator is trustworthy. A single command index is a draw/dispatch/barrier
    /// scope, which capability matrix SCP-002 records as unsupported: the observed sample
    /// denominator expands beyond the selected command, so a returned value would look ordinary
    /// while meaning something else.
    /// </summary>
    private static void RequireRangeGrainScope(EventKey scope, string factFamily)
    {
        var eventRange = scope.EventRange?.Trim();
        if (string.IsNullOrEmpty(eventRange))
        {
            throw new BridgeSchemaException(
                "The selected event has no event range, so its scope grain is unknown.");
        }
        if (eventRange.Contains('-', StringComparison.Ordinal))
        {
            return;
        }
        throw new BridgeScopeUnsupportedException(
            "trace.unsupported_draw_scope",
            $"{factFamily} requires a pass/marker range scope.",
            $"eventRange={eventRange}; description={scope.Description}; " +
            "a single command scope has no reliable PC-sampling denominator (SCP-002)");
    }

    /// <summary>
    /// Classifies a range row by whether the Viewer names it after a D3D12 API
    /// object/call or after the caller's own marker. This is a naming fact, not
    /// a judgement about which rows matter; both grains are returned.
    /// </summary>
    private static string OutlineGrain(string description) =>
        description.StartsWith("ID3D12", StringComparison.Ordinal) ||
        description.StartsWith("ExecuteCommandLists", StringComparison.Ordinal) ||
        description.EndsWith("CommandQueue", StringComparison.Ordinal) ||
        description.Contains("CommandQueue (", StringComparison.Ordinal) ||
        D3D12CallPrefixes.Any(prefix =>
            description.StartsWith(prefix, StringComparison.Ordinal))
            ? "container"
            : "marker";

    private static readonly string[] D3D12CallPrefixes =
    [
        "Draw", "Dispatch", "Clear", "Copy", "Resolve", "Set", "Begin", "End",
        "ResourceBarrier", "Present", "IASet", "OMSet", "RSSet", "ExecuteIndirect",
    ];

    private static void ValidateCompleteModel(JsonElement export, string name)    {
        if (!RequiredBoolean(export, "totalCountExact"))
        {
            throw new BridgeSchemaException($"{name} totalCount is not exact.");
        }
        if (RequiredBoolean(export, "truncated"))
        {
            throw new BridgeSchemaException($"{name} raw projection is truncated.");
        }
        var returned = RequiredInt32(export, "returnedCount");
        var total = RequiredInt32(export, "totalCount");
        if (returned != total)
        {
            throw new BridgeSchemaException(
                $"{name} returnedCount {returned} does not close to totalCount {total}.");
        }
    }

    private static Dictionary<int, string> HeadersByColumn(JsonElement export)
    {
        var headers = new Dictionary<int, string>();
        foreach (var header in RequiredArray(export, "headers").EnumerateArray())
        {
            headers.Add(
                RequiredInt32(header, "column"),
                RequiredString(header, "display"));
        }
        return headers;
    }

    private static Dictionary<int, int> OccurrencesByColumn(
        IReadOnlyDictionary<int, string> headers)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new Dictionary<int, int>();
        foreach (var pair in headers.OrderBy(pair => pair.Key))
        {
            var occurrence = counts.GetValueOrDefault(pair.Value);
            counts[pair.Value] = occurrence + 1;
            result[pair.Key] = occurrence;
        }
        return result;
    }

    private static JsonElement CellValue(
        JsonElement node,
        int column,
        string property)
    {
        foreach (var cell in RequiredArray(node, "cells").EnumerateArray())
        {
            if (RequiredInt32(cell, "column") == column)
            {
                return OptionalProperty(cell, property);
            }
        }
        return default;
    }

    private static string? CellText(JsonElement node, int column) =>
        ElementText(CellValue(node, column, "display"));

    private static string? CellTooltipText(JsonElement node, int column) =>
        ElementText(CellValue(node, column, "tooltip"));

    private static string? ArrayText(JsonElement array, int index) =>
        index >= 0 && index < array.GetArrayLength()
            ? ElementText(array[index])
            : null;

    private static string? ElementText(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                element.GetRawText(),
            _ => element.ToString(),
        };

    private static object? ToSafeValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.Array => element.EnumerateArray().Select(ToSafeValue).ToArray(),
            _ => element.ToString(),
        };

    private static string? NormalizeShaderHash(string? value) =>
        value is { Length: 18 } &&
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : null;

    private static long SumIntegerArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return ToInt64(element) ?? 0;
        }
        long total = 0;
        foreach (var item in element.EnumerateArray())
        {
            total = checked(total + (ToInt64(item) ?? 0));
        }
        return total;
    }

    private static int? ToInt32(JsonElement element)
    {
        var value = ToInt64(element);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static long? ToInt64(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out var number))
        {
            return number;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var text = element.GetString()?.Trim().Replace(",", string.Empty);
        return long.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number)
            ? number
            : null;
    }

    private static int? StaticInstructionCount(string tooltip)
    {
        var match = Regex.Match(
            tooltip,
            @"^\s*(?<count>[\d,]+)\s+-\s+Total Instructions",
            RegexOptions.CultureInvariant);
        return match.Success &&
            int.TryParse(
                match.Groups["count"].Value.Replace(",", string.Empty),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count)
            ? count
            : null;
    }

    private static IReadOnlyList<ShaderInstructionCategoryFact> ParseInstructionMix(
        string tooltip)
    {
        var result = new List<ShaderInstructionCategoryFact>();
        foreach (var line in tooltip.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(" - ", StringSplitOptions.None);
            if (parts.Length < 3)
            {
                continue;
            }
            var countMatch = Regex.Match(
                parts[0],
                @"\((?<count>[\d,]+)\)",
                RegexOptions.CultureInvariant);
            if (!countMatch.Success ||
                !long.TryParse(
                    countMatch.Groups["count"].Value.Replace(",", string.Empty),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var count))
            {
                continue;
            }
            result.Add(new(
                parts[1].Trim(),
                string.Join(" - ", parts.Skip(2)).Trim(),
                count));
        }
        return result;
    }

    private static string? FirstStallReason(string? tooltip)
    {
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return null;
        }
        var firstLine = tooltip.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }
        var separator = firstLine.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0 ? firstLine[..separator].Trim() : firstLine;
    }

    private static string? MissingPdb(string tooltip)
    {
        var match = Regex.Match(
            tooltip,
            @"debug symbols:\s*(?<pdb>[^<]+?\.pdb)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["pdb"].Value.Trim() : null;
    }

    private static string? AverageWarpLatency(string? tooltip)
    {
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return null;
        }
        var match = Regex.Match(
            tooltip,
            @"Average:\s*(?<value>[^<]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string SafeValueKind(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Undefined => "absent",
            JsonValueKind.Null => "null",
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number when element.TryGetInt64(out _) => "integer",
            JsonValueKind.Number => "number",
            JsonValueKind.Array => "list",
            _ => "opaque",
        };

    private static string Availability(object? value)
    {
        if (value is null)
        {
            return "absent";
        }
        if (value is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "absent";
            }
            if (text is "--" or "N/A")
            {
                return "unavailable";
            }
        }
        return "available";
    }

    private static string? UnitFromHeader(string header)
    {
        if (header.EndsWith('%'))
        {
            return "%";
        }
        if (header.Equals("GB/s", StringComparison.OrdinalIgnoreCase))
        {
            return "GB/s";
        }
        if (header.Equals("Bytes", StringComparison.OrdinalIgnoreCase))
        {
            return "bytes";
        }
        return null;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string PathKey(IEnumerable<int> path) =>
        string.Join('.', path.Select(value =>
            value.ToString(CultureInfo.InvariantCulture)));

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeSchemaException($"Required object '{name}' is missing.");
        }
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new BridgeSchemaException($"Required array '{name}' is missing.");
        }
        return value;
    }

    private static JsonElement OptionalProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value : default;

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new BridgeSchemaException($"Required string '{name}' is missing.");
        }
        return value.GetString()!;
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
        {
            throw new BridgeSchemaException($"Required integer '{name}' is missing.");
        }
        return number;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new BridgeSchemaException($"Required boolean '{name}' is missing.");
        }
        return value.GetBoolean();
    }

    private static IReadOnlyList<int> RequiredInt32Array(JsonElement parent, string name)
    {
        var array = RequiredArray(parent, name);
        var values = new List<int>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetInt32(out var number) || number < 0)
            {
                throw new BridgeSchemaException(
                    $"Array '{name}' contains an invalid row index.");
            }
            values.Add(number);
        }
        return values;
    }

    private sealed record RawMetricTable(
        string Name,
        int Occurrence,
        int RawIndex,
        JsonElement Export,
        IReadOnlyDictionary<int, string> Headers);

    private sealed record RawSourceView(
        int RawIndex,
        string ParentId,
        string Kind,
        int Occurrence,
        string Label,
        JsonElement Export,
        IReadOnlyDictionary<int, string> Headers);
}
