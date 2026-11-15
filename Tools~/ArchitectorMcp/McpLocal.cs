using System.Text.Json.Nodes;
using Architector.Editor;
using Architector.Editor.Agents;
using Newtonsoft.Json.Linq;

// query and edit without the Unity Editor: the Architector core (Tools~/ArchitectorCore, the package's own Editor/ code
// compiled without Unity) answers on the Architector files of the project, with the code facts from Roslyn. The rules
// are the Editor's: the revision check, validation, group locks, the agent work token. An edit holds
// Library/Architector/write.lock from reading the files to the last write, so there is one writer at a time with the
// Editor and other command lines; the open Editor reloads the change within a second. Approvals need a person.
// Data built here is JObject, not anonymous types: the published binary is trimmed, and only the core keeps its types whole.
public static partial class McpServer
{
    // How long a busy Editor may take to start a call before the call goes to the files. A call the Editor did not
    // start in that time is cancelled there and never runs. An Editor up to 0.3.13 ignores it and keeps its 120 s.
    private const int ClaimSeconds = 5;

    private static bool localReady;

    private static void PrepareLocal()
    {
        if (localReady)
            return;
        localReady = true;
        ArchitectorHost.Current = new ArchitectorProcessHost(root!);
        ArchitectorAgentModeMirror.Adopt(root!);
        ArchitectorTypeIndex.CreateFacts = _ => new McpTypeFacts(root!);
        ArchitectorAgentEdit.PlanTokenFolder = Path.Combine(root!, "Library", "Architector", "plan-tokens");
    }

    // The reply of the Architector core for this project, as JSON with source "files".
    private static JsonNode LocalAnswer(string tool, JsonObject arguments, string integrationId, string why)
    {
        PrepareLocal();
        var paths = new ArchitectorPaths(root!);
        var parameters = JObject.Parse(arguments.ToJsonString());
        JsonNode Reply(ArchitectorAgentResult answer)
        {
            var node = JsonNode.Parse(tool == "edit" ? WithCodeReferences(answer.ToJson()) ?? answer.ToJson() : answer.ToJson())!.AsObject();
            node["source"] = "files";
            node["source_reason"] = why;
            return node;
        }

        // The saved switch counts as in the Editor; the command line never creates the Architector data.
        if (!IntegrationOn(integrationId))
            return Reply(ArchitectorAgentResult.Fail("integration_disabled", new JObject
            {
                ["integration"] = integrationId,
                ["message"] = $"The integration '{ArchitectorAgentIntegrationRegistry.DisplayName(integrationId)}' is off for this project. Turn it on in Window > Architector > Settings > AI > Connections."
            }));
        if (!File.Exists(paths.ProjectPath))
            return Reply(ArchitectorAgentResult.Fail("no_data", new JObject { ["message"] = NoData }));
        ArchitectorAgentResult Run()
        {
            var repository = new ArchitectorRepository(paths, newSession: false);
            var aiWork = new ArchitectorAiWorkCoordinator(true, recoveryPath: paths.AiWorkRecoveryPath, locksWindows: () => true);
            using var typeIndex = new ArchitectorTypeIndex(repository);
            return tool == "edit"
                ? ArchitectorAgentEdit.Handle(repository, aiWork, parameters, typeIndex)
                : ArchitectorAgentQuery.Handle(repository, aiWork, parameters, typeIndex);
        }

        ArchitectorAgentResult result;
        try
        {
            result = tool == "edit" ? ArchitectorWriteLock.Run(paths, Run) : Run();
        }
        catch (ArchitectorWriteBusyException exception)
        {
            result = ArchitectorAgentResult.Fail("busy", new JObject { ["message"] = exception.Message });
        }
        catch (ArchitectorDataException exception)
        {
            result = ArchitectorAgentResult.Fail("invalid_data", new JObject { ["message"] = exception.Message });
        }
        return Reply(result);
    }
}
