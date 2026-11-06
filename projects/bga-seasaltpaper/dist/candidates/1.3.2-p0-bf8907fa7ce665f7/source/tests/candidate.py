"""Build a content-addressed candidate, or verify a pinned candidate before an offline run."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def inventory(folder):
    return {p.relative_to(folder).as_posix(): digest(p) for p in sorted(folder.rglob("*")) if p.is_file()}


def verify(folder, pin):
    manifest_path = folder / "candidate.json"
    if digest(manifest_path) != pin.upper():
        raise ValueError("Candidate manifest SHA256 differs from the release pin")
    data = json.loads(manifest_path.read_text(encoding="utf-8"))
    for group in ("source", "bin", "integration"):
        if inventory(folder / group) != data[group]:
            raise ValueError(f"Missing, extra or modified files in {group}")
    binaries = (folder / "bin").resolve()
    for name, expected in (("SeaSaltDesk", {"SeaSaltDesk"}), ("SeatControl", {"SeaSaltDesk", "SeatControl"}),
                           ("Checks", {"Checks", "SeaSaltDesk", "SeatControl"})):
        exe = binaries / (name + ".exe")
        result = subprocess.run([str(exe), "--build-identity"], capture_output=True, text=True, timeout=20, check=True)
        actual = json.loads(result.stdout)
        if Path(actual["process"]).resolve() != exe or actual["processSha256"] != data["bin"][exe.name]:
            raise ValueError(f"{name} ran a different executable")
        if {a["name"] for a in actual["loaded"]} != expected:
            raise ValueError(f"{name} loaded unexpected application assemblies")
        for assembly in actual["loaded"]:
            dll = binaries / (assembly["name"] + ".dll")
            if Path(assembly["path"]).resolve() != dll or assembly["sha256"] != data["bin"][dll.name]:
                raise ValueError(f"{name} loaded a different {dll.name}")
        print(json.dumps(actual, ensure_ascii=True), flush=True)
    return data


def build(project, patches):
    # Copy build inputs only. Login XML, user profiles and historical releases never enter the snapshot.
    work = Path(tempfile.mkdtemp(prefix="SeaSalt-frozen-build-"))
    source = work / "source"
    for part in ("app", "control", "tests"):
        for item in sorted((project / part).iterdir()):
            if item.is_file() and item.suffix in (".cs", ".csproj", ".js", ".py"):
                (source / part).mkdir(parents=True, exist_ok=True)
                shutil.copy2(item, source / part / item.name)
    shutil.copy2(project / "Directory.Build.targets", source)
    files = inventory(source)
    source_id = hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()
    build_id = "1.3.2-p0-" + source_id[:16]
    target_parent = project / "dist" / "candidates"
    target_parent.mkdir(parents=True, exist_ok=True)
    target = target_parent / build_id
    if target.exists():
        raise FileExistsError(f"Already frozen: {target}")
    command = ["dotnet", "build", "tests/Checks.csproj", "-c", "Release", "--nologo", "-p:Version=1.3.2",
               "-p:InformationalVersion=" + build_id, "-p:ContinuousIntegrationBuild=true", f"-p:PathMap={source}=/_/SeaSalt"]
    subprocess.run(command, cwd=source, check=True)
    stage = Path(tempfile.mkdtemp(prefix=".assembling-", dir=target_parent))
    for relative, hash_value in files.items():
        item = source / relative
        dest = stage / "source" / relative
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(item, dest)
        if digest(dest) != hash_value:
            raise ValueError(f"Source changed during build: {relative}")
    output = source / "tests" / "bin" / "Release" / "net10.0-windows"
    shutil.copytree(output, stage / "bin", ignore=shutil.ignore_patterns("*.xml", "*.pdb"))
    integration = stage / "integration"
    integration.mkdir()
    for patch in patches:
        dest = integration / patch.name
        if patch.suffix != ".patch" or dest.exists():
            raise ValueError(f"Expected distinct .patch inputs: {patch}")
        shutil.copy2(patch, dest)
    data = {"id": build_id, "sourceSha256": source_id.upper(), "sdk": subprocess.check_output(["dotnet", "--version"], text=True).strip(),
            "deployment": "framework-dependent .NET 10 Windows Desktop + WebView2",
            "command": [arg.replace(str(source), "<frozen-source>") for arg in command],
            "source": files, "bin": inventory(stage / "bin"), "integration": inventory(integration)}
    manifest = stage / "candidate.json"
    manifest.write_text(json.dumps(data, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    pin = digest(manifest)
    verify(stage, pin)
    for item in stage.rglob("*"):
        if item.is_file():
            item.chmod(stat.S_IREAD)
    stage.rename(target)
    print(f"Candidate {target}\nManifest SHA256 {pin}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="operation", required=True)
    make = sub.add_parser("build")
    make.add_argument("--patch", type=Path, action="append", default=[])
    check = sub.add_parser("verify")
    check.add_argument("folder", type=Path)
    check.add_argument("manifest_sha256")
    args = parser.parse_args()
    if args.operation == "build":
        build(Path(__file__).resolve().parents[1], args.patch)
    else:
        verify(args.folder.resolve(), args.manifest_sha256)
