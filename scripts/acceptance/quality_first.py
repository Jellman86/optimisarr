"""Independent checks on retained adaptive audio/image search evidence."""
from .core import require


def validate_image_command(target, selected, command):
    require(target in ("jpeg", "webp"), "Unsupported adaptive image target")
    option = f"-q:v {round(31 - 29 * selected / 100)} " if target == "jpeg" else f"-quality {selected} "
    require(option in command, "Full image encode ignored its selected encoder control")


def validate_search(search, kind, source_hash, baseline):
    require(isinstance(search, dict), "Missing quality-first search evidence")
    require(search.get("kind") == kind and search.get("encoder"), "Search has wrong media kind or no encoder")
    require(search.get("sourceSha256", "").lower() == source_hash.lower(), "Search belongs to another source")
    result = search.get("result") or {}
    probes, decision = result.get("probes", []), result.get("decision") or {}
    require(len(probes) <= 4 and len({p["value"] for p in probes}) == len(probes), "Search exceeded its budget or repeated a setting")
    require(not probes or probes[0]["value"] == baseline, "Search did not measure the saved baseline first")
    require(all(p.get("encodedBytes", -1) > 0 or (p.get("encodedBytes") == 0
                and p.get("measurement", {}).get("available") is False and decision.get("fellBack") is True)
                for p in probes), "Search recorded no encoded bytes for an available measurement")
    require(decision.get("reason") and decision.get("nextValue") is None, "Search has not settled or explains no decision")
    if decision.get("fellBack"):
        require(decision.get("selectedValue") == baseline, "Fallback silently changed the saved setting")
    else:
        passing = [p for p in probes if p.get("measurement", {}).get("available") is True
                   and p["measurement"].get("meetsTarget") is True]
        require(passing, "Selection has no complete passing measurement")
        selected = min(passing, key=lambda p: (p["encodedBytes"], p["value"]))
        require(selected["value"] == decision.get("selectedValue"), "Selection did not choose the smallest measured passing candidate")
    windows = search.get("audioWindows", [])
    if search.get("preparationBlocked"):
        require(not probes and not windows and decision.get("fellBack") is True, "Source refusal contains candidate measurements")
        return decision
    if kind == "Audio":
        require(1 <= len(windows) <= 3 and sum(w["durationSeconds"] for w in windows) <= 90, "Wrong sampled audio coverage")
        require(all(1 <= w["durationSeconds"] <= 30 and w["startSeconds"] >= 0 for w in windows), "Invalid audio window")
        require(all(b["startSeconds"] >= a["startSeconds"] + a["durationSeconds"] for a, b in zip(windows, windows[1:])), "Audio windows overlap")
    else:
        require(not windows, "Still-image search unexpectedly sampled a timeline")
    return decision
