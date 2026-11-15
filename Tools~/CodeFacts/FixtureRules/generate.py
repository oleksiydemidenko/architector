"""Writes Tools~/CodeFacts/FixtureRules: a minimal project for the startedBy absence rule with seeded errors and decoys
in the shapes Unity writes (transforms with parents, variants whose root is a prefab instance, nested instances with
stripped documents, overrides that add list entries and deactivate objects), and for slice (a state type held by an
entity prefab that names its base and authority prefabs in text fields).

Usage: python generate.py   (writes next to this file)
"""
import json
import os

root = os.path.dirname(os.path.abspath(__file__))


def write(relative, text):
    path = os.path.join(root, relative)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def guid(n):
    return f"{n:032x}"


ENGINE = """public class Engine : UnityEngine.MonoBehaviour
    {
        private Speed _speed;
        private Ticker _ticker;

        private void OnEnable() { _ticker.Subscribe(Tick, Phase.Post); }

        private void OnDisable()
        {
            _ticker.Unsubscribe(Tick, Phase.Post);
            _speed.Set(0f);
        }

        private void Tick() { Push(); }

        private void Push() { _speed.Set(1f); }
    }"""

scripts = {
    "Handler": ("public abstract class Handler : UnityEngine.MonoBehaviour { }", 0xA01),
    "Starter": ("public class Starter : UnityEngine.MonoBehaviour\n    {\n        [UnityEngine.SerializeField] private System.Collections.Generic.List<Handler> _handlers;\n    }", 0xA02),
    "PhaseHost": ("public class PhaseHost : Handler\n    {\n        [UnityEngine.SerializeReference] private System.Collections.Generic.List<IBehaviour> _behaviours;\n    }", 0xA03),
    "IBehaviour": ("public interface IBehaviour { }", 0xA04),
    "StartList": ("[System.Serializable]\n    public class StartList : IBehaviour\n    {\n        [UnityEngine.SerializeField] private System.Collections.Generic.List<Handler> _handlers;\n    }", 0xA05),
    "TickList": ("[System.Serializable]\n    public class TickList : IBehaviour\n    {\n        [UnityEngine.SerializeField] private System.Collections.Generic.List<Handler> _handlers;\n    }", 0xA06),
    "MoveHandler": ("public class MoveHandler : Handler { }", 0xB01),
    "JumpHandler": ("public class JumpHandler : Handler { }", 0xB02),
    "AimHandler": ("public class AimHandler : Handler { }", 0xB03),
    "FuseHandler": ("public class FuseHandler : Handler { }", 0xB04),
    "ShieldHandler": ("public class ShieldHandler : Handler { }", 0xB05),
    "LookHandler": ("public class LookHandler : Handler { }", 0xB06),
    "SoundEmitter": ("public class SoundEmitter : UnityEngine.MonoBehaviour { }", 0xB07),
    "Speed": ("[System.Serializable]\n    public class Speed\n    {\n        public float Value;\n\n        public void Set(float value) { Value = value; }\n    }", 0xD01),
    "Ticker": ("public enum Phase { Pre, Post }\n\n    public class Ticker\n    {\n        public void Subscribe(System.Action action, Phase phase) { }\n\n"
               "        public void Unsubscribe(System.Action action, Phase phase) { }\n    }", 0xD02),
    "Holder": ("public class Holder : UnityEngine.MonoBehaviour\n    {\n        [UnityEngine.SerializeReference] public Speed speed;\n        public string _baseId;\n"
               "        public string _authorityId;\n    }", 0xD03),
    "Engine": (ENGINE, 0xD04),
    "Gauge": ("public class Gauge : UnityEngine.MonoBehaviour\n    {\n        private Speed _speed;\n\n        private void Update() { Show(_speed.Value); }\n\n"
              "        private void Show(float value) { }\n    }", 0xD05),
}
for name, (body, number) in scripts.items():
    write(f"Assets/Rules/{name}.cs", f"namespace Rules\n{{\n    {body}\n}}\n")
    write(f"Assets/Rules/{name}.cs.meta", f"fileFormatVersion: 2\nguid: {guid(number)}\n")

HEADER = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"
PREFABS = {"Seeds": 0xC01, "Runtime": 0xC02, "Base": 0xC03, "VariantComplete": 0xC04, "VariantMissing": 0xC05, "Part": 0xC06,
           "Nested": 0xC07, "NestedMissing": 0xC08, "OneNest": 0xC09, "Empty": 0xC0A, "VariantHidden": 0xC0B, "VariantOfVariant": 0xC0C,
           "Car": 0xE01, "CarBase": 0xE02, "CarAuthority": 0xE03}


def game_object(file_id, name, components, active=1):
    items = "".join(f"  - component: {{fileID: {c}}}\n" for c in components)
    return (f"--- !u!1 &{file_id}\nGameObject:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {{fileID: 0}}\n  m_PrefabInstance: {{fileID: 0}}\n"
            f"  serializedVersion: 6\n  m_Component:\n{items}  m_Layer: 0\n  m_Name: {name}\n  m_IsActive: {active}\n")


def transform(file_id, owner, father=0):
    return (f"--- !u!4 &{file_id}\nTransform:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {{fileID: 0}}\n  m_PrefabInstance: {{fileID: 0}}\n"
            f"  m_GameObject: {{fileID: {owner}}}\n  serializedVersion: 2\n  m_Children: []\n  m_Father: {{fileID: {father}}}\n")


def obj(go, tr, name, components, father=0, active=1):
    """A GameObject with its Transform (fileID tr) as the first component."""
    return game_object(go, name, [tr] + components, active) + transform(tr, go, father)


def behaviour(file_id, owner, script, fields=""):
    return (f"--- !u!114 &{file_id}\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {{fileID: 0}}\n  m_PrefabInstance: {{fileID: 0}}\n"
            f"  m_GameObject: {{fileID: {owner}}}\n  m_Enabled: 1\n"
            f"  m_Script: {{fileID: 11500000, guid: {guid(scripts[script][1])}, type: 3}}\n  m_Name: \n{fields}")


def entries(ids, indent):
    return "".join(f"{indent}- {{fileID: {i}}}\n" for i in ids)


def starter(file_id, owner, ids):
    return behaviour(file_id, owner, "Starter", "  _handlers:" + (" []\n" if not ids else "\n" + entries(ids, "  ")))


def host(file_id, owner, start, tick):
    def block(rid, cls, ids):
        lists = " []\n" if not ids else "\n" + entries(ids, "        ")
        return f"    - rid: {rid}\n      type: {{class: {cls}, ns: Rules, asm: Assembly-CSharp}}\n      data:\n        _handlers:{lists}"
    return behaviour(file_id, owner, "PhaseHost",
                     "  _behaviours:\n  - rid: 1\n  - rid: 2\n  references:\n    version: 2\n    RefIds:\n" + block(1, "StartList", start) + block(2, "TickList", tick))


def instance(file_id, source, parent, modifications=()):
    text = (f"--- !u!1001 &{file_id}\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Modification:\n    serializedVersion: 3\n"
            f"    m_TransformParent: {{fileID: {parent}}}\n    m_Modifications:")
    if not modifications:
        text += " []\n"
    else:
        text += "\n"
        for target, path, value, reference in modifications:
            quoted = f"'{path}'" if "[" in path else path
            text += (f"    - target: {{fileID: {target}, guid: {guid(PREFABS[source])}, type: 3}}\n      propertyPath: {quoted}\n"
                     f"      value: {value}\n      objectReference: {{fileID: {reference}}}\n")
    text += ("    m_RemovedComponents: []\n    m_RemovedGameObjects: []\n    m_AddedGameObjects: []\n    m_AddedComponents: []\n"
             f"  m_SourcePrefab: {{fileID: 100100000, guid: {guid(PREFABS[source])}, type: 3}}\n")
    return text


def stripped(class_id, kind, file_id, source, source_id, owning_instance, extra=""):
    return (f"--- !u!{class_id} &{file_id} stripped\n{kind}:\n  m_CorrespondingSourceObject: {{fileID: {source_id}, guid: {guid(PREFABS[source])}, type: 3}}\n"
            f"  m_PrefabInstance: {{fileID: {owning_instance}}}\n  m_PrefabAsset: {{fileID: 0}}\n{extra}")


def stripped_behaviour(file_id, source, source_id, owning_instance, script):
    return stripped(114, "MonoBehaviour", file_id, source, source_id, owning_instance,
                    f"  m_GameObject: {{fileID: 0}}\n  m_Enabled: 1\n  m_Script: {{fileID: 11500000, guid: {guid(scripts[script][1])}, type: 3}}\n  m_Name: \n")


def prefab(name, text):
    write(f"Assets/Prefabs/{name}.prefab", HEADER + text)
    write(f"Assets/Prefabs/{name}.prefab.meta", f"fileFormatVersion: 2\nguid: {guid(PREFABS[name])}\n")


def root_with_lists(handlers, start, tick, name):
    return obj(1, 101, name, [10, 11]) + starter(10, 1, handlers) + host(11, 1, start, tick)


# Seeds: one starter with a correct handler, four real errors, a non-handler (D1), a handler on an inactive object
# (D5) and one under an inactive parent (D6).
seeds = root_with_lists([11, 20, 21, 22, 23, 24], start=[20, 22, 24], tick=[20, 22, 23], name="Seeds")
seeds += obj(2, 102, "Move", [20], 101) + behaviour(20, 2, "MoveHandler")           # correct
seeds += obj(3, 103, "Jump", [21], 101) + behaviour(21, 3, "JumpHandler")           # R2: missing from StartList and TickList
seeds += obj(4, 104, "Aim", [22], 101) + behaviour(22, 4, "AimHandler")             # correct
seeds += obj(5, 105, "Shield", [23], 101) + behaviour(23, 5, "ShieldHandler")       # R3: missing from StartList
seeds += obj(6, 106, "Look", [24], 101) + behaviour(24, 6, "LookHandler")           # R4: missing from TickList
seeds += obj(7, 107, "Fuse", [25], 101) + behaviour(25, 7, "FuseHandler")           # R1: in no list
seeds += obj(8, 108, "Sound", [26], 101) + behaviour(26, 8, "SoundEmitter")         # D1: not a handler
seeds += obj(9, 109, "Parked", [27], 101, active=0) + behaviour(27, 9, "MoveHandler")  # D5: inactive object
seeds += obj(12, 112, "Folder", [], 101, active=0)                                   # D6: inactive parent ...
seeds += obj(13, 113, "Deep", [28], 112) + behaviour(28, 13, "MoveHandler")         # ... of an active object
prefab("Seeds", seeds)

# Runtime (D2): lists empty in the asset; the rule names the file in filledAtRuntime.
prefab("Runtime", root_with_lists([], [], [], "Runtime") + obj(2, 102, "Move", [20], 101) + behaviour(20, 2, "MoveHandler"))
# Empty (R8): the same lists empty, but nothing says code fills them.
prefab("Empty", root_with_lists([], [], [], "Empty") + obj(2, 102, "Move", [20], 101) + behaviour(20, 2, "MoveHandler"))

# Base: a clean starter, the source of the variants.
prefab("Base", root_with_lists([11, 20], [20], [20], "Base") + obj(2, 102, "Move", [20], 101) + behaviour(20, 2, "MoveHandler"))


def variant(name, lists, under=951, hide_move=False):
    """A variant of Base whose root is a prefab instance; it adds a Fuse handler and the given list entries."""
    modifications = [(1, "m_Name", name, 0)] + [(target, path, "", 500) for target, path in lists]
    if hide_move:
        modifications.append((2, "m_IsActive", "0", 0))
    text = instance(900, "Base", 0, modifications)
    text += stripped(1, "GameObject", 950, "Base", 1, 900) + stripped(4, "Transform", 951, "Base", 101, 900) + stripped(4, "Transform", 952, "Base", 102, 900)
    text += obj(600, 601, "Fuse", [500], under) + behaviour(500, 600, "FuseHandler")
    prefab(name, text)


ALL = [(10, "_handlers.Array.data[2]"), (11, "managedReferences[1]._handlers.Array.data[1]"), (11, "managedReferences[2]._handlers.Array.data[1]")]
variant("VariantComplete", ALL)                                   # D3: every entry added by override
variant("VariantMissing", ALL[:2])                                # R5: the TickList entry is missing
variant("VariantHidden", [], under=952, hide_move=True)           # D8: under a base object the variant deactivates

# VariantOfVariant (D9): a variant of VariantComplete adds a Look handler to Base's lists. Objects that come from a
# prefab instance have no document of their own: Unity names them (instance fileID XOR source fileID).
deep = instance(920, "VariantComplete", 0, [(1 ^ 900, "m_Name", "VariantOfVariant", 0),
                                             (10 ^ 900, "_handlers.Array.data[3]", "", 700),
                                             (11 ^ 900, "managedReferences[1]._handlers.Array.data[2]", "", 700),
                                             (11 ^ 900, "managedReferences[2]._handlers.Array.data[2]", "", 700)])
deep += stripped(4, "Transform", 921, "VariantComplete", 101 ^ 900, 920)
deep += obj(701, 702, "Look", [700], 921) + behaviour(700, 701, "LookHandler")
prefab("VariantOfVariant", deep)

# Part: a plain prefab with a handler and no starter, nested by the next prefabs.
prefab("Part", obj(1, 101, "Part", [20]) + behaviour(20, 1, "JumpHandler"))
# Nested (D4): its lists hold the nested Part's handler through a stripped document.
nested = root_with_lists([11, 20, 700], [20, 700], [20, 700], "Nested") + obj(2, 102, "Move", [20], 101) + behaviour(20, 2, "MoveHandler")
nested += instance(800, "Part", 101) + stripped_behaviour(700, "Part", 20, 800, "JumpHandler")
prefab("Nested", nested)
# NestedMissing (R6): the nested Part's handler is in no list.
prefab("NestedMissing", root_with_lists([11, 20], [20], [20], "NestedMissing") + obj(2, 102, "Move", [20], 101) + behaviour(20, 2, "MoveHandler")
       + instance(800, "Part", 101))
# OneNest (R7): its own root and lists (it nests one Base instance, which makes it no variant); its handler misses
# TickList. Base's handlers are checked in Base, not against OneNest's lists.
prefab("OneNest", root_with_lists([11, 20], [20], [], "OneNest") + obj(2, 102, "Move", [20], 101) + behaviour(20, 2, "MoveHandler")
       + instance(800, "Base", 101))

# slice: Car holds Speed and names its base and authority prefabs in text fields; CarBase reads Speed, CarAuthority
# writes it from a tick callback.
car = obj(1, 101, "Car", [10]) + behaviour(10, 1, "Holder", "  speed:\n    rid: 5\n  _baseId: CarBase\n  _authorityId: CarAuthority\n"
                                         "  references:\n    version: 2\n    RefIds:\n    - rid: 5\n      type: {class: Speed, ns: Rules, asm: Assembly-CSharp}\n"
                                         "      data:\n        Value: 0\n")
prefab("Car", car)
prefab("CarBase", obj(1, 101, "CarBase", [10]) + behaviour(10, 1, "Gauge"))
prefab("CarAuthority", obj(1, 101, "CarAuthority", [10]) + behaviour(10, 1, "Engine"))

write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.3.0f1\n")
write("Architector/Layers/layer_rules.Rules.layer.json", json.dumps({
    "schemaVersion": 1, "layerId": "layer_rules", "name": "Rules", "purpose": "Absence rule and slice fixture.", "parentGroupId": "",
    "orderKey": "a", "scope": {"folders": ["Assets/Rules"], "namespaces": []}, "nodes": [], "edges": []}, indent=4) + "\n")
write("Architector/code-facts.json", json.dumps({"rules": {"startedBy": [{
    "name": "handlers are started", "component": "Handler", "in": "Starter",
    "lists": ["Starter._handlers", "PhaseHost/StartList._handlers", "PhaseHost/TickList._handlers"],
    "filledAtRuntime": ["Assets/Prefabs/Runtime.prefab"]}]}}, indent=4) + "\n")
print("written", root)
