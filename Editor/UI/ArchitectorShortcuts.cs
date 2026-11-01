using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorCanvasShortcutContext : IShortcutContext
    {
        public bool active => !EditorGUIUtility.editingTextField
                              && EditorWindow.focusedWindow is ArchitectorMasterWindow { CanvasHasPointer: true };
    }

    public sealed class ArchitectorWindowShortcutContext : IShortcutContext
    {
        public bool active => !EditorGUIUtility.editingTextField
                              && EditorWindow.focusedWindow is ArchitectorMasterWindow;
    }

    [InitializeOnLoad]
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

        static ArchitectorShortcuts()
        {
            ShortcutManager.RegisterContext(new ArchitectorCanvasShortcutContext());
            ShortcutManager.RegisterContext(new ArchitectorWindowShortcutContext());
        }

        private static void Send(string command)
        {
            if (EditorWindow.focusedWindow is ArchitectorMasterWindow window)
                window.SendCanvasCommand(command);
        }

        [Shortcut(FocusView, typeof(ArchitectorCanvasShortcutContext), KeyCode.F)]
        private static void OnFocusView() => Send(FocusView);

        [Shortcut(FocusLayer, typeof(ArchitectorCanvasShortcutContext), KeyCode.F, ShortcutModifiers.Shift)]
        private static void OnFocusLayer() => Send(FocusLayer);

        [Shortcut(Arrange, typeof(ArchitectorCanvasShortcutContext), KeyCode.A)]
        private static void OnArrange() => Send(Arrange);

        [Shortcut(ArrangeAsCenters, typeof(ArchitectorCanvasShortcutContext), KeyCode.A, ShortcutModifiers.Shift)]
        private static void OnArrangeAsCenters() => Send(ArrangeAsCenters);

        [Shortcut(SelectAll, typeof(ArchitectorCanvasShortcutContext), KeyCode.A, ShortcutModifiers.Action)]
        private static void OnSelectAll() => Send(SelectAll);

        [Shortcut(ActualSize, typeof(ArchitectorCanvasShortcutContext), KeyCode.Alpha1)]
        private static void OnActualSize() => Send(ActualSize);

        [Shortcut(NewNode, typeof(ArchitectorCanvasShortcutContext), KeyCode.N)]
        private static void OnNewNode() => Send(NewNode);

        [Shortcut(Rename, typeof(ArchitectorCanvasShortcutContext), KeyCode.R)]
        private static void OnRename() => Send(Rename);

        [Shortcut(LinkType, typeof(ArchitectorCanvasShortcutContext), KeyCode.L)]
        private static void OnLinkType() => Send(LinkType);

        [Shortcut(NeighbourLeft, typeof(ArchitectorCanvasShortcutContext), KeyCode.LeftArrow)]
        private static void OnNeighbourLeft() => Send(NeighbourLeft);

        [Shortcut(NeighbourRight, typeof(ArchitectorCanvasShortcutContext), KeyCode.RightArrow)]
        private static void OnNeighbourRight() => Send(NeighbourRight);

        [Shortcut(NeighbourUp, typeof(ArchitectorCanvasShortcutContext), KeyCode.UpArrow)]
        private static void OnNeighbourUp() => Send(NeighbourUp);

        [Shortcut(NeighbourDown, typeof(ArchitectorCanvasShortcutContext), KeyCode.DownArrow)]
        private static void OnNeighbourDown() => Send(NeighbourDown);

        [Shortcut(PreviousLayer, typeof(ArchitectorWindowShortcutContext), KeyCode.LeftArrow, ShortcutModifiers.Alt)]
        private static void OnPreviousLayer() => (EditorWindow.focusedWindow as ArchitectorMasterWindow)?.NavigateHistory(-1);

        [Shortcut(NextLayer, typeof(ArchitectorWindowShortcutContext), KeyCode.RightArrow, ShortcutModifiers.Alt)]
        private static void OnNextLayer() => (EditorWindow.focusedWindow as ArchitectorMasterWindow)?.NavigateHistory(1);

        [Shortcut(ReviewSelectAll, typeof(ArchitectorReviewWindow), KeyCode.A, ShortcutModifiers.Action)]
        private static void OnReviewSelectAll(ShortcutArguments arguments) => (arguments.context as ArchitectorReviewWindow)?.SelectAll();
    }
}
