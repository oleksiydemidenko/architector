using System;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public static class ArchitectorApprovalGate
    {
        public static Func<string, bool> Confirm { get; set; } = AskUser;

        private static bool AskUser(string question)
        {
            return !Application.isBatchMode
                   && EditorUtility.DisplayDialog("Agent Request", question, "Allow", "Refuse");
        }
    }
}
