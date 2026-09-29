using WSGM.Controls;

namespace WSGM.Tests;

public class OnScreenKeyboardTests
{
    [Fact]
    public void InsertExternalText_ReplacesSelectionAndMovesCaret()
    {
        var box = new Avalonia.Controls.TextBox
        {
            Text = "before OLD after",
            SelectionStart = 7,
            SelectionEnd = 10,
        };
        var keyboard = new OnScreenKeyboard { Target = box };

        keyboard.InsertExternalText("NEW");

        Assert.Equal("before NEW after", box.Text);
        Assert.Equal(10, box.CaretIndex);
        Assert.Equal(box.SelectionStart, box.SelectionEnd);
    }

    [Fact]
    public void InsertExternalText_TruncatesPasteAtMaximumLength()
    {
        var box = new Avalonia.Controls.TextBox
        {
            Text = "1234",
            CaretIndex = 4,
            SelectionStart = 4,
            SelectionEnd = 4,
            MaxLength = 6,
        };
        var keyboard = new OnScreenKeyboard { Target = box };

        keyboard.InsertExternalText("56789");

        Assert.Equal("123456", box.Text);
        Assert.Equal(6, box.CaretIndex);
    }

    [Fact]
    public void Backspace_RemovesTheSelectionBeforeTouchingThePreviousCharacter()
    {
        var box = new Avalonia.Controls.TextBox
        {
            Text = "keep REMOVE keep",
            SelectionStart = 5,
            SelectionEnd = 11,
        };
        var keyboard = new OnScreenKeyboard { Target = box };

        keyboard.Backspace();

        Assert.Equal("keep  keep", box.Text);
        Assert.Equal(5, box.CaretIndex);
        Assert.Equal(5, box.SelectionStart);
        Assert.Equal(5, box.SelectionEnd);
    }

    [Fact]
    public void Backspace_DeletesOneCharacterAndCollapsesTheCaret()
    {
        var box = new Avalonia.Controls.TextBox
        {
            Text = "abcd",
            SelectionStart = 3,
            SelectionEnd = 3,
            CaretIndex = 3,
        };
        var keyboard = new OnScreenKeyboard { Target = box };

        keyboard.Backspace();

        Assert.Equal("abd", box.Text);
        Assert.Equal(2, box.CaretIndex);
        Assert.Equal(2, box.SelectionStart);
        Assert.Equal(2, box.SelectionEnd);
    }

    [Fact]
    public void EveryCharacterAWpaPassphraseMayContainIsReachable()
    {
        // This keyboard is the only text entry in game mode: Windows' own touch
        // keyboard is rendered by an immersive-shell AppX that cannot activate
        // with no Explorer running. A printable ASCII character missing from the
        // layout is therefore a network whose password cannot be typed at all.
        var reachable = new HashSet<char>(string.Concat(OnScreenKeyboard.AllKeys()));
        var missing = new List<char>();
        for (var c = ' '; c <= '~'; c++)
        {
            if (!reachable.Contains(c))
            {
                missing.Add(c);
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void TheLayoutOffersBothLetterCases()
    {
        var reachable = new HashSet<char>(string.Concat(OnScreenKeyboard.AllKeys()));
        Assert.Contains('a', reachable);
        Assert.Contains('Z', reachable);
    }
}
