"""slice on FixtureRules: Car.prefab holds a Speed and names CarBase and CarAuthority in its text fields _baseId and
_authorityId; Engine (on CarAuthority) writes Speed from a tick callback it subscribes in OnEnable and resets it in
OnDisable; Gauge (on CarBase) reads it in Update.

Usage: python slice_test.py <server binary>
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

server = os.path.abspath(sys.argv[1])
project = os.path.join(tempfile.mkdtemp(), "FixtureRules")
shutil.copytree(os.path.join(os.path.dirname(os.path.abspath(__file__)), "FixtureRules"), project)


def run(*arguments):
    result = subprocess.run([server, *arguments, "--project", project], capture_output=True, text=True, encoding="utf-8")
    assert result.returncode == 0, result.stdout + result.stderr
    return result.stdout


speed = run("slice", "Speed")
lines = speed.splitlines()
assert lines[0].startswith("# Speed (class) - Rules"), speed
assert "held in: Car.prefab" in lines, speed
assert "## instance on Car.prefab" in lines, speed
writers, readers = lines.index("writers:"), lines.index("readers:")
assert writers < readers, speed
assert lines[writers + 1].startswith("- Engine (Rules) in CarAuthority.prefab (loaded by Car.prefab._authorityId)"), speed
# The tick write comes first, with how it runs and the line itself; the lifecycle reset follows.
assert lines[writers + 2] == "  Subscribe Post, set up in OnEnable; via Tick: Engine.cs:18 private void Push() { _speed.Set(1f); }", speed
assert lines[writers + 3] == "  Unity OnDisable: Engine.cs:13 _speed.Set(0f);", speed
assert lines[readers + 1].startswith("- Gauge (Rules) in CarBase.prefab (loaded by Car.prefab._baseId)"), speed
assert "Unity Update: Gauge.cs:7" in lines[readers + 2], speed

engine = run("slice", "Assets/Rules/Engine.cs")
assert engine.startswith("# Engine (class : MonoBehaviour) - Rules"), engine
assert "placed: CarAuthority.prefab (loaded by Car.prefab._authorityId); in no list" in engine, engine
assert "runs: Tick <- Subscribe Post in OnEnable" in engine, engine
assert "lifecycle OnDisable: Engine.cs:13 _speed.Set(0f);" in engine, engine
assert "writes Speed.Set (Subscribe Post, set up in OnEnable; via Tick): Engine.cs:18" in engine, engine
assert "Speed: other writers none; readers Gauge" in engine, engine

reply = json.loads(run("slice", "Speed", "--json"))
assert reply["success"] and reply["type"] == "Rules.Speed" and reply["text"] == speed and reply["chars"] == len(speed), reply
missing = subprocess.run([server, "slice", "NoSuchType", "--project", project], capture_output=True, text=True, encoding="utf-8")
assert missing.returncode == 6 and json.loads(missing.stdout)["code"] == "type_not_found", missing.stdout
print("slice test passed")
