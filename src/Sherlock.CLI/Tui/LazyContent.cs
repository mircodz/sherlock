using System;
using System.Threading;
using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Widgets;

namespace Sherlock.CLI.Tui;

/// <summary><see cref="AsyncContent"/> that passes focus and keys to the widget it builds. Cellar's version never
/// focuses its content, so a lazily built table in a tab ignored the keyboard. Tab is left to the enclosing tabs,
/// which switch views with it; a focused table would otherwise consume it.</summary>
internal sealed class LazyContent : Widget
{
    private readonly AsyncContent _inner;
    private volatile Widget? _content;

    public LazyContent(Func<CancellationToken, Widget> build) =>
        _inner = new AsyncContent(cancellation => _content = build(cancellation));

    public override bool IsFocusable => true;

    public override bool HasFocus
    {
        get => base.HasFocus;
        set
        {
            base.HasFocus = value;
            _inner.HasFocus = value;
            if (_content is { } content)
            {
                content.HasFocus = value;
            }
        }
    }

    public override Size Measure(Size available) => _inner.Measure(available);

    public override void Render(Surface surface, Rect area)
    {
        // The content may finish building after focus was assigned.
        if (_content is { } content && content.HasFocus != HasFocus)
        {
            content.HasFocus = HasFocus;
        }
        _inner.Render(surface, area);
    }

    public override bool OnEvent(InputEvent input) =>
        input is not KeyEvent { Key: Key.Tab } && (_content is { } content ? content.OnEvent(input) : _inner.OnEvent(input));

    protected override void VisitChildren(Action<Widget> visit) => visit(_inner);
}

/// <summary>A tab's content: forwards focus and keys, except Tab, which the enclosing tabs use to switch views, and
/// any key <paramref name="declines"/> leaves to the screen (such as Backspace, which goes back).</summary>
internal sealed class TabPane(Widget content, Func<KeyEvent, bool>? declines = null) : Widget
{
    public override bool IsFocusable => true;

    public override bool HasFocus
    {
        get => base.HasFocus;
        set
        {
            base.HasFocus = value;
            content.HasFocus = value;
        }
    }

    public override Size Measure(Size available) => content.Measure(available);

    public override void Render(Surface surface, Rect area) => content.Render(surface, area);

    public override bool OnEvent(InputEvent input) =>
        input is not KeyEvent { Key: Key.Tab } && !(input is KeyEvent key && declines?.Invoke(key) == true) && content.OnEvent(input);

    protected override void VisitChildren(Action<Widget> visit) => visit(content);
}
