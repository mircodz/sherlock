using System;
using System.Threading;
using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Widgets;

namespace Sherlock.CLI.Tui;

/// <summary>The content of one tab. Every view is wrapped in one, so the keys mean the same on every screen: Tab always
/// switches views, and the arrows only ever move inside a view. Cellar's tab bar acts on whatever keys a view ignores,
/// so on its own it switches views on ←/→ in a table but not in a tree, which uses them.</summary>
internal sealed class View : Widget
{
    private readonly Widget _frame;
    private volatile Widget? _content;

    private View(Widget content)
    {
        _frame = content;
        _content = content;
    }

    private View(Func<CancellationToken, Widget> build) => _frame = new AsyncContent(cancellation => _content = build(cancellation));

    public static View Of(Widget content) => new(content);

    /// <summary>Builds the content in the background, showing progress until it's ready. Cellar's
    /// <see cref="AsyncContent"/> never focuses what it builds, so the view passes its focus on.</summary>
    public static View Background(Func<CancellationToken, Widget> build) => new(build);

    /// <summary>Whether the content exists yet; background views build it on first display.</summary>
    internal bool IsBuilt => _content is not null;

    public override bool IsFocusable => true;

    public override bool HasFocus
    {
        get => base.HasFocus;
        set
        {
            base.HasFocus = value;
            _frame.HasFocus = value;
            if (_content is { } content)
            {
                content.HasFocus = value;
            }
        }
    }

    public override Size Measure(Size available) => _frame.Measure(available);

    public override void Render(Surface surface, Rect area)
    {
        // Background content may finish building after focus was assigned.
        if (_content is { } content && content.HasFocus != HasFocus)
        {
            content.HasFocus = HasFocus;
        }
        _frame.Render(surface, area);
    }

    public override bool OnEvent(InputEvent input)
    {
        Widget target = _content ?? _frame;
        if (input is not KeyEvent key)
        {
            return target.OnEvent(input);
        }
        if (key.Key == Key.Tab)
        {
            return false;
        }
        return target.OnEvent(input) || key.Key is Key.Left or Key.Right;
    }

    protected override void VisitChildren(Action<Widget> visit) => visit(_frame);
}

/// <summary>Sees each key before <paramref name="content"/>: <paramref name="hook"/> returns true when it handled the
/// key, false to leave it to the screen without the widget seeing it, or null to pass it on.</summary>
internal sealed class KeyHook(Widget content, Func<KeyEvent, bool?> hook) : Widget
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
        input is KeyEvent key && hook(key) is bool handled ? handled : content.OnEvent(input);

    protected override void VisitChildren(Action<Widget> visit) => visit(content);
}
