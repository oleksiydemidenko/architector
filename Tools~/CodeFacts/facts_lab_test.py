"""Checks the 0.3.7 and 0.3.8 code facts on FixtureLab: script assets resolved by file, managed references and prefab-variant
overrides, [SerializeReference]-only types, reflection creation, behaviour facts, the audit and facts snapshots.

Usage: python facts_lab_test.py <server binary>
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

server = [os.path.abspath(sys.argv[1])] if not sys.argv[1].endswith(".dll") else ["dotnet", os.path.abspath(sys.argv[1])]
project = os.path.join(tempfile.mkdtemp(), "FixtureLab")
shutil.copytree(os.path.join(os.path.dirname(os.path.abspath(__file__)), "FixtureLab"), project)
process = subprocess.Popen(server + ["--project", project], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
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


def wiring(name):
    return answer({"action": "layer_facts", "target": "Game", "type": name})["wiring"]


def behaviour(name):
    return answer({"action": "layer_facts", "target": "Game", "type": name})["behaviour"]


def places(entry):
    return [(item["file"], item["object"], item.get("via")) for item in entry["places"]["items"]]


# Reflection sites are reported with the types they can create (D).
evidence = answer({"action": "code_evidence"})
assert evidence["reflection_sites"] == [{"base": "Lab.Core.ModuleBase", "at": "Assets/Lab/Core/ModuleLoader.cs:17", "matching_types": 2}], evidence

game = answer({"action": "layer_facts", "target": "Game"})
assert game["wiring"] == {"wired": 23, "test_only": 1, "base_only": 3, "created_in_code": 3, "created_by_reflection": 2, "unwired": 7, "not_tracked": 41}, game["wiring"]
assert "**/* OLD.prefab" in game["wiring_test_places"], game["wiring_test_places"]

# 1. The script asset is found from every part of a partial type, by a name that differs only in case, and as the only
# class of a file with another name.
settings = wiring("Settings")
assert settings["status"] == "wired" and settings["script_name"] == {"file": "Assets/Lab/Game/Settings.cs", "match": "exact"}, settings
assert places(settings) == [("Assets/Lab/Data/Settings.asset", "Settings", None)], settings
mover = wiring("RigidbodyMover")
assert mover["status"] == "wired" and mover["script_name"]["match"] == "case" and "letter case" in mover["name_warning"], mover
spawner = wiring("WaveSpawner")
assert spawner["status"] == "wired" and spawner["script_name"] == {"file": "Assets/Lab/Game/Spawner.cs", "match": "single_class"} and "name_warning" in spawner, spawner
assert wiring("SpawnPoint")["reason"] == "no script asset of its own (its file is not named after it)"

# 2. A generic base in the file of the placed class with the same name is base_only, never wired.
assert wiring("Turret")["status"] == "wired"
generic = wiring("Lab.Game.Turret`1")
assert generic["status"] == "base_only" and generic["base_of"] == ["Lab.Game.Turret"] and "script_name" not in generic, generic

# 3 and 5. Managed references, also those a variant adds by override, are placements with their objects.
audio = wiring("AudioInstaller")
assert audio["status"] == "wired" and places(audio) == [("Assets/Lab/Prefabs/Hero Variant.prefab", "Hero Variant", "variant_override")], audio
# 0.3.8: Unity quotes a property path with brackets and a value with a colon; the target line may wrap.
quoted = wiring("QuotedInstaller")
assert quoted["status"] == "wired" and places(quoted) == [("Assets/Lab/Prefabs/Hero Quoted Variant.prefab", "Hero: Quoted", "variant_override")], quoted
inputs = wiring("InputInstaller")
assert inputs["status"] == "wired" and places(inputs) == [("Assets/Lab/Prefabs/Hero.prefab", "Hero", "managed_reference")], inputs

# 4. A type only [SerializeReference] can store is unwired when no asset stores it; by-value storage or "new" keep it
# not_tracked and the note says why.
unused = wiring("UnusedInstaller")
assert unused["status"] == "unwired" and unused["unused"] and unused["siblings_in_assets"] == 3, unused
assert unused["reason"] == "stored only through [SerializeReference] fields (InstallerList.installers: IInstaller); no asset stores it", unused
value = wiring("ValueInstaller")
assert value["status"] == "not_tracked" and "by value at Assets/Lab/Game/InstallerList.cs:9" in value["note"], value
created = wiring("NewInstaller")
assert created["status"] == "not_tracked" and "Created with new at Assets/Lab/Game/InstallerList.cs:41" in created["note"], created

# 0.3.8: code that creates a type without "new X()" keeps it not_tracked: a generic method that creates its type argument
# (directly, through another generic method, with the argument inferred, or called as an extension method), Activator
# with typeof, and a generic type that creates its argument (new G<X>() or a base type G<X>); a type named only by
# typeof is undecidable. A type that code only reads stays unwired.
for name, how, line in [("FactoryInstaller", "Create<T>", 29), ("ForwardedInstaller", "Ensure<T>", 30), ("InferredInstaller", "TryMake<T>", 31),
                        ("TypeOfInstaller", "Activator.CreateInstance", 32), ("ExtensionInstaller", "Install<T>", 36)]:
    made = wiring(name)
    assert made["status"] == "not_tracked" and f"Created in code by {how} at Assets/Lab/Game/InstallerFactory.cs:{line}" in made["note"], made
for name, line in [("PooledInstaller", 14), ("InheritedInstaller", 17)]:
    pooled = wiring(name)
    assert pooled["status"] == "not_tracked" and f"Created in code by InstallerPool<T> at Assets/Lab/Game/InstallerPools.cs:{line}" in pooled["note"], pooled
# Review cases: target-typed new() of a creating type, Activator with typeof(T), a static member of a creating type, an
# interface method whose implementation creates, a method group, a type's own static factory called from outside, and the
# base class of a created type.
for name, how, line in [("TargetTypedInstaller", "InstallerPool<T>", 25), ("TypeOfGenericInstaller", "ViaTypeOf<T>", 31),
                        ("StaticCacheInstaller", "StaticCache<T>", 32), ("InterfaceInstaller", "Make<T>", 33),
                        ("GroupInstaller", "Create<T>", 34), ("RentedInstaller", "Create<T>", 50)]:
    made = wiring(name)
    assert made["status"] == "not_tracked" and f"Created in code by {how} at Assets/Lab/Game/ReviewCases.cs:{line}" in made["note"], made
for name, how, line in [("QualifiedInstaller", "StaticCache<T>", 11), ("SingletonInstaller", "Create<T>", 22), ("InitInstaller", "Create<T>", 32)]:
    made = wiring(name)
    assert made["status"] == "not_tracked" and f"Created in code by {how} at Assets/Lab/Game/ReviewCases2.cs:{line}" in made["note"], made
base = wiring("BaseReviewInstaller")
assert base["status"] == "not_tracked" and base["note"].startswith("Base of Lab.Game.DerivedReviewInstaller"), base
shared = wiring("SharedAsset")
assert shared["status"] == "created_in_code" and shared["reason"] == "created from code at Assets/Lab/Game/ReviewCases.cs:37", shared
self_made = wiring("SelfInstaller")
assert self_made["status"] == "unwired" and "note" not in self_made, self_made
named = wiring("NamedInstaller")
assert named["status"] == "not_tracked" and "Named by typeof at Assets/Lab/Game/InstallerFactory.cs:45" in named["note"], named
read_only = wiring("ReadOnlyInstaller")
assert read_only["status"] == "unwired" and read_only["siblings_in_assets"] == 3, read_only
# A registry attribute repeated on one type (a serialization union) names types by typeof but creates none.
assert "note" not in read_only and wiring("UnusedInstaller")["status"] == "unwired"

# 0.3.8: a type that only Editor code creates is created_in_code with a reason that names the Editor.
graph = wiring("GraphAsset")
assert graph["status"] == "created_in_code" and graph["reason"] == "created from Editor code at Assets/Lab/Game/Editor/GraphImporter.cs:5 (an importer or a tool; the assets it makes are not scanned)", graph

# 6. base_of has a total and pages.
effect = wiring("Effect")
assert effect["status"] == "base_only" and len(effect["base_of"]) == 10 and effect["base_of_total"] == 12 and effect["base_of_count"] == 12, effect
paged = answer({"action": "layer_facts", "target": "Game", "type": "Effect", "section": "base_of", "offset": 10, "limit": 5})["wiring"]["base_of"]
assert paged["items_total"] == 12 and paged["items"] == ["Lab.Game.Effect11", "Lab.Game.Effect12"], paged

# C. Test places take file patterns.
old = wiring("OldOnly")
assert old["status"] == "test_only" and old["test_only"], old

# D. Subclasses created by reflection; an unrelated class stays not_tracked.
module = wiring("AudioModule")
assert module["status"] == "created_by_reflection" and module["reason"] == "created by reflection at Assets/Lab/Core/ModuleLoader.cs:17 (subclasses of Lab.Core.ModuleBase)", module
assert wiring("SaveModule")["status"] == "created_by_reflection" and wiring("PlainHelper")["status"] == "not_tracked"

# 7 and 8. Non-generic overloads and memberCalls are writes; assignments to by-value copies are not assigns.
user = behaviour("HealthUser")
assert user["writes"] == [["Add(Health)", "Assets/Lab/Game/HealthUser.cs:14"], ["Health.Set", "Assets/Lab/Game/HealthUser.cs:15"]], user
assert user["reads"] == [["Get<Health>", "Assets/Lab/Game/HealthUser.cs:16"]], user
assert user["assigns"] == [["Parameter.Value", "Assets/Lab/Game/HealthUser.cs:25"]], user

# 9. Methods named like Unity callbacks on non-Unity types are not callbacks.
ticker = behaviour("Ticker")
assert ticker["counts"]["lifecycle"] == 0 and "loops" not in ticker, ticker
assert answer({"action": "layer_facts", "target": "Game", "type": "ITicker"})["behaviour"]["counts"]["lifecycle"] == 0

# 10. Per-frame work outside Update.
poller = behaviour("Poller")
assert poller["recurring"] == [
    ["UniTask.WaitUntil", "Assets/Lab/Game/Poller.cs:13"],
    ["Tweens.Schedule", "Assets/Lab/Game/Poller.cs:14"],
    ["UniTask.Yield in a loop", "Assets/Lab/Game/Poller.cs:21"],
    ["yield return null in a loop", "Assets/Lab/Game/Poller.cs:27"]], poller
assert "recurring_note" in poller and "loops" not in poller, poller

# 11. Callbacks of a project base class pair with the type's own.
listener = behaviour("ScoreListener")
assert "unpaired" not in listener and listener["inherited_lifecycle"] == [["OnDisable", "Lab.Game.Listener", "Assets/Lab/Game/Listener.cs:6"]], listener
# 0.3.8: unpaired_subscriptions keeps only an unpaired OnEnable that subscribes.
reset = behaviour("ResetOnEnable")
assert reset["unpaired"] == ["OnEnable without OnDisable"] and "unpaired_subscriptions" not in reset, reset
leaky = behaviour("LeakyListener")
assert leaky["unpaired_subscriptions"] == [["OnEnable", "Assets/Lab/Game/EnableOnly.cs:23"]], leaky
assert "unpaired_subscriptions" not in behaviour("ToggleListener")

# 12 and 13. Unity's full metadataName links nodes; facts dump keeps the full type name.
candidates = {item["type"]: item for item in game["node_candidates"]}
assert candidates["Lab.Game.OldOnly"]["node"] == "node_old" and candidates["Lab.Game.OldOnly"]["node_title"] == "Old Only", candidates["Lab.Game.OldOnly"]
game_types = answer({"action": "layer_facts", "target": "Game", "limit": 500})["type_list"]
assert next(item for item in game_types if item["type"] == "Lab.Game.Ticker")["nodes"] == ["node_ticker"], game_types
dump = answer({"action": "layer_dump", "target": "Game"})
assert {item["node"]: item["type"] for item in dump["items"] if "node" in item}["node_ticker"] == "Lab.Game.Ticker", dump

# A. The audit lists each contradiction once, with evidence, and nothing else.
audit = answer({"action": "facts_audit", "min_uses": 2})
assert audit["by_kind"] == {
    "central_type_without_node": 5, "isolated_node": 1, "node_on_created_in_code_type": 1, "node_on_test_only_type": 1,
    "node_on_unwired_type": 1, "relationship_on_wrong_types": 1, "relationship_without_evidence": 1}, audit["by_kind"]
items = {(item["kind"], item["node"] or item["type"] or item["layer"]): item for item in audit["items"]}
assert items[("node_on_unwired_type", "node_unused")]["evidence"]["status"] == "unwired"
assert items[("node_on_test_only_type", "node_old")]["evidence"]["places"] == ["Assets/Lab/Prefabs/Hero OLD.prefab"]
assert "check" in items[("node_on_created_in_code_type", "node_spawned")]["evidence"]
central = {item["type"] for item in audit["items"] if item["kind"] == "central_type_without_node"}
assert central == {"Lab.Core.ModuleBase", "Lab.Core.ParameterBase", "Lab.Core.Parameter`1", "Lab.Core.Container", "Lab.Core.IInstaller"}, central
assert items[("relationship_without_evidence", "node_hud")]["evidence"]["relationship"] == "rel_hud_loader"
wrong = items[("relationship_on_wrong_types", "Lab/Game")]["evidence"]
assert [item["relationship"] for item in wrong["relationships"]] == ["rel_ticker_loader"] and wrong["references"] == 80, wrong
assert ("isolated_node", "node_note") in items and not any(key[1] == "node_mover" for key in items), items.keys()
assert answer({"action": "facts_audit", "min_uses": 20})["by_kind"]["central_type_without_node"] == 5
ui = answer({"action": "facts_audit", "target": "Ui"})
assert ui["layer"] == "Lab/Ui" and ui["by_kind"] == {"isolated_node": 1, "relationship_without_evidence": 1}, ui


def cli(*arguments):
    result = subprocess.run(server + [*arguments, "--project", project], capture_output=True, text=True, encoding="utf-8")
    return result.returncode, result.stdout.strip()


code, text = cli("facts", "audit", "--min-uses", "2")
assert code == 0 and text == query({"action": "facts_audit", "min_uses": 2})[1], text

# 15. A file holds the full answer, not the first page.
code, text = cli("facts", "layer", "Game", "--type", "Effect", "--output", "file")
written = json.load(open(json.loads(text)["file"], encoding="utf-8"))
assert len(written["wiring"]["base_of"]) == 12, written["wiring"]

# B. Snapshot, change the code, diff.
code, text = cli("facts", "snapshot", "--name", "before")
assert code == 0 and json.loads(text)["snapshot"] == "before", text
with open(os.path.join(project, "Assets", "Lab", "Game", "Health.cs"), "a", encoding="utf-8") as handle:
    handle.write("\nnamespace Lab.Game { public sealed class Mana : Lab.Core.Parameter<int> { } }\n")
os.remove(os.path.join(project, "Assets", "Lab", "Prefabs", "Hero OLD.prefab"))
code, text = cli("facts", "diff", "before")
diff = json.loads(text)
assert code == 0 and diff["by_kind"] == {"evidence_changed": 1, "type_added": 1, "wiring_changed": 1}, diff
changes = {change["kind"]: change for change in diff["changes"]}
assert changes["type_added"]["type"] == "Lab.Game.Mana" and changes["type_added"]["owner"] == "Lab/Game", changes
assert (changes["wiring_changed"]["type"], changes["wiring_changed"]["from"], changes["wiring_changed"]["to"]) == ("Lab.Game.OldOnly", "test_only", "unwired"), changes
assert changes["evidence_changed"]["new_type_pairs"] == ["Lab.Game.Mana -> Lab.Core.Parameter`1"], changes
code, text = cli("facts", "snapshot", "--name", "after")
code, text = cli("facts", "diff", "before", "after")
assert code == 0 and json.loads(text)["by_kind"] == diff["by_kind"], text
code, text = cli("facts", "diff", "missing")
assert code == 6 and json.loads(text)["snapshots"] == ["after", "before"], text

process.stdin.close()
assert process.wait(timeout=30) == 0
print("code facts lab test passed")
