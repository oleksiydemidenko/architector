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
assert wiring["behaviour"]["reads"] == [["Get<Clock>", "Assets/Core/Wiring.cs:18"]], wiring
listener = answer({"action": "layer_facts", "target": "Player", "type": "PlayerListener"})["behaviour"]
assert listener["subscribes"] == [["PlayerSignals.Changed", "Assets/Player/PlayerShadow.cs:30"], ["PlayerSignals.Scored", "Assets/Player/PlayerShadow.cs:31"]], listener
assert listener["assigns"] == [["PlayerSignals.Score", "Assets/Player/PlayerShadow.cs:36"]], listener
assert [item[0] for item in listener["lifecycle"]] == ["OnEnable", "Update"], listener
assert listener["unpaired"] == ["OnEnable without OnDisable"] and listener["loops"] == ["Update"], listener
assert listener["counts"]["unsubscribes"] == 0, listener
player_layer = answer({"action": "layer_facts", "target": "Player"})
assert player_layer["wiring"] == {"wired": 6, "test_only": 1, "base_only": 2, "created_in_code": 1, "unwired": 2, "not_tracked": 3}, player_layer["wiring"]
assert player_layer["project"] == project and "no evidence either way" in player_layer["wiring_note"], player_layer
assert evidence["project"] == project, evidence
statuses = {item["type"]: item.get("wiring") for item in player_layer["type_list"]}
assert all(status is not None for status in statuses.values()), statuses
assert statuses["Sample.Player.PlayerLoadoutInitializerWithAVeryLongNameThatWraps"] == "wired", statuses
assert statuses["Sample.Player.PlayerBlockEntry"] == "wired" and statuses["Sample.Player.PlayerStatsValue"] == "not_tracked", statuses
part = answer({"action": "layer_facts", "target": "Player", "type": "PlayerPart"})["wiring"]
assert part["status"] == "base_only" and part["base_of"] == ["Sample.Player.PlayerHealth"] and part["base_of_count"] == 1, part
stat = answer({"action": "layer_facts", "target": "Player", "type": "Sample.Player.PlayerStat`1"})["wiring"]
assert stat["status"] == "base_only" and stat["base_of"] == ["Sample.Player.PlayerSpeed"], stat
legacy = answer({"action": "layer_facts", "target": "Player", "type": "PlayerLegacy"})["wiring"]
assert legacy["status"] == "unwired" and legacy["reason"] == "abstract, no subclasses", legacy
listener = answer({"action": "layer_facts", "target": "Player", "type": "PlayerListener"})["wiring"]
assert listener["status"] == "unwired" and "file is not named after it" in listener["reason"], listener
value = answer({"action": "layer_facts", "target": "Player", "type": "PlayerStatsValue"})["wiring"]
assert value["status"] == "not_tracked" and "proves nothing" in value["note"], value
assert {item["type"]: item.get("wiring") for item in player_layer["type_list"]}["Sample.Player.PlayerSpawner"] == "created_in_code", player_layer
spawner = answer({"action": "layer_facts", "target": "Player", "type": "PlayerSpawner"})["wiring"]
assert spawner["reason"] == "created from code at Assets/Player/PlayerFactory.cs:7", spawner
controller_wiring = answer({"action": "layer_facts", "target": "Player", "type": "PlayerController"})["wiring"]
assert (controller_wiring["status"], controller_wiring["prefabs"], controller_wiring["scenes"], controller_wiring["disabled_in"], controller_wiring["listed_by"]) == ("wired", 1, 1, 1, 1), controller_wiring
assert {"file": "Assets/Prefabs/Player.prefab", "object": "Spare", "enabled": False, "listed_by": ["PlayerController.spares"]} in controller_wiring["places"]["items"], controller_wiring
shadow_wiring = answer({"action": "layer_facts", "target": "Player", "type": "PlayerShadow"})["wiring"]
assert shadow_wiring["status"] == "test_only" and shadow_wiring["test_only"], shadow_wiring
inventory_wiring = answer({"action": "layer_facts", "target": "Player", "type": "PlayerInventory"})["wiring"]
assert inventory_wiring["serialized_references"] == ["Assets/Prefabs/Player.prefab"] and inventory_wiring["status"] == "wired", inventory_wiring
assert os.path.exists(os.path.join(project, "Library", "Architector", "wiring-index.json"))
paged = answer({"action": "layer_facts", "target": "Player", "type": "PlayerListener", "section": "subscribes", "limit": 1})["behaviour"]
assert len(paged["subscribes"]["items"]) == 1 and paged["subscribes"]["next_offset"] == 1 and "lifecycle" not in paged, paged
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
assert clock["members"][1][3] == {"kind": "method", "name": "Advance", "parameters": ["Single"]}, clock
assert clock["members"][0][3] == {"kind": "property", "name": "Now"}, clock

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
assert failed and "layer_not_found" in text and json.loads(text)["target"] == "Missing", text
failed, text = query({"action": "layer_facts", "target": "Cor"})
assert failed and json.loads(text)["candidates"] == ["Sample/Core"], text
assert answer({"action": "layer_facts", "target": "sample/core"})["layer"] == "Sample/Core"

count += 1
process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": count, "method": "tools/call", "params": {"name": "architector_route", "arguments": {"queries": ["Assets/Ui/HudView.cs"]}}}) + "\n")
process.stdin.flush()
route_text = json.loads(process.stdout.readline())["result"]["content"][0]["text"]
assert "layer: Sample/Ui (namespace scope Sample.Ui, node HudView)" in route_text, route_text

unevidenced = answer({"action": "code_evidence", "status": "unevidenced"})
assert unevidenced["pairs"] == [] and unevidenced["unevidenced_pairs"] == ["Sample/Core -> Sample/Ui"], unevidenced
assert references["items"][0][3:] == ["Sample/Player", "Sample/Core"], references
intra = answer({"action": "code_evidence", "include": "intra", "limit": 1})
assert len(intra["items"][0]) == 4, intra
assert all(item["folders"] == ["Assets/Core"] for item in rules["suggestions"]), rules


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

# The command line works from any folder with the options before the command, and never ends without output.
outside = tempfile.mkdtemp()
first = subprocess.run([server, "--project", project, "status"], capture_output=True, text=True, encoding="utf-8", cwd=outside, timeout=60)
assert first.returncode == 0 and json.loads(first.stdout)["has_architecture"], first
mistyped = subprocess.run([server, "--project", project, "stauts"], capture_output=True, text=True, encoding="utf-8", cwd=outside, timeout=60)
assert mistyped.returncode == 2 and json.loads(mistyped.stdout)["code"] == "unknown_command", mistyped
nowhere = subprocess.run([server, "facts", "evidence"], capture_output=True, text=True, encoding="utf-8", cwd=outside, timeout=60)
assert nowhere.returncode == 4 and json.loads(nowhere.stdout)["code"] == "no_project", nowhere

# An installed skill links the Workflow installed next to it.
package = os.path.join(project, "Packages", "com.oleksiydemidenko.architector")
repository = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
os.makedirs(package)
shutil.copy(os.path.join(repository, "package.json"), package)
shutil.copytree(os.path.join(repository, "Skills~"), os.path.join(package, "Skills~"))
shutil.copytree(os.path.join(repository, "Documentation~"), os.path.join(package, "Documentation~"))
code, text = cli("skill", "install", "claude")
assert code == 0 and json.loads(text)["skills"][0]["status"] == "installed", text
installed = os.path.join(project, ".claude", "skills", "architector-authoring")
skill_text = open(os.path.join(installed, "SKILL.md"), encoding="utf-8").read()
assert "](Workflow.md)" in skill_text and "Documentation~" not in skill_text, skill_text[:400]
assert "](SKILL.md)" in open(os.path.join(installed, "Workflow.md"), encoding="utf-8").read()

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

# Without Unity's project files the engine base classes are unseen and reported; PlayerShadow's inherited Clock is not
# counted (the totals above stay at 12). With them, their engine and package assemblies join the analysis.
binding = evidence["binding"]
assert binding["unity_references"] == 0 and binding["unseen_base_types"] >= 1 and "hint" in binding, binding
if evidence_tool:
    with open(os.path.join(project, "Assembly-CSharp.csproj"), "w", encoding="utf-8") as csproj:
        csproj.write('<Project><PropertyGroup><DefineConstants>UNITY_EDITOR</DefineConstants></PropertyGroup><ItemGroup>'
                     f'<Reference Include="Engine"><HintPath>{evidence_tool}</HintPath></Reference>'
                     '<Reference Include="netstandard"><HintPath>C:/missing/netstandard.dll</HintPath></Reference>'
                     '</ItemGroup></Project>')
    with_project_files = answer({"action": "code_evidence"})
    assert with_project_files["binding"]["unity_references"] == 1 and with_project_files["references"] == 12, with_project_files
    os.remove(os.path.join(project, "Assembly-CSharp.csproj"))

# A package base class that Unity's project file brings in as a ProjectReference: its inherited property Clock must bind
# to the package, never to the project's own Sample.Core.Clock. Without the package the name binds to the project type.
def write(relative, text):
    path = os.path.join(project, *relative.split("/"))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)

net_source = "namespace Sample.Net\n{\n    public class NetClock { public float Tick; }\n    public class NetBehaviour { public NetClock Clock { get; } = new NetClock(); }\n}\n"
write("Assets/Player/NetPlayer.cs", "using Sample.Core;\n\nnamespace Sample.Player\n{\n    public sealed class NetPlayer : Sample.Net.NetBehaviour\n    {\n        public float Read() => Clock.Tick;\n    }\n}\n")
unseen = answer({"action": "code_evidence"})
assert unseen["references"] == 13 and any(item["base"] == "Sample.Net.NetBehaviour" for item in unseen["binding"]["unseen_base_examples"]), unseen
write("Packages/com.sample.net/Runtime/NetBehaviour.cs", net_source)
write("Net.csproj", '<Project><PropertyGroup><AssemblyName>Sample.Net</AssemblyName><DefineConstants>UNITY_EDITOR</DefineConstants></PropertyGroup>'
      '<ItemGroup><Compile Include="Packages\\com.sample.net\\Runtime\\NetBehaviour.cs" /></ItemGroup></Project>')
write("Assembly-CSharp.csproj", '<Project><PropertyGroup><DefineConstants>UNITY_EDITOR</DefineConstants></PropertyGroup>'
      '<ItemGroup><ProjectReference Include="Net.csproj" /></ItemGroup></Project>')
from_sources = answer({"action": "code_evidence"})
assert from_sources["references"] == 12, from_sources
assert from_sources["binding"]["project_references"] == {"compiled": 0, "from_sources": 1, "missing": 0}, from_sources["binding"]
if shutil.which("dotnet"):
    library = tempfile.mkdtemp()
    with open(os.path.join(library, "Net.cs"), "w", encoding="utf-8") as handle:
        handle.write(net_source)
    with open(os.path.join(library, "Sample.Net.csproj"), "w", encoding="utf-8") as handle:
        handle.write('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Sample.Net</AssemblyName></PropertyGroup></Project>')
    built = subprocess.run(["dotnet", "build", library, "-c", "Release", "-o", os.path.join(library, "out"), "-nologo", "-v", "q"], capture_output=True, text=True)
    assert built.returncode == 0, built.stdout + built.stderr
    os.makedirs(os.path.join(project, "Library", "ScriptAssemblies"), exist_ok=True)
    shutil.copy(os.path.join(library, "out", "Sample.Net.dll"), os.path.join(project, "Library", "ScriptAssemblies", "Sample.Net.dll"))
    os.remove(os.path.join(project, "Packages", "com.sample.net", "Runtime", "NetBehaviour.cs"))
    with open(os.path.join(project, "Net.csproj"), "a", encoding="utf-8") as handle:
        handle.write("\n")
    compiled = answer({"action": "code_evidence"})
    assert compiled["references"] == 12 and compiled["binding"]["project_references"]["compiled"] == 1, compiled["binding"]
os.remove(os.path.join(project, "Net.csproj"))
missing = answer({"action": "code_evidence"})
assert missing["binding"]["missing_assemblies"] == ["Net"] and "Net" in missing["binding"]["hint"], missing["binding"]
for leftover in ("Assembly-CSharp.csproj", "Assets/Player/NetPlayer.cs"):
    os.remove(os.path.join(project, *leftover.split("/")))

# suggestions.exclude leaves uses in those folders out of the rule suggestions.
with open(os.path.join(project, "Architector", "code-facts.json"), "w", encoding="utf-8") as config:
    json.dump({"genericCalls": ["Get"], "injectionAttributes": ["InjectAttribute"], "suggestions": {"exclude": ["Assets/Core/**"]}}, config)
excluded = answer({"action": "code_evidence"})["rules"]
assert "suggestions" not in excluded and excluded["suggestions_exclude"] == ["Assets/Core/**"], excluded

process.stdin.close()
assert process.wait(timeout=30) == 0
print("code facts test passed" + (" (server equals CLI and DependencyEvidence)" if evidence_tool else ""))
