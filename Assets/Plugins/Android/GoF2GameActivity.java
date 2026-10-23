// GoF2GameActivity.java
// Remake: the game's Android activity, Unity's UnityPlayerGameActivity with one change to its game view. GameActivity's
// text input (games-activity 4.4.0, GameTextInput's InputConnection.onKey) handles a key event the on-screen keyboard
// sends to the view by editing its text and then calling InputMethodManager.restartInput, and a restart ends the
// keyboard's key repeat: Gboard sends Backspace to the game's view as key events (KEYCODE_DEL; the view isn't a text
// editor), so holding it deleted one character and stopped (players' reports; seen in logcat on a Galaxy S24 Ultra:
// sendKeyEvent DEL -> deleteSurroundingTextInCodePoints -> closeConnection -> startInput reason APP_CALLED_RESTART_INPUT_API).
// The view here takes the soft keyboard's Backspace while the game's keyboard is up and makes the same edit through the
// input connection (as GameTextInput's processKeyEvent does: finish the composition, delete the selection or the code
// point before the cursor), tells the keyboard where the cursor now is, and leaves the restart out. Everything else (other
// keys, a hardware keyboard's Backspace, the keyboard closed) goes on as before. Put in the manifest in place of
// UnityPlayerGameActivity by the Editor's AndroidGameActivity. In Unity's package to reach its view class.

package com.unity3d.player;

import android.content.Context;
import android.text.Editable;
import android.text.Selection;
import android.view.KeyEvent;
import android.view.inputmethod.BaseInputConnection;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;

import com.google.androidgamesdk.GameActivity;

public class GoF2GameActivity extends UnityPlayerGameActivity
{
    class KeyRepeatSurfaceView extends GameActivitySurfaceView
    {
        KeyRepeatSurfaceView(GameActivity activity) { super(activity); }

        @Override public boolean dispatchKeyEvent(KeyEvent event)
        {
            if (event.getKeyCode() == KeyEvent.KEYCODE_DEL && (event.getFlags() & KeyEvent.FLAG_SOFT_KEYBOARD) != 0)
            {
                // The view's own connection, only while the game's keyboard is up (null otherwise); the EditorInfo is a
                // throwaway the call copies the connection's into.
                com.google.androidgamesdk.gametextinput.InputConnection ic = onCreateInputConnection(new EditorInfo());
                if (ic != null && ic.getSoftKeyboardActive())
                {
                    if (event.getAction() == KeyEvent.ACTION_DOWN) deleteBack(ic);
                    return true;
                }
            }
            return super.dispatchKeyEvent(event);
        }

        void deleteBack(com.google.androidgamesdk.gametextinput.InputConnection ic)
        {
            Editable text = ic.getEditable();
            if (text == null) return;
            ic.beginBatchEdit();
            ic.finishComposingText();
            int start = Selection.getSelectionStart(text), end = Selection.getSelectionEnd(text);
            if (start >= 0 && end > start) ic.commitText("", 1);
            else ic.deleteSurroundingTextInCodePoints(1, 0);
            ic.endBatchEdit();   // GameTextInput passes the new text to the game here
            InputMethodManager imm = (InputMethodManager) getContext().getSystemService(Context.INPUT_METHOD_SERVICE);
            if (imm != null)
                imm.updateSelection(this, Selection.getSelectionStart(text), Selection.getSelectionEnd(text),
                                    BaseInputConnection.getComposingSpanStart(text), BaseInputConnection.getComposingSpanEnd(text));
        }
    }

    @Override protected InputEnabledSurfaceView createSurfaceView()
    {
        return new KeyRepeatSurfaceView(this);
    }
}
