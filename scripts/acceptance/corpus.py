"""Bounded, checksum-pinned, openly licensed media acquisition and provenance."""
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile

from .core import inside, require, save, sha256

SOURCES = {
    "bunny": {
        "url": "https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_320x180.mp4.zip",
        "sha256": "109e3ede8790bd633f374ca311d9cc61dce8d7f98f5b0797ca98199c9fbceedf",
        "bytes": 64657225, "member": "BigBuckBunny_320x180.mp4",
        "license": "CC-BY-3.0", "licenseUrl": "https://peach.blender.org/about/",
        "attribution": "(c) copyright 2008, Blender Foundation / www.bigbuckbunny.org",
        "description": "Lossy 320x180 distribution copy; workflow regression footage, not an HD/UHD master",
        "starts": [60, 180, 300]},
    "tears": {
        "url": "https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov",
        "sha256": "efa9062d9cdb7a338e40ad530dfdf234806743f29ae6a1a136b97ece4e588e8f",
        "bytes": 372178639,
        "license": "CC-BY-3.0", "licenseUrl": "https://media.xiph.org/tearsofsteel/README.txt",
        "attribution": "(CC) Blender Foundation | mango.blender.org",
        "description": "Lossy 720p distribution copy; decoded excerpts preserve that source's limitations",
        "starts": [60, 240, 480]},
}


def fetch(name, cache):
    spec = SOURCES[name]
    cache = Path(cache)
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / (name + (".zip" if "member" in spec else ".mov"))
    if not archive.exists():
        partial = archive.with_suffix(".partial")
        try:
            with urllib.request.urlopen(spec["url"], timeout=60) as response, partial.open("wb") as output:
                total = 0
                while block := response.read(1024 * 1024):
                    total += len(block)
                    require(total <= spec["bytes"], "Upstream media exceeds pinned size")
                    output.write(block)
            require(partial.stat().st_size == spec["bytes"] and sha256(partial) == spec["sha256"],
                    "Upstream media changed; review provenance before updating the lock")
            partial.replace(archive)
        finally:
            partial.unlink(missing_ok=True)
    require(archive.stat().st_size == spec["bytes"] and sha256(archive) == spec["sha256"], "Cached source checksum mismatch")
    source = archive
    if "member" in spec:
        source = cache / spec["member"]
        with zipfile.ZipFile(archive) as zip_file:
            member = zip_file.getinfo(spec["member"])
            require(member.file_size < 500_000_000, "Archive member exceeds limit")
            with zip_file.open(member) as input_file, source.open("wb") as output:
                shutil.copyfileobj(input_file, output)
    return source


def prepare(tools, root, names, seconds=12):
    root = Path(root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    clips = []
    for name in names:
        source = fetch(name, root / "cache")
        for start in SOURCES[name]["starts"]:
            identifier = f"{name}-{start}"
            path = root / (identifier + ".mkv")
            evidence = tools.fixture(path, source=source, start=start, seconds=seconds)
            clips.append({**evidence, "id": identifier, "path": path.name, "source": SOURCES[name]})
    manifest = root / "corpus.json"
    save(manifest, {"version": 1, "tools": tools.versions(), "clips": clips})
    (root / "ATTRIBUTION.txt").write_text("\n\n".join(SOURCES[name]["attribution"] + "\n" +
        SOURCES[name]["license"] + " " + SOURCES[name]["licenseUrl"] + "\n" + SOURCES[name]["description"] for name in names))
    return manifest


def import_corpus(manifest, destination):
    manifest, destination = Path(manifest).resolve(), Path(destination)
    data = json.loads(manifest.read_text())
    require(data["version"] == 1 and bool(data["clips"]), "Empty or unsupported corpus manifest")
    destination.mkdir(parents=True, exist_ok=False)
    identifiers = set()
    for clip in data["clips"]:
        identifier = clip["id"]
        require(identifier.replace("-", "").isalnum() and identifier not in identifiers, "Invalid or duplicate corpus ID")
        identifiers.add(identifier)
        source = inside(manifest.parent, manifest.parent / clip["path"])
        require(sha256(source) == clip["sha256"], "Corpus source checksum mismatch")
        require(clip["source"]["license"] and clip["source"]["attribution"], "Corpus has no licence provenance")
        target = destination / (identifier + ".mkv")
        shutil.copyfile(source, target)
        clip["path"] = target.name
    save(destination / "corpus.json", data)
    return destination / "corpus.json"
