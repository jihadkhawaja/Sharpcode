using Avalonia.Controls;
using Avalonia.Input;

namespace Sharpcode;

public partial class MainWindow
{
    private void InitializeEditorContextMenu()
    {
        var undo = EditorMenuItem("Undo", Immediate(() => Editor.Undo()), new(Key.Z, Primary));
        var redo = EditorMenuItem("Redo", Immediate(() => Editor.Redo()));
        var cut = EditorMenuItem("Cut", Immediate(Editor.Cut), new(Key.X, Primary));
        var copy = EditorMenuItem("Copy", Immediate(Editor.Copy), new(Key.C, Primary));
        var paste = EditorMenuItem("Paste", Immediate(Editor.Paste), new(Key.V, Primary));
        var selectAll = EditorMenuItem("Select All", Immediate(Editor.SelectAll), new(Key.A, Primary));
        var find = EditorMenuItem("Find in File", Immediate(ShowFind), new(Key.F, Primary));
        var replace = EditorMenuItem("Replace in File", Immediate(ShowReplace));
        var comment = EditorMenuItem("Toggle Line Comment", Immediate(ToggleComment));
        var goToLine = EditorMenuItem("Go to Line…", GoToLineAsync, new(Key.G, KeyModifiers.Control));
        var ask = EditorMenuItem("Ask Agent About This File", Immediate(AskAboutEditor));
        var suggest = EditorMenuItem("Suggest Code at Cursor", SuggestAsync);

        var menu = new ContextMenu
        {
            Items =
            {
                undo, redo, new Separator(),
                cut, copy, paste, selectAll, new Separator(),
                find, replace, comment, goToLine, new Separator(),
                ask, suggest
            }
        };
        menu.Opening += (_, args) =>
        {
            if (activeTab is null)
            {
                args.Cancel = true;
                return;
            }

            var editable = !Editor.IsReadOnly;
            undo.IsEnabled = editable && Editor.CanUndo;
            redo.IsEnabled = editable && Editor.CanRedo;
            cut.IsEnabled = editable && Editor.CanCut;
            copy.IsEnabled = Editor.CanCopy;
            paste.IsEnabled = editable && Editor.CanPaste;
            selectAll.IsEnabled = Editor.Document.TextLength > 0;
            replace.IsEnabled = editable;
            comment.IsEnabled = editable && LineCommentPrefix(activeTab.Path) is not null;
            ask.Header = Editor.SelectionLength > 0 ? "Ask Agent About This Selection" : "Ask Agent About This File";
            ask.IsEnabled = CanAttachActiveEditor;
            suggest.IsEnabled = editable && CanAttachActiveEditor && agentCancellation is null && !agentApplying && !switchingWorkspace;
        };
        Editor.TextArea.RightClickMovesCaret = true;
        Editor.TextArea.ContextMenu = menu;
    }

    private MenuItem EditorMenuItem(string label, Func<Task> action, KeyGesture? shortcut = null)
    {
        var item = new MenuItem { Header = label, InputGesture = shortcut };
        item.Click += async (_, _) =>
        {
            Editor.TextArea.Focus();
            await TryAsync(action);
        };
        return item;
    }

    private void AskAboutEditor()
    {
        if (!CanAttachActiveEditor)
            return;
        FillAgentPrompt(Editor.SelectionLength > 0 ? "Explain the selected code." : "Explain the attached code.", attachEditor: true);
        AttachEditor.IsChecked = true;
    }
}
