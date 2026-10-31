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
            return session.Repository.IsAgentIntegrationEnabled(integrationId)
                ? ArchitectorAgentQuery.Handle(session.Repository, session.AiWork, parameters, session.TypeIndex)
                : ArchitectorAgentResult.Fail("integration_disabled");
        }

        public static ArchitectorAgentResult Edit(JObject parameters, string integrationId)
        {
            var session = ArchitectorSession.Instance;
            return session.Repository.IsAgentIntegrationEnabled(integrationId)
                ? ArchitectorAgentEdit.Handle(session.Repository, session.AiWork, parameters, session.TypeIndex)
                : ArchitectorAgentResult.Fail("integration_disabled");
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
