"""Checks the code facts of the server on the fixture, and when a DependencyEvidence build is given, that the
server answers exactly like the CLI and that its evidence counts equal DependencyEvidence's.

Usage: python facts_test.py <server binary> [<DependencyEvidence.dll>]
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

server = os.path.abspath(sys.argv[1])
evidence_tool = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else None
project = os.path.join(tempfile.mkdtemp(), "Fixture")
shutil.copytree(os.path.join(os.path.dirname(os.path.abspath(__file__)), "Fixture"), project)

process = subprocess.Popen([server, "--project", project], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
count = 0


def query(arguments):
    global count
    count += 1
    request = {"jsonrpc": "2.0", "id": count, "method": "tools/call", "params": {"name": "architector_query", "arguments": arguments}}
    process.stdin.write(json.dumps(request) + "\n")
    process.stdin.flush()
    result = json.loads(process.stdout.readline())["result"]
    return result["isError"], result["content"][0]["text"]


def answer(arguments):
    failed, text = query(arguments)
    data = json.loads(text)
    assert not failed and data["success"], text
    return data


evidence = answer({"action": "code_evidence"})
assert (evidence["scoped_layers"], evidence["layer_pairs"], evidence["references"]) == (3, 4, 12), evidence
assert (evidence["undeclared"], evidence["cycles"], evidence["unevidenced"]) == (1, 1, 1), evidence
assert evidence["unevidenced_pairs"] == ["Sample/Core -> Sample/Ui"], evidence
undeclared = answer({"action": "code_evidence", "status": "undeclared"})["pairs"]
assert [(pair["source"], pair["target"], pair["references"]) for pair in undeclared] == [("Sample/Player", "Sample/Ui", 2)], undeclared
references = answer({"action": "code_evidence", "pair": "Sample/Player -> Sample/Core", "include": "references", "limit": 3})
assert references["items_total"] == 7 and len(references["items"]) == 3 and references["next_offset"] == 3, references
owners = answer({"action": "code_evidence", "include": "owners", "limit": 50})
assert ["Sample.Tools.Helper", None, "Assets/Tools/Helper.cs:3"] in owners["items"], owners

core = answer({"action": "layer_facts", "target": "Core"})
assert (core["layer"], core["files"], core["types"]) == ("Sample/Core", 4, 7), core
rules = evidence["rules"]
assert rules["presets"] == ["unity"], rules
assert [(item["rule"], item["name"], item["uses"]) for item in rules["suggestions"]] == [("genericCalls", "Fetch", 10), ("injectionAttributes", "Track", 10)], rules
controller = answer({"action": "layer_facts", "target": "Player", "type": "PlayerController"})
assert controller["extra"]["serialized"] == [["PlayerInventory", "Assets/Player/PlayerController.cs:10"]], controller
wiring = answer({"action": "layer_facts", "target": "Core", "type": "Wiring"})
assert wiring["extra"] == {"generic_calls": [["Get<Clock>", "Assets/Core/Wiring.cs:18"]], "injected": [["ITickable", "Assets/Core/Wiring.cs:9"]]}, wiring
assert next(item for item in core["type_list"] if item["type"] == "Sample.Core.Wiring")["generic_calls"] == 1, core
candidates = {item["type"]: item for item in core["node_candidates"]}
assert candidates["Sample.Core.Registry`1"]["rules"] == ["used_by_other_layers", "inherited_by_other_layers", "extension_point"], candidates
assert candidates["Sample.Core.Registry`1"]["node"] is None and candidates["Sample.Core.Clock"]["node"] == "node_clock", candidates
ui = answer({"action": "layer_facts", "target": "Sample/Ui"})
assert {item["type"]: item["rules"] for item in ui["node_candidates"]}["Sample.Ui.DebugMenu"] == ["unity_entry"], ui
hud = next(item for item in ui["type_list"] if item["type"] == "Sample.Ui.HudView")
assert hud["parts"] == ["Assets/Ui/HudView.cs:5"] and hud["nodes"] == ["node_hud"], hud
clock = answer({"action": "layer_facts", "target": "Core", "type": "Clock"})
assert [member[1] for member in clock["members"]] == ["float Now", "void Advance(float delta)"], clock
assert clock["members"][1][2] == "Assets/Core/Clock.cs:7", clock

what_if = answer({"action": "scope_what_if", "moves": [{"type": "Sample.Ui.HudView", "to": "Sample/Player"}]})
assert (what_if["undeclared"], what_if["cycles"]) == ("1 -> 0", "1 -> 0"), what_if
assert [item["relationship"] for item in what_if["relationships_losing_evidence"]] == ["edge_hud_player"], what_if
assert what_if["linked_nodes_moving"][0]["node"] == "node_hud", what_if
failed, text = query({"action": "scope_what_if", "moves": [{"type": "Nope", "to": "Sample/Core"}]})
assert "Nothing matches" in text, text

dump = answer({"action": "layer_dump", "target": "Sample/Core", "limit": 3})
assert dump["items_total"] == 6 and dump["next_offset"] == 3 and dump["items"][0]["node"] == "node_clock", dump
rest = answer({"action": "layer_dump", "target": "Sample/Core", "offset": 3})
assert [item["link"] for item in rest["items"]] == ["edge_player_clock", "edge_ui_registry", "edge_core_hud"], rest
assert rest["items"][0]["to"] == "Sample/Core::Clock" and "next_offset" not in rest, rest
lookup = answer({"action": "lookup", "names": ["Sample/Core::Clock::Advance", "Core::Nothing"]})["results"]
assert lookup[0]["member"] == "member_advance" and lookup[0]["symbolKey"].startswith("method|"), lookup
assert "error" in lookup[1], lookup
failed, text = query({"action": "layer_facts", "target": "Missing"})
assert failed and "layer_not_found" in text, text


def cli(*arguments):
    result = subprocess.run([server, *arguments, "--project", project], capture_output=True, text=True, encoding="utf-8")
    return result.returncode, result.stdout.strip()


code, text = cli("facts", "layer", "Core")
assert code == 0 and text == query({"action": "layer_facts", "target": "Core"})[1], text
code, text = cli("facts", "what-if", "--move", "Sample.Ui.HudView=Sample/Player")
assert code == 0 and json.loads(text)["undeclared"] == "1 -> 0", text
code, text = cli("facts", "lookup", "Sample/Core::Clock::Advance")
assert code == 0 and json.loads(text)["results"][0]["member"] == "member_advance", text
assert cli("facts", "layer", "Missing")[0] == 6
assert cli("facts", "nothing")[0] == 2
assert cli("query", "work_status")[0] == 7
code, text = cli("status")
assert code == 0 and json.loads(text)["cli"], text
code, text = cli("commands")
assert code == 0 and any(item["command"] == "facts layer" for item in json.loads(text)["commands"]), text
code, text = cli("skill", "install", "claude")
assert code == 4 and json.loads(text)["code"] == "package_not_found", text
code, text = cli("doctor")
assert code == 0 and any(item["check"] == "code_index" for item in json.loads(text)["checks"]), text
help_text = subprocess.run([server, "help", "facts"], capture_output=True, text=True, encoding="utf-8").stdout
assert "facts what-if" in help_text and "Exit codes" in help_text, help_text

if evidence_tool:
    for arguments in ({"action": "code_evidence"}, {"action": "layer_facts", "target": "Core"}, {"action": "scope_what_if", "moves": [{"folder": "Assets/Ui", "to": "Sample/Core"}]}, {"action": "layer_dump", "target": "Ui"}):
        action = arguments.pop("action")
        cli = subprocess.run(["dotnet", evidence_tool, project, f"--facts={action}", "--args=" + json.dumps(arguments), "--compact"],
                             capture_output=True, text=True, encoding="utf-8").stdout.strip()
        assert query(dict(arguments, action=action))[1] == cli, action
    text = subprocess.run(["dotnet", evidence_tool, project, "--observed"], capture_output=True, text=True, encoding="utf-8").stdout
    counted = {}
    for line in text.splitlines():
        match = re.match(r"^  (\S.*?) -> (.*?) \((\d+) references\)$", line)
        if match:
            counted[(match.group(1), match.group(2))] = int(match.group(3))
    all_pairs = answer({"action": "code_evidence", "limit": 100})["pairs"]
    assert {(pair["source"], pair["target"]): pair["references"] for pair in all_pairs} == counted, (all_pairs, counted)

process.stdin.close()
assert process.wait(timeout=30) == 0
print("code facts test passed" + (" (server equals CLI and DependencyEvidence)" if evidence_tool else ""))
