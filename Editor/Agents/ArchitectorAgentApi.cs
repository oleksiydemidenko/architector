using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Architector.Editor.Agents
{
    public static class ArchitectorAgentApi
    {
        public static ArchitectorAgentResult Query(JObject parameters, string integrationId)
        {
            var session = ArchitectorSession.Instance;
            return Refusal(session.Repository, integrationId)
                   ?? ArchitectorAgentQuery.Handle(session.Repository, session.AiWork, parameters, session.TypeIndex);
        }

        public static ArchitectorAgentResult Edit(JObject parameters, string integrationId)
        {
            var session = ArchitectorSession.Instance;
            return Refusal(session.Repository, integrationId)
                   ?? ArchitectorAgentEdit.Handle(session.Repository, session.AiWork, parameters, session.TypeIndex);
        }

        // Null while the integration is on; otherwise the refusal names the integration and where to turn it on.
        public static ArchitectorAgentResult Refusal(ArchitectorRepository repository, string integrationId)
        {
            if (repository.IsAgentIntegrationEnabled(integrationId))
                return null;
            var name = ArchitectorAgentIntegrationRegistry.DisplayName(integrationId);
            return ArchitectorAgentResult.Fail("integration_disabled", new
            {
                integration = integrationId,
                message = $"The integration '{name}' is off for this project. Turn it on in Window > Architector > Settings > AI > Connections."
            });
        }

        public static string QueryJson(string parameters, string integrationId)
        {
            return Run(parameters, parsed => Query(parsed, integrationId));
        }

        public static string EditJson(string parameters, string integrationId)
        {
            return Run(parameters, parsed => Edit(parsed, integrationId));
        }

        private static string Run(string parameters, Func<JObject, ArchitectorAgentResult> action)
        {
            JObject parsed;
            try
            {
                parsed = JObject.Parse(parameters ?? string.Empty);
            }
            catch (JsonException exception)
            {
                return ArchitectorAgentResult.Fail("invalid_parameters", new { message = exception.Message }).ToJson();
            }
            return action(parsed).ToJson();
        }
    }
}
