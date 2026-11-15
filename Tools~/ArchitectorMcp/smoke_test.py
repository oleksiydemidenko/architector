"""Starts the published server against a minimal project and checks it over stdio.

Usage: python smoke_test.py <server binary>
"""
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time

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
assert not failed and text.startswith("# Architecture Digest index") and "\nCore: Sample (0)" in text, text
failed, text = tool("architector_digest", {"form": "compact"})
assert not failed and text.startswith("# Architecture Digest (compact)") and "\n## Core/Sample" in text, text
failed, text = tool("architector_digest", {"form": "list"})
assert not failed and text.strip() == "Core/Sample", text
failed, text = tool("architector_digest", {"layer": "core/sample"})
assert not failed and "a sample layer" in text, text
failed, text = tool("architector_digest", {"layer": "missing"})
assert failed and "No approved layer or group is called missing" in text, text
failed, text = tool("architector_query", {"action": "project_summary"})
assert failed and json.loads(text)["source"] == "files" and json.loads(text)["code"] == "no_data", text
assert not os.path.exists(os.path.join(project, "Architector", "architector.project.json")), "the command line never creates the data"
assert call("no/such/method")["error"]["code"] == -32601
process.stdin.write("not json\n")
process.stdin.flush()
assert json.loads(process.stdout.readline())["error"]["code"] == -32700
process.stdin.close()
assert process.wait(timeout=10) == 0


def cli(*arguments):
    result = subprocess.run([server, *arguments, "--project", project], capture_output=True, text=True, encoding="utf-8")
    return result.returncode, result.stdout.strip()


# The command line answers route and digest without the Editor, with the text of the MCP tools.
code, text = cli("digest", "--form", "list")
assert code == 0 and text == "Core/Sample", text
code, text = cli("digest", "Sample")
assert code == 0 and "a sample layer" in text, text
code, text = cli("digest", "--json")
assert code == 0 and json.loads(text)["text"].startswith("# Architecture Digest index"), text
code, text = cli("digest", "missing")
assert code == 6 and json.loads(text)["code"] == "digest", text
code, text = cli("route", "Assets")
assert code == 6 and "layer: none" in text, text
code, text = cli("route")
assert code == 2 and json.loads(text)["code"] == "usage", text
code, text = cli("commands")
assert {"route", "digest"} <= {item["command"] for item in json.loads(text)["commands"]}, text


# query and edit without an Editor run on the files; through a stand-in Editor bridge the client names itself and, with
# --source editor, the command line waits for a running Unity.
def timed(*arguments):
    started = time.monotonic()
    result = subprocess.run([server, *arguments, "--project", project], capture_output=True, text=True, encoding="utf-8")
    return result.returncode, result.stdout.strip(), result.stderr, time.monotonic() - started


requests = []
listener = socket.socket()
listener.bind(("127.0.0.1", 0))
listener.listen()


def serve():
    while True:
        connection, _ = listener.accept()
        with connection, connection.makefile("rw", encoding="utf-8", newline="\n") as stream:
            line = stream.readline()
            # status only checks that the bridge accepts a connection.
            if not line.strip():
                continue
            request = json.loads(line)
            requests.append(request)
            stream.write(json.dumps({"success": True, "data": {"client": request.get("client")}}) + "\n")
            stream.flush()


threading.Thread(target=serve, daemon=True).start()
state = os.path.join(project, "Library", "Architector", "agent-bridge.json")
os.makedirs(os.path.dirname(state), exist_ok=True)
settings = os.path.join(project, "Architector", "architector.settings.json")


def publish_bridge(clients):
    with open(state, "w", encoding="utf-8") as file:
        json.dump({"port": listener.getsockname()[1], "token": "t", "clients": clients}, file)


code, text, _, seconds = timed("query", "capabilities")
assert code == 6 and json.loads(text)["code"] == "no_data" and "no Unity Editor" in json.loads(text)["source_reason"] and seconds < 10, text
code, text, _, seconds = timed("query", "capabilities", "--source", "editor", "--wait", "0")
assert code == 7 and json.loads(text)["code"] == "editor_unreachable" and "not running" in text and seconds < 10, text
code, text, _, _ = timed("query", "capabilities", "--wait", "soon")
assert code == 2 and json.loads(text)["code"] == "usage", text
with open(settings, "w", encoding="utf-8") as file:
    json.dump({"agent": {"integrations": [{"id": "architector-cli", "enabled": False}]}}, file)
code, text, _, _ = timed("query", "capabilities")
assert code == 6 and json.loads(text)["code"] == "integration_disabled" and "Architector CLI" in text, text
os.remove(settings)

# Unity holds Temp/UnityLockfile while it runs; a held lock with no bridge means a compile or a domain reload.
os.makedirs(os.path.join(project, "Temp"), exist_ok=True)
lock = open(os.path.join(project, "Temp", "UnityLockfile"), "w")
if os.name != "nt":
    import fcntl
    fcntl.flock(lock, fcntl.LOCK_EX)
code, text, errors, seconds = timed("query", "capabilities")
assert code == 6 and "does not answer now" in json.loads(text)["source_reason"] and seconds < 10, (text, errors)
code, text, errors, seconds = timed("query", "capabilities", "--source", "editor", "--wait", "1")
assert code == 7 and "did not answer within 1 s" in text and "Waiting up to 1 s" in errors and seconds >= 1, (text, errors)
threading.Timer(1.5, publish_bridge, args=(["cli", "mcp"],)).start()
code, text, errors, seconds = timed("query", "capabilities", "--source", "editor", "--wait", "20")
assert code == 0 and json.loads(text)["data"]["client"] == "cli" and json.loads(text)["source"] == "editor" and seconds >= 1.5, (text, errors)
assert requests[-1]["client"] == "cli" and requests[-1]["token"] == "t" and requests[-1]["parameters"]["action"] == "capabilities", requests
assert "wait" not in requests[-1]["parameters"] and "source" not in requests[-1]["parameters"] and "claim_seconds" not in requests[-1], requests
code, text, errors, seconds = timed("query", "capabilities")
assert code == 0 and json.loads(text)["source"] == "editor" and requests[-1]["claim_seconds"] == 5, (text, requests[-1])
code, text = cli("status")
status = json.loads(text)
assert status["unity_running"] and status["editor_connected"] and status["editor_serves"] == ["cli", "mcp"], text
assert status["integrations"] == {"architector-cli": True, "architector-mcp": True} and "30 s" in status["editor_wait"], text

# The MCP server names itself as the other client of the same bridge.
process = subprocess.Popen([server, "--project", project], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
assert call("initialize", {"protocolVersion": "2025-06-18"})["result"]["serverInfo"]["name"] == "architector"
failed, text = tool("architector_query", {"action": "capabilities"})
assert not failed and json.loads(text)["data"]["client"] == "mcp" and requests[-1]["client"] == "mcp", text
process.stdin.close()
assert process.wait(timeout=10) == 0
lock.close()
print("smoke test passed")
