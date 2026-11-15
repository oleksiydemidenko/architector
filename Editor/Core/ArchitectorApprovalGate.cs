using System;

namespace Architector.Editor
{
    public static class ArchitectorApprovalGate
    {
        public static Func<string, bool> Confirm { get; set; } = AskUser;

        private static bool AskUser(string question)
        {
            // Batch mode and the command line have nobody to ask: the request is refused.
            return ArchitectorHost.Current.CanAskPerson
                   && ArchitectorHost.Current.AskPerson("Agent Request", question, "Allow", "Refuse");
        }
    }
}
