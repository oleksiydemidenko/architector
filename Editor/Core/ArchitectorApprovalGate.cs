using System;

namespace Architector.Editor
{
    public static class ArchitectorApprovalGate
    {
        public static Func<string, bool> Confirm { get; set; } = AskUser;

#if UNITY_EDITOR
        // In the Editor a person answers the dialog; batch mode refuses it as user_refused.
        public static bool NobodyToAsk => false;
#else
        // The command line has no person to ask, unless a test replaced the question.
        public static bool NobodyToAsk => Confirm == (Func<string, bool>)AskUser;
#endif

        private static bool AskUser(string question)
        {
            return ArchitectorHost.Current.CanAskPerson
                   && ArchitectorHost.Current.AskPerson("Agent Request", question, "Allow", "Refuse");
        }
    }
}
