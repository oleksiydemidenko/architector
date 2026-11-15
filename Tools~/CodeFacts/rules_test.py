"""The startedBy absence rule, check, the optional hooks and the call log on FixtureRules.

Seeded errors (must be found): a handler in no list (R1), missing from both managed start lists (R2), from one each
(R3, R4), a variant that adds a handler and forgets one override entry (R5), a nested prefab's handler in no list (R6),
a prefab that nests one other prefab but keeps its own root and lists (R7, no variant), lists empty with no
filledAtRuntime (R8).
Decoys (must stay quiet): a component that is not a handler (D1), lists the rule says code fills (D2), a variant that adds
every entry by override (D3), a nested handler listed through its stripped document (D4), a handler on an inactive object
(D5), under an inactive parent (D6), the base's handlers seen through a nesting prefab (D7, OneNest), a variant's handler
under a base object the variant deactivates by override (D8), a variant of a variant that adds a handler to the base's
lists by override, named through Unity's instance fileID rule (D9).

Usage: python rules_test.py <server binary>
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
environment = dict(os.environ, ARCHITECTOR_SESSION_ID="test-session")


def run(*arguments, stdin=None):
    return subprocess.run([server, *arguments, "--project", project], capture_output=True, text=True, encoding="utf-8",
                          input=stdin, env=environment)


def check(*files):
    result = run("check", *files, "--json")
    assert result.returncode in (0, 3), result.stdout + result.stderr
    return json.loads(result.stdout)


# A clone without a code index says so instead of waiting; facts index builds it.
first = check()
assert first["status"] == "index_building", first
built = run("facts", "index")
assert built.returncode == 0 and json.loads(built.stdout)["success"], built.stdout

everything = check()
assert everything["status"] == "findings", everything
found = {(item["file"].split("/")[-1], item["component"], tuple(item["missing"])) for item in everything["findings"]}
ALL = ("Starter._handlers", "PhaseHost/StartList._handlers", "PhaseHost/TickList._handlers")
expected = {
    ("Seeds.prefab", "FuseHandler", ALL),
    ("Seeds.prefab", "JumpHandler", ALL[1:]),
    ("Seeds.prefab", "ShieldHandler", (ALL[1],)),
    ("Seeds.prefab", "LookHandler", (ALL[2],)),
    ("VariantMissing.prefab", "FuseHandler", (ALL[2],)),
    ("NestedMissing.prefab", "JumpHandler", ALL),
    ("OneNest.prefab", "MoveHandler", (ALL[2],)),
    ("Empty.prefab", "MoveHandler", ALL),
    ("Empty.prefab", "PhaseHost", (ALL[0],)),
}
assert found == expected, ("unexpected", found - expected, "missed", expected - found)
nested = [item for item in everything["findings"] if item["file"].endswith("NestedMissing.prefab")][0]
assert nested["via"] == "nested Assets/Prefabs/Part.prefab", nested
assert everything["skipped"] >= 4, everything  # D2 file, D5, D6, D8

# Changed files: a prefab answers for itself, a variant source also for its variants, a script for the files placing it.
assert [item["component"] for item in check("Assets/Prefabs/VariantMissing.prefab")["findings"]] == ["FuseHandler"]
clean = run("check", "Assets/Prefabs/VariantComplete.prefab")
assert clean.returncode == 0 and clean.stdout == "", clean.stdout  # silent when clean
from_base = {item["file"].split("/")[-1] for item in check("Assets/Prefabs/Base.prefab")["findings"]}
assert from_base == {"VariantMissing.prefab", "OneNest.prefab"}, from_base
from_part = {item["file"].split("/")[-1] for item in check(os.path.join(project, "Assets", "Prefabs", "Part.prefab"))["findings"]}
assert from_part == {"NestedMissing.prefab"}, from_part
from_script = {item["file"].split("/")[-1] for item in check("Assets/Rules/LookHandler.cs")["findings"]}
assert from_script == {"Seeds.prefab"}, from_script
text = run("check", "Assets/Prefabs/Seeds.prefab")
assert text.returncode == 3 and "FuseHandler on 'Fuse' is missing from Starter._handlers" in text.stdout, text.stdout

# facts layer places name both start lists apart and include what a variant adds by override; audit reports findings.
layer = json.loads(run("facts", "layer", "Rules", "--type", "FuseHandler", "--section", "wiring").stdout)
places = {place["file"].split("/")[-1]: place for place in layer["wiring"]["places"]["items"]}
assert set(places["VariantComplete.prefab"]["listed_by"]) == {"Starter._handlers", "PhaseHost/StartList._handlers", "PhaseHost/TickList._handlers"}, places
assert places["VariantMissing.prefab"]["missing"] == ["PhaseHost/TickList._handlers"], places
assert places["Seeds.prefab"]["missing"] == list(ALL), places
assert layer["wiring"]["started_by"]["findings"] == 2, layer["wiring"]
move = json.loads(run("facts", "layer", "Rules", "--type", "MoveHandler", "--section", "wiring").stdout)
deep = [place for place in move["wiring"]["places"]["items"] if place["object"] == "Deep"][0]
assert deep["active"] is False and "missing" not in deep, deep
audit = json.loads(run("facts", "audit").stdout)
assert audit["by_kind"].get("rule_violation") == len(expected), audit["by_kind"]
assert audit["rules"]["findings"] == len(expected) and audit["rules"]["files_checked"] >= 8, audit["rules"]

# Hooks: install, run the way Claude Code runs them, remove.
installed = json.loads(run("skill", "hooks").stdout)
assert installed["installed"] is False
settings_path = os.path.join(project, ".claude", "settings.local.json")
os.makedirs(os.path.dirname(settings_path))
with open(settings_path, "w", encoding="utf-8") as handle:
    json.dump({"permissions": {"allow": ["Bash(ls:*)"]}}, handle)
# skill install finds the package through a file: path in the manifest, as in a project that uses this clone.
package_root = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
os.makedirs(os.path.join(project, "Packages"))
with open(os.path.join(project, "Packages", "manifest.json"), "w", encoding="utf-8") as handle:
    json.dump({"dependencies": {"com.oleksiydemidenko.architector": "file:" + package_root.replace("\\", "/")}}, handle)
add = json.loads(run("skill", "install", "claude", "--hooks").stdout)
assert add["success"] and add["hooks"]["status"] == "installed", add
with open(settings_path, encoding="utf-8") as handle:
    settings = json.load(handle)
assert settings["permissions"] == {"allow": ["Bash(ls:*)"]}, settings
assert "check --hook post" in settings["hooks"]["PostToolUse"][0]["hooks"][0]["command"], settings
assert settings["hooks"]["PostToolUse"][0]["matcher"] == "Edit|Write|MultiEdit" and "check --hook stop" in settings["hooks"]["Stop"][0]["hooks"][0]["command"], settings
assert json.loads(run("skill", "install", "claude", "--hooks").stdout)["hooks"]["status"] == "updated"
with open(settings_path, encoding="utf-8") as handle:
    assert len(json.load(handle)["hooks"]["PostToolUse"]) == 1, "install twice keeps one entry"
assert json.loads(run("skill", "hooks").stdout)["installed"] is True
assert json.loads(run("skill", "hooks", "remove").stdout)["hooks"]["status"] == "removed"
with open(settings_path, encoding="utf-8") as handle:
    assert json.load(handle) == {"permissions": {"allow": ["Bash(ls:*)"]}}, "remove keeps the user's settings"
post_input = json.dumps({"session_id": "hook-session", "cwd": project, "tool_name": "Edit",
                         "tool_input": {"file_path": os.path.join(project, "Assets", "Prefabs", "VariantMissing.prefab")}})
post = subprocess.run([server, "check", "--hook", "post"], capture_output=True, text=True, encoding="utf-8", input=post_input, cwd=project)
assert post.returncode == 2 and "FuseHandler" in post.stderr and post.stdout == "", (post.returncode, post.stdout, post.stderr)
quiet = subprocess.run([server, "check", "--hook", "post"], capture_output=True, text=True, encoding="utf-8", cwd=project,
                       input=json.dumps({"cwd": project, "tool_input": {"file_path": os.path.join(project, "Assets", "Prefabs", "VariantComplete.prefab")}}))
assert quiet.returncode == 0 and quiet.stdout == "" and quiet.stderr == "", quiet
other = subprocess.run([server, "check", "--hook", "post"], capture_output=True, text=True, encoding="utf-8", cwd=project,
                       input=json.dumps({"cwd": project, "tool_input": {"file_path": os.path.join(project, "README.md")}}))
assert other.returncode == 0 and other.stdout == "" and other.stderr == "", other
again = subprocess.run([server, "check", "--hook", "stop"], capture_output=True, text=True, encoding="utf-8", cwd=project,
                       input=json.dumps({"cwd": project, "stop_hook_active": True}))
assert again.returncode == 0 and again.stderr == "", again

# The call log has one line per call with caller, action, size and duration; skipped hook runs are not logged.
with open(os.path.join(project, "Library", "Architector", "calls.jsonl"), encoding="utf-8") as handle:
    calls = [json.loads(line) for line in handle]
assert all({"time", "caller", "action", "arguments", "chars", "tokens", "ms"} <= set(call) for call in calls), calls[0]
hooks = [call for call in calls if call["caller"] == "hook"]
assert len(hooks) == 2 and hooks[0]["session"] == "hook-session" and hooks[0]["action"] == "check", hooks
assert any(call["caller"] == "cli" and call["action"] == "facts layer" and call["session"] == "test-session" and call["chars"] > 100 for call in calls), calls
print(f"rules test passed: {len(expected)} seeded errors found, 0 missed, 0 false; check, hooks and call log")
