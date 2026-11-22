using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AkioExplorer.App.Views;

/// <summary>Dialogs for cloud-service features: sharing, comments, activity, description.</summary>
public static class CloudDialogs
{
    private static readonly string[] RoleNames = ["Viewer", "Commenter", "Editor", "Owner"];

    // ------------------------------------------------------------------ share

    /// <summary>
    /// Google Drive's Share dialog: add people (with role and optional e-mail), change or remove access, and
    /// "General access" (restricted / anyone with the link + role) with Copy link. Every change applies at once.
    /// </summary>
    public static async Task ShowShareAsync(DialogService dialogs, ISharingProvider sharing, StorageItem item)
    {
        var cts = new CancellationTokenSource();
        bool busy = false, loading = false;
        ShareSettings? current = null;

        var email = new TextBox { PlaceholderText = "Add people by e-mail" };
        AutomationProperties.SetName(email, "E-mail address");
        var role = Combo(RoleNames[..3], 2);
        AutomationProperties.SetName(role, "Role for the new person");
        var add = new Button { Content = "Share", Style = Style("AccentButtonStyle"), IsEnabled = false };
        var notify = new CheckBox { Content = "Notify people by e-mail", IsChecked = true };
        var message = new TextBox { PlaceholderText = "Message (optional)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
        AutomationProperties.SetName(message, "Message");

        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(addRow, email, 0);
        Place(addRow, role, 1);
        Place(addRow, add, 2);

        var people = new StackPanel { Spacing = 6 };
        var peopleScroll = new ScrollViewer { Content = people, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var linkAccess = Combo(["Restricted – only people added above", "Anyone with the link"], 0);
        AutomationProperties.SetName(linkAccess, "General access");
        var linkRole = Combo(RoleNames[..3], 0);
        AutomationProperties.SetName(linkRole, "Role for anyone with the link");
        var copy = new Button { Content = "Copy link" };
        var linkRow = new Grid { ColumnSpacing = 8 };
        linkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        linkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        linkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(linkRow, linkAccess, 0);
        Place(linkRow, linkRole, 1);
        Place(linkRow, copy, 2);

        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var info = new InfoBar { IsClosable = true, IsOpen = false };

        var panel = new StackPanel { Spacing = 10, Width = 560 };
        panel.Children.Add(addRow);
        panel.Children.Add(notify);
        panel.Children.Add(message);
        panel.Children.Add(progress);
        panel.Children.Add(info);
        panel.Children.Add(Heading("People with access"));
        panel.Children.Add(peopleScroll);
        panel.Children.Add(Heading("General access"));
        panel.Children.Add(linkRow);

        var dialog = new ContentDialog { Title = $"Share \"{item.Name}\"", Content = panel, CloseButtonText = "Done" };
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;

        void Show(ShareSettings settings)
        {
            current = settings;
            loading = true;
            people.Children.Clear();
            foreach (var person in settings.People)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var who = new StackPanel();
                who.Children.Add(new TextBlock { Text = person.IsYou ? $"{person.DisplayName} (you)" : person.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
                if (person.Email is { } mail && mail != person.DisplayName)
                    who.Children.Add(new TextBlock { Text = mail, Style = Style("CaptionTextBlockStyle"), Opacity = 0.7 });
                Place(row, who, 0);
                bool fixedRole = person.Role == ShareRole.Owner || person.Inherited || person.IsYou;
                if (fixedRole)
                {
                    var label = new TextBlock
                    {
                        Text = person.Role == ShareRole.Owner ? "Owner" : person.Inherited ? $"{RoleNames[(int)person.Role]} (from folder)" : RoleNames[(int)person.Role],
                        VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75,
                    };
                    Place(row, label, 1);
                }
                else
                {
                    var combo = Combo(RoleNames, (int)person.Role);
                    AutomationProperties.SetName(combo, $"Role of {person.DisplayName}");
                    combo.SelectionChanged += async (_, _) =>
                    {
                        if (loading || combo.SelectedIndex < 0) return;
                        var chosen = (ShareRole)combo.SelectedIndex;
                        await RunAsync(ct => sharing.SetRoleAsync(item, person.Id, chosen, ct),
                            chosen == ShareRole.Owner ? $"{person.DisplayName} is now the owner." : null);
                    };
                    Place(row, combo, 1);
                    var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
                    ToolTipService.SetToolTip(remove, $"Remove {person.DisplayName}");
                    AutomationProperties.SetName(remove, $"Remove {person.DisplayName}");
                    remove.Click += async (_, _) => await RunAsync(ct => sharing.RemovePersonAsync(item, person.Id, ct), $"Removed {person.DisplayName}.");
                    Place(row, remove, 2);
                }
                people.Children.Add(row);
            }
            if (settings.People.Count == 0)
                people.Children.Add(new TextBlock { Text = "Only you.", Opacity = 0.7 });
            linkAccess.SelectedIndex = settings.Link == LinkAccess.AnyoneWithLink ? 1 : 0;
            linkRole.SelectedIndex = Math.Min(2, (int)settings.LinkRole);
            linkRole.IsEnabled = settings.Link == LinkAccess.AnyoneWithLink;
            loading = false;
        }

        async Task RunAsync(Func<CancellationToken, Task> work, string? done = null)
        {
            if (busy) return;
            busy = true;
            info.IsOpen = false;
            progress.Visibility = Visibility.Visible;
            try
            {
                await work(cts.Token);
                if (done is not null) Info(InfoBarSeverity.Success, done);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotSupported)
            {
                Info(InfoBarSeverity.Informational, ex.Message); // e.g. ownership request sent
            }
            catch (Exception ex)
            {
                Info(InfoBarSeverity.Error, ex.Message);
            }
            finally
            {
                busy = false;
                progress.Visibility = Visibility.Collapsed;
            }
            try
            {
                Show(await sharing.GetSharingAsync(item, cts.Token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Info(InfoBarSeverity.Error, ex.Message);
            }
        }

        void Info(InfoBarSeverity severity, string text)
        {
            info.Severity = severity;
            info.Message = text;
            info.IsOpen = true;
        }

        email.TextChanged += (_, _) => add.IsEnabled = email.Text.Contains('@') && email.Text.Trim().Length > 3;
        notify.Checked += (_, _) => message.IsEnabled = true;
        notify.Unchecked += (_, _) => message.IsEnabled = false;
        async Task AddAsync()
        {
            if (!add.IsEnabled) return;
            string who = email.Text.Trim();
            var chosen = (ShareRole)Math.Max(0, role.SelectedIndex);
            await RunAsync(ct => sharing.AddPersonAsync(item, who, chosen, notify.IsChecked == true, message.Text, ct), $"Shared with {who}.");
            if (info.Severity != InfoBarSeverity.Error)
            {
                email.Text = string.Empty;
                message.Text = string.Empty;
            }
        }
        add.Click += async (_, _) => await AddAsync();
        email.KeyDown += async (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            await AddAsync();
        };
        linkAccess.SelectionChanged += async (_, _) =>
        {
            if (loading) return;
            var access = linkAccess.SelectedIndex == 1 ? LinkAccess.AnyoneWithLink : LinkAccess.Restricted;
            linkRole.IsEnabled = access == LinkAccess.AnyoneWithLink;
            await RunAsync(ct => sharing.SetLinkAccessAsync(item, access, (ShareRole)Math.Max(0, linkRole.SelectedIndex), ct),
                access == LinkAccess.AnyoneWithLink ? "Anyone with the link can open it now." : "Only people added above can open it now.");
        };
        linkRole.SelectionChanged += async (_, _) =>
        {
            if (loading || linkAccess.SelectedIndex != 1) return;
            await RunAsync(ct => sharing.SetLinkAccessAsync(item, LinkAccess.AnyoneWithLink, (ShareRole)Math.Max(0, linkRole.SelectedIndex), ct));
        };
        copy.Click += (_, _) =>
        {
            if (current?.WebLink is not { } link) return;
            var package = new DataPackage();
            package.SetText(link);
            Clipboard.SetContent(package);
            Info(InfoBarSeverity.Success, current.Link == LinkAccess.AnyoneWithLink
                ? "Link copied. Anyone with it can open the item."
                : "Link copied. Only people with access can open it.");
        };
        dialog.Opened += async (_, _) =>
        {
            await RunAsync(_ => Task.CompletedTask);
            email.Focus(FocusState.Programmatic);
        };
        dialog.Closed += (_, _) => cts.Cancel();
        await dialogs.ShowAsync(dialog);
        cts.Dispose();
    }

    // ------------------------------------------------------------------ comments

    /// <summary>Comments on a file, newest first, with reply / resolve and a box for a new comment.</summary>
    public static async Task ShowCommentsAsync(DialogService dialogs, ICommentsProvider comments, StorageItem item)
    {
        var cts = new CancellationTokenSource();
        var list = new StackPanel { Spacing = 12 };
        var scroll = new ScrollViewer { Content = list, Height = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var box = new TextBox { PlaceholderText = "Add a comment", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
        AutomationProperties.SetName(box, "New comment");
        var post = new Button { Content = "Comment", Style = Style("AccentButtonStyle"), IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right };
        var showResolved = new CheckBox { Content = "Show resolved" };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, IsOpen = false };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 8, Width = 560 };
        panel.Children.Add(showResolved);
        panel.Children.Add(progress);
        panel.Children.Add(error);
        panel.Children.Add(scroll);
        panel.Children.Add(box);
        panel.Children.Add(post);
        var dialog = new ContentDialog { Title = $"Comments on \"{item.Name}\"", Content = panel, CloseButtonText = "Close" };
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;
        IReadOnlyList<FileComment> loaded = [];

        async Task RunAsync(Func<CancellationToken, Task> work)
        {
            error.IsOpen = false;
            progress.Visibility = Visibility.Visible;
            try
            {
                await work(cts.Token);
                loaded = await comments.ListCommentsAsync(item, cts.Token);
                Show();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                error.Message = ex.Message;
                error.IsOpen = true;
            }
            finally
            {
                progress.Visibility = Visibility.Collapsed;
            }
        }

        void Show()
        {
            list.Children.Clear();
            var visible = loaded.Where(c => showResolved.IsChecked == true || !c.Resolved).ToList();
            if (visible.Count == 0)
                list.Children.Add(new TextBlock { Text = loaded.Count == 0 ? "No comments yet." : "No open comments.", Opacity = 0.7 });
            foreach (var comment in visible)
            {
                var card = new StackPanel { Spacing = 4, Padding = new Thickness(12), CornerRadius = new CornerRadius(6),
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Opacity = comment.Resolved ? 0.6 : 1 };
                card.Children.Add(new TextBlock
                {
                    Text = $"{comment.Author} · {(comment.When is { } w ? Formatting.FormatDate(w) : "")}{(comment.Resolved ? " · resolved" : "")}",
                    Style = Style("CaptionTextBlockStyle"), Opacity = 0.75,
                });
                if (comment.Quote is { Length: > 0 } quote)
                    card.Children.Add(new TextBlock { Text = $"“{quote}”", FontStyle = Windows.UI.Text.FontStyle.Italic, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
                card.Children.Add(new TextBlock { Text = comment.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                foreach (var reply in comment.Replies)
                {
                    var r = new StackPanel { Margin = new Thickness(16, 4, 0, 0) };
                    r.Children.Add(new TextBlock { Text = $"{reply.Author} · {(reply.When is { } rw ? Formatting.FormatDate(rw) : "")}", Style = Style("CaptionTextBlockStyle"), Opacity = 0.75 });
                    r.Children.Add(new TextBlock { Text = reply.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                    card.Children.Add(r);
                }
                if (!comment.Resolved)
                {
                    var replyBox = new TextBox { PlaceholderText = "Reply", Margin = new Thickness(16, 6, 0, 0) };
                    AutomationProperties.SetName(replyBox, $"Reply to {comment.Author}");
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
                    var reply = new Button { Content = "Reply", IsEnabled = false };
                    var resolve = new Button { Content = "Resolve" };
                    replyBox.TextChanged += (_, _) => reply.IsEnabled = replyBox.Text.Trim().Length > 0;
                    reply.Click += async (_, _) => await RunAsync(ct => comments.ReplyAsync(item, comment.Id, replyBox.Text.Trim(), resolve: false, ct));
                    resolve.Click += async (_, _) => await RunAsync(ct => comments.ReplyAsync(item, comment.Id, replyBox.Text.Trim(), resolve: true, ct));
                    buttons.Children.Add(reply);
                    buttons.Children.Add(resolve);
                    card.Children.Add(replyBox);
                    card.Children.Add(buttons);
                }
                list.Children.Add(card);
            }
        }

        box.TextChanged += (_, _) => post.IsEnabled = box.Text.Trim().Length > 0;
        post.Click += async (_, _) =>
        {
            string text = box.Text.Trim();
            await RunAsync(ct => comments.AddCommentAsync(item, text, ct));
            if (!error.IsOpen) box.Text = string.Empty;
        };
        showResolved.Checked += (_, _) => Show();
        showResolved.Unchecked += (_, _) => Show();
        dialog.Opened += async (_, _) => await RunAsync(_ => Task.CompletedTask);
        dialog.Closed += (_, _) => cts.Cancel();
        await dialogs.ShowAsync(dialog);
        cts.Dispose();
    }

    // ------------------------------------------------------------------ activity

    public static async Task ShowActivityAsync(DialogService dialogs, IActivityProvider activity, StorageItem item)
    {
        var cts = new CancellationTokenSource();
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, Height = 360, Margin = new Thickness(-12, 0, -12, 0) };
        AutomationProperties.SetName(list, "Activity");
        var status = new TextBlock { Text = "Loading…", Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8, Width = 560 };
        panel.Children.Add(status);
        panel.Children.Add(list);
        var dialog = new ContentDialog { Title = $"Activity · {item.Name}", Content = panel, CloseButtonText = "Close" };
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var entries = await activity.ListActivityAsync(item, cts.Token);
                status.Text = entries.Count == 0 ? "No activity recorded." : $"{entries.Count} most recent actions{(item.IsContainer ? " in this folder" : "")}.";
                foreach (var entry in entries)
                {
                    var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(new TextBlock { Text = entry.When is { } w ? Formatting.FormatDate(w) : "—", Opacity = 0.7 });
                    var what = new TextBlock { Text = $"{entry.Who} · {entry.What}", TextWrapping = TextWrapping.Wrap };
                    Grid.SetColumn(what, 1);
                    row.Children.Add(what);
                    list.Items.Add(row);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                status.Opacity = 1;
            }
        };
        dialog.Closed += (_, _) => cts.Cancel();
        await dialogs.ShowAsync(dialog);
        cts.Dispose();
    }

    // ------------------------------------------------------------------ description

    /// <summary>Returns true when the description was saved.</summary>
    public static async Task<bool> EditDescriptionAsync(DialogService dialogs, IDescriptionProvider descriptions, StorageItem item)
    {
        string? current;
        try
        {
            current = await descriptions.GetDescriptionAsync(item, CancellationToken.None);
        }
        catch (StorageException ex)
        {
            await dialogs.ConfirmAsync("Couldn't read the description", ex.Message, "OK");
            return false;
        }
        var box = new TextBox { Text = current ?? string.Empty, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, Width = 460 };
        AutomationProperties.SetName(box, "Description");
        var dialog = new ContentDialog
        {
            Title = $"Description of \"{item.Name}\"",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        bool saved = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await descriptions.SetDescriptionAsync(item, box.Text, CancellationToken.None);
                saved = true;
            }
            catch (StorageException ex)
            {
                box.Header = ex.Message;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialogs.ShowAsync(dialog);
        return saved;
    }

    // ------------------------------------------------------------------ helpers

    private static ComboBox Combo(IEnumerable<string> items, int selected)
    {
        var combo = new ComboBox { MinWidth = 120 };
        foreach (var text in items) combo.Items.Add(text);
        combo.SelectedIndex = selected;
        return combo;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Style Style(string key) => (Style)Application.Current.Resources[key];
}
