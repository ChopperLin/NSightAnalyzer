#!/usr/bin/env python3
"""Keep the selected shader's complete source oracle, removing project text.

The input is decoded bridge JSON, never a GPU Trace container. Preserve all
selected DXIL/SASS row identities, sample vectors, instruction/register values,
explicit empty cells, and view totals. The source text is deliberately replaced;
the fixture tests attribution and navigation, not any project's shader program.
"""
import argparse
import copy
import json


def sanitize(document):
    retained = (
        "schema", "status", "mode", "pluginVersion", "qtRuntimeVersion",
        "selectionMatchesTarget", "metricsStable", "settleTimedOut", "eventSelector",
        "targetSelection", "currentSelection", "modelSelectionMatchesTarget",
        "comboSelectionMatchesTarget", "modelSelectionTargetProviderReady",
        "targetModelSelection", "targetModelSelectionParent", "modelSelector",
    )
    result = {key: copy.deepcopy(document[key]) for key in retained if key in document}
    for key in ("targetSelection", "currentSelection"):
        result[key]["cells"][0] = "Pass01"

    original_hash = document["targetModelSelection"]["cells"][3][2:].lower()
    fixture_hash = "a000000000000001"
    fixture_name = "Shader01"
    fixture_pipeline = "Pipeline01"
    result["targetModelSelection"]["cells"] = [
        document["targetModelSelection"]["cells"][0], None,
        fixture_name, "0x" + fixture_hash,
    ]
    result["targetModelSelectionParent"]["cells"] = [
        "ID3D12PipelineState", None, fixture_pipeline,
    ]
    result["modelSelector"]["value"] = "0x" + fixture_hash
    result["comboSelectionDerivedShaderName"] = fixture_name
    result["comboSelectionDerivedPipelineName"] = fixture_pipeline
    result["currentComboSelection"] = {"text": f"{fixture_pipeline} - {fixture_name}"}

    models = document["models"]
    parents = {
        model["parentId"] for model in models
        if any(original_hash in str(cell.get("display", "")).lower()
               for node in model["export"]["nodes"] for cell in node["cells"])
    }
    if len(parents) != 1:
        raise ValueError("The selected shader must have one exact source provider")
    selected = [model for model in models if model["parentId"] in parents]
    result["models"] = []
    for model in selected:
        export = model["export"]
        minimal = {key: copy.deepcopy(export[key]) for key in (
            "totalCountExact", "truncated", "returnedCount", "totalCount", "headers", "nodes")}
        for node in minimal["nodes"]:
            for cell in node["cells"]:
                # Tooltip and source text can contain arbitrary project identifiers.
                cell.pop("tooltip", None)
                if cell["column"] != 3 or not cell.get("display"):
                    continue
                text = cell["display"]
                if node["ordinal"] == 0:
                    if text.lower().startswith("dxil ("):
                        cell["display"] = f"dxil ({fixture_hash})"
                    elif text.startswith("SASS"):
                        cell["display"] = f"SASS - {fixture_name}"
                    else:
                        raise ValueError("This sanitizer handles the proven DXIL/SASS fixture only")
                else:
                    prefix = "// " if text.lstrip().startswith("//") else ""
                    cell["display"] = f"{prefix}sanitized source row {node['ordinal']}"
        result["models"].append({
            "class": model["class"], "parentId": "fixture-source-provider", "export": minimal,
        })
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source")
    parser.add_argument("destination")
    arguments = parser.parse_args()
    with open(arguments.source, encoding="utf-8-sig") as handle:
        result = sanitize(json.load(handle))
    with open(arguments.destination, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(result, handle, ensure_ascii=False, separators=(",", ":"))
        handle.write("\n")


if __name__ == "__main__":
    main()
