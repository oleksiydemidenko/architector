"""Checks query and edit without a Unity Editor on a copy of a project's Architector data: the revision stays put
between calls, a plan is dry-run and applied by its token in an unlocked group, a stale expected_revision and a
locked group are refused, approvals need a person, and a second writer waits for the write lock.

Usage: python editor_free_test.py <architector-mcp binary> <Unity project with Architector data>
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

binary = os.path.abspath(sys.argv[1])
source = os.path.abspath(sys.argv[2])
project = tempfile.mkdtemp()
for folder in ("Assets", "ProjectSettings"):
    os.makedirs(os.path.join(project, folder))
shutil.copytree(os.path.join(source, "Architector"), os.path.join(project, "Architector"))


def cli(*arguments, check=None):
    result = subprocess.run([binary, *arguments, "--project", project], capture_output=True, text=True, encoding="utf-8", timeout=300)
    reply = json.loads(result.stdout)
    if check is not None:
        assert result.returncode == check, (result.returncode, result.stdout[:2000])
    return reply


def write_plan(plan):
    path = os.path.join(project, "plan.json")
    with open(path, "w", encoding="utf-8") as file:
        json.dump(plan, file)
    return path


summary = cli("query", "project_summary", check=0)
assert summary["source"] == "files" and "no Unity Editor" in summary["source_reason"], summary
revision = summary["data"]["revision"]
assert cli("query", "capabilities", check=0)["data"]["revision"] == revision, "a read must not change the revision"
groups = {group["name"]: group for group in summary["data"]["root_groups"]}
workspace = groups.get("Workspace")
assert workspace and not workspace["locked"], groups

begin = cli("edit", "begin_work", "--work_label", "Editor-free check", "--expected_revision", str(revision), check=0)
token = begin["data"]["work_token"]
assert os.path.exists(os.path.join(project, "Library", "Architector", "ai-work-session.json")), "the work marker is shared through the file"

plan = {
    "layers": [{"reference": "check", "name": "Editor Free Check", "parent": "Workspace", "purpose": "Written by the command line without Unity."}],
    "nodes": [
        {"reference": "a", "layer": "check", "title": "Reader", "responsibility": "Reads."},
        {"reference": "b", "layer": "check", "title": "Writer", "responsibility": "Writes."}
    ],
    "edges": [{"reference": "e", "sourceLayer": "check", "source": "a", "targetLayer": "check", "target": "b", "kind": "dependency"}]
}
dry = cli("edit", "dry_run_plan", "--plan", write_plan(plan), "--expected_revision", str(revision), check=0)
plan_token = dry["data"]["plan_token"]
assert cli("query", "capabilities", check=0)["data"]["revision"] == revision, "a dry run writes nothing"

stale = cli("edit", "apply_plan", "--plan", json.dumps({"plan_token": plan_token}), "--expected_revision", str(revision - 1), "--work_token", token, check=6)
assert stale["code"] in ("stale_revision", "revision_mismatch") or "revision" in json.dumps(stale), stale

applied = cli("edit", "apply_plan", "--plan", json.dumps({"plan_token": plan_token}), "--expected_revision", str(revision), "--work_token", token, check=0)
after = applied["data"]["revision"]
assert after > revision, applied
with open(os.path.join(project, "Library", "Architector", "revision.txt"), encoding="utf-8") as file:
    assert int(file.read().strip()) == after, "the command line stores its revision for the Editor"
layer = cli("query", "layer_graph", "--target", "Workspace/Editor Free Check", check=0)
assert layer["data"]["node_total"] == 2 and layer["data"]["edge_total"] == 1, layer
assert any("Editor-Free-Check" in name for name in os.listdir(os.path.join(project, "Architector", "Layers")))

locked = [group for group in summary["data"]["root_groups"] if group["locked"]]
if locked:
    core = locked[0]["name"]
    refused = cli("edit", "apply_plan", "--plan", write_plan({"layers": [{"reference": "x", "name": "Forbidden", "parent": core}]}),
                  "--expected_revision", str(after), "--work_token", token, check=6)
    assert "lock" in json.dumps(refused).lower(), refused

layer_id = layer["data"]["layer"]["id"]
# Without the mirrored workflow mode (no Editor opened yet) the command line is in Safe mode: a person approves.
approval = cli("edit", "approve_layers", "--layers", layer_id, "--approve_changes", "true",
               "--expected_revision", str(after), "--work_token", token, check=6)
assert approval["code"] == "needs_person", approval

# The Editor mirrors the user's Lazy mode; the command line then approves on its own and the baseline says so.
with open(os.path.join(project, "Library", "Architector", "agent-mode.json"), "w", encoding="utf-8") as file:
    json.dump({"workflowMode": "lazy", "displayName": "Lazy", "description": "", "agentRule": "Approve on your own.",
               "agentMayApprove": True, "showsNotice": True}, file)
assert cli("query", "capabilities", check=0)["data"]["workflow_mode"] == "lazy"
approved = cli("edit", "approve_layers", "--layers", layer_id, "--approve_changes", "true",
               "--expected_revision", str(after), "--work_token", token, check=0)
baselines = os.path.join(project, "Architector", "Baselines")
baseline = [name for name in os.listdir(baselines) if layer_id in name]
assert baseline and json.load(open(os.path.join(baselines, baseline[0]), encoding="utf-8")).get("approvedByAgent"), (approved, baseline)

# A second writer waits for the write lock, then answers busy and changes nothing. .NET locks with FileShare.None:
# on Windows any open handle blocks it, on Linux and macOS only an advisory flock.
lock_path = os.path.join(project, "Library", "Architector", "write.lock")
held = open(lock_path, "a")
if os.name != "nt":
    import fcntl
    fcntl.flock(held, fcntl.LOCK_EX | fcntl.LOCK_NB)
try:
    started = time.time()
    busy = cli("edit", "end_work", "--work_token", token, check=6)
    assert busy["code"] == "busy" and time.time() - started >= 10, busy
finally:
    held.close()
cli("edit", "end_work", "--work_token", token, check=0)
assert not os.path.exists(os.path.join(project, "Library", "Architector", "ai-work-session.json"))

shutil.rmtree(project)
print("editor-free test passed")
