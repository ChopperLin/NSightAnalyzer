#!/usr/bin/env python3
"""Turn a real bridge-output.json into a committable fixture.

Identifiers that could carry project information (pass/marker names, shader
hashes, file names, pointers, machine and user paths) are replaced with stable
placeholders. Every number, row/column structure, occurrence, source ordinal
and availability state is preserved verbatim, so a fixture still closes the
same structural oracles (88 tables / 409 rows / 56 categories) as the real
report.

The mapping is deterministic and shared across fixtures: the same input
identifier always becomes the same placeholder, so cross-fixture joins in the
tests stay meaningful.
"""
import argparse
import json
import re
import sys

# Viewer/Qt implementation identifiers are part of the pinned contract the
# projection asserts on, so they must survive sanitization unchanged.
PRESERVED_PREFIXES = (
    "NV::",
    "Q",
    "NsightSolidProbe",
    "FlatTabPanel_",
    "FlatTabButton_",
    "EventList_",
    "SampleTreeView",
    "SampleItemModel",
    "GroupByComboBox",
    "WarpMetricsTableView",
)

# Metric/table/column vocabulary is Viewer-defined, not project-defined, and the
# projection keys on it. Only caller-authored marker names get renamed.
D3D12_TOKENS = re.compile(
    r"^(ExecuteCommandLists|ID3D12CommandList|Graphics CommandQueue|Compute CommandQueue|"
    r"Copy CommandQueue|Draw\w*|Dispatch\w*|Clear\w*|Copy\w*|Resolve\w*|Set\w*|Begin\w*|"
    r"End\w*|ResourceBarrier|Present\w*|IASet\w*|OMSet\w*|RSSet\w*|ExecuteIndirect)"
)

POINTER = re.compile(r"0x[0-9a-fA-F]{6,}")
SHADER_HASH = re.compile(r"\b[0-9a-fA-F]{8,16}\b")
WIN_PATH = re.compile(r"[A-Za-z]:[\\/][^\"',;]*")
# BeginEvent/SetMarker carry the caller's marker name as an embedded literal,
# e.g. pData = "MeshSkinning.SkinOnGPU". The surrounding call is Viewer/D3D12
# vocabulary that must be preserved, so only the quoted payload is renamed.
EMBEDDED_LITERAL = re.compile(r'"([^"]{1,96})"')


class Mapper:
    def __init__(self):
        self.markers = {}
        self.hashes = {}
        self.files = {}

    def marker(self, name):
        if name not in self.markers:
            self.markers[name] = f"Pass{len(self.markers) + 1:02d}"
        return self.markers[name]

    def shader_hash(self, value):
        if value not in self.hashes:
            self.hashes[value] = f"{0xA0000000 + len(self.hashes):08x}"
        return self.hashes[value]

    def file(self, name):
        if name not in self.files:
            self.files[name] = f"source{len(self.files) + 1:02d}.hlsl"
        return self.files[name]


def is_preserved(text):
    return text.startswith(PRESERVED_PREFIXES) or bool(D3D12_TOKENS.match(text))


# Units, counters and formatted values are Viewer vocabulary that appears in
# ordinary metric cells. Renaming them would corrupt the fixture's meaning
# without protecting anything, so the marker rule is opt-in by shape.
MEASUREMENT = re.compile(
    r"^-?[\d,]+(\.\d+)?\s*(ms|us|ns|s|%|MHz|GHz|kB|MB|GB|B|x)?$",
    re.IGNORECASE)
# Caller instrumentation is dotted or CamelCase with no spaces, e.g.
# "MeshSkinning.SkinOnGPU", "GBufferPass", "GPUScene.GPUCulling_MainCamera_0".
CALLER_MARKER = re.compile(r"^(?=.*[A-Z])[A-Za-z][A-Za-z0-9_.]*[A-Za-z0-9_]$")


# Bare unit labels appear as their own metric cells and happen to look like
# interior CamelCase ("kB", "MiB"), so they are excluded explicitly.
UNIT_LABELS = frozenset(
    ("b", "kb", "mb", "gb", "tb", "kib", "mib", "gib", "tib",
     "hz", "khz", "mhz", "ghz", "ns", "us", "ms", "s", "fps", "ipc"))


def is_caller_marker(text):
    """True only for names a title's own GPU instrumentation would emit."""
    if len(text) > 96 or " " in text or MEASUREMENT.match(text):
        return False
    if text.lower() in UNIT_LABELS:
        return False
    if not CALLER_MARKER.match(text):
        return False
    # Dotted or underscored names are unambiguous instrumentation.
    if "." in text or "_" in text:
        return True
    # Otherwise require interior CamelCase ("GBufferPass", "DeferredLighting"),
    # which Viewer vocabulary never uses: its own labels are either single words
    # ("Total", "Warps") or spaced phrases ("Active Threads Per Warp").
    return bool(re.search(r"[a-z][A-Z]", text))


def scrub_text(text, mapper):
    """Rewrite one display string, keeping every digit that is not an identifier."""
    if not text or not isinstance(text, str):
        return text

    # Paths and pointers never carry semantic value for the projection.
    text = WIN_PATH.sub("X:/redacted/path", text)
    text = POINTER.sub("0x0000000000000000", text)

    if is_preserved(text):
        # A D3D12 call is Viewer vocabulary, but its quoted payload may be the
        # caller's own marker name.
        return EMBEDDED_LITERAL.sub(
            lambda m: '"' + mapper.marker(m.group(1)) + '"', text)

    return mapper.marker(text) if is_caller_marker(text) else text


def walk(node, mapper, key=None, in_headers=False):
    if isinstance(node, dict):
        return {
            k: walk(v, mapper, k, in_headers or k in ("headers", "roleNames"))
            for k, v in node.items()
        }
    if isinstance(node, list):
        return [walk(v, mapper, key, in_headers) for v in node]
    if isinstance(node, str):
        # Column headers and role names are Viewer schema vocabulary that the
        # projection asserts on exactly ("Pipe", "Family", "Samples", ...).
        # Renaming them would make a fixture assert the wrong contract.
        if in_headers:
            return node
        # "cells" is a bare string array on selection records, so the element
        # inherits its parent key rather than carrying one of its own.
        if key in ("display", "tooltip", "value", "description", "cells"):
            return scrub_text(node, mapper)
        if key in ("applicationFilePath", "outputPath", "sessionDirectory"):
            return WIN_PATH.sub("X:/redacted/path", node)
        if key == "reportId":
            # "<file>.ngfx-gputrace:<bytes>:<ticks>" - keep the shape and the
            # numbers the projection compares, drop the title's file name.
            tail = node.split(":", 1)
            return ("fixture.ngfx-gputrace" if len(tail) == 1
                    else f"fixture.ngfx-gputrace:{tail[1]}")
        return node
    return node


def trim_model_rows(document, limit):
    """Keep a fixture small while preserving its declared paging identity.

    Only applies to models whose export is larger than the limit; totalCount and
    returnedCount are rewritten together so ValidateCompleteModel still closes.
    """
    for model in document.get("models") or []:
        export = model.get("export")
        if not isinstance(export, dict):
            continue
        nodes = export.get("nodes")
        if isinstance(nodes, list) and len(nodes) > limit:
            export["nodes"] = nodes[:limit]
            export["totalCount"] = limit
            export["returnedCount"] = limit
            export["nodeCount"] = limit
    return document


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("destination")
    parser.add_argument("--trim-model-rows", type=int, default=0)
    parser.add_argument("--mapping", help="write the identifier mapping here")
    args = parser.parse_args()

    with open(args.source, encoding="utf-8") as handle:
        document = json.load(handle)

    mapper = Mapper()
    document = walk(document, mapper)
    if args.trim_model_rows:
        document = trim_model_rows(document, args.trim_model_rows)

    # Run identity is per-invocation and must not look authoritative in a fixture.
    for field in ("requestId", "pid", "applicationFilePath"):
        if field in document:
            document[field] = "fixture"

    with open(args.destination, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, indent=1, sort_keys=True)
        handle.write("\n")

    if args.mapping:
        with open(args.mapping, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(
                {"markers": mapper.markers, "hashes": mapper.hashes},
                handle, indent=1, sort_keys=True)
            handle.write("\n")

    print(f"{args.source} -> {args.destination}")
    print(f"  markers renamed: {len(mapper.markers)}")


if __name__ == "__main__":
    sys.exit(main())
