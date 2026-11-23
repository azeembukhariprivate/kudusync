// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
// Puts every window into the chosen language as it's shown, and again when its content changes, so the apps' code can
// stay in English. Also keeps the choice of language.
using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
using Microsoft.Win32;
using Kudusync.Controls;
using Kudusync.Core;

namespace Kudusync.Views
{
    /// <summary>
    /// Shows the app in the chosen language. Every window is looked through when it opens and whenever what it shows
    /// changes: the texts on it (labels, buttons, menus, tips, the title) are swapped for the chosen language's. What
    /// people type and what the app works with is never touched, and in English nothing is done at all.
    /// </summary>
    public static class LangUi
    {
        /// <summary>The name of the setting that keeps the language chosen.</summary>
        private const string ValueName = "AppLanguage"; // not "Language": KUDUTEXT keeps the language people speak under that name
        /// <summary>Where the app's settings are kept in the registry.</summary>
        private static string KeyPath => @"Software\" + AppInfo.Name;
        /// <summary>True once windows are being watched.</summary>
        private static bool _started;

        private sealed class Was
        {
            /// <summary>The text as it was last put into the language, so it isn't done twice.</summary>
            public string Text;
        }

        // What each text was last set to by us, so the same text isn't looked up again and again.
        private static readonly ConditionalWeakTable<object, Was> Done = new ConditionalWeakTable<object, Was>();
        /// <summary>The windows being watched, each with its watcher (forgotten when the window is gone).</summary>
        private static readonly ConditionalWeakTable<Window, Watch> Watches = new ConditionalWeakTable<Window, Watch>();

        /// <summary>The language chosen in Settings: a code, or empty for "the same as Windows".</summary>
        public static string Chosen
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                        return key?.GetValue(ValueName) as string ?? string.Empty;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException)
                {
                    return string.Empty;
                }
            }
            set
            {
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                        key?.SetValue(ValueName, value ?? string.Empty);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException)
                {
                    // The choice isn't kept; the app goes on in the language it has.
                }
            }
        }

        /// <summary>
        /// Call once when the app starts, before its first window. "--lang ml" on the command line picks a language for
        /// this run; the self-test runs in English unless it's given one.
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;
            var args = Environment.GetCommandLineArgs();
            var at = Array.FindIndex(args, a => string.Equals(a, "--lang", StringComparison.OrdinalIgnoreCase));
            string code;
            if (at >= 0 && at + 1 < args.Length) code = Lang.Has(args[at + 1]) ? args[at + 1] : "en";
            else if (args.Any(a => a.StartsWith("--self-test", StringComparison.OrdinalIgnoreCase))) code = "en";
            else code = Lang.Choose(Chosen);
            Lang.Use(code);
            // Always listening, so the language can also be changed while the app runs (the self-test does); in
            // English each window is left alone.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, e) => Apply(s as DependencyObject)));
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, e) => Apply(s as DependencyObject)));
            EventManager.RegisterClassHandler(typeof(Popup), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) => Apply(s as DependencyObject)));
        }

        /// <summary>Changes the language while the app runs (the self-test does), for windows opened from now on.</summary>
        public static void Use(string code)
        {
            Start();
            Lang.Use(code);
        }

        /// <summary>When a window is shown: mirrors it for right-to-left languages and starts watching it.</summary>
        private static void WindowLoaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is Window window) || !ReferenceEquals(e.OriginalSource, sender) || Lang.IsEnglish) return;
            if (Watches.TryGetValue(window, out _)) return;
            if (Lang.RightToLeft) window.FlowDirection = FlowDirection.RightToLeft;
            var watch = new Watch(window);
            Watches.Add(window, watch);
            watch.Look();
        }

        /// <summary>Looks through a window again each time its layout changes, but not more often than is cheap.</summary>
        private sealed class Watch
        {
            /// <summary>The window being watched.</summary>
            private readonly Window _window;
            /// <summary>Looks again a little later, when the layout changed during the wait.</summary>
            private readonly DispatcherTimer _later;
            /// <summary>When the window was last looked through.</summary>
            private DateTime _last = DateTime.MinValue;
            /// <summary>The least time between two looks; it grows for big windows, which take longer.</summary>
            private TimeSpan _gap = TimeSpan.FromMilliseconds(120);
            /// <summary>True during a look, so the changes it makes don't start another one.</summary>
            private bool _looking;

            /// <summary>
            /// Starts watching a window: each time its layout changes it's looked through again, but not more often
            /// than is cheap.
            /// </summary>
            public Watch(Window window)
            {
                _window = window;
                _later = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = _gap };
                _later.Tick += (s, e) =>
                {
                    _later.Stop();
                    Look();
                };
                window.LayoutUpdated += (s, e) =>
                {
                    if (_looking) return;
                    if (DateTime.UtcNow - _last >= _gap) Look();
                    else if (!_later.IsEnabled) _later.Start();
                };
                window.Closed += (s, e) => _later.Stop();
            }

            /// <summary>Looks through the window now, putting any new texts into the language.</summary>
            public void Look()
            {
                if (_looking) return;
                _looking = true;
                var clock = Stopwatch.StartNew();
                try { Apply(_window); }
                finally
                {
                    _looking = false;
                    _last = DateTime.UtcNow;
                    // A big window takes longer to look through: do it less often.
                    _gap = TimeSpan.FromMilliseconds(Math.Max(120, Math.Min(1000, clock.ElapsedMilliseconds * 12)));
                    _later.Interval = _gap;
                }
            }
        }

        /// <summary>
        /// The text in the chosen language, or null when it's already done or doesn't change (so it isn't set again).
        /// </summary>
        private static string Fresh(object owner, string now)
        {
            if (string.IsNullOrEmpty(now)) return null;
            Done.TryGetValue(owner, out var was);
            if (was != null && was.Text == now) return null;
            var other = Lang.T(now);
            if (was == null) Done.Add(owner, was = new Was());
            was.Text = other;
            return other == now ? null : other;
        }

        /// <summary>Puts the texts of a window, menu or any part of one into the chosen language.</summary>
        public static void Apply(DependencyObject root)
        {
            if (root == null || Lang.IsEnglish) return;
            One(root);
            foreach (var child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject d) Apply(d);
        }

        /// <summary>
        /// Puts one part of a window into the language: its text, content, header, tooltip, or a list's items.
        /// </summary>
        private static void One(DependencyObject o)
        {
            switch (o)
            {
                case TextBlock t:
                    if (BindingOperations.IsDataBound(t, TextBlock.TextProperty)) break;
                    if (t.Inlines.Count <= 1 && (t.Inlines.Count == 0 || t.Inlines.FirstInline is Run first && !BindingOperations.IsDataBound(first, Run.TextProperty)))
                    {
                        var text = Fresh(t, t.Text);
                        if (text != null) t.Text = text;
                    }
                    break;
                case Run r:
                    if (r.Parent is TextBlock parent && parent.Inlines.Count <= 1) break; // done with its text block
                    if (BindingOperations.IsDataBound(r, Run.TextProperty)) break;
                    var run = Fresh(r, r.Text);
                    if (run != null) r.Text = run;
                    break;
                case Window w:
                    var title = Fresh(w, w.Title);
                    if (title != null) w.Title = title;
                    break;
                case HeaderedContentControl h:
                    Header(h, HeaderedContentControl.HeaderProperty);
                    Content(h);
                    break;
                case HeaderedItemsControl hi:
                    Header(hi, HeaderedItemsControl.HeaderProperty);
                    break;
                case ContentControl c:
                    Content(c);
                    break;
                case ComboBox combo:
                    // Words to choose from stay as they are for the app; only what's shown is put into the language.
                    if (combo.ItemTemplate == null && string.IsNullOrEmpty(combo.DisplayMemberPath) && combo.ItemTemplateSelector == null &&
                        combo.Items.Count > 0 && combo.Items.Cast<object>().All(i => i is string))
                        combo.ItemTemplate = WordTemplate;
                    break;
                case ListView list when list.View is GridView grid:
                    foreach (var column in grid.Columns)
                        if (column.Header is string head)
                        {
                            var other = Fresh(column, head);
                            if (other != null) column.Header = other;
                        }
                    break;
                case DataGrid data:
                    foreach (var column in data.Columns)
                        if (column.Header is string head)
                        {
                            var other = Fresh(column, head);
                            if (other != null) column.Header = other;
                        }
                    break;
            }
            if (o is FrameworkElement fe)
            {
                if (fe.ToolTip is string tip)
                {
                    var other = Lang.T(tip);
                    if (other != tip) fe.ToolTip = other;
                }
                else if (fe.ToolTip is DependencyObject richTip) Apply(richTip);
                var name = System.Windows.Automation.AutomationProperties.GetName(fe);
                if (!string.IsNullOrEmpty(name))
                {
                    var other = Lang.T(name);
                    if (other != name) System.Windows.Automation.AutomationProperties.SetName(fe, other);
                }
            }
        }

        /// <summary>A button's or label's content when it's plain text.</summary>
        private static void Content(ContentControl c)
        {
            if (!(c.Content is string s) || BindingOperations.IsDataBound(c, ContentControl.ContentProperty)) return;
            var other = Fresh(c, s);
            if (other != null) c.Content = other;
        }

        /// <summary>A header (of a tab, a group or a column) when it's plain text.</summary>
        private static void Header(DependencyObject o, DependencyProperty property)
        {
            if (!(o.GetValue(property) is string s) || BindingOperations.IsDataBound(o, property)) return;
            var other = Fresh(o, s);
            if (other != null) o.SetValue(property, other);
        }

        private sealed class Words : IValueConverter
        {
            /// <summary>Puts the text shown into the language.</summary>
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string s ? Lang.T(s) : value;
            /// <summary>Nothing to turn back: the text is only shown.</summary>
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value;
        }

        /// <summary>The template that shows a list's words in the language, made once.</summary>
        private static DataTemplate _wordTemplate;

        private static DataTemplate WordTemplate
        {
            get
            {
                if (_wordTemplate != null) return _wordTemplate;
                var text = new FrameworkElementFactory(typeof(TextBlock));
                text.SetBinding(TextBlock.TextProperty, new Binding { Converter = new Words() });
                return _wordTemplate = new DataTemplate { VisualTree = text };
            }
        }

        /// <summary>
        /// The language chooser for the Settings window: "The same as Windows", then each language by its own name. The
        /// choice is kept at once and used from the next start (the note under it says so).
        /// </summary>
        public static FrameworkElement Picker()
        {
            var box = new ComboBox { MinWidth = 220, MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Left };
            box.Items.Add(new ComboBoxItem { Content = "The same as Windows", Tag = string.Empty });
            foreach (var (code, name) in Lang.All) box.Items.Add(new ComboBoxItem { Content = name, Tag = code });
            var chosen = Chosen;
            box.SelectedIndex = Math.Max(0, box.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == chosen));
            var note = Ui.Subtle("The language changes the next time you start " + AppInfo.Name + ".", 12);
            note.Visibility = Visibility.Collapsed;
            box.SelectionChanged += (s, e) =>
            {
                Chosen = (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
                note.Visibility = Lang.Choose(Chosen) == Lang.Current ? Visibility.Collapsed : Visibility.Visible;
            };
            System.Windows.Automation.AutomationProperties.SetName(box, "Language");
            return Ui.Column(box, note.Margin(0, 6, 0, 0));
        }
    }
}
