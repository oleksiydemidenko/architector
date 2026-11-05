using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Architector.Editor
{
    public static class ArchitectorShortcuts
    {
        public const string FocusView = "Architector/Canvas/Focus View";
        public const string FocusLayer = "Architector/Canvas/Focus Whole Layer";
        public const string Arrange = "Architector/Canvas/Arrange";
        public const string ArrangeAsCenters = "Architector/Canvas/Arrange As Centers";
        public const string SelectAll = "Architector/Canvas/Select All";
        public const string ClearSelection = "Architector.ClearSelection";
        public const string ActualSize = "Architector/Canvas/Zoom 100%";
        public const string NewNode = "Architector/Canvas/New Empty Node";
        public const string Rename = "Architector/Canvas/Rename Node";
        public const string LinkType = "Architector/Canvas/Link Type";
        public const string OpenPortal = "Architector.OpenPortal";
        public const string NeighbourLeft = "Architector/Canvas/Connected Node Left";
        public const string NeighbourRight = "Architector/Canvas/Connected Node Right";
        public const string NeighbourUp = "Architector/Canvas/Connected Node Up";
        public const string NeighbourDown = "Architector/Canvas/Connected Node Down";
        public const string PreviousLayer = "Architector/Window/Previous Layer";
        public const string NextLayer = "Architector/Window/Next Layer";
        public const string ReviewSelectAll = "Architector/Review/Select All";

        private static void Send(ShortcutArguments arguments, string command)
        {
            if (arguments.context is ArchitectorMasterWindow { CanvasHasPointer: true } window
                && !EditorGUIUtility.editingTextField)
                window.SendCanvasCommand(command);
        }

        private static void Step(ShortcutArguments arguments, string command, int direction)
        {
            if (arguments.context is not ArchitectorMasterWindow window || EditorGUIUtility.editingTextField)
                return;
            if (window.ExplorerHasKeyboardFocus)
                window.StepExplorerLayer(direction);
            else if (window.CanvasHasPointer)
                window.SendCanvasCommand(command);
        }

        private static void Navigate(ShortcutArguments arguments, int direction)
        {
            if (arguments.context is ArchitectorMasterWindow window && !EditorGUIUtility.editingTextField)
                window.NavigateHistory(direction);
        }

        [Shortcut(FocusView, typeof(ArchitectorMasterWindow), KeyCode.F)]
        private static void OnFocusView(ShortcutArguments arguments) => Send(arguments, FocusView);

        [Shortcut(FocusLayer, typeof(ArchitectorMasterWindow), KeyCode.F, ShortcutModifiers.Shift)]
        private static void OnFocusLayer(ShortcutArguments arguments) => Send(arguments, FocusLayer);

        [Shortcut(Arrange, typeof(ArchitectorMasterWindow), KeyCode.A)]
        private static void OnArrange(ShortcutArguments arguments) => Send(arguments, Arrange);

        [Shortcut(ArrangeAsCenters, typeof(ArchitectorMasterWindow), KeyCode.A, ShortcutModifiers.Shift)]
        private static void OnArrangeAsCenters(ShortcutArguments arguments) => Send(arguments, ArrangeAsCenters);

        [Shortcut(SelectAll, typeof(ArchitectorMasterWindow), KeyCode.A, ShortcutModifiers.Action)]
        private static void OnSelectAll(ShortcutArguments arguments) => Send(arguments, SelectAll);

        [Shortcut(ActualSize, typeof(ArchitectorMasterWindow), KeyCode.Alpha1)]
        private static void OnActualSize(ShortcutArguments arguments) => Send(arguments, ActualSize);

        [Shortcut(NewNode, typeof(ArchitectorMasterWindow), KeyCode.N)]
        private static void OnNewNode(ShortcutArguments arguments) => Send(arguments, NewNode);

        [Shortcut(Rename, typeof(ArchitectorMasterWindow), KeyCode.R)]
        private static void OnRename(ShortcutArguments arguments) => Send(arguments, Rename);

        [Shortcut(LinkType, typeof(ArchitectorMasterWindow), KeyCode.L)]
        private static void OnLinkType(ShortcutArguments arguments) => Send(arguments, LinkType);

        [Shortcut(NeighbourLeft, typeof(ArchitectorMasterWindow), KeyCode.LeftArrow)]
        private static void OnNeighbourLeft(ShortcutArguments arguments) => Send(arguments, NeighbourLeft);

        [Shortcut(NeighbourRight, typeof(ArchitectorMasterWindow), KeyCode.RightArrow)]
        private static void OnNeighbourRight(ShortcutArguments arguments) => Send(arguments, NeighbourRight);

        [Shortcut(NeighbourUp, typeof(ArchitectorMasterWindow), KeyCode.UpArrow)]
        private static void OnNeighbourUp(ShortcutArguments arguments) => Step(arguments, NeighbourUp, -1);

        [Shortcut(NeighbourDown, typeof(ArchitectorMasterWindow), KeyCode.DownArrow)]
        private static void OnNeighbourDown(ShortcutArguments arguments) => Step(arguments, NeighbourDown, 1);

        [Shortcut(PreviousLayer, typeof(ArchitectorMasterWindow), KeyCode.LeftArrow, ShortcutModifiers.Alt)]
        private static void OnPreviousLayer(ShortcutArguments arguments) => Navigate(arguments, -1);

        [Shortcut(NextLayer, typeof(ArchitectorMasterWindow), KeyCode.RightArrow, ShortcutModifiers.Alt)]
        private static void OnNextLayer(ShortcutArguments arguments) => Navigate(arguments, 1);

        [Shortcut(ReviewSelectAll, typeof(ArchitectorReviewWindow), KeyCode.A, ShortcutModifiers.Action)]
        private static void OnReviewSelectAll(ShortcutArguments arguments) => (arguments.context as ArchitectorReviewWindow)?.SelectAll();
    }
}
