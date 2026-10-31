"""Starts the published server against a minimal project and checks it over stdio.

Usage: python smoke_test.py <server binary>
"""
import json
import os
import subprocess
import sys
import tempfile

server = os.path.abspath(sys.argv[1])
project = tempfile.mkdtemp()
for folder in ("Assets", "ProjectSettings", "Architector/Groups", "Architector/Layers", "Architector/Links"):
    os.makedirs(os.path.join(project, folder))
with open(os.path.join(project, "Architector", "Architecture.digest.md"), "w", encoding="utf-8") as digest:
    digest.write("# Architecture\n\n## Core/Sample\n\nPurpose: a sample layer.\n")

process = subprocess.Popen([server], cwd=os.path.join(project, "Assets"), stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
count = 0


def call(method, params=None):
    global count
    count += 1
    process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": count, "method": method, "params": params or {}}) + "\n")
    process.stdin.flush()
    return json.loads(process.stdout.readline())


def tool(name, arguments=None):
    result = call("tools/call", {"name": name, "arguments": arguments or {}})["result"]
    return result["isError"], result["content"][0]["text"]


assert call("initialize", {"protocolVersion": "2025-06-18"})["result"]["serverInfo"]["name"] == "architector"
names = [item["name"] for item in call("tools/list")["result"]["tools"]]
assert names == ["architector_status", "architector_route", "architector_digest", "architector_query", "architector_edit"], names
failed, text = tool("architector_status")
status = json.loads(text)
assert not failed and status["has_architecture"] and status["has_digest"] and not status["editor_connected"], text
assert os.path.realpath(status["project"]) == os.path.realpath(project), text
failed, text = tool("architector_route", {"queries": ["Assets"]})
assert not failed and "layer: none" in text, text
failed, text = tool("architector_digest")
assert not failed and text.strip() == "Core/Sample", text
failed, text = tool("architector_digest", {"layer": "core/sample"})
assert not failed and "a sample layer" in text, text
failed, text = tool("architector_query", {"action": "project_summary"})
assert failed and "not running" in text, text
assert call("no/such/method")["error"]["code"] == -32601
process.stdin.write("not json\n")
process.stdin.flush()
assert json.loads(process.stdout.readline())["error"]["code"] == -32700
process.stdin.close()
assert process.wait(timeout=10) == 0
print("smoke test passed")
