using System.Globalization;
using Microsoft.Win32;

namespace DesktopAssistant.Desktop;

public partial class MainWindow
{
    private string? _appointmentId;
    private DateOnly ViewedDay => DateOnly.FromDateTime(ScheduleDay.SelectedDate ?? DateTime.Today);

    private void ScheduleDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        RefreshAppointments();
        ResetEditor();
    }

    private void Today(object sender, RoutedEventArgs e) => ScheduleDay.SelectedDate = DateTime.Today;

    private void RefreshAppointments()
    {
        try
        {
            List<Appointment> items = _store.Appointments(ViewedDay);
            ScheduleList.ItemsSource = items.Select(item => new AppointmentRow(item)).ToList();
            ScheduleEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error) { ScheduleStatus.Text = error.Message; }
    }

    private void ResetEditor(Appointment? appointment = null)
    {
        _appointmentId = appointment?.Id;
        AppointmentHeading.Text = appointment is null ? "新日程" : "编辑日程";
        EditDay.SelectedDate = (appointment?.Day ?? ViewedDay).ToDateTime(TimeOnly.MinValue);
        EditTitle.Text = appointment?.Title ?? "";
        EditStart.Text = appointment?.Start.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        EditEnd.Text = appointment?.End?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        EditNote.Text = appointment?.Note ?? "";
        EditDone.IsChecked = appointment?.Done ?? false;
        DeleteAppointmentButton.IsEnabled = appointment is not null && appointment.Id != _draft?.Id;
        ScheduleStatus.Text = "";
    }

    private void NewAppointment(object sender, RoutedEventArgs e)
    {
        ScheduleList.SelectedItem = null;
        ResetEditor();
        EditTitle.Focus();
    }

    private void AppointmentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && ScheduleList.SelectedItem is AppointmentRow row)
            ResetEditor(row.Item);
    }

    private void OpenDraft(object sender, RoutedEventArgs e)
    {
        if (_draft is not { } draft)
            return;
        ShowPage(1);
        ScheduleDay.SelectedDate = draft.Day.ToDateTime(TimeOnly.MinValue);
        ResetEditor(draft);
        AppointmentHeading.Text = "核对日程草稿";
        EditTitle.Focus();
    }

    private void SaveAppointment(object sender, RoutedEventArgs e)
    {
        try
        {
            if (EditDay.SelectedDate is not DateTime day || !TryTime(EditStart.Text, out TimeOnly start))
                throw new ArgumentException("请选择日期，并按 HH:mm 输入开始时间。");
            TimeOnly? end = null;
            if (!string.IsNullOrWhiteSpace(EditEnd.Text))
            {
                if (!TryTime(EditEnd.Text, out TimeOnly parsed))
                    throw new ArgumentException("请按 HH:mm 输入结束时间。");
                end = parsed;
            }
            var appointment = new Appointment(_appointmentId ?? Guid.NewGuid().ToString("N"),
                DateOnly.FromDateTime(day), start, end, EditTitle.Text.Trim(), EditNote.Text.Trim(), EditDone.IsChecked == true);
            _store.SaveAppointment(appointment);
            if (_draft?.Id == appointment.Id)
            {
                _draft = null;
                RefreshChat();
            }
            ScheduleDay.SelectedDate = day;
            RefreshAppointments();
            ScheduleList.SelectedItem = ((IEnumerable<AppointmentRow>)ScheduleList.ItemsSource)
                .FirstOrDefault(row => row.Item.Id == appointment.Id);
            ResetEditor(appointment);
            ScheduleStatus.Text = "已保存日程。";
            PollReminders();
        }
        catch (Exception error) { ScheduleStatus.Text = error.Message; }
    }

    private static bool TryTime(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private void DeleteAppointment(object sender, RoutedEventArgs e)
    {
        if (_appointmentId is null || !Confirm("删除这条日程？"))
            return;
        try
        {
            _store.DeleteAppointment(_appointmentId);
            RefreshAppointments();
            ResetEditor();
            ScheduleStatus.Text = "已删除。";
        }
        catch (Exception error) { ScheduleStatus.Text = error.Message; }
    }

    private void SearchHistory(object sender, TextChangedEventArgs e)
    {
        if (_ready)
            RefreshHistory();
    }

    private void RefreshHistory()
    {
        try { HistoryList.ItemsSource = _store.Conversations(HistorySearch.Text).Select(item => new HistoryRow(item)); }
        catch (Exception error) { HistoryHeading.Text = error.Message; }
    }

    private void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        HistoryMessages.Children.Clear();
        HistoryActions.IsEnabled = HistoryList.SelectedItem is HistoryRow;
        try
        {
            if (HistoryList.SelectedItem is HistoryRow row)
            {
                HistoryHeading.Text = row.Title;
                foreach (Message item in _store.Messages(row.Item.Id))
                    AddMessage(HistoryMessages, item);
            }
            else
                HistoryHeading.Text = "选择一条会话";
        }
        catch (Exception error) { HistoryHeading.Text = error.Message; }
    }

    private void ResumeConversation(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row || _conversation == row.Item.Id)
        {
            ShowPage(0);
            return;
        }
        try
        {
            InterruptReply();
            _conversation = row.Item.Id;
            _draft = null;
            ChatMessages.Children.Clear();
            Composer.Clear();
            ChatStatus.Text = "";
            foreach (Message item in _store.Messages(row.Item.Id))
                AddMessage(ChatMessages, item);
            RefreshChat();
            ShowPage(0);
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void ExportHistory(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row)
            return;
        SaveFileDialog dialog = new()
        {
            Title = "导出会话", AddExtension = true, FileName = "会话-" + row.Item.UpdatedAt.ToLocalTime().ToString("yyyyMMdd-HHmm"),
            Filter = "Markdown (*.md)|*.md|JSON (*.json)|*.json"
        };
        try
        {
            if (dialog.ShowDialog(this) == true)
                File.WriteAllText(dialog.FileName, _store.Export(row.Item, dialog.FilterIndex == 2));
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void DeleteHistory(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row || !Confirm("永久删除这条会话及其消息？"))
            return;
        try
        {
            _store.DeleteConversation(row.Item.Id);
            if (_conversation == row.Item.Id)
                NewChat(sender, e);
            RefreshHistory();
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    public sealed record AppointmentRow(Appointment Item)
    {
        public string Title => Item.Title;
        public string Timing => Item.Start.ToString("HH:mm") +
            (Item.End is TimeOnly end ? " – " + end.ToString("HH:mm") : "") + (Item.Done ? "  ·  已完成" : "");
    }

    public sealed record HistoryRow(Conversation Item)
    {
        public string Title => Item.Title;
        public string Updated => Item.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    }
}
