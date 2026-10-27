// 출입 기록 조회와 조회 버튼으로 확정한 검색 조건을 관리합니다.
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AccessControlServer;

public partial class MainWindow
{
    private ICollectionView? historyView;
    private DateTime? appliedHistoryFrom, appliedHistoryUntil;
    private string appliedHistoryQuery = "";

    private void InitializeHistoryFilters()
    {
        // 별도 뷰를 사용해 필터가 메인 관제의 최근 기록 목록에 영향을 주지 않게 합니다.
        historyView = new ListCollectionView(rows) { Filter = MatchesHistoryFilters };
        Events.ItemsSource = historyView;
    }

    private bool MatchesHistoryFilters(object item)
    {
        if (item is not AccessEventRow row) return false;
        if (appliedHistoryFrom is not null || appliedHistoryUntil is not null)
        {
            if (!DateTime.TryParseExact(row.시각, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var at)) return false;
            if (appliedHistoryFrom is { } from && at.Date < from.Date) return false;
            if (appliedHistoryUntil is { } until && at.Date > until.Date) return false;
        }
        string query = appliedHistoryQuery;
        return query.Length == 0 || new[] { row.카메라, row.사원번호, row.이름, row.결과, row.사유 }
            .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyHistoryFilters()
    {
        appliedHistoryFrom = HistoryFrom.SelectedDate;
        appliedHistoryUntil = HistoryUntil.SelectedDate;
        appliedHistoryQuery = HistorySearch.Text.Trim();
        historyView?.Refresh();
    }
    private void InsertHistory(AccessEventRow row)
    {
        if (rows.Any(r => r.이벤트ID == row.이벤트ID))
            return;
        int index = 0;
        while (index < rows.Count && string.CompareOrdinal(rows[index].시각, row.시각) > 0)
            index++;
        rows.Insert(index, row);
        while (rows.Count > 500)
            rows.RemoveAt(rows.Count - 1);
        HistoryStatus.Text = $"DB 출입 기록 {rows.Count}건 표시";
    }

    private async void RefreshHistory(object sender, RoutedEventArgs e)
    {
        ApplyHistoryFilters();
        var db = Volatile.Read(ref faceDatabase);
        if (db is null)
        {
            HistoryStatus.Text = "서버 DB 연결을 먼저 해 주세요.";
            return;
        }

        if (!HistoryRefresh.IsEnabled)
            return;
        HistoryRefresh.IsEnabled = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var history = await db.EventsAsync(cts.Token);
            if (closing || !ReferenceEquals(db, Volatile.Read(ref faceDatabase)))
                return;
            rows.Clear();
            foreach (var row in history)
                rows.Add(row);
            HistoryStatus.Text = $"DB 최근 {rows.Count}건 조회";
        }
        catch (Exception)
        {
            if (!closing)
                HistoryStatus.Text = "출입 기록 조회 실패 · DB 연결을 확인하세요.";
        }
        finally
        {
            if (!closing)
                HistoryRefresh.IsEnabled = true;
        }
    }

}
