﻿#light

namespace Vim.Modes.Insert
open Vim
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Editor
open System

type CommandFunction = unit -> ProcessResult

/// This is information describing a particular TextEdit which was done 
/// to the ITextBuffer.  
[<RequireQualifiedAccess>]
type TextEdit =

    /// A character was inserted into the ITextBuffer
    | InsertChar of char

    /// A character was replaced in the ITextBuffer.  The first char is the
    /// original and the second is the new value
    | ReplaceChar of char * char

    /// A newline was inserted into the ITextBuffer
    | NewLine

    /// An unknown edit operation occurred.  Happens when actions like a
    /// normal mode command is run.  This breaks the ability for certain
    /// operations like replace mode to do a back space properly
    | UnknownEdit 

/// Data relating to a particular Insert mode session
type InsertSessionData = {

    /// The transaction which is bracketing this Insert mode session
    Transaction : ILinkedUndoTransaction option

    /// If this Insert is a repeat operation this holds the count and 
    /// whether or not a newline should be inserted after the text
    RepeatData : (int * bool) option

    /// The set of edit's which have occurred in this session
    TextEditList : TextEdit list

} with

    member x.AddTextEdit edit = { x with TextEditList = edit::x.TextEditList }

type internal InsertMode
    ( 
        _buffer : IVimBuffer, 
        _operations : ICommonOperations,
        _broker : IDisplayWindowBroker, 
        _editorOptions : IEditorOptions,
        _undoRedoOperations : IUndoRedoOperations,
        _textChangeTracker : ITextChangeTracker,
        _insertUtil : IInsertUtil,
        _isReplace : bool,
        _keyboard : IKeyboardDevice,
        _mouse : IMouseDevice
    ) as this =

    let _bag = DisposableBag()
    let _textView = _buffer.TextView
    let _textBuffer = _buffer.TextBuffer
    let _editorOperations = _operations.EditorOperations
    let _commandRanEvent = Event<_>()
    let mutable _commandMap : Map<KeyInput, CommandFunction> = Map.empty
    let mutable _processDirectInsertCount = 0
    let _emptySessionData = {
        Transaction = None
        RepeatData = None
        TextEditList = List.empty
    }
    let mutable _sessionData = _emptySessionData

    /// The set of commands supported by insert mode.  The final bool is for whether
    /// or not the command should end the current text change before running
    static let commands : (string * InsertCommand * CommandFlags * bool) list =
        [
            ("<Left>", InsertCommand.MoveCaret Direction.Left, CommandFlags.Movement, false)
            ("<Down>", InsertCommand.MoveCaret Direction.Down, CommandFlags.Movement, false)
            ("<Right>", InsertCommand.MoveCaret Direction.Right, CommandFlags.Movement, false)
            ("<Up>", InsertCommand.MoveCaret Direction.Up, CommandFlags.Movement, false)
            ("<C-i>", InsertCommand.InsertTab, CommandFlags.Repeatable, false)
            ("<C-d>", InsertCommand.ShiftLineLeft, CommandFlags.Repeatable, true)
            ("<C-t>", InsertCommand.ShiftLineRight, CommandFlags.Repeatable, true)
        ]

    do
        let oldCommands : (string * CommandFunction) list = 
            [
                ("<Esc>", this.ProcessEscape)
                ("<Insert>", this.ProcessInsert)
                ("<C-o>", this.ProcessNormalModeOneCommand)
            ]

        let mappedCommands : (string * CommandFunction) list = 
            commands
            |> Seq.map (fun (name, command, commandFlags, completesChange) ->
                let func () = 
                    let keyInputSet = KeyNotationUtil.StringToKeyInputSet name
                    this.RunInsertCommand command keyInputSet commandFlags completesChange
                (name, func))
            |> List.ofSeq

        let both = Seq.append oldCommands mappedCommands
        _commandMap <-
            oldCommands
            |> Seq.append mappedCommands
            |> Seq.map (fun (str, func) -> (KeyNotationUtil.StringToKeyInput str), func)
            |> Map.ofSeq

        // Caret changes can end a text change operation.
        _textView.Caret.PositionChanged
        |> Observable.subscribe (fun _ -> this.OnCaretPositionChanged() )
        |> _bag.Add

    member x.CaretPoint = TextViewUtil.GetCaretPoint _textView

    member x.CaretLine = TextViewUtil.GetCaretLine _textView

    member x.CurrentSnapshot = _textView.TextSnapshot

    member x.IsProcessingDirectInsert = _processDirectInsertCount > 0

    member x.ModeKind = if _isReplace then ModeKind.Replace else ModeKind.Insert

    /// Is this the currently active mode?
    member x.IsActive = x.ModeKind = _buffer.ModeKind

    /// Is this KeyInput a raw text insert into the ITextBuffer.  Anything that would be 
    /// processed by adding characters to the ITextBuffer.  This is anything which has an
    /// associated character that is not an insert mode command
    member x.IsDirectInsert (keyInput : KeyInput) = 
        match Map.tryFind keyInput _commandMap with
        | Some _ ->
            // Known commands are not direct text insert
            false
        | None ->
            // Not a command so check for known direct text inserts
            match keyInput.Key with
            | VimKey.Enter -> true
            | VimKey.Back -> true
            | VimKey.Delete -> true
            | _ -> Option.isSome keyInput.RawChar

    /// Process the direct text insert command
    member x.ProcessDirectInsert (ki : KeyInput) = 

        // Actually process the edit
        let processReplaceEdit () =
            let sessionData = _sessionData
            match ki.Key with
            | VimKey.Enter -> 
                let sessionData = sessionData.AddTextEdit TextEdit.NewLine
                _editorOperations.InsertNewLine(), sessionData
            | VimKey.Back ->
                // In replace we only support a backspace if the TextEdit stack is not
                // empty and points to something we can handle 
                match sessionData.TextEditList with 
                | [] ->
                    // Even though we take no action here we handled the KeyInput
                    true, sessionData
                | h::t ->
                    match h with 
                    | TextEdit.InsertChar _ -> 
                        let sessionData = { sessionData with TextEditList = t }
                        _editorOperations.Delete(), sessionData
                    | TextEdit.ReplaceChar (oldChar, newChar) ->
                        let point = 
                            SnapshotPointUtil.TryGetPreviousPointOnLine x.CaretPoint 1 
                            |> OptionUtil.getOrDefault x.CaretPoint
                        let span = Span(point.Position, 1)
                        let sessionData = { sessionData with TextEditList = t }
                        let result = _editorOperations.ReplaceText(span, (oldChar.ToString()))

                        // If the replace succeeded we need to position the caret back at the 
                        // start of the replace
                        if result then
                            TextViewUtil.MoveCaretToPosition _textView point.Position

                        result, sessionData
                    | TextEdit.NewLine -> 
                        true, sessionData
                    | TextEdit.UnknownEdit ->
                        true, sessionData
            | VimKey.Delete ->
                // Strangely a delete in replace actually does a delete but doesn't affect 
                // the edit stack
                _editorOperations.Delete(), sessionData
            | _ ->
                let text = ki.Char.ToString()
                let caretPoint = TextViewUtil.GetCaretPoint _textView
                let edit = 
                    if SnapshotPointUtil.IsInsideLineBreak caretPoint then
                        TextEdit.InsertChar ki.Char
                    else
                        TextEdit.ReplaceChar ((caretPoint.GetChar()), ki.Char)
                let sessionData = sessionData.AddTextEdit edit
                _editorOperations.InsertText(text), sessionData

        let processInsertEdit () =
            match ki.Key with
            | VimKey.Enter -> 
                _editorOperations.InsertNewLine()
            | VimKey.Back -> 
                _editorOperations.Backspace()
            | VimKey.Delete -> 
                _editorOperations.Delete()
            | _ ->
                let text = ki.Char.ToString()
                _editorOperations.InsertText(text)

        _processDirectInsertCount <- _processDirectInsertCount + 1
        try
            let value, sessionData =
                if _isReplace then 
                    processReplaceEdit()
                else
                    processInsertEdit(), _sessionData

            // If the edit succeeded then record the new InsertSessionData
            if value then
                _sessionData <- sessionData

            if value then
                ProcessResult.Handled ModeSwitch.NoSwitch
            else
                ProcessResult.NotHandled
         finally 
            _processDirectInsertCount <- _processDirectInsertCount - 1

    /// Process the <Insert> command.  This toggles between insert an replace mode
    member x.ProcessInsert () = 

        let mode = if _isReplace then ModeKind.Insert else ModeKind.Replace
        ProcessResult.Handled (ModeSwitch.SwitchMode mode)

    /// Enter normal mode for a single command.  
    member x.ProcessNormalModeOneCommand () =

        // If we're in replace mode then this will be a blocking edit.  Record it
        if _isReplace then
            _sessionData <- _sessionData.AddTextEdit TextEdit.UnknownEdit

        let switch = ModeSwitch.SwitchModeWithArgument (ModeKind.Normal, ModeArgument.OneTimeCommand x.ModeKind)
        ProcessResult.Handled switch

    /// Apply the repeated edits the the ITextBuffer
    member x.MaybeApplyRepeatedEdits () = 

        try
            match _sessionData.RepeatData, _textChangeTracker.CurrentChange with
            | None, None -> ()
            | None, Some _ -> ()
            | Some _, None -> ()
            | Some (count, addNewLines), Some change -> _operations.ApplyTextChange change addNewLines (count - 1)
        finally
            // Make sure to close out the transaction
            match _sessionData.Transaction with
            | None -> 
                ()
            | Some transaction -> 
                transaction.Complete()
                _sessionData <- { _sessionData with Transaction = None }

    member x.ProcessEscape () =

        let moveCaretLeft () = 
            match SnapshotPointUtil.TryGetPreviousPointOnLine x.CaretPoint 1 with
            | None -> ()
            | Some point -> _operations.MoveCaretToPointAndEnsureVisible point

        this.MaybeApplyRepeatedEdits()

        if _broker.IsCompletionActive || _broker.IsSignatureHelpActive || _broker.IsQuickInfoActive then
            _broker.DismissDisplayWindows()
            moveCaretLeft()
            ProcessResult.OfModeKind ModeKind.Normal

        else
            // Need to adjust the caret on exit.  Typically it's just a move left by 1 but if we're
            // in virtual space we just need to get out of it.
            let virtualPoint = TextViewUtil.GetCaretVirtualPoint _textView
            if virtualPoint.IsInVirtualSpace then 
                _operations.MoveCaretToPoint virtualPoint.Position
            else
                moveCaretLeft()
            ProcessResult.OfModeKind ModeKind.Normal

    /// Can Insert mode handle this particular KeyInput value 
    member x.CanProcess ki = 
        if Map.containsKey ki _commandMap then
            true
        else
            x.IsDirectInsert ki

    /// Run the insert command with the given information
    member x.RunInsertCommand command keyInputSet commandFlags completesChange = 

        // Certain commands don't have an relation to the inserted text and effectively 
        // complete the existing text change.  
        if completesChange then
            _textChangeTracker.CompleteChange()

        let result = _insertUtil.RunInsertCommand command
        let data = {
            CommandBinding = CommandBinding.InsertBinding (keyInputSet, commandFlags, command)
            Command = Command.InsertCommand command
            CommandResult = result }
        _commandRanEvent.Trigger data

        if completesChange then
            _textChangeTracker.ClearChange()

        ProcessResult.OfCommandResult result

    /// Try and process the KeyInput by considering the current text edit in Insert Mode
    member x.ProcessWithCurrentChange keyInput = 

        // Actually try and process this with the current change 
        let func (text : string) = 
            let data = 
                if text.EndsWith("0") && keyInput = KeyInputUtil.CharWithControlToKeyInput 'd' then
                    let keyInputSet = KeyNotationUtil.StringToKeyInputSet "0<C-d>"
                    Some (InsertCommand.DeleteAllIndent, keyInputSet, "0")
                else
                    None

            match data with
            | None ->
                None
            | Some (command, keyInputSet, text) ->

                // First step is to delete the portion of the current change which matches up with
                // our command.
                if x.CaretPoint.Position >= text.Length then
                    let span = 
                        let startPoint = SnapshotPoint(x.CurrentSnapshot, x.CaretPoint.Position - text.Length)
                        SnapshotSpan(startPoint, text.Length)
                    _textBuffer.Delete(span.Span) |> ignore

                // Now run the command
                x.RunInsertCommand command keyInputSet CommandFlags.Repeatable true |> Some

        match _textChangeTracker.CurrentChange with
        | None ->
            None
        | Some textChange ->
            match textChange.LastChange with
            | TextChange.Insert text -> func text
            | TextChange.Delete _ -> None
            | TextChange.Combination _ -> None

    /// Process the KeyInput value
    member x.Process keyInput = 

        // First try and process by examining the current change
        match x.ProcessWithCurrentChange keyInput with
        | Some result ->
            result
        | None ->
            match Map.tryFind keyInput _commandMap with
            | Some func -> 
                func()
            | None -> 
                if x.IsDirectInsert keyInput then 
                    x.ProcessDirectInsert keyInput
                else
                    ProcessResult.NotHandled

    /// This is raised when caret changes.  If this is the result of a user click then 
    /// we need to complete the change.
    ///
    /// Need to be careful to not end the edit due to the caret moving as a result of 
    /// normal typing
    ///
    /// TODO: We really need to reconsider how this is used.  If the user has mapped say 
    /// '1' to 'Left' then we will misfire here.  Not a huge concern I think but we need
    /// to find a crisper solution here.
    member x.OnCaretPositionChanged () = 
        if _mouse.IsLeftButtonPressed then 
            _textChangeTracker.CompleteChange()
        elif _buffer.ModeKind = ModeKind.Insert then 
            let keyMove = 
                [ VimKey.Left; VimKey.Right; VimKey.Up; VimKey.Down ]
                |> Seq.map (fun k -> KeyInputUtil.VimKeyToKeyInput k)
                |> Seq.filter (fun k -> _keyboard.IsKeyDown k.Key)
                |> SeqUtil.isNotEmpty
            if keyMove then 
                _textChangeTracker.CompleteChange()

    /// Called when the IVimBuffer is closed.  We need to unsubscribe from several events
    /// when this happens to prevent the ITextBuffer / ITextView from being kept alive
    member x.OnClose () =
        _bag.DisposeAll()

    /// Entering an insert or replace mode.  Setup the InsertSessionData based on the 
    /// ModeArgument value. 
    member x.OnEnter arg =

        // When starting insert mode we want to track the edits to the IVimBuffer as a 
        // text change
        _textChangeTracker.Enabled <- true

        // On enter we need to check the 'count' and possibly set up a transaction to 
        // lump edits and their repeats together
        let transaction, repeatData =
            match arg with
            | ModeArgument.InsertWithCount count ->
                if count > 1 then
                    let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                    Some transaction, Some (count, false)
                else
                    None, None
            | ModeArgument.InsertWithCountAndNewLine count ->
                if count > 1 then
                    let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                    Some transaction, Some (count, true)
                else
                    None, None
            | ModeArgument.InsertWithTransaction transaction ->
                Some transaction, None
            | _ -> 
                if _isReplace then
                    // Replace mode occurs under a transaction even if we are not repeating
                    let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                    Some transaction, None
                else
                    None, None

        _sessionData <- {
            Transaction = transaction
            RepeatData = repeatData
            TextEditList = List.empty
        }

        // If this is replace mode then go ahead and setup overwrite
        if _isReplace then
            _editorOptions.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, true)

    /// Called when leaving insert mode.  Here we will do any remaining cleanup on the
    /// InsertSessionData.  It's possible to get here with active session data if there
    /// is an exception during the processing of the transaction.
    ///
    /// Or more sinister.  A simple API call to OnLeave could force us to leave while 
    /// a transaction was open
    member x.OnLeave () =

        // When leaving insert mode we complete the current change
        _textChangeTracker.CompleteChange()
        _textChangeTracker.Enabled <- false

        try
            match _sessionData.Transaction with
            | None -> ()
            | Some transaction -> transaction.Complete()
        finally
            _sessionData <- _emptySessionData

        // If this is replace mode then go ahead and undo overwrite
        if _isReplace then
            _editorOptions.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, false)

    interface IInsertMode with 
        member x.VimBuffer = _buffer
        member x.CommandNames =  _commandMap |> Seq.map (fun p -> p.Key) |> Seq.map OneKeyInput
        member x.ModeKind = x.ModeKind
        member x.IsProcessingDirectInsert = x.IsProcessingDirectInsert
        member x.CanProcess ki = x.CanProcess ki
        member x.IsDirectInsert ki = x.IsDirectInsert ki
        member x.Process ki = x.Process ki
        member x.OnEnter arg = x.OnEnter arg
        member x.OnLeave () = x.OnLeave ()
        member x.OnClose() = x.OnClose ()

        [<CLIEvent>]
        member x.CommandRan = _commandRanEvent.Publish

